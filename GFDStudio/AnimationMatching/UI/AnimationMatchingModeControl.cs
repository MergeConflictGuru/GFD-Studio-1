using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using GFDStudio.AnimationMatching.Core;

namespace GFDStudio.AnimationMatching.UI;

public delegate void AnimationMatchResultEventHandler(object? sender, AnimationMatchResult result);

public sealed class ThumbnailRequest : EventArgs
{
    private readonly Action<IReadOnlyList<Image>> _complete;

    public ThumbnailRequest(AnimationMatchResult result, int width, int height, Action<IReadOnlyList<Image>> complete)
    {
        Result = result;
        Width = width;
        Height = height;
        _complete = complete;
    }

    public AnimationMatchResult Result { get; }
    public int Width { get; }
    public int Height { get; }
    public void Complete(IReadOnlyList<Image> frames) => _complete(frames);
}

/// <summary>Displays the target-model thumbnail frames as a lightweight looping animation.</summary>
internal sealed class AnimationThumbnailControl : Control
{
    private readonly Timer _timer;
    private IReadOnlyList<Image> _frames;
    private int _frameIndex;

    public AnimationThumbnailControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 24);
        _timer = new Timer { Interval = 90 };
        _timer.Tick += (_, _) =>
        {
            if (_frames == null || _frames.Count < 2)
                return;
            _frameIndex = (_frameIndex + 1) % _frames.Count;
            Invalidate();
        };
    }

    public void SetFrames(IReadOnlyList<Image> frames)
    {
        DisposeFrames();
        _frames = frames;
        _frameIndex = 0;
        _timer.Enabled = _frames != null && _frames.Count > 1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_frames == null || _frames.Count == 0 || _frames[_frameIndex] == null)
            return;

        var image = _frames[_frameIndex];
        var scale = Math.Min((float)ClientSize.Width / image.Width, (float)ClientSize.Height / image.Height);
        if (scale <= 0)
            return;

        var width = image.Width * scale;
        var height = image.Height * scale;
        var destination = new RectangleF(
            (ClientSize.Width - width) * 0.5f,
            (ClientSize.Height - height) * 0.5f,
            width,
            height);
        e.Graphics.DrawImage(image, destination);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            DisposeFrames();
        }
        base.Dispose(disposing);
    }

    private void DisposeFrames()
    {
        if (_frames == null)
            return;
        foreach (var frame in _frames)
            frame?.Dispose();
        _frames = null;
    }
}

/// <summary>
/// Right-side animation matching surface. The normal showroom/model viewport and transport stay
/// untouched on the left; this control only replaces the Character Browser lists while matching.
/// </summary>
public sealed class AnimationMatchingModeControl : UserControl
{
    private readonly TextBox _root = new()
    {
        ReadOnly = true,
        BackColor = Color.FromArgb(45, 45, 48),
        ForeColor = Color.Gainsboro,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill
    };

    private readonly Button _browse = MakeButton("Browse");
    private readonly Button _back = MakeButton("Back");
    private readonly Button _reindex = MakeButton("Reindex");
    private readonly Button _export = MakeButton("Export…");
    private readonly CheckBox _blend = new()
    {
        Text = "Blend",
        Checked = true,
        AutoSize = true,
        ForeColor = Color.Gainsboro,
        BackColor = Color.Transparent,
        Anchor = AnchorStyles.Left
    };
    private readonly NumericUpDown _blendMs = new()
    {
        Minimum = 0,
        Maximum = 2000,
        Increment = 10,
        Value = 120,
        Width = 62,
        BackColor = Color.FromArgb(45, 45, 48),
        ForeColor = Color.Gainsboro,
        BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Label _source = new()
    {
        AutoEllipsis = true,
        ForeColor = Color.Gainsboro,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        Text = "No source animation"
    };
    private readonly Label _status = new()
    {
        AutoEllipsis = true,
        ForeColor = Color.Silver,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill,
        Text = "Select a timeline range and press Match"
    };
    private readonly FlowLayoutPanel _results = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        BackColor = Color.FromArgb(30, 30, 30),
        Padding = new Padding(3)
    };

    private (int start, int end)? _selection;
    private AnimationMatchResult? _selectedResult;

    public AnimationMatchingModeControl()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(30, 30, 30);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(6),
            BackColor = Color.FromArgb(30, 30, 30)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var rootBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        rootBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _root.Margin = new Padding(0, 4, 4, 4);
        _browse.Margin = new Padding(2, 3, 0, 3);
        rootBar.Controls.Add(_root, 0, 0);
        rootBar.Controls.Add(_browse, 1, 0);

        var actionBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = Padding.Empty };
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));

        var ms = new Label
        {
            Text = "ms",
            AutoSize = true,
            ForeColor = Color.Silver,
            Anchor = AnchorStyles.Left
        };
        _back.Margin = new Padding(0, 3, 2, 3);
        _source.Margin = new Padding(5, 0, 4, 0);
        _blend.Margin = new Padding(2, 0, 0, 0);
        _blendMs.Margin = new Padding(2, 5, 1, 3);
        _reindex.Margin = new Padding(2, 3, 2, 3);
        _export.Margin = new Padding(2, 3, 0, 3);

        actionBar.Controls.Add(_back, 0, 0);
        actionBar.Controls.Add(_source, 1, 0);
        actionBar.Controls.Add(_blend, 2, 0);
        actionBar.Controls.Add(_blendMs, 3, 0);
        actionBar.Controls.Add(ms, 4, 0);
        actionBar.Controls.Add(_reindex, 5, 0);
        actionBar.Controls.Add(_export, 6, 0);

        layout.Controls.Add(rootBar, 0, 0);
        layout.Controls.Add(actionBar, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(_results, 0, 3);
        Controls.Add(layout);

        _browse.Click += (_, _) => BrowseRequested?.Invoke(this, EventArgs.Empty);
        _back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        _reindex.Click += (_, _) => ReindexRequested?.Invoke(this, EventArgs.Empty);
        _export.Click += (_, _) => ExportRequested?.Invoke(this, EventArgs.Empty);
        _blend.CheckedChanged += (_, _) => ReactivateSelected();
        _blendMs.ValueChanged += (_, _) => ReactivateSelected();
    }

    public (int start, int end)? Selection => _selection;
    public bool BlendingEnabled => _blend.Checked;
    public float BlendSeconds => (float)_blendMs.Value / 1000f;

    public event EventHandler? BrowseRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? ReindexRequested;
    public event EventHandler? SearchRequested;
    public event EventHandler? ExportRequested;
    public event AnimationMatchResultEventHandler? CandidateActivated;
    public event EventHandler<ThumbnailRequest>? ThumbnailRequested;

    public void SetRootPath(string? path) => _root.Text = path ?? string.Empty;

    public void SetSelection((int start, int end)? selection) => _selection = selection;

    /// <summary>Called by the Match button beside the normal transport controls.</summary>
    public void BeginSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);

    public void SetSource(string name, int frameCount, float fps)
    {
        _selectedResult = null;
        _source.Text = $"{name} · {frameCount:N0}f";
    }

    // The combined timeline is rendered by MainForm's shared timeline strip under the viewport.
    public void SetTransitionFrame(int frame) { }
    public void SetCombinedTimeline(int totalFrames, int transitionFrame) { }

    public void SetStatus(string text) => _status.Text = text;

    public void SetBusy(bool busy, string? status = null)
    {
        _reindex.Enabled = !busy;
        _export.Enabled = !busy;
        _browse.Enabled = !busy;
        if (status is not null)
            _status.Text = status;
    }

    public void SetResults(IReadOnlyList<AnimationMatchResult> results)
    {
        _selectedResult = null;
        _results.SuspendLayout();
        try
        {
            while (_results.Controls.Count > 0)
                _results.Controls[0].Dispose();

            for (var i = 0; i < results.Count; i++)
                _results.Controls.Add(CreateResultCard(i + 1, results[i]));
        }
        finally
        {
            _results.ResumeLayout();
        }
    }

    private void ReactivateSelected()
    {
        if (_selectedResult is not null)
            CandidateActivated?.Invoke(this, _selectedResult);
    }

    private Control CreateResultCard(int rank, AnimationMatchResult result)
    {
        const int cardWidth = 164;
        const int cardHeight = 142;
        var card = new Panel
        {
            Width = cardWidth,
            Height = cardHeight,
            Margin = new Padding(3),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(37, 37, 38),
            Cursor = Cursors.Hand,
            Tag = result
        };
        var image = new AnimationThumbnailControl
        {
            Left = 4,
            Top = 4,
            Width = cardWidth - 10,
            Height = 88,
            BackColor = Color.FromArgb(24, 24, 24)
        };
        var title = new Label
        {
            Left = 5,
            Top = 95,
            Width = cardWidth - 12,
            Height = 21,
            AutoEllipsis = true,
            ForeColor = Color.Gainsboro,
            Text = $"#{rank}  {result.Candidate.DisplayName}",
            Font = new Font(Font, FontStyle.Bold)
        };
        var detail = new Label
        {
            Left = 5,
            Top = 117,
            Width = cardWidth - 12,
            Height = 20,
            AutoEllipsis = true,
            ForeColor = Color.Silver,
            Text = $"{result.Score:0.0} · {result.CandidateTimeSeconds:0.00}s · f{result.CandidateFrame}"
        };
        card.Controls.AddRange(new Control[] { image, title, detail });

        void Activate(object? _, EventArgs __)
        {
            _selectedResult = result;
            foreach (Control child in _results.Controls)
                child.BackColor = Color.FromArgb(37, 37, 38);
            card.BackColor = Color.FromArgb(55, 72, 84);
            CandidateActivated?.Invoke(this, result);
        }

        card.Click += Activate;
        image.Click += Activate;
        title.Click += Activate;
        detail.Click += Activate;

        ThumbnailRequested?.Invoke(this, new ThumbnailRequest(result, image.Width, image.Height, frames =>
        {
            if (IsDisposed || image.IsDisposed)
            {
                if (frames != null)
                    foreach (var frame in frames)
                        frame?.Dispose();
                return;
            }

            void Apply()
            {
                image.SetFrames(frames);
            }

            if (InvokeRequired)
                BeginInvoke((Action)Apply);
            else
                Apply();
        }));
        return card;
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(50, 50, 54),
        ForeColor = Color.WhiteSmoke,
        TabStop = false
    };
}
