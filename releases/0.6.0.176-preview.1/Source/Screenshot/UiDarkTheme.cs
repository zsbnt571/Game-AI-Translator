namespace ScreenshotTranslationUiTester;

// Conservative colour pass: native control painting, fonts and layout stay untouched.
internal static class UiDarkTheme
{
    internal static readonly Color WindowBackground = Color.FromArgb(31, 35, 42);
    internal static readonly Color PanelBackground = Color.FromArgb(37, 42, 50);
    internal static readonly Color ControlBackground = Color.FromArgb(55, 62, 73);
    internal static readonly Color ControlHover = Color.FromArgb(67, 76, 90);
    internal static readonly Color InputBackground = SystemColors.Window;
    internal static readonly Color InputForeground = SystemColors.WindowText;
    internal static readonly Color TextPrimary = Color.FromArgb(235, 239, 244);
    internal static readonly Color TextSecondary = Color.FromArgb(204, 212, 223);
    internal static readonly Color TextDisabled = Color.FromArgb(155, 164, 176);
    internal static readonly Color Border = Color.FromArgb(91, 101, 116);
    internal static readonly Color Accent = Color.FromArgb(40, 104, 181);
    internal static readonly Font SafeUiFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

    internal static void Apply(Control root)
    {
        ApplyOne(root);
        foreach (Control child in root.Controls) Apply(child);
        // Do not invalidate: callers own the normal layout/paint cycle.
    }

    private static void ApplyOne(Control control)
    {
        switch (control)
        {
            case Form form: form.BackColor = WindowBackground; form.ForeColor = TextPrimary; break;
            case TabControl tabs: tabs.BackColor = WindowBackground; tabs.ForeColor = TextPrimary; break;
            case TabPage tab: tab.BackColor = WindowBackground; tab.ForeColor = TextPrimary; break;
            case TextBoxBase text: text.BackColor = InputBackground; text.ForeColor = InputForeground; break;
            case ComboBox combo: combo.BackColor = InputBackground; combo.ForeColor = InputForeground; break;
            case NumericUpDown number: number.BackColor = InputBackground; number.ForeColor = InputForeground; break;
            case Button button:
                button.UseVisualStyleBackColor = false; button.BackColor = ControlBackground;
                button.ForeColor = button.Enabled ? TextPrimary : TextDisabled;
                break;
            case CheckBox check: check.ForeColor = check.Enabled ? TextPrimary : TextDisabled; break;
            case RadioButton radio: radio.ForeColor = radio.Enabled ? TextPrimary : TextDisabled; break;
            case GroupBox group: group.BackColor = WindowBackground; group.ForeColor = TextPrimary; break;
            case Label label: label.ForeColor = label.Enabled ? TextPrimary : TextDisabled; break;
            case ListBox list: list.BackColor = InputBackground; list.ForeColor = InputForeground; break;
            case Panel or FlowLayoutPanel or TableLayoutPanel:
                if (control.BackColor == SystemColors.Control || control.BackColor == Color.Transparent) control.BackColor = WindowBackground;
                control.ForeColor = TextPrimary; break;
        }
    }
}

// Native page host; owner drawing is restricted to individual header rectangles.
internal sealed class DarkTabControl : TabControl
{
    private const int WmPaint=0x000F;
    internal DarkTabControl() { DrawMode = TabDrawMode.OwnerDrawFixed; SizeMode = TabSizeMode.Normal; }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabCount) return;PaintHeaderItem(e.Graphics,e.Index,Point.Empty);
    }
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);if(m.Msg!=WmPaint||IsDisposed||!IsHandleCreated)return;
        using var graphics=CreateGraphics();PaintThemeFrame(graphics,Point.Empty);
    }
    internal void PaintThemeFrame(Graphics graphics,Point offset)
    {
        var palette=UiTheme.Current;
        // Native TabControl can repaint its header strip white after a live theme
        // switch. Repaint the complete strip (including the unused right side),
        // matching the dark navigation controls instead of leaving a white band.
        var headerBottom=TabCount>0?Enumerable.Range(0,TabCount).Max(i=>GetTabRect(i).Bottom)+2:ItemSize.Height+4;
        using(var header=new SolidBrush(palette.Main))graphics.FillRectangle(header,offset.X,offset.Y,ClientSize.Width,headerBottom);
        for(var i=0;i<TabCount;i++)PaintHeaderItem(graphics,i,offset);
        // The bright 1px rectangle is the native TabControl client-frame, not a
        // Tab header or the Settings page background. Cover precisely those four
        // frame strips with the same border token used by navigation buttons.
        var display=Rectangle.Inflate(DisplayRectangle,4,4);display.Offset(offset);
        using var brush=new SolidBrush(palette.Border);const int thickness=6;
        graphics.FillRectangle(brush,display.Left,display.Top,display.Width,thickness);
        graphics.FillRectangle(brush,display.Left,display.Bottom-thickness,display.Width,thickness);
        graphics.FillRectangle(brush,display.Left,display.Top,thickness,display.Height);
        graphics.FillRectangle(brush,display.Right-thickness,display.Top,thickness,display.Height);
    }
    private void PaintHeaderItem(Graphics graphics,int index,Point offset)
    {
        var palette=UiTheme.Current;var selected=index==SelectedIndex;var bounds=GetTabRect(index);bounds.Offset(offset);
        using var fill=new SolidBrush(selected?palette.Secondary:palette.Control);graphics.FillRectangle(fill,bounds);
        TextRenderer.DrawText(graphics,TabPages[index].Text,Font,bounds,palette.Text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix);
        using var border=new Pen(palette.Border);graphics.DrawRectangle(border,Rectangle.Inflate(bounds,-1,-1));
    }
}
