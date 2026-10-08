using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GFDStudio.AnimationMatching.UI;

public sealed class RangeTimelineControl : Control
{
    private TrackBar _playback;
    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, ref ThumbRectangle rectangle);
    private const int TrackBarGetChannelRect = 0x41A; // TBM_GETCHANNELRECT
    private const int TrackBarGetThumbRect = 0x419;   // TBM_GETTHUMBRECT

    public void BindPlayback(TrackBar playback)
    {
        if (_playback != null)
        {
            _playback.ValueChanged -= PlaybackChanged;
            _playback.SizeChanged -= PlaybackChanged;
        }
        _playback = playback;
        _playback.ValueChanged += PlaybackChanged;
        _playback.SizeChanged += PlaybackChanged;
        Invalidate();
    }

    private void PlaybackChanged(object sender, EventArgs e) => Invalidate();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _playback != null)
        {
            _playback.ValueChanged -= PlaybackChanged;
            _playback.SizeChanged -= PlaybackChanged;
        }
        base.Dispose(disposing);
    }

    private int _frameCount = 1;
    private int _dragStart = -1;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private int _transitionFrame = -1;
    private int _blendEndFrame = -1;

    public RangeTimelineControl()
    {
        DoubleBuffered = true;
        Height = 42;
        Cursor = Cursors.Cross;
        BackColor = SystemColors.ControlDarkDark;
        ForeColor = SystemColors.ControlLightLight;
    }

    public int? DefaultFrame { get; set; }
    public int FrameCount { get => _frameCount; set { _frameCount = Math.Max(1, value); _blendEndFrame = -1; ClearSelection(); Invalidate(); } }
    public (int start, int end)? Selection => _selectionStart < 0 ? null : (Math.Min(_selectionStart, _selectionEnd), Math.Max(_selectionStart, _selectionEnd));
    public int TransitionFrame { get => _transitionFrame; set { _transitionFrame = value; Invalidate(); } }
    public int BlendEndFrame { get => _blendEndFrame; set { _blendEndFrame = value; Invalidate(); } }
    public event EventHandler SelectionChanged;

    public void ClearSelection()
    {
        _selectionStart = _selectionEnd = -1;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelection((int start, int end)? selection)
    {
        if (selection is { } value)
        {
            _selectionStart = Math.Clamp(value.start, 0, _frameCount - 1);
            _selectionEnd = Math.Clamp(value.end, 0, _frameCount - 1);
        }
        else
        {
            _selectionStart = _selectionEnd = -1;
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragStart = XToFrame(e.X);
        _selectionStart = _selectionEnd = _dragStart;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Capture || _dragStart < 0) return;
        _selectionEnd = XToFrame(e.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        Capture = false;
        _dragStart = -1;
        // A click selects one explicit frame. Right click clears and restores implicit last-frame mode.
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right) ClearSelection();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int top = _playback == null ? 0 : 17;
        int stripHeight = Math.Max(10, Height - top);
        var (trackLeft, trackRight) = PlaybackEndpoints();
        var track = new Rectangle(trackLeft, top + stripHeight / 2 - 5,
            Math.Max(1, trackRight - trackLeft), 10);
        using var trackBrush = new SolidBrush(Color.FromArgb(70, ForeColor));
        g.FillRectangle(trackBrush, track);

        if (Selection is { } s)
        {
            var x1 = FrameToX(s.start);
            var x2 = FrameToX(s.end);
            using var selectionBrush = new SolidBrush(Color.FromArgb(110, SystemColors.Highlight));
            g.FillRectangle(selectionBrush, Math.Min(x1, x2), top + 2, Math.Max(2, Math.Abs(x2 - x1)), stripHeight - 4);
        }
        else
        {
            // The first pane defaults to its end; the second pane defaults to its start.
            var x = FrameToX(Math.Clamp(DefaultFrame ?? FrameCount - 1, 0, FrameCount - 1));
            using var implicitPen = new Pen(Color.FromArgb(180, ForeColor), 2f);
            g.DrawLine(implicitPen, x, top + 2, x, Height - 2);
        }

        if (_transitionFrame >= 0)
        {
            var x = FrameToX(Math.Clamp(_transitionFrame, 0, FrameCount - 1));
            if (_blendEndFrame > _transitionFrame)
            {
                int end = FrameToX(Math.Clamp(_blendEndFrame, 0, FrameCount - 1));
                var area = new Rectangle(x, track.Top, Math.Max(2, end - x), track.Height);
                using var blendBrush = new LinearGradientBrush(area, Color.Orange, Color.Yellow, LinearGradientMode.Horizontal);
                g.FillRectangle(blendBrush, area);
            }
            using var boundaryPen = new Pen(Color.DarkOrange, 2f);
            using var markerBrush = new SolidBrush(Color.DarkOrange);
            g.DrawLine(boundaryPen, x, top, x, Height - 1);
            g.FillPolygon(markerBrush, new[] { new Point(x - 4, top), new Point(x + 4, top), new Point(x, top + 5) });
        }
        if (_playback != null && _playback.IsHandleCreated && IsHandleCreated)
        {
            var thumb = new ThumbRectangle();
            SendMessage(_playback.Handle, TrackBarGetThumbRect, IntPtr.Zero, ref thumb);
            int center = PointToClient(_playback.PointToScreen(new Point((thumb.Left + thumb.Right) / 2, 0))).X;
            string time = (_playback.Value / 1000d).ToString("0.000", CultureInfo.InvariantCulture) + " s";
            var size = TextRenderer.MeasureText(time, Font, Size.Empty, TextFormatFlags.NoPadding);
            int left = Math.Clamp(center - size.Width / 2, 0, Math.Max(0, Width - size.Width));
            TextRenderer.DrawText(g, time, Font, new Point(left, 0), ForeColor, TextFormatFlags.NoPadding);
        }
    }

    private int XToFrame(int x)
    {
        var (left, right) = PlaybackEndpoints();
        var t = Math.Clamp((x - left) / Math.Max(1f, right - left), 0f, 1f);
        return (int)MathF.Round(t * (FrameCount - 1));
    }

    private int FrameToX(int frame)
    {
        var (left, right) = PlaybackEndpoints();
        return left + (int)MathF.Round(frame / (float)Math.Max(1, FrameCount - 1) * Math.Max(1, right - left));
    }

    private (int left, int right) PlaybackEndpoints()
    {
        int left = 8, right = Math.Max(left + 1, Width - 8);
        if (_playback == null || !_playback.IsHandleCreated || !IsHandleCreated)
            return (left, right);

        var channel = new ThumbRectangle();
        var thumb = new ThumbRectangle();
        SendMessage(_playback.Handle, TrackBarGetChannelRect, IntPtr.Zero, ref channel);
        SendMessage(_playback.Handle, TrackBarGetThumbRect, IntPtr.Zero, ref thumb);
        int halfThumb = Math.Max(1, (thumb.Right - thumb.Left) / 2);
        int nativeLeft = channel.Left + halfThumb;
        int nativeRight = channel.Right - halfThumb;
        if (nativeRight > nativeLeft)
            (left, right) = (
                PointToClient(_playback.PointToScreen(new Point(nativeLeft, 0))).X,
                PointToClient(_playback.PointToScreen(new Point(nativeRight, 0))).X);
        return (left, right);
    }
}
