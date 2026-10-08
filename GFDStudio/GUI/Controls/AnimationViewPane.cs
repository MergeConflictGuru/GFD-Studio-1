using System;
using System.Drawing;
using System.Windows.Forms;
using MetroSet_UI.Controls;
using GFDLibrary;
using GFDLibrary.Animations;
using GFDLibrary.Models;
using GFDStudio.AnimationMatching.UI;

namespace GFDStudio.GUI.Controls;

/// <summary>A second showroom view with independent playback, seeking, and frame selection.</summary>
internal sealed class AnimationViewPane : UserControl
{
    public ModelViewControl Viewer { get; } = new(false);
    public RangeTimelineControl Timeline { get; } = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, DefaultFrame = 0,
        BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.FromArgb(220, 220, 220) };
    private readonly Label mCaption = new() { Dock = DockStyle.Top, Height = 44, AutoEllipsis = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TrackBar mSeek = new() { Dock = DockStyle.Fill, Minimum = 0, TickStyle = TickStyle.None, Margin = Padding.Empty };
    private readonly MetroSetButton mPlay = SquareButton("▶", "Play or pause");
    private readonly MetroSetButton mStop = SquareButton("■", "Stop");
    private bool mUpdatingSeek;
    private const float FramesPerSecond = 30f;
    public event EventHandler RangeChanged;

    public AnimationViewPane()
    {
        Name = "SecondAnimationView";
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.Gainsboro;
        Font = SystemFonts.MessageBoxFont;
        var transport = new TableLayoutPanel
        { Dock = DockStyle.Bottom, Height = 72, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(2, 6, 2, 2) };
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        transport.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        transport.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Timeline.BindPlayback(mSeek);
        transport.Controls.Add(Timeline, 0, 0);
        transport.SetColumnSpan(Timeline, 3);
        transport.Controls.Add(mSeek, 0, 1);
        transport.Controls.Add(mPlay, 1, 1);
        transport.Controls.Add(mStop, 2, 1);
        var viewport = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
        viewport.Controls.Add(Viewer);
        Controls.Add(viewport);
        Controls.Add(mCaption);
        Controls.Add(transport);
        mPlay.Click += (_, _) => Viewer.AnimationPlayback = Viewer.AnimationPlayback == AnimationPlaybackState.Playing
            ? AnimationPlaybackState.Paused : AnimationPlaybackState.Playing;
        mStop.Click += (_, _) => Viewer.AnimationPlayback = AnimationPlaybackState.Stopped;
        Viewer.AnimationPlaybackStateChanged += (_, playback) => mPlay.Text = playback == AnimationPlaybackState.Playing ? "Ⅱ" : "▶";
        Viewer.AnimationLoaded += (_, animation) => mSeek.Maximum = Math.Max(0, (int)Math.Round(animation.Duration * 1000));
        Viewer.AnimationTimeChanged += (_, seconds) =>
        {
            mUpdatingSeek = true;
            mSeek.Value = Math.Clamp((int)Math.Round(seconds * 1000), mSeek.Minimum, mSeek.Maximum);
            mUpdatingSeek = false;
        };
        mSeek.ValueChanged += (_, _) =>
        {
            if (mUpdatingSeek) return;
            Viewer.AnimationTime = mSeek.Value / 1000d;
            Viewer.Invalidate();
        };
        mSeek.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            mSeek.Value = (int)Math.Round(Math.Clamp(e.X / (double)Math.Max(1, mSeek.Width - 1), 0, 1) * mSeek.Maximum);
        };
        Timeline.SelectionChanged += (_, _) =>
        {
            if (Timeline.Selection is { } range)
                Viewer.SetAnimationLoop(range.start / FramesPerSecond, (range.end + 1) / FramesPerSecond);
            else Viewer.ClearAnimationLoop();
            RangeChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public void LoadClip(ModelPack model, Animation animation, string caption, int frameCount)
    {
        Viewer.LoadModel(model);
        Viewer.LoadAnimation(animation, true);
        mCaption.Text = "2 · " + caption;
        Timeline.FrameCount = frameCount;
        Timeline.TransitionFrame = -1;
    }

    public void SeekFrame(int frame) { Viewer.AnimationTime = frame / FramesPerSecond; Viewer.Invalidate(); }

    private static MetroSetButton SquareButton(string text, string accessibleName) => new()
    {
        Text = text, AccessibleName = accessibleName, Dock = DockStyle.Fill, Margin = new Padding(1),
        Style = MetroSet_UI.Enums.Style.Dark,
        NormalColor = Color.FromArgb(65, 177, 225), NormalBorderColor = Color.FromArgb(65, 177, 225),
        NormalTextColor = Color.White, HoverColor = Color.FromArgb(95, 207, 255),
        HoverBorderColor = Color.FromArgb(95, 207, 255), HoverTextColor = Color.White,
        PressColor = Color.FromArgb(35, 147, 195), PressBorderColor = Color.FromArgb(35, 147, 195), PressTextColor = Color.White,
        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10f), Padding = Padding.Empty
    };
}
