using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Globalization;
using System.Threading.Tasks;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;
using GFDLibrary;
using GFDStudio.AnimationMatching.Core;
using GFDStudio.AnimationMatching.Integration;
using GFDStudio.GUI.Controls;

namespace GFDStudio.AnimationMatching.UI;

public delegate void AnimationMatchResultEventHandler(object? sender, AnimationMatchResult result);

public sealed class ThumbnailRequest : EventArgs
{
    private readonly Action<AnimationThumbnailScene?> _complete;
    public CancellationToken CancellationToken { get; init; }

    public ThumbnailRequest(
        AnimationMatchResult result,
        int width,
        int height,
        Action<AnimationThumbnailScene?> complete)
    {
        Result = result;
        Width = width;
        Height = height;
        _complete = complete;
    }

    public AnimationMatchResult Result { get; }
    public int Width { get; }
    public int Height { get; }
    public void Complete(AnimationThumbnailScene? scene) => _complete(scene);
}

/// <summary>
/// One shared wall clock keeps all candidate scenes on the same seam. A candidate whose available
/// lead-in is shorter than the normal preview window simply holds its first pose until the shared
/// seam time; the same hold is applied to its final pose after the candidate tail ends.
/// </summary>
internal sealed class AnimationThumbnailPlayback
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Reset() => _clock.Restart();

    public double GetLoopPositionSeconds()
        => _clock.Elapsed.TotalSeconds % AnimationMatchingPreviewTiming.LoopDurationSeconds;

    public double GetAnimationTime(AnimationThumbnailScene scene)
        => GetAnimationTime( scene, GetLoopPositionSeconds() );

    public double GetAnimationTime(AnimationThumbnailScene scene, double loopPosition)
    {
        var seamTime = Math.Clamp(scene.SeamTimeSeconds, 0.0, (double)scene.Animation.Duration);
        // Map the shared preview clock around each clip's actual seam. At the shared
        // seam instant, every card therefore samples its own seam time. Clips with
        // too little lead-in hold their first pose; clips with too little tail hold
        // their last pose.
        var animationTime = seamTime +
                             ( loopPosition - AnimationMatchingPreviewTiming.BeforeSeamSeconds );
        return Math.Clamp(animationTime, 0.0, (double)scene.Animation.Duration);
    }
}

/// <summary>Displays the latest frame produced by the shared live thumbnail renderer.</summary>
internal sealed class AnimationThumbnailControl : Control
{
    private AnimationThumbnailScene? _scene;
    private Bitmap? _atlas;
    private Rectangle _atlasSource;

    public AnimationThumbnailControl()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 24);
        TabStop = false;
    }

    public AnimationThumbnailScene? Scene => _scene;

    public void SetScene(AnimationThumbnailScene? scene)
    {
        _scene = scene;
        Invalidate();
    }

    public void SetAtlasFrame(Bitmap atlas, Rectangle source)
    {
        _atlas = atlas;
        _atlasSource = source;
        Invalidate();
    }

    public void ClearAtlasFrame()
    {
        _atlas = null;
        _atlasSource = Rectangle.Empty;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (_atlas == null || _atlasSource.Width <= 0 || _atlasSource.Height <= 0)
            return;

        e.Graphics.DrawImage(
            _atlas,
            ClientRectangle,
            _atlasSource,
            GraphicsUnit.Pixel );
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}

/// <summary>
/// Right-side animation matching surface. The normal showroom/model viewport and transport stay
/// untouched on the left; this control only replaces the Character Browser lists while matching.
/// </summary>
public sealed class AnimationMatchingModeControl : UserControl
{
    private sealed class ThumbnailRenderEntry
    {
        public AnimationThumbnailControl Control { get; init; }
        public AnimationThumbnailScene Scene { get; init; }
    }

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
    private readonly Button _exportSelection = MakeButton("Export sel.");
    
    private readonly CheckBox _alignPositionAndYaw = new()
    {
        Text = "X/Z + facing",
        Checked = true,
        AutoSize = true,
        ForeColor = Color.Gainsboro,
        BackColor = Color.Transparent,
        Anchor = AnchorStyles.Left
    };
    private readonly CheckBox _matchUp = new() {Text="Y (height)",AutoSize=true,ForeColor=Color.Gainsboro};
    private readonly NumericUpDown _yawJitter = new() {Minimum=0,Maximum=180,DecimalPlaces=1,Increment=1,Width=55};
    private readonly RadioButton _simpleBlend = new()
    {
        Text = "Simple",
        AutoSize = true,
        ForeColor = Color.Gainsboro,
        BackColor = Color.Transparent,
        Anchor = AnchorStyles.Left
    };
    private readonly RadioButton _noBlend = new() { Text = "None", AutoSize = true, ForeColor = Color.Gainsboro };
    private readonly RadioButton _aiBlend = new() { Text = "AI", AutoSize = true, ForeColor = Color.Gainsboro };
    private readonly ComboBox _styleHint = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, BackColor = Color.FromArgb(45,45,48), ForeColor = Color.Gainsboro };
    private readonly TrackBar _blendDurationSlider = new()
    {
        Minimum = 0, Maximum = 1000, SmallChange = 26, LargeChange = 130,
        TickStyle = TickStyle.None, AutoSize = false, Width = 90, Height = 28,
        Margin = new Padding(2, 1, 2, 1), AccessibleName = "Blend duration"
    };
    private readonly TextBox _blendDurationMs = new()
    {
        Text = "500", Width = 48, TextAlign = HorizontalAlignment.Right,
        BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.Gainsboro,
        BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Blend milliseconds"
    };
    private readonly Timer _durationEditTimer = new() { Interval = 250 };
    private float _blendMilliseconds = 500;
    private readonly TrackBar _trajectoryBlend = new()
    {
        Minimum=0, Maximum=100, Value=50, TickStyle=TickStyle.None,
        AutoSize=false, Width=90, Height=28, Margin=new Padding(2,1,2,1),
        AccessibleName="Trajectory blend: B immediately to A persists"
    };
    private readonly Label _trajectoryValue = new() {Text="50% A",AutoSize=true,ForeColor=Color.Gainsboro,Margin=new Padding(2,5,0,0)};
    private bool _updatingDuration;

    internal static float SliderMilliseconds(int position)
        => (float)(3000 * (Math.Pow(10, Math.Clamp(position, 0, 1000) / 1000d) - 1) / 9);
    internal static int DurationSliderPosition(float milliseconds)
        => (int)Math.Round(1000 * Math.Log10(1 + 9 * Math.Clamp(milliseconds, 0, 3000) / 3000d));

    private readonly Label _source = new()
    {
        AutoEllipsis = false,
        Height = 42,
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
    private readonly TextBox _filter = new()
    {
        PlaceholderText = "Filter results…",
        BackColor = Color.FromArgb(45, 45, 48),
        ForeColor = Color.Gainsboro,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        Margin = new Padding(4, 3, 0, 3)
    };

    private (int start, int end)? _selection;
    private AnimationMatchResult? _selectedResult;
    private readonly List<(AnimationMatchResult Result, Control Card)> _resultCards = new();
    private bool _canLoadMore;
    private bool _loadMoreArmed = true;
    private bool _loadMoreCheckPending;
    private readonly AnimationThumbnailPlayback _thumbnailPlayback = new();
    private readonly List<ThumbnailRenderEntry> _thumbnailRenderEntries = new();
    private readonly AnimationThumbnailRenderWorker _thumbnailRenderer = new();
    private CancellationTokenSource _thumbnailLoads = new();
    private readonly List<(AnimationMatchResult Result, AnimationThumbnailControl Image)> _thumbnailCards = new();
    private readonly Dictionary<AnimationThumbnailControl,CancellationTokenSource> _thumbnailRequests = new();
    private readonly HashSet<AnimationThumbnailControl> _thumbnailFailed = new();
    private readonly RenderFrameClock _thumbnailRefreshTimer;

    internal long ThumbnailFrameCount {get;private set;}
    private int _surfaceLayoutGeneration;
    private Rectangle[] _surfaceRectangles=Array.Empty<Rectangle>();
    private bool _surfaceLayoutDirty=true;
    internal bool GpuSurfaceEnabled {get;set;}=true;
    private void MarkSurfaceLayoutDirty()
    {
        _surfaceLayoutDirty=true;
    }
    private Rectangle ThumbnailSurfaceRectangle(Control image)
        => new(_thumbnailRenderer.Surface.PointToClient(image.PointToScreen(Point.Empty)),image.Size);
    private void UpdateSurfaceLayout()
    {
        if(!GpuSurfaceEnabled)return;
        var surface=_thumbnailRenderer.Surface;
        var bounds=RectangleToClient(_results.RectangleToScreen(_results.ClientRectangle));
        bool resized=surface.Bounds!=bounds;
        if(resized){surface.Bounds=bounds;}

        var region=new Region();region.MakeEmpty();
        var rectangles=new List<Rectangle>();
        foreach(var (_,image) in _thumbnailCards)
            if(IsThumbnailVisible(image))
            {
                var rectangle=ThumbnailSurfaceRectangle(image);
                rectangle.Intersect(new Rectangle(Point.Empty,surface.ClientSize));
                if(rectangle.Width>0 && rectangle.Height>0){region.Union(rectangle);rectangles.Add(rectangle);}
            }
        var updated=rectangles.ToArray();
        if(!resized && updated.SequenceEqual(_surfaceRectangles)){_surfaceLayoutDirty=false;region.Dispose();return;}
        _surfaceLayoutDirty=false;Interlocked.Increment(ref _surfaceLayoutGeneration);surface.Visible=false;
        var previous=surface.Region;surface.Region=region;previous?.Dispose();
        _surfaceRectangles=updated;surface.BringToFront();
    }
    private void SurfaceClick(object sender,MouseEventArgs e)
    {
        var point=_results.PointToClient(_thumbnailRenderer.Surface.PointToScreen(e.Location));
        var card=_resultCards.FirstOrDefault(item=>item.Card.Visible && item.Card.Bounds.Contains(point));
        if(card.Card==null)return;
        _selectedResult=card.Result;
        foreach(var item in _resultCards)item.Card.BackColor=ReferenceEquals(item.Card,card.Card)?Color.FromArgb(55,72,84):Color.FromArgb(37,37,38);
        if(e.Clicks>1)CandidateOpened?.Invoke(this,card.Result);else CandidateActivated?.Invoke(this,card.Result);
    }
    private Bitmap _thumbnailAtlasFrame;
    private Task<AnimationThumbnailRenderBatch> _thumbnailRenderTask;
    private int _thumbnailRenderGeneration;

    public AnimationMatchingModeControl()
    {
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(30, 30, 30);

        // All cards are ordinary WinForms controls. One hidden ModelViewControl owns the only
        // thumbnail GL context/model and renders the current pose of every card into one reusable
        // off-screen target per tick.
        _thumbnailRefreshTimer=new RenderFrameClock(this,RefreshThumbnailFrames);

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
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var rootBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        rootBar.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        rootBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i=0;i<3;i++) rootBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _root.Margin = new Padding(0, 4, 4, 4);
        rootBar.Controls.Add(_root, 0, 0);
        rootBar.Controls.Add(_browse, 1, 0);
        rootBar.Controls.Add(_reindex, 2, 0);
        rootBar.Controls.Add(_back,3,0);


        var actionBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize=false, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        for(int i=0;i<3;i++) actionBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _source.Dock=DockStyle.Top;
        _source.Margin=new Padding(0,3,0,5);
        void SizeSource()
        {
            if (_source.Width<20) return;
            _source.Height=Math.Max(28,TextRenderer.MeasureText(_source.Text,_source.Font,new Size(_source.Width,1000),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height+8);
        }
        _source.SizeChanged+=(_,_)=>SizeSource();
        _source.TextChanged+=(_,_)=>SizeSource();
        var sourceBar=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
        sourceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        sourceBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sourceBar.Controls.Add(_source,0,0);
        sourceBar.Controls.Add(_export,1,0);
        sourceBar.Controls.Add(_exportSelection,2,0);
        actionBar.Controls.Add(sourceBar,0,0);
        FlowLayoutPanel Options()=>new() {Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Margin=Padding.Empty,Padding=Padding.Empty};
        Label Caption(string text)=>new() {Text=text,AutoSize=true,ForeColor=Color.Gainsboro,Margin=new Padding(0,5,4,3)};
        var matchOptions=Options();
        matchOptions.Controls.Add(Caption("Match:"));
        matchOptions.Controls.Add(_alignPositionAndYaw);
        matchOptions.Controls.Add(_matchUp);
        matchOptions.Controls.Add(Caption("Yaw variation °"));
        matchOptions.Controls.Add(_yawJitter);
        actionBar.Controls.Add(matchOptions,0,1);
        var stitchOptions=Options();
        stitchOptions.Font=new Font(Font.FontFamily,9f);
        stitchOptions.Controls.Add(Caption("Blend:"));
        stitchOptions.Controls.Add(_noBlend);
        stitchOptions.Controls.Add(_simpleBlend);
        stitchOptions.Controls.Add(_aiBlend);



        stitchOptions.Controls.Add(_styleHint);
        actionBar.Controls.Add(stitchOptions,0,2);
        stitchOptions.Controls.Add(_blendDurationSlider);
        stitchOptions.Controls.Add(_blendDurationMs);
        stitchOptions.Controls.Add(Caption("ms"));
        stitchOptions.Controls.Add(Caption("Trajectory B"));
        stitchOptions.Controls.Add(_trajectoryBlend);
        stitchOptions.Controls.Add(_trajectoryValue);
        bool sizingHeader=false;
        void SizeHeader()
        {
            if(sizingHeader || actionBar.Width<40)return;
            sizingHeader=true;
            try
            {
                var rows=new Control[]{sourceBar,matchOptions,stitchOptions};
                int total=0;
                for(int i=0;i<rows.Length;i++)
                {
                    int height;
                    if(i==0)height=Math.Max(_source.Height+_source.Margin.Vertical,_export.GetPreferredSize(Size.Empty).Height+_export.Margin.Vertical);
                    else
                    {
                        int lineWidth=0,lineHeight=0; height=0;
                        foreach(Control child in rows[i].Controls)
                        {
                            var size=child.AutoSize?child.GetPreferredSize(Size.Empty):child.Size;
                            int width=size.Width+child.Margin.Horizontal;
                            if(lineWidth>0 && lineWidth+width>actionBar.Width){height+=lineHeight;lineWidth=0;lineHeight=0;}
                            lineWidth+=width;lineHeight=Math.Max(lineHeight,size.Height+child.Margin.Vertical);
                        }
                        height+=lineHeight;
                    }
                    actionBar.RowStyles[i].SizeType=SizeType.Absolute;
                    actionBar.RowStyles[i].Height=height;
                    total+=height;
                }
                layout.RowStyles[1].Height=total+2;
            }
            finally{sizingHeader=false;}
        }
        actionBar.SizeChanged+=(_,_)=>SizeHeader();
        actionBar.Layout+=(_,_)=>SizeHeader();
        _source.TextChanged+=(_,_)=>SizeHeader();
        FontChanged+=(_,_)=>SizeHeader();
        HandleCreated+=(_,_)=>SizeHeader();        if (SlideAiBlend.Available)
        {
            _styleHint.Items.AddRange(SlideAiBlend.StyleNames);
            _styleHint.SelectedItem = "Acrobatic";
        }
        _styleHint.Enabled = _aiBlend.Checked && _styleHint.Items.Count > 0;
        var aiTip = new ToolTip();
        aiTip.SetToolTip(_aiBlend, "AI blend generates one sample per animation frame across the slider duration, using the selected motion style.");
        aiTip.SetToolTip(_styleHint, "Motion style guides generation; the boundary poses still determine the motion.");
        aiTip.SetToolTip(_trajectoryBlend, "Final horizontal position: left follows B's motion immediately; right carries A's motion through the blend. Between them mixes both displacements.");
        Disposed += (_, _) => aiTip.Dispose();
        _aiBlend.Enabled = SlideAiBlend.Available;
        _aiBlend.Checked = _aiBlend.Enabled;
        _styleHint.Enabled = _aiBlend.Checked && _styleHint.Items.Count > 0;
        _simpleBlend.Checked = !_aiBlend.Enabled;
        aiTip.SetToolTip(_blendDurationMs, "Type any nonnegative duration in milliseconds. The slider covers 0–3000 ms with finer adjustment near zero.");

        var statusBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            BackColor = Color.Transparent
        };
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        statusBar.Controls.Add(_status, 0, 0);
        statusBar.Controls.Add(_filter, 1, 0);

        layout.Controls.Add(rootBar, 0, 0);
        layout.Controls.Add(actionBar, 0, 1);
        layout.Controls.Add(statusBar, 0, 2);
        layout.Controls.Add(_results, 0, 3);
        Controls.Add(layout);
        var surface=_thumbnailRenderer.Surface;surface.PreviewSurface=true;surface.Dock=DockStyle.None;surface.TabStop=false;
        Controls.Add(surface);surface.Visible=false;
        surface.MouseClick+=SurfaceClick;surface.MouseDoubleClick+=SurfaceClick;
        surface.MouseWheel+=(_,e)=>
        {
            int y=-_results.AutoScrollPosition.Y-e.Delta/120*SystemInformation.MouseWheelScrollLines*20;
            _results.AutoScrollPosition=new Point(0,Math.Max(0,y));MarkSurfaceLayoutDirty();MaybeRequestMoreResults();
        };
        _results.Layout+=(_,_)=>MarkSurfaceLayoutDirty();
        _results.SizeChanged+=(_,_)=>MarkSurfaceLayoutDirty();
        VisibleChanged+=(_,_)=>MarkSurfaceLayoutDirty();

        _browse.Click += (_, _) => BrowseRequested?.Invoke(this, EventArgs.Empty);
        _back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        _reindex.Click += (_, _) => ReindexRequested?.Invoke(this, EventArgs.Empty);
        _export.Click += (_, _) => ExportRequested?.Invoke(this, EventArgs.Empty);
        _exportSelection.Enabled=false;
        _exportSelection.Click += (_,_) => ExportSelectionRequested?.Invoke(this,EventArgs.Empty);
        _results.Scroll += (_, _) => {MarkSurfaceLayoutDirty();MaybeRequestMoreResults();};
        _filter.TextChanged += (_, _) => ApplyResultFilter();
        _alignPositionAndYaw.CheckedChanged += (_, _) => ReactivateSelected();
        _simpleBlend.CheckedChanged += (_, _) => { if (_simpleBlend.Checked) ReactivateSelected(); };
        _noBlend.CheckedChanged += (_, _) => { if (_noBlend.Checked) ReactivateSelected(); };
        _aiBlend.CheckedChanged += (_, _) => { _styleHint.Enabled = _aiBlend.Checked && _styleHint.Items.Count > 0; if (_aiBlend.Checked) ReactivateSelected(); };
        _styleHint.SelectedIndexChanged += (_, _) => { if (_aiBlend.Checked) ReactivateSelected(); };
        _blendDurationSlider.Value = DurationSliderPosition(_blendMilliseconds);
        _durationEditTimer.Tick += (_, _) => { _durationEditTimer.Stop(); ReactivateSelected(); };
        _trajectoryBlend.ValueChanged += (_, _) =>
        {
            _trajectoryValue.Text = $"{_trajectoryBlend.Value}% A";
            _durationEditTimer.Stop(); _durationEditTimer.Start();
        };
        _blendDurationSlider.ValueChanged += (_, _) =>
        {
            if (_updatingDuration) return;
            _blendMilliseconds = MathF.Round(SliderMilliseconds(_blendDurationSlider.Value));
            _updatingDuration = true;
            _blendDurationMs.Text = _blendMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
            _updatingDuration = false;
            QueueDurationPreview();
        };
        _blendDurationMs.TextChanged += (_, _) =>
        {
            if (_updatingDuration) return;
            if (!float.TryParse(_blendDurationMs.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
                !float.TryParse(_blendDurationMs.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return;
            if (!float.IsFinite(value) || value < 0) return;
            _blendMilliseconds = value;
            _updatingDuration = true;
            _blendDurationSlider.Value = DurationSliderPosition(value);
            _updatingDuration = false;
            QueueDurationPreview();
        };
        _blendDurationMs.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _durationEditTimer.Stop(); ReactivateSelected();
        };
        _matchUp.CheckedChanged += (_, _) => ReactivateSelected();
        _yawJitter.ValueChanged += (_, _) => ReactivateSelected();
    }

    public (int start, int end)? Selection => _selection;
    public bool AlignPositionAndYaw => _alignPositionAndYaw.Checked;
    public bool CollisionCorrectionEnabled => false;
    public bool MatchUp => _matchUp.Checked;
    public float YawJitterDegrees => (float)_yawJitter.Value;
    public bool BlendingEnabled => !_noBlend.Checked;
    public bool AiBlendEnabled => _aiBlend.Checked;
    public string StyleHint => _styleHint.Text.Trim();
    public float BlendSeconds => _blendMilliseconds / 1000f;
    public float TrajectoryPersistence => _trajectoryBlend.Value / 100f;

    public event EventHandler? BrowseRequested;
    public event EventHandler? BackRequested;
    public event EventHandler? ReindexRequested;
    public event EventHandler? SearchRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? ExportPartsRequested;
    public event EventHandler? ExportSelectionRequested;
    public event EventHandler? LoadMoreRequested;
    public event AnimationMatchResultEventHandler? CandidateActivated;
    public event AnimationMatchResultEventHandler? CandidateOpened;
    public event EventHandler<ThumbnailRequest>? ThumbnailRequested;

    public void SetRootPath(string? path) => _root.Text = path ?? string.Empty;

    public void SetSelection((int start, int end)? selection) { _selection = selection; _exportSelection.Enabled=selection.HasValue; }

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
        _exportSelection.Enabled = !busy && _selection.HasValue;
        _browse.Enabled = !busy;
        if (status is not null)
            _status.Text = status;
    }

    public void SetResults(IReadOnlyList<AnimationMatchResult> results)
    {
        _selectedResult = null;
        MarkSurfaceLayoutDirty();
        _loadMoreArmed = true;
        _thumbnailPlayback.Reset();
        ClearThumbnailAtlas();
        _thumbnailLoads.Cancel();
        _thumbnailLoads.Dispose();
        _thumbnailLoads = new CancellationTokenSource();
        _thumbnailCards.Clear();
        _thumbnailFailed.Clear();
        _thumbnailRequests.Clear();
        _thumbnailRenderEntries.Clear();
        _resultCards.Clear();
        _results.SuspendLayout();
        try
        {
            while (_results.Controls.Count > 0)
                _results.Controls[0].Dispose();

            foreach (var result in results)
                AddResultCard(result);

            ApplyResultFilter();
        }
        finally
        {
            _results.ResumeLayout();
        }
    }

    public void AppendResults(IReadOnlyList<AnimationMatchResult> results)
    {
        if (results == null || results.Count == 0)
            return;

        _results.SuspendLayout();
        try
        {
            foreach (var result in results)
                AddResultCard(result);

            ApplyResultFilter();
        }
        finally
        {
            _results.ResumeLayout();
        }
    }

    private async void RefreshThumbnailFrames()
    {
        if ( IsDisposed || !Visible || !_results.Visible ) return;
        UpdateSurfaceLayout();
        RequestVisibleThumbnails();
        if (_thumbnailRenderEntries.Count == 0) return;

        if ( _thumbnailRenderTask is { IsCompleted: false } )
            return;

        var visibleEntries = new List<ThumbnailRenderEntry>();
        foreach ( var entry in _thumbnailRenderEntries )
        {
            if ( IsThumbnailVisible( entry.Control ) )
                visibleEntries.Add( entry );
        }

        if ( visibleEntries.Count == 0 )
            return;

        // Render every visible candidate from the same wall-clock position in one shared
        // OpenGL batch. RenderAnimationThumbnailBatch performs one model draw per cell but
        // reads the complete atlas back only once, so cards appear in sync instead of forming
        // a one-card-per-tick rolling scan.
        var firstScene = visibleEntries[0].Scene;
        var renderGeneration = _thumbnailRenderGeneration;
        var loopPosition = _thumbnailPlayback.GetLoopPositionSeconds();
        var requests = new List<AnimationThumbnailRenderRequest>( visibleEntries.Count );
        foreach ( var entry in visibleEntries )
        {
            requests.Add( new AnimationThumbnailRenderRequest(
                entry.Scene.Animation,
                _thumbnailPlayback.GetAnimationTime( entry.Scene, loopPosition ),
                entry.Control.Width,
                entry.Control.Height ) );
        }

        var modelPack = firstScene.ModelPack;
        int layoutGeneration=Volatile.Read(ref _surfaceLayoutGeneration);
        var destinations=GpuSurfaceEnabled?visibleEntries.Select(entry=>ThumbnailSurfaceRectangle(entry.Control)).ToArray():null;
        var renderTask = _thumbnailRenderer.RenderAsync(modelPack,requests,destinations,_thumbnailRenderer.Surface.ClientSize,
            ()=>layoutGeneration==Volatile.Read(ref _surfaceLayoutGeneration));
        _thumbnailRenderTask = renderTask;

        try
        {
            var batch = await renderTask;
            if ( renderGeneration != _thumbnailRenderGeneration || IsDisposed )
            {
                batch.Atlas?.Dispose();
                return;
            }

            if(GpuSurfaceEnabled)
            {
                                if(layoutGeneration==Volatile.Read(ref _surfaceLayoutGeneration))
                {
                    ThumbnailFrameCount++;
                    _thumbnailRenderer.Surface.Visible=Visible && _surfaceRectangles.Length>0;
                }
                return;
            }
            var atlas = batch.Atlas;
            var sourceRectangles = batch.SourceRectangles;
            ClearThumbnailAtlas();
            _thumbnailAtlasFrame = atlas;
            ThumbnailFrameCount++;
            for ( var index = 0; index < visibleEntries.Count; index++ )
            {
                visibleEntries[index].Control.SetAtlasFrame(
                    _thumbnailAtlasFrame,
                    sourceRectangles[index] );
            }
        }
        catch ( Exception exception )
        {
            Trace.TraceWarning( $"Could not refresh live animation thumbnails: {exception.Message}" );
        }
        finally
        {
            if ( ReferenceEquals( _thumbnailRenderTask, renderTask ) )
                _thumbnailRenderTask = null;
        }
    }

    private void RequestVisibleThumbnails()
    {
        foreach(var request in _thumbnailRequests.ToArray())
            if(!IsThumbnailVisible(request.Key))request.Value.Cancel();
        foreach(var (result,image) in _thumbnailCards)
        {
            if(_thumbnailRequests.Count>=4)break;
            if(image.Scene!=null || _thumbnailFailed.Contains(image) || _thumbnailRequests.ContainsKey(image) || !IsThumbnailVisible(image))continue;
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_thumbnailLoads.Token);
            _thumbnailRequests.Add(image,cancellation);
            ThumbnailRequested?.Invoke(this,new ThumbnailRequest(result,image.Width,image.Height,scene=>
            {
                void Finish()
                {
                    if(_thumbnailRequests.TryGetValue(image,out var pending) && ReferenceEquals(pending,cancellation))_thumbnailRequests.Remove(image);
                    bool usable=!cancellation.IsCancellationRequested && !IsDisposed && !image.IsDisposed;
                    cancellation.Dispose();
                    if(!usable)return;
                    image.SetScene(scene);
                    if(scene==null)_thumbnailFailed.Add(image);
                    if(scene!=null)_thumbnailRenderEntries.Add(new ThumbnailRenderEntry {Control=image,Scene=scene});
                }
                if(IsDisposed){cancellation.Dispose();return;}
                if(InvokeRequired)BeginInvoke((Action)Finish);else Finish();
            }) {CancellationToken=cancellation.Token});
        }
    }

    private bool IsThumbnailVisible( AnimationThumbnailControl control )
    {
        if ( control.IsDisposed || !control.Visible || !control.IsHandleCreated || !_results.IsHandleCreated )
            return false;

        var viewport = _results.RectangleToScreen( _results.ClientRectangle );
        var thumbnail = control.RectangleToScreen( control.ClientRectangle );
        return viewport.IntersectsWith( thumbnail );
    }

    private void ClearThumbnailAtlas()
    {
        _thumbnailRenderGeneration++;
        foreach ( var entry in _thumbnailRenderEntries )
            entry.Control.ClearAtlasFrame();

        _thumbnailAtlasFrame?.Dispose();
        _thumbnailAtlasFrame = null;
    }

    public void SetCanLoadMore(bool canLoadMore)
    {
        _canLoadMore = canLoadMore;
        if (canLoadMore)
        {
            _loadMoreArmed = true;
            if (IsHandleCreated && !IsDisposed && !_loadMoreCheckPending)
            {
                _loadMoreCheckPending = true;
                BeginInvoke((Action)(() =>
                {
                    _loadMoreCheckPending = false;
                    MaybeRequestMoreResults();
                }));
            }
        }
    }

    private void MaybeRequestMoreResults()
    {
        if (!_canLoadMore || !_loadMoreArmed)
            return;

        var scroll = _results.VerticalScroll;
        var remaining = scroll.Maximum - scroll.Value - scroll.LargeChange;
        if (remaining > 2 * 172)
            return;

        _loadMoreArmed = false;
        LoadMoreRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ActivatePairResult(AnimationMatchResult result)
    {
        _filter.Clear();
        _selectedResult = result;
        CandidateActivated?.Invoke(this, result);
    }

    private void QueueDurationPreview()
    {
        _durationEditTimer.Stop();
        _durationEditTimer.Start();
    }

    private void ReactivateSelected()
    {
        if (_selectedResult is not null)
            CandidateActivated?.Invoke(this, _selectedResult);
    }

    private void AddResultCard(AnimationMatchResult result)
    {
        var card = CreateResultCard(result);
        _resultCards.Add((result, card));
        _results.Controls.Add(card);
    }

    private void ApplyResultFilter()
    {
        MarkSurfaceLayoutDirty();
        var filter = _filter.Text?.Trim();
        _results.SuspendLayout();
        try
        {
            foreach (var (result, card) in _resultCards)
            {
                card.Visible = FilterTextMatcher.Matches(result.Candidate.DisplayName, filter);
            }

            if (_selectedResult is not null &&
                !_resultCards.Any(item => ReferenceEquals(item.Result, _selectedResult) && item.Card.Visible))
                _selectedResult = null;
        }
        finally
        {
            _results.ResumeLayout(true);
        }
    }

    private Control CreateResultCard(AnimationMatchResult result)
    {
        const int cardWidth = 132;
        const int cardHeight = 216;
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
            Height = 72,
            BackColor = Color.FromArgb(24, 24, 24)
        };
        var title = new Label
        {
            Left = 5,
            Top = 78,
            Width = cardWidth - 12,
            Height = 42,
            AutoEllipsis = false,
            AutoSize = false,
            UseCompatibleTextRendering = true,
            ForeColor = Color.Gainsboro,
            Text = AddTitleBreakPoints(result.Candidate.DisplayName),
            Font = new Font(Font.FontFamily, 8f, FontStyle.Regular)
        };
        var detail = new Label
        {
            Left = 5,
            Top = 194,
            Width = cardWidth - 12,
            Height = 29,
            AutoEllipsis = false,
            AutoSize = false,
            UseCompatibleTextRendering = true,
            ForeColor = Color.Silver,
            Text = $"{result.Score:0.0}% · f{result.SourceFrame} → f{result.CandidateFrame}"
        };
        title.Height = title.GetPreferredSize(new Size(title.Width, 0)).Height;
        detail.Top = title.Bottom;
        detail.Font = new Font(Font.FontFamily, 8f);
        detail.Height=detail.GetPreferredSize(new Size(detail.Width,0)).Height;
        card.Height = detail.Bottom + 4;
        var titleToolTip = new ToolTip();
        titleToolTip.SetToolTip(title, result.Candidate.DisplayName);
        titleToolTip.SetToolTip(detail, $"{result.Score:0.0}% · f{result.SourceFrame} → f{result.CandidateFrame}");
        card.Disposed += (_, _) => titleToolTip.Dispose();
        card.Controls.AddRange(new Control[] { image, title, detail });

        void SelectCard()
        {
            _selectedResult = result;
            foreach (Control child in _results.Controls)
                child.BackColor = Color.FromArgb(37, 37, 38);
            card.BackColor = Color.FromArgb(55, 72, 84);
        }

        void Activate(object? _, EventArgs __)
        {
            SelectCard();
            CandidateActivated?.Invoke(this, result);
        }

        void Open(object? _, EventArgs __)
        {
            SelectCard();
            CandidateOpened?.Invoke(this, result);
        }

        card.Click += Activate;
        image.Click += Activate;
        title.Click += Activate;
        detail.Click += Activate;
        card.DoubleClick += Open;
        image.DoubleClick += Open;
        title.DoubleClick += Open;
        detail.DoubleClick += Open;

        _thumbnailCards.Add((result,image));
        return card;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _thumbnailRefreshTimer?.Stop();
            _thumbnailRefreshTimer?.Dispose();
            _durationEditTimer.Dispose();
            _thumbnailLoads.Cancel();
            _thumbnailLoads.Dispose();
            Controls.Remove(_thumbnailRenderer.Surface);
            _thumbnailRenderer.Dispose();
            ClearThumbnailAtlas();
            _thumbnailRenderEntries.Clear();
        }

        base.Dispose(disposing);
    }

    private static string AddTitleBreakPoints(string title)
        => title.Replace("\\", "\\\u200B").Replace("/", "/\u200B");

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        Dock = DockStyle.None,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(50, 50, 54),
        ForeColor = Color.WhiteSmoke,
        TabStop = false
    };
}
















