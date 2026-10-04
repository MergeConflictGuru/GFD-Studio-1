using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.AnimationMatching.Search;
using GFDStudio.AnimationMatching.Stitching;
using GFDStudio.GUI.Controls;

namespace GFDStudio.GUI.Forms;

public partial class MainForm
{
    private TableLayoutPanel mPairedShowroom;
    private Panel mPairFirstPane;
    private AnimationViewPane mPairSecondPane;
    private Label mPairFirstCaption;
    private Label mPairStatus;
    private CheckBox mPairInterpolate;
    private NumericUpDown mPairBlendSeconds;
    private Button mPairExport;
    private Button mPairExportParts;
    private Button mPairInputs;
    private Animation[] mPairAnimations;
    private GfdAnimationClip[] mPairMatchClips;
    private GfdTargetAnimationClip[] mPairFullClips;
    private CharacterAnimationEntry[] mPairEntries;
    private ModelPack mPairModel;
    private StitchedAnimation mPairBlend;
    private (int start, int end)? mPairFirstRange;
    private CancellationTokenSource mPairWork;
    private string mPairKey;
    private int mPairSavedSplitter;
    private int mPairSavedPanelMinimum;
    private bool mPairShowingBlend;
    private bool mPairLoading;
    private bool mPairUpdatingRange;
    private bool IsPairedShowroom => mPairedShowroom?.Visible == true;

    private void InitializeCompactShowroomTransport()
    {
        var table = tableLayoutPanel_AnimationControls;
        table.SuspendLayout();
        var stack = mAnimationTrackBar.Parent;
        stack.Controls.Remove(mAnimationTrackBar);
        stack.Controls.Remove(mAnimationMatchTimeline);
        table.Controls.Remove(stack);
        stack.Dispose();
        table.RowCount = 3;
        table.RowStyles.Clear();
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        table.ColumnStyles.Clear();
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        table.Margin = Padding.Empty;
        table.Padding = new Padding(2);
        table.Controls.Add(mAnimationMatchTimeline, 0, 0);
        table.SetColumnSpan(mAnimationMatchTimeline, 4);
        table.Controls.Add(mAnimationTrackBar, 0, 1);
        table.SetCellPosition(mAnimationPlaybackButton, new TableLayoutPanelCellPosition(1, 1));
        table.SetCellPosition(mAnimationStopButton, new TableLayoutPanelCellPosition(2, 1));
        table.SetCellPosition(mAnimationMatchButton, new TableLayoutPanelCellPosition(3, 1));
        table.Controls.Add(mTimelineMarksControl, 0, 2);
        table.SetColumnSpan(mTimelineMarksControl, 4);
        mAnimationTrackBar.TickStyle = TickStyle.None;
        table.ResumeLayout();
        foreach (Control button in new Control[] { mAnimationPlaybackButton, mAnimationStopButton })
        {
            button.Margin = new Padding(1);
            button.Padding = Padding.Empty;
            button.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f);
            button.MaximumSize = new Size(32, 32);
        }
        mAnimationPlaybackButton.Text = "▶";
        mAnimationPlaybackButton.AccessibleName = "Play or pause first animation";
        mAnimationStopButton.AccessibleName = "Stop first animation";
        mAnimationMatchButton.Font = SystemFonts.MessageBoxFont;
        mAnimationMatchButton.Margin = new Padding(2, 1, 1, 1);
        mAnimationMatchButton.Padding = Padding.Empty;
        SetCompactTransportHeight(false);
    }

    private void SetCompactTransportHeight(bool hasMarks)
    {
        int height = hasMarks ? 78 : 58;
        if (splitContainer_LeftSide.Panel2MinSize == height - 4) return;
        splitContainer_LeftSide.Panel2MinSize = height - 4;
        if (splitContainer_LeftSide.Height > height + splitContainer_LeftSide.Panel1MinSize)
            splitContainer_LeftSide.SplitterDistance = splitContainer_LeftSide.Height - height;
    }

    private bool TryLoadPairedShowroomSelection()
    {
        var entries = mCharacterAnimationListBox.SelectedItems.Cast<CharacterAnimationEntry>().Take(2).ToArray();
        if (entries.Length < 2) return false;
        var model = GetAnimationMatchingTargetModelPack();
        if (model?.Model == null) return false;
        var key = string.Join("|", entries.Select(GetCorrectedAnimationMatchClipId));
        if (IsPairedShowroom && key == mPairKey && ReferenceEquals(model, mPairModel)) return true;
        _ = LoadPairedShowroomAsync(entries, model, key);
        return true;
    }

    private async Task LoadPairedShowroomAsync(CharacterAnimationEntry[] entries, ModelPack model, string key)
    {
        var (generation, token) = BeginCharacterBrowserAnimationLoad();
        mPairLoading = true;
        mAnimationMatchButton.Enabled = false;
        CancelPairWork();
        ++mAnimationMatchPreviewGeneration;
        mAnimationMatchController?.Dispose();
        mAnimationMatchController = null;
        mAnimationMatchControllerContext = null;
        SetCharacterBrowserStatus("Loading the first two selected animations…");
        try
        {
            var firstContext = CaptureCharacterBrowserAnimationPreparationContext(entries[0]);
            var secondContext = CaptureCharacterBrowserAnimationPreparationContext(entries[1]);
            var first = await PrepareCharacterBrowserAnimationAsync(firstContext, token);
            var second = await PrepareCharacterBrowserAnimationAsync(secondContext, token);
            if (IsDisposed || !IsCurrentCharacterBrowserAnimationLoad(generation, token)) return;
            if (first.Animation == null || second.Animation == null)
                throw new InvalidOperationException("One of the selected animations could not be loaded.");

            mPairAnimations = new[] { first.Animation, second.Animation };
            mPairEntries = entries;
            mPairModel = model;
            mPairKey = key;
            mPairBlend = null;
            mPairFirstRange = null;
            mAnimationMatchTimeline.Enabled = true;
            var animations = mPairAnimations;
            mPairMatchClips = entries.Select((entry, i) => new GfdAnimationClip(
                GetCorrectedAnimationMatchClipId(entry), entry.DisplayName, model.Model,
                () => animations[i], AnimationMatchingFramesPerSecond)).ToArray();
            mPairFullClips = entries.Select((entry, i) => new GfdTargetAnimationClip(
                GetCorrectedAnimationMatchClipId(entry), entry.DisplayName, model.Model,
                mPairAnimations[i], AnimationMatchingFramesPerSecond)).ToArray();
            ShowPairedShowroom();
            RememberBrowserTimelineMarks(first.Animation, entries[0]);
            LoadPairFirstAnimation(first.Animation);
            mAnimationMatchCurrentSource = mPairMatchClips[0];
            mPairUpdatingRange = true;
            mAnimationMatchTimeline.FrameCount = mPairMatchClips[0].FrameCount;
            mAnimationMatchTimeline.TransitionFrame = -1;
            mPairUpdatingRange = false;
            mPairSecondPane.LoadClip(model, second.Animation, entries[1].DisplayName, mPairMatchClips[1].FrameCount);
            mPairFirstCaption.Text = "1 · " + entries[0].DisplayName;
            mPairShowingBlend = false;
            mAnimationMatchButton.Enabled = true;
            SetPairResultAvailable(false);
            mPairStatus.Text = "Highlight ranges, then Match · no highlights: end → start";
            SetCharacterBrowserStatus("Two views: first two selected animations only; Match ignores the library.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && IsCurrentCharacterBrowserAnimationLoad(generation, token))
                SetCharacterBrowserStatus("Could not open two views: " + ex.Message);
        }
        finally
        {
            if (!IsDisposed && IsCurrentCharacterBrowserAnimationLoad(generation, token))
            {
                mPairLoading = false;
                mAnimationMatchButton.Enabled = true;
            }
        }
    }

    private void EnsurePairedShowroomControls()
    {
        if (mPairedShowroom != null) return;
        mPairedShowroom = new TableLayoutPanel
        {
            Name = "PairedShowroom", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty, Visible = false, BackColor = Theme.DarkBG
        };
        mPairedShowroom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        mPairedShowroom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        mPairedShowroom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mPairedShowroom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        mPairFirstPane = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 3, 0) };
        mPairFirstCaption = new Label
        {
            Dock = DockStyle.Top, Height = 23, AutoEllipsis = true, Font = SystemFonts.MessageBoxFont,
            ForeColor = Color.Gainsboro, BackColor = Theme.DarkBG, TextAlign = ContentAlignment.MiddleLeft
        };
        mPairFirstPane.Controls.Add(mPairFirstCaption);
        mPairSecondPane = new AnimationViewPane { Margin = new Padding(3, 0, 0, 0) };
        mPairSecondPane.RangeChanged += (_, _) => InvalidatePairBlend();
        mPairedShowroom.Controls.Add(mPairFirstPane, 0, 0);
        mPairedShowroom.Controls.Add(mPairSecondPane, 1, 0);
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = Padding.Empty,
            Padding = Padding.Empty, Font = SystemFonts.MessageBoxFont, BackColor = Theme.DarkBG, ForeColor = Color.Gainsboro
        };
        foreach (int width in new[] { 70, 54, 91, 60, 64, 60 })
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mPairInputs = PairButton("Inputs", (_, _) => ShowPairInputs());
        mPairInterpolate = new CheckBox { Text = "Blend (s)", Checked = true, Dock = DockStyle.Fill, AutoSize = false, Margin = new Padding(2, 0, 0, 0) };
        mPairBlendSeconds = new NumericUpDown
        { DecimalPlaces = 2, Minimum = 0, Maximum = 10, Increment = .05m, Value = .35m, Dock = DockStyle.Fill, Margin = new Padding(2, 3, 2, 3), AccessibleName = "Interpolation seconds" };
        mPairBlendSeconds.ValueChanged += (_, _) => InvalidatePairBlend();
        mPairInterpolate.CheckedChanged += (_, _) => InvalidatePairBlend();
        mPairExport = PairButton("Export…", async (_, _) => await ExportPairBlendAsync(false));
        mPairExportParts = PairButton("Parts…", async (_, _) => await ExportPairBlendAsync(true));
        mPairStatus = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(5, 0, 0, 0) };
        var tips = new ToolTip(components);
        tips.SetToolTip(mPairBlendSeconds, "Interpolation duration in seconds");
        tips.SetToolTip(mPairInterpolate, "Blend the poses across the chosen transition");
        tips.SetToolTip(mPairInputs, "Return to the two input animations to edit their highlights");
        tips.SetToolTip(mPairExportParts, "Export the two cut animation parts");
        tips.SetToolTip(mAnimationMatchTimeline, "Drag to highlight eligible frames; right-click to clear");
        tips.SetToolTip(mPairSecondPane.Timeline, "Drag to highlight eligible frames; right-click to clear");
        actions.Controls.Add(mPairInputs, 1, 0);
        actions.Controls.Add(mPairInterpolate, 2, 0);
        actions.Controls.Add(mPairBlendSeconds, 3, 0);
        actions.Controls.Add(mPairExport, 4, 0);
        actions.Controls.Add(mPairExportParts, 5, 0);
        actions.Controls.Add(mPairStatus, 6, 0);
        mPairedShowroom.Controls.Add(actions, 0, 1);
        mPairedShowroom.SetColumnSpan(actions, 2);
        splitContainer_Main.Panel1.Controls.Add(mPairedShowroom);
        EnableShowroomGapDrop(mPairedShowroom);
    }

    private static Button PairButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, Margin = new Padding(1),
            FlatStyle = FlatStyle.Flat, Font = SystemFonts.MessageBoxFont, ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(45, 45, 48) };
        button.Click += click;
        return button;
    }

    private void ShowPairedShowroom()
    {
        EnsurePairedShowroomControls();
        if (!IsPairedShowroom)
        {
            mPairSavedSplitter = splitContainer_Main.SplitterDistance;
            mPairSavedPanelMinimum = splitContainer_Main.Panel1MinSize;
            int maximum = splitContainer_Main.Width - splitContainer_Main.Panel2MinSize - splitContainer_Main.SplitterWidth;
            splitContainer_Main.Panel1MinSize = Math.Min(600, maximum);
            splitContainer_Main.SplitterDistance = Math.Clamp(
                Math.Max(mPairSavedSplitter, splitContainer_Main.Width - 320), splitContainer_Main.Panel1MinSize, maximum);
        }
        HideAnimationMatchingResults();
        splitContainer_LeftSide.Parent?.Controls.Remove(splitContainer_LeftSide);
        mPairFirstPane.Controls.Add(splitContainer_LeftSide);
        splitContainer_LeftSide.Dock = DockStyle.Fill;
        splitContainer_LeftSide.BringToFront();
        tableLayoutPanel_AnimationControls.Controls.Remove(mAnimationMatchButton);
        var actions = (TableLayoutPanel)mPairedShowroom.GetControlFromPosition(0, 1);
        actions.Controls.Add(mAnimationMatchButton, 0, 0);
        tableLayoutPanel_AnimationControls.ColumnStyles[3].Width = 0;
        mPairedShowroom.Visible = true;
        mPairedShowroom.BringToFront();
    }

    private void HidePairedShowroom()
    {
        CancelPairWork();
        mPairLoading = false;
        mAnimationMatchButton.Enabled = true;
        if (!IsPairedShowroom) return;
        mPairSecondPane.Viewer.AnimationPlayback = AnimationPlaybackState.Paused;
        mPairFirstPane.Controls.Remove(splitContainer_LeftSide);
        splitContainer_Main.Panel1.Controls.Add(splitContainer_LeftSide);
        splitContainer_LeftSide.Dock = DockStyle.Fill;
        mAnimationMatchButton.Parent?.Controls.Remove(mAnimationMatchButton);
        tableLayoutPanel_AnimationControls.Controls.Add(mAnimationMatchButton, 3, 1);
        mAnimationMatchButton.Enabled = true;
        tableLayoutPanel_AnimationControls.ColumnStyles[3].Width = 68;
        mPairedShowroom.Visible = false;
        splitContainer_Main.Panel1MinSize = mPairSavedPanelMinimum;
        splitContainer_Main.SplitterDistance = Math.Clamp(mPairSavedSplitter,
            splitContainer_Main.Panel1MinSize, splitContainer_Main.Width - splitContainer_Main.Panel2MinSize - splitContainer_Main.SplitterWidth);
        splitContainer_LeftSide.BringToFront();
        mAnimationMatchTimeline.Enabled = true;
        mPairKey = null;
        mPairBlend = null;
        mPairShowingBlend = false;
    }

    private void CancelPairWork() { mPairWork?.Cancel(); }

    private void InvalidatePairBlend()
    {
        CancelPairWork();
        mAnimationMatchButton.Enabled = !mPairLoading;
        if ((mPairBlend != null || mPairShowingBlend) && mPairStatus != null)
            mPairStatus.Text = "Selections changed · press Match again";
        mPairBlend = null;
        if (mPairExport != null) SetPairResultAvailable(false);
    }

    private void SetPairResultAvailable(bool available)
    {
        mPairExport.Enabled = mPairExportParts.Enabled = available;
        mPairInputs.Enabled = mPairShowingBlend;
    }

    private void PairFirstRangeChanged()
    {
        if (!IsPairedShowroom || mPairUpdatingRange || mPairShowingBlend) return;
        mPairFirstRange = mAnimationMatchTimeline.Selection;
        InvalidatePairBlend();
    }

    private void LoadPairFirstAnimation(Animation animation)
    {
        mAnimationMatchPreviewLoad = true;
        try { ModelViewControl.Instance.LoadAnimation(animation, true); }
        finally { mAnimationMatchPreviewLoad = false; }
    }

    private void ShowPairInputs()
    {
        if (!IsPairedShowroom || mPairAnimations == null) return;
        mPairShowingBlend = false;
        mAnimationMatchTimeline.Enabled = true;
        LoadPairFirstAnimation(mPairAnimations[0]);
        mPairFirstCaption.Text = "1 · " + mPairEntries[0].DisplayName;
        mPairUpdatingRange = true;
        mAnimationMatchTimeline.FrameCount = mPairMatchClips[0].FrameCount;
        mAnimationMatchTimeline.TransitionFrame = -1;
        mAnimationMatchTimeline.SetSelection(mPairFirstRange);
        mPairUpdatingRange = false;
        mPairInputs.Enabled = false;
    }

    private async Task MatchPairedShowroomAsync()
    {
        if (!IsPairedShowroom || mPairLoading || mPairMatchClips == null) return;
        CancelPairWork();
        mPairWork?.Dispose();
        mPairWork = new CancellationTokenSource();
        var work = mPairWork;
        var token = work.Token;
        var firstRange = mPairFirstRange;
        var secondRange = mPairSecondPane.Timeline.Selection;
        var clips = mPairMatchClips;
        var fullClips = mPairFullClips;
        var model = mPairModel;
        float seconds = mPairInterpolate.Checked ? (float)mPairBlendSeconds.Value : 0;
        mPairStatus.Text = "Matching only these two ranges…";
        mAnimationMatchButton.Enabled = false;
        try
        {
            var output = await Task.Run(() =>
            {
                var result = AnimationPairMatcher.Match(clips[0], clips[1], firstRange, secondRange, token);
                var stitched = new StitchedAnimation(fullClips[0], result.SourceFrame, fullClips[1], result.CandidateFrame, seconds);
                var baked = GfdAnimationClipBaker.Bake(stitched, model.Model, model.Version, token);
                return (result, stitched, baked);
            }, token);
            if (IsDisposed || token.IsCancellationRequested || !ReferenceEquals(mPairWork, work) || !IsPairedShowroom) return;
            mPairBlend = output.stitched;
            mPairShowingBlend = true;
            mAnimationMatchTimeline.Enabled = false;
            LoadPairFirstAnimation(output.baked);
            mPairUpdatingRange = true;
            mAnimationMatchTimeline.FrameCount = output.stitched.FrameCount;
            mAnimationMatchTimeline.TransitionFrame = output.result.SourceFrame;
            mPairUpdatingRange = false;
            mPairSecondPane.Viewer.AnimationPlayback = AnimationPlaybackState.Paused;
            mPairSecondPane.SeekFrame(output.result.CandidateFrame);
            mPairSecondPane.Timeline.TransitionFrame = output.result.CandidateFrame;
            mPairFirstCaption.Text = "Blend preview · Inputs returns to animation 1";
            mPairStatus.Text = $"f{output.result.SourceFrame} → f{output.result.CandidateFrame} · {output.result.Score:0.0}% · {seconds:0.00}s";
            SetPairResultAvailable(true);
            SetCharacterBrowserStatus("Two-animation blend ready: " + mPairStatus.Text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !token.IsCancellationRequested) mPairStatus.Text = "Match failed: " + ex.Message;
        }
        finally
        {
            if (!IsDisposed && ReferenceEquals(mPairWork, work)) mAnimationMatchButton.Enabled = true;
        }
    }

    private async Task ExportPairBlendAsync(bool parts)
    {
        if (mPairBlend == null) return;
        try
        {
            if (parts) await ((IGfdAnimationMatchingHost)this).ExportAnimationPartsAsync(mPairBlend, CancellationToken.None);
            else await ((IGfdAnimationMatchingHost)this).ExportAnimationAsync(mPairBlend, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { mPairStatus.Text = "Export failed: " + ex.Message; }
    }
}
