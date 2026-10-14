using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

// Local screenshot-page styling. Shared game-library controls keep their own theme.
internal static class ScreenshotRebuildStyle
{
    private static readonly ThemePalette Day = new(
        Color.FromArgb(245,248,252), Color.White, Color.FromArgb(234,240,246),
        Color.FromArgb(40,65,83), Color.FromArgb(99,127,155),
        Color.FromArgb(45,118,221), Color.FromArgb(217,228,241));
    internal static ThemePalette Palette => UiTheme.Current.Main.GetBrightness() >= .5f ? Day : UiTheme.Current;
    internal static int S(Control control, int value) => Math.Max(1, (int)Math.Round(value * control.DeviceDpi / 96d));
    internal static Font Font(float size = 8.25f, FontStyle style = FontStyle.Regular) => FontManager.Create("Microsoft YaHei UI", size, style);
}

internal sealed class ScreenshotRebuildButton : Button
{
    private readonly Font localFont;
    private readonly int logicalWidth;
    private bool hover, pressed, primary, applyingLayout;
    private Size measuredSize;
    private string? measuredText, measuredIcon;
    private Font? measuredFont;
    private int measuredDpi, measuredHeight;
    private int? layoutFixedLogicalWidth;
    private string iconKey;
    internal int LogicalHeight { get; set; } = 26;
    internal bool Primary { get => primary; set { primary = value; Invalidate(); } }
    internal string IconKey { get => iconKey; set { iconKey = value ?? ""; Invalidate(); } }
    internal bool Borderless { get; set; }
    internal ScreenshotRebuildButton(string text, string icon = "", int width = 80)
    {
        Text = text; iconKey = icon; logicalWidth = width;
        localFont = ScreenshotRebuildStyle.Font(); Font = localFont;
        FontManager.MarkPreviewTextRoot(this);
        Margin = Padding.Empty; Padding = Padding.Empty;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        RefreshMetrics();
    }
    internal void RefreshMetrics()
    {
        var size = MeasureLayoutSize();
        if (layoutFixedLogicalWidth is { } width) size.Width = ScreenshotRebuildStyle.S(this, width);
        Size = size;
    }
    private Size MeasureLayoutSize()
    {
        if (measuredText == Text && measuredIcon == iconKey && ReferenceEquals(measuredFont, Font)
            && measuredDpi == DeviceDpi && measuredHeight == LogicalHeight) return measuredSize;
        int S(int value) => ScreenshotRebuildStyle.S(this, value);
        int measured = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        measuredText = Text; measuredIcon = iconKey; measuredFont = Font;
        measuredDpi = DeviceDpi; measuredHeight = LogicalHeight;
        return measuredSize = new(Math.Max(S(logicalWidth), measured + S(iconKey.Length > 0 ? 30 : 16)), S(LogicalHeight));
    }
    // A compact presentation must not grow to its default width before being
    // shortened again. Commit its label and final bounds as one layout step.
    internal void SetLayout(string label, int? fixedLogicalWidth, int x, int y, bool alignRight = false)
    {
        applyingLayout = true;
        try { if (Text != label) Text = label; }
        finally { applyingLayout = false; }
        layoutFixedLogicalWidth = fixedLogicalWidth;
        var size = MeasureLayoutSize();
        if (fixedLogicalWidth is { } width) size.Width = ScreenshotRebuildStyle.S(this, width);
        SetBounds(alignRight ? x - size.Width : x, y, size.Width, size.Height);
    }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); RefreshMetrics(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (localFont is not null && !applyingLayout) RefreshMetrics(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (localFont is not null && !applyingLayout) RefreshMetrics(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int value) => ScreenshotRebuildStyle.S(this, value);
        var p = ScreenshotRebuildStyle.Palette; var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Secondary); g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = primary ? p.Accent : hover || pressed ? p.Control : p.Secondary;
        var color = Enabled ? primary ? Color.White : p.Text : p.SecondaryText;
        using var shape = WorkspaceDrawing.Round(Rectangle.Inflate(ClientRectangle, -1, -1), S(4));
        if (!Borderless || hover || primary) { using var brush = new SolidBrush(fill); g.FillPath(brush, shape); }
        if (!Borderless) { using var border = new Pen(primary ? p.Accent : p.Border); g.DrawPath(border, shape); }
        int icon = iconKey.Length > 0 ? S(12) : 0, gap = icon > 0 && Text.Length > 0 ? S(5) : 0;
        int tw = Text.Length == 0 ? 0 : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int x = Math.Max(S(3), (Width - icon - gap - tw) / 2);
        if (icon > 0) WorkspaceSkin.Icon(g, iconKey, new(x, (Height - icon) / 2, icon, icon), color);
        if (Text.Length > 0) TextRenderer.DrawText(g, Text, Font, new Rectangle(x + icon + gap, 0, Math.Max(1, Width - x - icon - gap - S(3)), Height), color, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -S(3), -S(3)), p.Accent, fill);
    }
    protected override void Dispose(bool disposing) { try { base.Dispose(disposing); } finally { if (disposing) localFont.Dispose(); } }
}

internal sealed class ScreenshotRebuildSegments : Control
{
    private ScreenshotPaneView selected;
    private int hover = -1;
    internal event EventHandler? SelectionChanged;
    internal ScreenshotPaneView Selected
    {
        get => selected;
        set { if (!Enum.IsDefined(value) || selected == value) return; selected = value; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
    }
    internal ScreenshotRebuildSegments()
    {
        TabStop = true; Cursor = Cursors.Hand; AccessibleName = "显示内容：原图、译图、原文、译文";
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    private int IndexAt(int x) => Math.Clamp((x - 3) * 4 / Math.Max(1, Width - 6), 0, 3);
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Selected = (ScreenshotPaneView)IndexAt(e.X); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int next = IndexAt(e.X); if (next != hover) { hover = next; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int next = e.KeyCode switch { Keys.Left => Math.Max(0, (int)selected - 1), Keys.Right => Math.Min(3, (int)selected + 1), Keys.Home => 0, Keys.End => 3, _ => -1 };
        if (next >= 0) { Selected = (ScreenshotPaneView)next; e.Handled = e.SuppressKeyPress = true; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        int S(int value) => ScreenshotRebuildStyle.S(this, value);
        var p = ScreenshotRebuildStyle.Palette; var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? p.Secondary); g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = WorkspaceDrawing.Round(ClientRectangle, S(5)))
        using (var brush = new SolidBrush(p.Control)) g.FillPath(brush, path);
        int pad = S(3), available = Math.Max(1, Width - pad * 2);
        for (int i = 0; i < 4; i++)
        {
            int left = pad + i * available / 4, right = pad + (i + 1) * available / 4;
            var box = new Rectangle(left, pad, right - left, Math.Max(1, Height - pad * 2));
            if (i == (int)selected)
            {
                using var path = WorkspaceDrawing.Round(box, S(4)); using var brush = new SolidBrush(p.Secondary); g.FillPath(brush, path);
            }
            var color = i == (int)selected || i == hover ? p.Accent : p.SecondaryText;
            var title=UiLanguage.Current switch
            {
                ApplicationLanguage.English=>new[]{"Image","Translated","Source","Text"}[i],
                ApplicationLanguage.Russian=>new[]{"Снимок","Перевод","Оригинал","Текст"}[i],
                _=>ScreenshotRebuildPane.ViewTitle((ScreenshotPaneView)i)
            };
            TextRenderer.DrawText(g, title, Font, box, color, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -S(2), -S(2)), p.Accent, p.Control);
    }
}

// WinForms Label adds glyph padding before deciding to wrap. These two compact
// pane labels intentionally reserve a single line, matching the reference UI.
internal sealed class ScreenshotRebuildSingleLineLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
    {
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        flags |= TextAlign switch
        {
            ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter => TextFormatFlags.HorizontalCenter,
            ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.Left
        };
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
    }
}
