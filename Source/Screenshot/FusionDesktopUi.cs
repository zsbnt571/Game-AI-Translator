using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private bool _desktopReady;
    private string? _desktopAppearance;
    private ApiSettings _appliedDesktopSettings=new();
    private readonly Dictionary<ScrollableControl,Point> _pagePositions=new();
    private string? _profileReturnPage;
    private readonly Button _profileReturnButton=new(){Text="保存并返回",AutoSize=true,Visible=false};
    private readonly Button _profileBackButton=new(){Text="返回",AutoSize=true,Visible=false};

    private void RememberPosition(Control root)
    {
        foreach(var page in UiLayoutBatch.Walk(root).OfType<ScrollableControl>().Where(x=>x.AutoScroll&&(x.Visible||!_pagePositions.ContainsKey(x))))
            _pagePositions[page]=new(-page.AutoScrollPosition.X,-page.AutoScrollPosition.Y);
    }
    private void RestorePosition(Control root)
    {
        foreach(var page in UiLayoutBatch.Walk(root).OfType<ScrollableControl>().Where(x=>x.AutoScroll&&x.Visible))
            if(_pagePositions.TryGetValue(page,out var position))RestoreScroll(page,position);
    }
    private static void RestoreScroll(ScrollableControl page,Point p)
    {
        if(page.IsDisposed)return;
        page.AutoScrollPosition=new Point(Math.Clamp(p.X,0,Math.Max(0,page.DisplayRectangle.Width-page.ClientSize.Width)),
            Math.Clamp(p.Y,0,Math.Max(0,page.DisplayRectangle.Height-page.ClientSize.Height)));
    }
    private void ApplyDesktopAppearance(ApiSettings settings)
    {
        // No credentials or translation parameters participate in this UI identity.
        var identity=JsonSerializer.Serialize(new{settings.ThemeMode,settings.CustomThemeMainBackground,
            settings.CustomThemeSecondaryBackground,settings.CustomThemeText,settings.CustomThemeSecondaryText,
            settings.CustomThemeAccent,settings.CustomThemeBorder,settings.UiFontMode,settings.UiFontFamily,settings.UiFontSize});
        if(identity==_desktopAppearance)return;
        _appliedDesktopSettings=ApiSettingsSnapshot.Copy(settings);
        RememberPosition(this);
        using(var layout=new UiLayoutBatch(this))
        {
            UiTheme.Apply(this,settings);FontManager.ApplyUi(this,settings);
            RefreshFusionChrome();_settingsNavigation.Commit(_settingsTabs?.SelectedIndex??-1);
            ApplyHistoryScrollTheme();
        }
        Invalidate();
        _desktopAppearance=identity;RestorePosition(this);
    }
    private void ReturnFromProfile(bool save)
    {
        if(_profileReturnPage is not { } page)return;
        if(save?!SaveProfileEditor():!ConfirmProfileLeave())return;
        ShowPage(page);
        if(_currentMainPage==page){_profileReturnPage=null;_profileReturnButton.Visible=_profileBackButton.Visible=false;}
    }
    private void ClearProfileReturn()
    {_profileReturnPage=null;_profileReturnButton.Visible=_profileBackButton.Visible=false;}
    private static void SetLabelText(Label label,string value){if(label.Text!=value)label.Text=value;}
}
