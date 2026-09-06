using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.UI;

public delegate void AnimationMatchResultEventHandler(object? sender, AnimationMatchResult result);

public sealed class ThumbnailRequest : EventArgs
{
    private readonly Action<Image?> _complete;
    public ThumbnailRequest(AnimationMatchResult result, int width, int height, Action<Image?> complete)
    { Result = result; Width = width; Height = height; _complete = complete; }
    public AnimationMatchResult Result { get; }
    public int Width { get; }
    public int Height { get; }
    public void Complete(Image? image) => _complete(image);
}

/// <summary>
/// Drop-in WinForms surface for the new mode. The existing GFD OpenGL control belongs in
/// PreviewHostPanel; the controller/host adapter supplies animation data and thumbnails.
/// </summary>
public sealed class AnimationMatchingModeControl : UserControl
{
    private readonly Label _source = new() { AutoSize = true, Text = "No animation" };
    private readonly Button _reindex = new() { Text = "Reindex" };
    private readonly Button _search = new() { Text = "Search" };
    private readonly Button _export = new() { Text = "Export stitched…" };
    private readonly CheckBox _blend = new() { Text = "Blend", Checked = true, AutoSize = true };
    private readonly NumericUpDown _blendMs = new() { Minimum = 0, Maximum = 2000, Increment = 10, Value = 120, Width = 64 };
    private readonly Label _status = new() { AutoSize = true, Text = "Ready" };
    private readonly FlowLayoutPanel _results = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly RangeTimelineControl _timeline = new() { Dock = DockStyle.Bottom };
    private readonly Panel _previewHost = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private IReadOnlyList<AnimationMatchResult> _currentResults = Array.Empty<AnimationMatchResult>();
    private AnimationMatchResult? _selectedResult;

    public AnimationMatchingModeControl()
    {
        Dock = DockStyle.Fill;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(4), WrapContents = false };
        toolbar.Controls.AddRange(new Control[] { _source, _reindex, _search, _blend, _blendMs, new Label { Text = "ms", AutoSize = true }, _export, _status });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 720 };
        split.Panel1.Controls.Add(_previewHost);
        split.Panel1.Controls.Add(_timeline);
        split.Panel2.Controls.Add(_results);
        Controls.Add(split);
        Controls.Add(toolbar);

        _reindex.Click += (_, _) => ReindexRequested?.Invoke(this, EventArgs.Empty);
        _search.Click += (_, _) => SearchRequested?.Invoke(this, EventArgs.Empty);
        _export.Click += (_, _) => ExportRequested?.Invoke(this, EventArgs.Empty);
        _blend.CheckedChanged += (_, _) => ReactivateSelected();
        _blendMs.ValueChanged += (_, _) => ReactivateSelected();
    }

    public Panel PreviewHostPanel => _previewHost;
    public (int start, int end)? Selection => _timeline.Selection;
    public bool BlendingEnabled => _blend.Checked;
    public float BlendSeconds => (float)_blendMs.Value / 1000f;

    public event EventHandler? ReindexRequested;
    public event EventHandler? SearchRequested;
    public event EventHandler? ExportRequested;
    public event AnimationMatchResultEventHandler? CandidateActivated;
    public event EventHandler<ThumbnailRequest>? ThumbnailRequested;

    public void SetSource(string name, int frameCount, float fps)
    {
        _selectedResult = null;
        _source.Text = $"Source: {name}  ·  {frameCount:N0}f @ {fps:0.##} fps";
        _timeline.FrameCount = frameCount;
        _timeline.TransitionFrame = -1;
    }

    public void SetTransitionFrame(int frame) => _timeline.TransitionFrame = frame;
    public void SetCombinedTimeline(int totalFrames, int transitionFrame)
    {
        _timeline.FrameCount = Math.Max(1, totalFrames);
        _timeline.TransitionFrame = transitionFrame;
    }
    public void SetStatus(string text) => _status.Text = text;

    public void SetBusy(bool busy, string? status = null)
    {
        _search.Enabled = !busy;
        _reindex.Enabled = !busy;
        _export.Enabled = !busy;
        if (status is not null) _status.Text = status;
    }

    public void SetResults(IReadOnlyList<AnimationMatchResult> results)
    {
        _currentResults = results;
        _results.SuspendLayout();
        try
        {
            while (_results.Controls.Count > 0) _results.Controls[0].Dispose();
            for (var i = 0; i < results.Count; i++)
                _results.Controls.Add(CreateResultCard(i + 1, results[i]));
        }
        finally { _results.ResumeLayout(); }
    }

    private void ReactivateSelected()
    {
        if (_selectedResult is not null) CandidateActivated?.Invoke(this, _selectedResult);
    }

    private Control CreateResultCard(int rank, AnimationMatchResult result)
    {
        var card = new Panel { Width = 310, Height = 92, Margin = new Padding(3), BorderStyle = BorderStyle.FixedSingle, Tag = result };
        var image = new PictureBox { Left = 4, Top = 4, Width = 112, Height = 82, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 28, 28) };
        var title = new Label { Left = 122, Top = 5, Width = 182, Height = 34, Text = $"#{rank}  {result.Candidate.DisplayName}", Font = new Font(Font, FontStyle.Bold) };
        var detail = new Label { Left = 122, Top = 41, Width = 182, Height = 43, Text = $"Score {result.Score:0.0}\nCandidate {result.CandidateTimeSeconds:0.000}s · source f{result.SourceFrame}" };
        card.Controls.AddRange(new Control[] { image, title, detail });

        void activate(object? _, EventArgs __)
        {
            _selectedResult = result;
            CandidateActivated?.Invoke(this, result);
        }
        card.Click += activate;
        image.Click += activate;
        title.Click += activate;
        detail.Click += activate;

        ThumbnailRequested?.Invoke(this, new ThumbnailRequest(result, image.Width, image.Height, thumb =>
        {
            if (IsDisposed || image.IsDisposed) { thumb?.Dispose(); return; }
            void apply() { var old = image.Image; image.Image = thumb; old?.Dispose(); }
            if (InvokeRequired) BeginInvoke((Action)apply); else apply();
        }));
        return card;
    }
}
