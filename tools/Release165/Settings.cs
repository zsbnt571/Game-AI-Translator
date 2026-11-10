using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using ScreenshotTranslationUiTester;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;
using I = System.Windows.Media.Imaging;

static class SettingsAcceptance
{
    static IEnumerable<W.DependencyObject> Walk(W.DependencyObject root)
    { yield return root; foreach (var c in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>()) foreach (var v in Walk(c)) yield return v; }
    static T Id<T>(W.DependencyObject root, string id) where T : W.DependencyObject
        => Walk(root).OfType<T>().Single(x => W.Automation.AutomationProperties.GetAutomationId(x) == id);
    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    static void Click(C.Button button) { button.RaiseEvent(new W.RoutedEventArgs(C.Button.ClickEvent)); Pump(); }
    static W.Rect Bounds(W.FrameworkElement item, W.FrameworkElement root) => item.TransformToAncestor(root).TransformBounds(new W.Rect(0, 0, item.ActualWidth, item.ActualHeight));
    static void Shot(W.FrameworkElement view, string path)
    {
        var bitmap = new I.RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, M.PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new I.PngBitmapEncoder(); encoder.Frames.Add(I.BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    [STAThread] static int Main(string[] args)
    {
        string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var checks = new List<object>(); var overflow = new List<object>(); int failures = 0; string? error = null;
        void Check(bool passed, string name) { checks.Add(new { name, passed }); if (!passed) { failures++; Console.WriteLine("FAIL " + name); } }
        AppDataPaths.Initialize(["--data-root=" + Path.Combine(output, "data"), "--verify-data-root", "--disable-global-input"]);
        WpfWorkspace.Enabled = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var app = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(WpfThemeResources.Create()); WpfThemeResources.Apply(app.Resources);
        UiLanguage.Set(ApplicationLanguage.SimplifiedChinese);
        string settingsFile = Path.Combine(AppDataPaths.Root, "settings.json");
        try
        {
            using (var main = new MainForm(settingsFile))
            using (var workspace = new WpfSettingsWorkspace(main))
            {
                var surface = new C.Grid(); surface.SetResourceReference(C.Panel.BackgroundProperty, "Fusion.Main"); surface.Children.Add(workspace);
                var window = new W.Window { Content = surface, Width = 1100, Height = 760, ShowInTaskbar = false, WindowStyle = W.WindowStyle.None };
                window.Show(); Pump();
                void Page(int category) { main.NativeSelectSettings(category); workspace.Refresh(); window.UpdateLayout(); Pump(); }
                void Size(double width) { window.Width = width; window.Height = 760; window.UpdateLayout(); Pump(); }
                Page(0);
                Check(Walk(workspace).OfType<C.Button>().Count(x => W.Automation.AutomationProperties.GetAutomationId(x).StartsWith("SettingsCategory_")) == 11, "All eleven settings categories remain reachable");
                Check(Walk(workspace).OfType<C.TextBlock>().Count(x => W.Automation.AutomationProperties.GetAutomationId(x).StartsWith("SettingsGroup_")) == 4, "Settings navigation has four purpose-based groups");
                var rowOrder = Walk(workspace).OfType<C.Button>().Select(x => W.Automation.AutomationProperties.GetAutomationId(x)).Where(x => x.StartsWith("SettingsCategory_")).ToArray();
                Check(rowOrder.SequenceEqual(new[] {0,1,2,3,4,5,7,6,8,10,9}.Select(i => "SettingsCategory_" + i)), "Reordered categories preserve stable page identifiers");
                foreach (var theme in new[] { ApplicationThemeMode.Day, ApplicationThemeMode.Night })
                {
                    using var themeOwner = new System.Windows.Forms.Form(); UiTheme.Apply(themeOwner, new ApiSettings { ThemeMode = theme }); WpfThemeResources.Apply(app.Resources);
                    foreach (double width in new[] { 800d, 1100d, 1600d })
                    {
                        Size(width);
                        foreach (int page in Enumerable.Range(0, 11))
                        {
                            Page(page); var scroll = Id<C.ScrollViewer>(workspace, "SettingsPage_" + page); scroll.ScrollToTop(); window.UpdateLayout(); Pump();
                            var controls = Walk(scroll).OfType<C.Control>().Where(x => x.IsVisible && x.ActualWidth > 0 && x is C.TextBox or C.PasswordBox or C.ComboBox or C.Button).ToArray();
                            var clipped = controls.Select(c => (c, box: Bounds(c, scroll))).Where(x => x.box.Left < -1 || x.box.Right > scroll.ActualWidth + 1).ToArray();
                            foreach (var item in clipped) overflow.Add(new { theme, width, page, id = W.Automation.AutomationProperties.GetAutomationId(item.c), type = item.c.GetType().Name, left = item.box.Left, right = item.box.Right, available = scroll.ActualWidth });
                            Check(clipped.Length == 0, $"{theme} {width}px category {page}: fields stay inside the page");
                            Check(Id<C.Button>(workspace, "SaveDesktopSettings").IsVisible == (page is 0 or 1 or 2 or 4 or 5 or 6), $"{theme} {width}px category {page}: save action matches page behavior");
                            if (page == 3)
                            {
                                var saveBox = Bounds(Id<C.Button>(workspace, "SaveProfile"), workspace);
                                Check(saveBox.Top >= 0 && saveBox.Bottom <= workspace.ActualHeight && saveBox.Right <= workspace.ActualWidth, $"{theme} {width}px: API save stays visible without scrolling");
                            }
                            if (width == 1100 && page is 0 or 3 or 8 || width == 800 && page == 3) Shot(surface, Path.Combine(output, $"settings-{theme}-{page}-{width}.png"));
                        }
                    }
                }
                Size(1100); Page(3);
                var model = Id<C.TextBox>(workspace, "ProfileModel"); model.Text = "fixture-model";
                Id<C.TextBox>(workspace, "ApiUrl").Text = "https://example.invalid/v1/chat/completions";
                Id<C.PasswordBox>(workspace, "ApiKey").Password = "synthetic-local-key";
                Id<C.TextBox>(workspace, "ProfileName").Text = "Settings 162 fixture";
                Id<C.CheckBox>(workspace, "AdvancedApiToggle").IsChecked = true;
                Id<C.TextBox>(workspace, "AdvancedApiParameters").Text = """{"reasoning_effort":"none"}""";
                string connectionBefore = main.ProfileDraft.Snapshot().AdvancedApiParameters;
                Click(Id<C.Button>(workspace, "SettingsProfileTab_2")); window.UpdateLayout();
                Check(!model.IsVisible && Id<C.Button>(workspace, "SaveTranslationContent").IsVisible, "Translation-content tab hides connection fields and shows its own save action");
                Id<C.TextBox>(workspace, "CustomTranslationPrompt").Text = "Keep this unsaved draft while switching tabs.";
                Click(Id<C.Button>(workspace, "SettingsProfileTab_0"));
                Check(!Walk(workspace).OfType<C.Button>().Any(x => x.IsVisible && W.Automation.AutomationProperties.GetAutomationId(x) is "SaveTranslationContent" or "SaveProfile"), "Defaults tab does not advertise an unrelated save action");
                Click(Id<C.Button>(workspace, "SettingsProfileTab_1"));
                Check(model.IsVisible && model.Text == "fixture-model" && main.ProfileDraft.ApiKey == "synthetic-local-key" && main.ProfileDraft.AdvancedParameters == connectionBefore, "Switching tabs retains model, masked key and thinking parameters");
                Check(main.ContentDraft.Prompt == "Keep this unsaved draft while switching tabs.", "Unsaved translation content survives tab switches");
                Click(Id<C.Button>(workspace, "SaveProfile"));
                Check(main.ProfileDraft.SaveStatus.StartsWith("保存成功"), "Existing API profile save still validates and reloads the complete profile");
                Check(main.ContentDraft.Prompt == "Keep this unsaved draft while switching tabs.", "Saving an API profile preserves the independent translation draft");
                Click(Id<C.Button>(workspace, "SettingsProfileTab_2"));
                Click(Id<C.Button>(workspace, "SaveTranslationContent"));
                Check(main.ContentDraft.Status.Contains("已保存"), "Translation content saves independently");
                Page(1); main.ProfileDraft.Returning = true; Page(3);
                Check(Id<C.TextBox>(workspace, "ApiUrl").IsVisible, "Game-to-settings return flow opens connection tab");
                main.ProfileDraft.Returning = false;
                Page(8); var disclosure = Id<C.Expander>(workspace, "SettingsDataMigrationDisclosure");
                Check(!disclosure.IsExpanded && !Id<C.TextBox>(workspace, "MigrationSource").IsVisible, "Migration fields are initially folded without removing them");
                disclosure.IsExpanded = true; window.UpdateLayout(); Pump();
                Check(Id<C.TextBox>(workspace, "MigrationSource").IsVisible && Id<C.Button>(workspace, "MigrationInspect").IsVisible, "Expanding migration restores source input and check action");
                Page(1);
                var close = Id<C.ComboBox>(workspace, "CloseMainWindowBehavior");
                close.SelectedIndex = 0;
                Page(4); Page(1);
                Check(close.SelectedIndex == 0, "Pending desktop preference survives category navigation");
                Click(Id<C.Button>(workspace, "SaveDesktopSettings"));
                Check(main.NativeSettingsStatus == "设置已保存", "Global desktop save remains functional");
                window.Close(); Pump();
            }
            using (var reopened = new MainForm(settingsFile))
            {
                reopened.NativePrepareProfilePage();
                Check(reopened.ProfileDraft.Model == "fixture-model" && reopened.ProfileDraft.ApiKey == "synthetic-local-key" && reopened.ProfileDraft.AdvancedParameters.Contains("none"), "Restart restores saved connection, key and disabled-thinking parameter");
                Check(reopened.ContentDraft.Prompt == "Keep this unsaved draft while switching tabs.", "Restart restores saved translation content");
                Check(reopened.DesktopDraft.Get<CloseMainWindowBehavior>(nameof(ApiSettings.CloseMainWindowBehavior)) == (CloseMainWindowBehavior)0, "Restart restores desktop preferences");
            }
        }
        catch (Exception ex) { failures++; error = ex.ToString(); Console.WriteLine(error); }
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MainForm).Assembly.Location)));
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { checks, failures, error, overflow, assemblySha256 = hash, apiRequests = 0, actualGamesLaunched = 0, scope = "Isolated native settings controls, layout, draft preservation and persistence; synthetic credentials only." }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}
