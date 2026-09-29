namespace ScreenshotTranslationUiTester;

internal sealed record ThemePalette(Color Main, Color Secondary, Color Control, Color Text,
    Color SecondaryText, Color Accent, Color Border)
{
    internal static ThemePalette Night { get; } = new(Color.FromArgb(18,23,34),Color.FromArgb(29,34,44),
        Color.FromArgb(48,57,72),Color.FromArgb(244,247,251),Color.FromArgb(190,205,223),
        Color.FromArgb(55,139,230),Color.FromArgb(70,84,106));
    internal static ThemePalette Day { get; } = new(Color.FromArgb(249,252,255),Color.White,
        Color.FromArgb(239,245,251),Color.FromArgb(15,28,68),Color.FromArgb(105,124,154),
        Color.FromArgb(0,112,255),Color.FromArgb(218,229,244));
}

internal static class UiTheme
{
    internal static ThemePalette Current { get; private set; } = ThemePalette.Night;

    internal static Color Sidebar=>Current.Main.GetBrightness()<.5f?Current.Secondary:Current.Control;

    internal static void Apply(Control root, ApiSettings settings,bool dataSurface=false)
    {
        using var timing=UiPerformanceTrace.Measure("theme-apply");
        Current = Resolve(settings);
        using(var batch=new UiLayoutBatch(root))ApplyRecursive(root, Current,dataSurface);
        root.Invalidate(true);
    }

    private static ThemePalette Resolve(ApiSettings settings)
    {
        if(settings.ThemeMode==ApplicationThemeMode.Day)return ThemePalette.Day;
        if(settings.ThemeMode==ApplicationThemeMode.Night)return ThemePalette.Night;
        return new(Parse(settings.CustomThemeMainBackground,ThemePalette.Night.Main),
            Parse(settings.CustomThemeSecondaryBackground,ThemePalette.Night.Secondary),
            Blend(Parse(settings.CustomThemeSecondaryBackground,ThemePalette.Night.Secondary),Parse(settings.CustomThemeMainBackground,ThemePalette.Night.Main)),
            Parse(settings.CustomThemeText,ThemePalette.Night.Text),Parse(settings.CustomThemeSecondaryText,ThemePalette.Night.SecondaryText),
            Parse(settings.CustomThemeAccent,ThemePalette.Night.Accent),Parse(settings.CustomThemeBorder,ThemePalette.Night.Border));
    }

    private static void ApplyRecursive(Control control, ThemePalette p,bool dataSurface=false,bool navigationSurface=false)
    {
        dataSurface|=control is GameDataSurface;navigationSurface|=control is LibrarySidebar;
        switch(control)
        {
            case GameDataGrid grid: grid.ApplyPalette();break;
            case WorkspaceDataGrid grid: grid.ApplyPalette();break;
            case Form or TabPage or SettingsPage: control.BackColor=p.Main;control.ForeColor=p.Text;break;
            case Button button: button.BackColor=Equals(button.Tag,"workspace-selected")?p.Accent:p.Control;button.ForeColor=Equals(button.Tag,"workspace-selected")?Color.White:p.Text;button.FlatAppearance.BorderColor=p.Border;break;
            case ComboBox combo: combo.FlatStyle=FlatStyle.Flat;combo.BackColor=p.Secondary;combo.ForeColor=p.Text;break;
            case TextBoxBase or NumericUpDown or ListBox: control.BackColor=p.Secondary;control.ForeColor=p.Text;break;
            case Label label: label.BackColor=Color.Transparent;label.ForeColor=label.ForeColor==Color.LightSkyBlue?p.Accent:p.Text;break;
            default: control.BackColor=navigationSurface?Sidebar:dataSurface?p.Secondary:p.Main;control.ForeColor=p.Text;break;
        }
        foreach(Control child in control.Controls)ApplyRecursive(child,p,dataSurface,navigationSurface);
    }

    internal static Color Parse(string value,Color fallback)
    {
        try{var color=ColorTranslator.FromHtml(value);return color.IsEmpty||color.A!=255?fallback:color;}
        catch{return fallback;}
    }
    private static Color Blend(Color a,Color b)=>Color.FromArgb((a.R*3+b.R)/4,(a.G*3+b.G)/4,(a.B*3+b.B)/4);
}
