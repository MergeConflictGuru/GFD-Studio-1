using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;
using GFDLibrary.Animations;
using GFDStudio.GUI.Controls;

namespace GFDStudio.GUI.Forms;

public partial class MainForm
{
    private readonly ConditionalWeakTable<Animation, List<TimelineMark>> mTimelineMarks = new();
    private readonly Dictionary<string, List<TimelineMark>> mBrowserTimelineMarks = new(StringComparer.OrdinalIgnoreCase);
    private TimelineMarksControl mTimelineMarksControl;
    private ToolStripMenuItem mAddTimelineMarkMenuItem;
    private ToolStripMenuItem mExportTimelineMarksMenuItem;

    private List<TimelineMark> GetTimelineMarks()
        => ModelViewControl.Instance.Animation is { } animation
            ? mTimelineMarks.GetOrCreateValue(animation) : null;

    private void RememberBrowserTimelineMarks(Animation animation, CharacterAnimationEntry entry)
    {
        var key = $"{entry.PackPath}|{entry.Kind}|{entry.Index}";
        if (!mBrowserTimelineMarks.TryGetValue(key, out var marks))
            mBrowserTimelineMarks.Add(key, marks = new List<TimelineMark>());
        mTimelineMarks.Remove(animation);
        mTimelineMarks.Add(animation, marks);
    }

    private void InitializeTimelineMarks()
    {
        mTimelineMarksControl = new TimelineMarksControl { Dock = DockStyle.Fill, Margin = Padding.Empty };
        mTimelineMarksControl.MarkRequested += milliseconds => EditTimelineMark(null, milliseconds);
        mTimelineMarksControl.MarkSelected += mark =>
        {
            ModelViewControl.Instance.AnimationPlayback = AnimationPlaybackState.Paused;
            ModelViewControl.Instance.AnimationTime = mark.Milliseconds / 1000d;
        };
        mTimelineMarksControl.MarkEdited += mark => EditTimelineMark(mark, mark.Milliseconds);
        mTimelineMarksControl.MarkRemoved += mark => { GetTimelineMarks()?.Remove(mark); RefreshTimelineMarks(); };

        mAddTimelineMarkMenuItem = new ToolStripMenuItem("Add timeline mark…", null, (_, _) => AddTimelineMarkAtPlayhead())
        { ShortcutKeys = Keys.Control | Keys.Shift | Keys.M };
        mExportTimelineMarksMenuItem = new ToolStripMenuItem("Export timeline marks…", null, (_, _) => ExportTimelineMarks());
        mFileToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
        mFileToolStripMenuItem.DropDownItems.Add(mAddTimelineMarkMenuItem);
        mFileToolStripMenuItem.DropDownItems.Add(mExportTimelineMarksMenuItem);
        ModelViewControl.Instance.AnimationLoaded += (_, _) => RefreshTimelineMarks();
        ModelViewControl.Instance.AnimationTimeChanged += (_, time) =>
        {
            mTimelineMarksControl.PlayheadMilliseconds = (int)Math.Round(Math.Max(0, time) * 1000);
            mTimelineMarksControl.Invalidate();
        };
        RefreshTimelineMarks();
    }

    private void RefreshTimelineMarks()
    {
        var marks = GetTimelineMarks();
        mTimelineMarksControl.Marks = marks;
        mTimelineMarksControl.DurationMilliseconds = mAnimationTrackBar.Maximum;
        mTimelineMarksControl.Enabled = marks != null;
        mAddTimelineMarkMenuItem.Enabled = marks != null;
        bool hasMarks = marks?.Count > 0;
        mTimelineMarksControl.Visible = hasMarks;
        if (tableLayoutPanel_AnimationControls.RowCount >= 3)
            tableLayoutPanel_AnimationControls.RowStyles[2].Height = hasMarks ? 20 : 0;
        SetCompactTransportHeight(hasMarks);
        mExportTimelineMarksMenuItem.Enabled = marks?.Count > 0;
        mTimelineMarksControl.Invalidate();
    }

    private void AddTimelineMarkAtPlayhead()
        => EditTimelineMark(null, mAnimationTrackBar.Value);

    private void EditTimelineMark(TimelineMark mark, int milliseconds)
    {
        var marks = GetTimelineMarks();
        if (marks == null) return;
        ModelViewControl.Instance.AnimationPlayback = AnimationPlaybackState.Paused;
        using var dialog = new Form
        {
            Text = mark == null ? "Add timeline mark" : "Edit timeline mark",
            ClientSize = new Size(360, 148), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            BackColor = settings.DarkMode ? Theme.DarkBG : Theme.LightBG,
            ForeColor = settings.DarkMode ? Theme.DarkText : Theme.LightText
        };
        var name = new TextBox { Text = mark?.Name ?? $"Mark {marks.Count + 1}", Location = new Point(100, 16), Width = 242, MaxLength = 200 };
        var time = new NumericUpDown
        {
            Location = new Point(100, 54), Width = 242, DecimalPlaces = 3, Increment = 0.001m,
            Maximum = mAnimationTrackBar.Maximum / 1000m,
            Value = Math.Clamp(milliseconds, 0, mAnimationTrackBar.Maximum) / 1000m
        };
        var ok = new Button { Text = "OK", Location = new Point(176, 106), Width = 80 };
        var cancel = new Button { Text = "Cancel", Location = new Point(262, 106), Width = 80, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange(new Control[]
        {
            new Label { Text = "Name", Location = new Point(16, 20), AutoSize = true }, name,
            new Label { Text = "Seconds", Location = new Point(16, 58), AutoSize = true }, time, ok, cancel
        });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text.IndexOfAny(new[] { ':', '\r', '\n' }) >= 0)
            {
                MessageBox.Show(dialog, "Enter a name without colons or line breaks.", "Timeline mark");
                name.Focus();
                return;
            }
            dialog.DialogResult = DialogResult.OK;
        };
        dialog.Shown += (_, _) => { name.Focus(); name.SelectAll(); };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (mark == null) { mark = new TimelineMark(); marks.Add(mark); }
        mark.Name = name.Text.Trim();
        mark.Milliseconds = (int)Math.Round(time.Value * 1000);
        RefreshTimelineMarks();
    }

    private void ExportTimelineMarks()
    {
        var marks = GetTimelineMarks();
        if (marks?.Count > 0 != true) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Export timeline marks (seconds)", Filter = "Text files (*.txt)|*.txt",
            DefaultExt = "txt", AddExtension = true,
            FileName = string.IsNullOrEmpty(LastOpenedFilePath) ? "timeline-marks.txt"
                : Path.GetFileNameWithoutExtension(LastOpenedFilePath) + "-marks.txt"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllLines(dialog.FileName, marks.OrderBy(mark => mark.Milliseconds)
                .Select(mark => $"{mark.Name}: {(mark.Milliseconds / 1000d).ToString("F3", CultureInfo.InvariantCulture)}"), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "Could not export timeline marks", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class TimelineMark
{
    public string Name { get; set; }
    public int Milliseconds { get; set; }
}

internal sealed class TimelineMarksControl : Control
{
    private readonly ToolTip mTooltip = new();
    private TimelineMark mHoveredMark;
    public List<TimelineMark> Marks { get; set; }
    public int DurationMilliseconds { get; set; }
    public int PlayheadMilliseconds { get; set; }
    public event Action<int> MarkRequested;
    public event Action<TimelineMark> MarkSelected;
    public event Action<TimelineMark> MarkEdited;
    public event Action<TimelineMark> MarkRemoved;

    public TimelineMarksControl() { DoubleBuffered = true; ResizeRedraw = true; Cursor = Cursors.Cross; Name = "TimelineMarks"; }
    private int TimeToX(int time) => 8 + (int)Math.Round(time / (double)Math.Max(1, DurationMilliseconds) * Math.Max(1, Width - 16));
    private int XToTime(int x) => (int)Math.Round(Math.Clamp((x - 8d) / Math.Max(1, Width - 16), 0, 1) * DurationMilliseconds);
    private TimelineMark MarkAt(int x) => Marks?.OrderBy(mark => Math.Abs(TimeToX(mark.Milliseconds) - x))
        .FirstOrDefault(mark => Math.Abs(TimeToX(mark.Milliseconds) - x) <= 7);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Marks == null || Marks.Count == 0)
        {
            TextRenderer.DrawText(e.Graphics, "Double-click to mark • times in seconds", Font, ClientRectangle, ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        using var pen = new Pen(Color.Goldenrod, 2);
        using var playheadPen = new Pen(Color.FromArgb(65, 177, 225));
        var playheadX = TimeToX(PlayheadMilliseconds);
        e.Graphics.DrawLine(playheadPen, playheadX, 0, playheadX, Height);
        var sorted = Marks.OrderBy(mark => mark.Milliseconds).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var mark = sorted[i];
            int x = TimeToX(mark.Milliseconds);
            e.Graphics.DrawLine(pen, x, 0, x, Height - 2);
            int end = i + 1 < sorted.Count ? TimeToX(sorted[i + 1].Milliseconds) - 2 : Width;
            if (end - x > 20)
                TextRenderer.DrawText(e.Graphics, mark.Name, Font, new Rectangle(x + 3, 0, Math.Max(1, end - x - 3), Height),
                    ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var mark = MarkAt(e.X);
        if (mark == mHoveredMark) return;
        mHoveredMark = mark;
        mTooltip.SetToolTip(this, mark == null ? "Double-click to add a mark" :
            $"{mark.Name}: {(mark.Milliseconds / 1000d).ToString("F3", CultureInfo.InvariantCulture)} s • Right-click to edit or delete");
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var mark = MarkAt(e.X);
        if (mark != null) MarkEdited?.Invoke(mark);
        else MarkRequested?.Invoke(XToTime(e.X));
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var mark = MarkAt(e.X);
        if (e.Button == MouseButtons.Left && mark != null) MarkSelected?.Invoke(mark);
        if (e.Button != MouseButtons.Right) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Add mark here…", null, (_, _) => MarkRequested?.Invoke(XToTime(e.X)));
        if (mark != null)
        {
            menu.Items.Add("Edit mark…", null, (_, _) => MarkEdited?.Invoke(mark));
            menu.Items.Add("Delete mark", null, (_, _) => MarkRemoved?.Invoke(mark));
        }
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(this, e.Location);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) mTooltip.Dispose();
        base.Dispose(disposing);
    }
}
