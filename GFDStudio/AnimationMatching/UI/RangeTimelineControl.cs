using System;
using System.Drawing;
using System.Windows.Forms;

namespace GFDStudio.AnimationMatching.UI;

public sealed class RangeTimelineControl : Control
{
    private int _frameCount = 1;
    private int _dragStart = -1;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private int _transitionFrame = -1;

    public RangeTimelineControl()
    {
        DoubleBuffered = true;
        Height = 42;
        Cursor = Cursors.Cross;
        BackColor = SystemColors.ControlDarkDark;
        ForeColor = SystemColors.ControlLightLight;
    }

    public int FrameCount { get => _frameCount; set { _frameCount = Math.Max(1, value); ClearSelection(); Invalidate(); } }
    public (int start, int end)? Selection => _selectionStart < 0 ? null : (Math.Min(_selectionStart, _selectionEnd), Math.Max(_selectionStart, _selectionEnd));
    public int TransitionFrame { get => _transitionFrame; set { _transitionFrame = value; Invalidate(); } }

    public void ClearSelection() { _selectionStart = _selectionEnd = -1; Invalidate(); }

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
        var track = new Rectangle(8, Height / 2 - 5, Math.Max(1, Width - 16), 10);
        using var trackBrush = new SolidBrush(Color.FromArgb(70, ForeColor));
        g.FillRectangle(trackBrush, track);

        if (Selection is { } s)
        {
            var x1 = FrameToX(s.start);
            var x2 = FrameToX(s.end);
            using var selectionBrush = new SolidBrush(Color.FromArgb(110, SystemColors.Highlight));
            g.FillRectangle(selectionBrush, Math.Min(x1, x2), 5, Math.Max(2, Math.Abs(x2 - x1)), Height - 10);
        }
        else
        {
            // Implicit selection: last frame.
            var x = FrameToX(FrameCount - 1);
            using var implicitPen = new Pen(Color.FromArgb(180, ForeColor), 2f);
            g.DrawLine(implicitPen, x, 5, x, Height - 5);
        }

        if (_transitionFrame >= 0)
        {
            var x = FrameToX(Math.Clamp(_transitionFrame, 0, FrameCount - 1));
            using var boundaryPen = new Pen(Color.Gold, 2f);
            g.DrawLine(boundaryPen, x, 1, x, Height - 1);
        }
    }

    private int XToFrame(int x)
    {
        var t = Math.Clamp((x - 8f) / Math.Max(1f, Width - 16f), 0f, 1f);
        return (int)MathF.Round(t * (FrameCount - 1));
    }

    private int FrameToX(int frame)
        => 8 + (int)MathF.Round(frame / (float)Math.Max(1, FrameCount - 1) * Math.Max(1, Width - 16));
}
