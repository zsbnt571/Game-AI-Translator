using System.Drawing.Drawing2D;
namespace ScreenshotTranslationUiTester;

internal sealed class ScreenshotRebuildSplit : Control
{
    internal Panel Panel1 { get; } = new BufferedPage { AutoScroll = false };
    internal Panel Panel2 { get; } = new BufferedPage { AutoScroll = false };
    private double percent = 50;
    private int maximized = -1;
    private bool dragging;
    private Rectangle divider;
    internal event EventHandler? SplitChanged;
    internal int MaximizedPane
    {
        get => maximized;
        set { var next = value is 0 or 1 ? value : -1; if (next == maximized) return; maximized = next; dragging = false; Capture = false; Arrange(publishChildren: true); }
    }
    internal double SplitPercent
    {
        get => percent;
        set { var next = double.IsFinite(value) ? Math.Clamp(value, 25, 75) : 50; if (Math.Abs(next - percent) < .01) return; percent = next; Arrange(publishChildren: true); SplitChanged?.Invoke(this, EventArgs.Empty); }
    }
    internal ScreenshotRebuildSplit()
    {
        TabStop = true; AccessibleRole = AccessibleRole.Separator; AccessibleName = "调整左右分栏";
        AccessibleDescription = "拖动或方向键调整，双击或Home恢复等分";
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.Add(Panel1); Controls.Add(Panel2);
    }
    private int S(int n) => ScreenshotRebuildStyle.S(this, n);
    private void Arrange(bool publishChildren = false)
    {
        if (Panel1 is null || Panel2 is null || IsDisposed) return;
        var previousDivider = divider;
        // A divider move changes both the origin and width of native child
        // windows. Retained pixels from either old pane are no longer valid.
        // Batch only this synchronous split operation, then publish all settled
        // children before returning from the same mouse message. Window resize
        // keeps its separate, locally buffered path.
        bool redrawBatch = publishChildren && IsHandleCreated && Visible;
        if (redrawBatch) WorkspaceRepaint.SetRedraw(this, false);
        SuspendLayout();
        try
        {
            Panel1.Visible = maximized != 1; Panel2.Visible = maximized != 0;
            if (maximized >= 0)
            {
                divider = Rectangle.Empty;
                var panel = maximized == 0 ? Panel1 : Panel2;
                if (panel.Bounds != ClientRectangle) panel.Bounds = ClientRectangle;
            }
            else
            {
                int gap = S(14), available = Math.Max(0, Width - gap), min = Math.Min(available / 2, S(230));
                int first = Math.Clamp((int)Math.Round(available * percent / 100d), min, Math.Max(min, available - min));
                var firstBounds = new Rectangle(0, 0, first, Height);
                var secondBounds = new Rectangle(first + gap, 0, Math.Max(0, available - first), Height);
                if (Panel1.Bounds != firstBounds) Panel1.Bounds = firstBounds;
                divider = new(first, 0, gap, Height);
                if (Panel2.Bounds != secondBounds) Panel2.Bounds = secondBounds;
            }
        }
        finally
        {
            try { ResumeLayout(true); }
            finally
            {
                if (redrawBatch)
                {
                    WorkspaceRepaint.SetRedraw(this, true);
                    WorkspaceRepaint.Complete(this);
                }
            }
        }
        if (redrawBatch) return;
        var repaint = previousDivider.IsEmpty ? divider : divider.IsEmpty ? previousDivider : Rectangle.Union(previousDivider, divider);
        if (!repaint.IsEmpty) Invalidate(repaint);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Arrange(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (divider.IsEmpty) return;
        var p = ScreenshotRebuildStyle.Palette; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int x = divider.Left + divider.Width / 2;
        using var line = new Pen(p.Border); g.DrawLine(line, x, 0, x, Height);
        var color = dragging || Focused ? p.Accent : p.Main.GetBrightness() > .5f ? Color.FromArgb(171,191,212) : p.SecondaryText;
        using var grip = new Pen(color, S(5)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(grip, x, Height / 2 - S(23), x, Height / 2 + S(23));
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(divider, -1, -1), p.Accent, BackColor);
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && divider.Contains(e.Location)) { Focus(); dragging = true; Capture = true; Invalidate(); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = dragging || divider.Contains(e.Location) ? Cursors.VSplit : Cursors.Default; if (dragging) SplitPercent = (e.X - divider.Width / 2d) * 100 / Math.Max(1, Width - divider.Width); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) { dragging = false; Capture = false; Invalidate(); } }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) dragging = false; }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (divider.Contains(e.Location)) SplitPercent = 50; }
    protected override bool IsInputKey(Keys keys) => (keys & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home || base.IsInputKey(keys);
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (maximized >= 0) return; if (e.KeyCode == Keys.Home) SplitPercent = 50; else if (e.KeyCode == Keys.Left) SplitPercent -= 3; else if (e.KeyCode == Keys.Right) SplitPercent += 3; else return; e.Handled = e.SuppressKeyPress = true; }
}

