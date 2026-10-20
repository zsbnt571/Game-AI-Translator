using System.Text.Json;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private bool _desktopReady;
    private string? _desktopAppearance;
    private ApiSettings _appliedDesktopSettings=new();
    private readonly Dictionary<ScrollableControl,Point> _pagePositions=new();
    private string? _profileReturnPage;
    private readonly Button _profileReturnButton=WpfWorkspace.Enabled ? null! : new(){Text="保存并返回",AutoSize=true,Visible=false};
    private readonly Button _profileBackButton=WpfWorkspace.Enabled ? null! : new(){Text="返回",AutoSize=true,Visible=false};

    private void RememberPosition(Control root)
    {
        foreach(var page in UiLayoutBatch.Walk(root).OfType<ScrollableControl>().Where(x=>x.AutoScroll&&(x.Visible||!_pagePositions.ContainsKey(x))))
            _pagePositions[page]=new(-page.AutoScrollPosition.X,-page.AutoScrollPosition.Y);
    }
    private void RememberPosition(DesktopApplicationController root) { if (root.HasLegacyWindow) RememberPosition(root.LegacyWindow); }
    private void RestorePosition(DesktopApplicationController root) { if (root.HasLegacyWindow) RestorePosition(root.LegacyWindow); }
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
        var identity=JsonSerializer.Serialize(new{settings.InterfaceLanguage,settings.ThemeMode,settings.CustomThemeMainBackground,
            settings.CustomThemeSecondaryBackground,settings.CustomThemeText,settings.CustomThemeSecondaryText,
            settings.CustomThemeAccent,settings.CustomThemeBorder,settings.UiFontMode,settings.UiFontFamily,settings.UiFontSize});
        if(identity==_desktopAppearance)return;
        var previous=_appliedDesktopSettings;
        bool metricsChanged=_desktopAppearance is null||previous.InterfaceLanguage!=settings.InterfaceLanguage||
            previous.UiFontMode!=settings.UiFontMode||previous.UiFontFamily!=settings.UiFontFamily||previous.UiFontSize!=settings.UiFontSize;
        _appliedDesktopSettings=ApiSettingsSnapshot.Copy(settings);
        bool languageChanged=previous.InterfaceLanguage!=settings.InterfaceLanguage;
        if (WpfWorkspace.Enabled)
        {
            UiLanguage.Set(settings.InterfaceLanguage);
            if(languageChanged)RefreshInterfaceDisplayNames();
            UiTheme.Apply(this, settings);
            if (metricsChanged) FontManager.ApplyUi(this, settings);
            foreach (var item in _nativeNavigation.Values) item.Font = Font;
            foreach (var module in WpfSessionModules)
            {
                UiTheme.Apply(module, settings);
                if (metricsChanged) FontManager.ApplyUi(module, settings);
            }
            RefreshFusionChrome();
            _desktopAppearance = identity;
            return;
        }
        using var repaint=new WorkspacePaintBatch(LegacyWindow);
        if(metricsChanged)RememberPosition(this);
        using(var layout=new UiLayoutBatch(LegacyWindow))
        {
            UiLanguage.Set(settings.InterfaceLanguage);
            if(languageChanged)RefreshInterfaceDisplayNames();
            UiTheme.Apply(this,settings);if(metricsChanged)FontManager.ApplyUi(this,settings);
            UiLanguage.Preserve(_sessionCompactTitle);UiLanguage.BindTree(LegacyWindow);
            foreach(var button in _navigationButtons.Values)UiLanguage.Bind(button);
            if (!WpfWorkspace.Enabled) UiLanguage.Bind(_statusLabel);
            RefreshFusionChrome();if(!WpfWorkspace.Enabled)_settingsNavigation.Commit(_settingsTabs?.SelectedIndex??-1);
            ApplyHistoryScrollTheme();
        }
        if(metricsChanged)
        {
            if(_settingsTabs is not null)
                foreach(var page in _settingsTabs.TabPages)
                    if(page.Tag is SettingsContent content)content.FitTo(page);
            _workspaceCaption?.PerformLayout();
            ArrangeShotWorkspace(); if (!WpfWorkspace.Enabled) { LegacyShotLeft.PerformLayout(); LegacyShotRight.PerformLayout(); }
            RestorePosition(this);
        }
        Invalidate();_desktopAppearance=identity;
    }
    private void ReturnFromProfile(bool save)
    {
        if(_profileReturnPage is not { } page)return;
        if(save?!SaveProfileEditor():!ConfirmProfileLeave())return;
        ShowPage(page);
        if(_currentMainPage==page){_profileReturnPage=null;_profileDraft.Returning=false;if(!WpfWorkspace.Enabled)_profileReturnButton.Visible=_profileBackButton.Visible=false;}
    }
    private void ClearProfileReturn()
    {_profileReturnPage=null;_profileDraft.Returning=false;if(!WpfWorkspace.Enabled)_profileReturnButton.Visible=_profileBackButton.Visible=false;}
    private static void SetLabelText(Label label,string value){if(label.Text!=value)label.Text=value;}
}
