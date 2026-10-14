using System.Reflection;
using System.Windows.Forms;
using ScreenshotTranslationUiTester;
using W = System.Windows;
using WC = System.Windows.Controls;
static class Program
{
    [STAThread] static int Main(string[] args)
    {
        string output=Path.GetFullPath(args[0]);Directory.CreateDirectory(output);
        AppDataPaths.Initialize(["--data-root="+Path.Combine(output,"data"),"--verify-data-root","--disable-global-input"]);
        var checks=new List<object>();int failures=0;
        void Check(string name,bool passed){checks.Add(new{name,passed});if(!passed)failures++;}
        var resources=WpfThemeResources.Create();WpfThemeResources.Apply(resources);
        Check("Native control templates parse successfully",resources.Count>5);
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using(var fields=new WpfSettingsFields())
        using(var parent=new Panel{Visible=false})
        {
            var check=new CheckBox{Text="Synthetic choice",Checked=true};parent.Controls.Add(check);
            var view=(WC.CheckBox)fields.Create(check);
            Check("Hidden model ancestor does not hide native field",view.Visibility==W.Visibility.Visible);
            view.IsChecked=false;Check("Native checkbox edits original model",!check.Checked);
            check.Checked=true;Check("Original model updates native checkbox",view.IsChecked==true);
            check.Visible=false;fields.Refresh();Check("Explicitly hidden fields stay hidden",view.Visibility==W.Visibility.Collapsed);
            int clicks=0;var action=new Button{Text="Existing action"};parent.Controls.Add(action);action.Click+=(_,_)=>clicks++;
            var button=(WC.Button)fields.Create(action);button.RaiseEvent(new(WC.Button.ClickEvent));Check("Hidden model action called exactly once",clicks==1);
            action.Enabled=false;button.RaiseEvent(new(WC.Button.ClickEvent));Check("Disabled action not invoked",clicks==1);
            var combo=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};combo.Items.AddRange(["Chinese","English"]);combo.SelectedIndex=0;
            var selection=(WC.ComboBox)fields.Create(combo);selection.SelectedIndex=1;Check("Native selection edits shared model",combo.SelectedIndex==1);
            combo.SelectedIndex=0;Check("Model selection updates view",selection.SelectedIndex==0);
            combo.Items.Add("退出程序"); UiLanguage.Set(ApplicationLanguage.English); fields.Refresh();
            Check("Native combo localizes labels without rewriting values",selection.Items[2]?.ToString()=="Exit the app" && combo.Items[2]?.ToString()=="退出程序");
            UiLanguage.Set(ApplicationLanguage.SimplifiedChinese); fields.Refresh();
            var text=new TextBox{Text="Initial"};var editor=(WC.TextBox)fields.Create(text);editor.Text="Edited";Check("Text binding writes to original field",text.Text=="Edited");
            text.Text="Reloaded";Check("Model reload replaces native text",editor.Text=="Reloaded");
            var secret=new TextBox{UseSystemPasswordChar=true,Text="synthetic-secret-not-a-key"};
            var protectedView=(WC.Grid)fields.Create(secret);var password=protectedView.Children.OfType<WC.PasswordBox>().Single();var revealed=protectedView.Children.OfType<WC.TextBox>().Single();
            Check("Secret uses protected password control",password.Password==secret.Text&&revealed.Text.Length==0&&revealed.Visibility==W.Visibility.Collapsed);
            Check("Secret is not an automation name",!W.Automation.AutomationProperties.GetName(protectedView).Contains(secret.Text));
            secret.UseSystemPasswordChar=false;fields.Refresh();Check("Explicit reveal retains existing behavior",revealed.Text==secret.Text&&password.Visibility==W.Visibility.Collapsed);
            secret.UseSystemPasswordChar=true;fields.Refresh();Check("Remasking clears exposed text",revealed.Text.Length==0);
            var numeric=new NumericUpDown{Minimum=1,Maximum=5,Value=4};var frame=(WC.Border)fields.Create(numeric);var row=(WC.Grid)frame.Child;
            var plus=row.Children.OfType<WC.Button>().Last();plus.RaiseEvent(new(WC.Button.ClickEvent));Check("Numeric step updates bounded value",numeric.Value==5);
            var number=row.Children.OfType<WC.TextBox>().Single();number.Text="999";Check("Invalid numeric input does not overwrite settings",numeric.Value==5);
            Check("Fresh install language defaults to Chinese",new ApiSettings().InterfaceLanguage==ApplicationLanguage.SimplifiedChinese);
            var bindingModel=new InputBindingCaptureButton(InputBindingDefaults.StartCapture){Width=60};
            var bindingView=(System.Windows.Forms.Integration.WindowsFormsHost)fields.Create(bindingModel);
            Check("Key editor keeps space for a full combination",bindingView.Width>=190);
        }
        WpfWorkspace.Enabled=true;
        using(var captureMain=new MainForm(Path.Combine(AppDataPaths.Root,"settings.json")))
        {
            var window=new WpfWorkspaceWindow(captureMain);
            var windowHandle=new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            Check("Dialogs and capture resolve the WPF top-level window",((IWin32Window)captureMain).Handle==windowHandle && captureMain.Handle!=windowHandle);
            NativeMethods.GetWindowDisplayAffinity(windowHandle,out var before);
            bool excluded=TemporaryWindowCaptureExclusion.TryBegin([captureMain],out var scope,out var error);
            Check("Capture exclusion applies to WPF HWND",excluded && NativeMethods.GetWindowDisplayAffinity(windowHandle,out var during) && during==NativeMethods.WdaExcludeFromCapture);
            scope?.Dispose();
            Check("Capture affinity is restored exactly",scope?.RestoreSucceeded==true && NativeMethods.GetWindowDisplayAffinity(windowHandle,out var after) && after==before);
            window.Show(); window.UpdateLayout();
            captureMain.WindowState=FormWindowState.Minimized;
            using(var preview=new Form())
            {
                WindowStateGuard.ShowPreviewWithoutRestoringMain(captureMain,preview);
                Check("Preview does not restore the minimized WPF window",window.WindowState==W.WindowState.Minimized && preview.Visible);
            }
            captureMain.RestoreFromExternalLaunch();
            Check("External activation restores the native window",window.IsVisible && window.WindowState==W.WindowState.Normal);
            captureMain.TrayHideForSmoke();
            Check("Tray hide affects the native window",!window.IsVisible && !window.ShowInTaskbar);
            captureMain.RestoreFromExternalLaunch();
            Check("Tray restore retains the native window",window.IsVisible && window.ShowInTaskbar);
            window.Close();
        }
        using(var workspaceMain=new MainForm(Path.Combine(AppDataPaths.Root,"settings.json")))
        {
            using var detail=new WpfGameSessionWorkspace(workspaceMain);
            Check("Game detail and retained engine editors construct",detail.Content is WC.Panel);
            using var screenshots=new WpfScreenshotWorkspace(workspaceMain);
            Check("Screenshot workspace retains both viewer panes",screenshots.Children.Count>0 && workspaceMain.WpfShotLeft.Parent is not null && workspaceMain.WpfShotRight.Parent is not null);
            workspaceMain.WpfShotSplit=64;Check("Native screenshot layout shares split persistence",workspaceMain.ScreenshotWorkspaceStateForSmoke().SplitPercent==64);
        }
        using(var main=new MainForm(Path.Combine(AppDataPaths.Root,"settings.json")))
        {
            var inventory=new List<object>();
            for(int i=0;i<10;i++)
            {
                var root=main.WpfSettingsContent(i);
                var sections=root.Controls.OfType<SettingsSection>().ToArray();
                using var fields=new WpfSettingsFields();
                foreach(var section in sections)foreach(var field in section.Fields)fields.Create(field.value);
                inventory.Add(new{category=i,name=main.WpfSettingsNames[i],sections=sections.Length,fields=fields.NativeFields,hosted=fields.HostedTypes});
                Check("Settings category "+i+" retains all field handlers",sections.Length>0&&fields.NativeFields>0);
            }
            File.WriteAllText(Path.Combine(output,"settings-inventory.json"),System.Text.Json.JsonSerializer.Serialize(inventory,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        }
        File.WriteAllText(Path.Combine(output,"results.json"),System.Text.Json.JsonSerializer.Serialize(new{passed=failures==0,checks},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"{(failures==0?"PASS":"FAIL")} {checks.Count-failures}/{checks.Count}");return failures==0?0:1;
    }
}
