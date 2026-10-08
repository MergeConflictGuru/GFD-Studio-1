using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.AnimationMatching.Search;
using GFDStudio.GUI.Controls;

namespace GFDStudio.GUI.Forms;

public partial class MainForm
{
    private TableLayoutPanel mPairedShowroom;
    private Panel mPairFirstPane;
    private AnimationViewPane mPairSecondPane;
    private Label mPairFirstCaption;
    private Label mPairStatus;
    private Animation[] mPairAnimations;
    private GfdAnimationClip[] mPairMatchClips;
    private GfdTargetAnimationClip[] mPairFullClips;
    private CharacterAnimationEntry[] mPairEntries;
    private ModelPack mPairModel;
    private (int start, int end)? mPairFirstRange;
    private CancellationTokenSource mPairWork;
    private string mPairKey;
    private bool mPairLoading;
    private bool mPairResultInAniMatch;
    private bool mPairUpdatingRange;
    private bool IsPairedShowroom => mPairedShowroom?.Visible == true;
    private readonly List<string> mAnimationPickOrder = new();

    private CharacterAnimationEntry[] GetAnimationsInPickOrder()
    {
        var selected=mCharacterAnimationListBox.SelectedItems.Cast<CharacterAnimationEntry>().ToArray();
        var byId=selected.ToDictionary(GetCorrectedAnimationMatchClipId);
        mAnimationPickOrder.RemoveAll(id=>!byId.ContainsKey(id));
        foreach(var entry in selected)
        {
            var id=GetCorrectedAnimationMatchClipId(entry);
            if(!mAnimationPickOrder.Contains(id)) mAnimationPickOrder.Add(id);
        }
        return mAnimationPickOrder.Select(id=>byId[id]).ToArray();
    }

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
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
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
        mAnimationMatchTimeline.BindPlayback(mAnimationTrackBar);
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
        int height = hasMarks ? 92 : 72;
        if (splitContainer_LeftSide.Panel2MinSize == height - 4) return;
        splitContainer_LeftSide.Panel2MinSize = height - 4;
        if (splitContainer_LeftSide.Height > height + splitContainer_LeftSide.Panel1MinSize)
            splitContainer_LeftSide.SplitterDistance = splitContainer_LeftSide.Height - height;
    }

    private bool TryLoadPairedShowroomSelection()
    {
        var entries = GetAnimationsInPickOrder().Take(2).ToArray();
        if (entries.Length < 2) return false;
        if (mCharacterBrowserApplyingSavedSelection && !mCharacterBrowserModelDiscoveryComplete)
            return true;
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
            mAnimationMatchButton.Enabled = true;
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
            Dock = DockStyle.Top, Height = 44, AutoEllipsis = false, Font = SystemFonts.MessageBoxFont,
            ForeColor = Color.Gainsboro, BackColor = Theme.DarkBG, TextAlign = ContentAlignment.MiddleLeft
        };
        mPairFirstPane.Controls.Add(mPairFirstCaption);
        mPairSecondPane = new AnimationViewPane { Margin = new Padding(3, 0, 0, 0) };
        mPairSecondPane.RangeChanged += (_, _) => InvalidatePairBlend();
        mPairSecondPane.UserSeeked += (_, seconds) => SyncPairSeek(mPairSecondPane.Viewer, seconds);
        mAnimationTrackBar.Scroll += (_, _) => SyncPairSeek(ModelViewControl.Instance, mAnimationTrackBar.Value / 1000d);
        mAnimationMatchTimeline.UserSelectionChanged += (_, _) => SyncPairSelection(true);
        mPairSecondPane.Timeline.UserSelectionChanged += (_, _) => SyncPairSelection(false);
        ModelViewControl.Instance.MouseCameraChanged += (sender, shift) => SyncPairCamera((ModelViewControl)sender, shift);
        mPairSecondPane.Viewer.MouseCameraChanged += (sender, shift) => SyncPairCamera((ModelViewControl)sender, shift);
        mPairedShowroom.Controls.Add(mPairFirstPane, 0, 0);
        mPairedShowroom.Controls.Add(mPairSecondPane, 1, 0);
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty,
            Padding = Padding.Empty, Font = SystemFonts.MessageBoxFont, BackColor = Theme.DarkBG, ForeColor = Color.Gainsboro
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mPairStatus = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(5, 0, 0, 0) };
        var tips = new ToolTip(components);
        tips.SetToolTip(mAnimationMatchTimeline, "Drag to highlight eligible frames; right-click to clear");
        tips.SetToolTip(mPairSecondPane.Timeline, "Drag to highlight eligible frames; right-click to clear");
        actions.Controls.Add(mPairStatus, 1, 0);
        mPairedShowroom.Controls.Add(actions, 0, 1);
        mPairedShowroom.SetColumnSpan(actions, 2);
        splitContainer_Main.Panel1.Controls.Add(mPairedShowroom);
        EnableShowroomGapDrop(mPairedShowroom);
    }

    private void SyncPairSeek(ModelViewControl source, double seconds)
    {
        if (!IsPairedShowroom || (ModifierKeys & Keys.Shift) == 0) return;
        var other = ReferenceEquals(source, ModelViewControl.Instance) ? mPairSecondPane.Viewer : ModelViewControl.Instance;
        if (!other.IsAnimationLoaded) return;
        if (ReferenceEquals(other, mPairSecondPane.Viewer))
        {
            mPairSecondPane.SeekSeconds(seconds);
            return;
        }
        other.AnimationTime = Math.Clamp(seconds, 0, mAnimationTrackBar.Maximum / 1000d);
        other.Invalidate();
    }

    private void SyncPairSelection(bool fromFirst)
    {
        if (!IsPairedShowroom || (ModifierKeys & Keys.Shift) == 0) return;
        var source = fromFirst ? mAnimationMatchTimeline : mPairSecondPane.Timeline;
        var other = fromFirst ? mPairSecondPane.Timeline : mAnimationMatchTimeline;
        other.SetSelection(source.Selection);
    }

    private void SyncPairCamera(ModelViewControl source, bool shift)
    {
        if (!IsPairedShowroom || !shift) return;
        var other = ReferenceEquals(source, ModelViewControl.Instance) ? mPairSecondPane.Viewer : ModelViewControl.Instance;
        other.CopyCameraFrom(source);
    }

    private void ShowPairedShowroom()
    {
        EnsurePairedShowroomControls();
        HideAnimationMatchingResults();
        // Lay out the destination while it is visible before moving the viewer.
        // A hidden table initially gives its first cell a tiny default height,
        // below the viewer split's minimum, and reparenting then throws.
        mPairedShowroom.Visible = true;
        mPairedShowroom.Bounds = splitContainer_Main.Panel1.ClientRectangle;
        mPairedShowroom.PerformLayout();
        mPairFirstPane.PerformLayout();
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
        mPairResultInAniMatch = false;
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
        splitContainer_LeftSide.BringToFront();
        mAnimationMatchTimeline.Enabled = true;
        mPairKey = null;
    }

    private void CancelPairWork() { mPairWork?.Cancel(); }

    private void InvalidatePairBlend()
    {
        CancelPairWork();
        mAnimationMatchButton.Enabled = !mPairLoading;
    }

    private void PairFirstRangeChanged()
    {
        if (!IsPairedShowroom || mPairUpdatingRange) return;
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
        mAnimationMatchTimeline.Enabled = true;
        LoadPairFirstAnimation(mPairAnimations[0]);
        mPairFirstCaption.Text = "1 · " + mPairEntries[0].DisplayName;
        mPairUpdatingRange = true;
        mAnimationMatchTimeline.FrameCount = mPairMatchClips[0].FrameCount;
        mAnimationMatchTimeline.TransitionFrame = -1;
        mAnimationMatchTimeline.SetSelection(mPairFirstRange);
        mPairUpdatingRange = false;
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

        mPairStatus.Text = "Matching only these two ranges…";
        mAnimationMatchButton.Enabled = false;
        try
        {
            var result = await Task.Run(() =>
                AnimationPairMatcher.Match(clips[0], clips[1], firstRange, secondRange, token), token);
            if (IsDisposed || token.IsCancellationRequested || !ReferenceEquals(mPairWork, work) || !IsPairedShowroom) return;
            var pairResult = result with { Candidate = fullClips[1] };
            mPairStatus.Text = $"f{result.SourceFrame} → f{result.CandidateFrame} · {result.Score:0.0}%";
            HidePairedShowroom();
            mPairResultInAniMatch = true;
            ShowAnimationMatchingResults();
            EnsureAnimationMatchingController();
            mAnimationMatchController.ShowPairResult(fullClips[0], pairResult);
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

}
