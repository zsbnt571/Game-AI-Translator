using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly App = null!;
    private static readonly List<string> Passed = [];
    private static readonly List<string> Errors = [];
    private static string Output = "";
    private static string Phase = "";
    private const string SyntheticKey = "alpha-local-test-placeholder";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("first" or "readback" or "invalid-root" or "cover")) throw new ArgumentException("Usage: AlphaReadiness.Tests first|readback|invalid-root|cover <absolute-evidence-directory>. Run only inside an isolated copy beside GameTranslator.dll.");
        Phase = args[0]; Output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Output);
        if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Source")) || Directory.Exists(Path.Combine(AppContext.BaseDirectory, ".git"))) throw new InvalidOperationException("Refusing to test a source checkout.");
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var key = (string)variable.Key;
            if (key.StartsWith("ST_") || key.StartsWith("FUSION_") || key.StartsWith("GAME_AI_") || key.StartsWith("PYTHON") || key == "SCREENSHOT_TRANSLATION_TESTER_CONFIG") Environment.SetEnvironmentVariable(key, null);
        }
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        App = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "GameTranslator.dll"));
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Errors.Add(e.Exception.ToString());
        var prepared = (string[])Call(Type("FusionRuntime"), "Prepare", (object)new[] { "--disable-global-input" })!;
        Call(Type("AppDataPaths"), "Initialize", (object)prepared);
        Call(Type("FusionRuntime"), "Initialize");
        if (Phase == "invalid-root") Environment.SetEnvironmentVariable("GAME_AI_TRANSLATOR_PAYLOAD_ROOT", "relative-invalid-fixture");
        string data = (string)Type("AppDataPaths").GetProperty("Root", Flags)!.GetValue(null)!;
        Check("normal bootstrap selects application-local data", data == Path.Combine(AppContext.BaseDirectory, "data"));
        Check("normal bootstrap selects application-local OCR runtime", (string)Type("FusionRuntime").GetProperty("Root", Flags)!.GetValue(null)! == Path.Combine(AppContext.BaseDirectory, "runtime"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "library-preferences.json"), "{\"AutomaticChineseNames\":false}");
        using var form = (Form)Activator.CreateInstance(Type("MainForm"), new object?[] { null })!;
        form.Opacity = 0; form.ShowInTaskbar = false;
        using var watchdog = new System.Windows.Forms.Timer { Interval = 60000 };
        watchdog.Tick += (_, _) => { Errors.Add("UI harness timed out"); Call(form, "ExitForSmoke"); };
        form.Shown += async (_, _) =>
        {
            watchdog.Start();
            try { await Exercise(form, data); }
            catch (Exception ex) { Errors.Add(ex.ToString()); }
            finally
            {
                watchdog.Stop();
                // Discard any failed synthetic draft so shutdown cannot wait on an unsaved-profile dialog.
                try { Call(form, "LoadEditedProfile"); } catch (Exception ex) { Errors.Add("Draft cleanup: " + ex); }
                Call(form, "ExitForSmoke");
            }
        };
        Application.Run(form);
        Check("normal form close completed", form.IsDisposed || !form.Visible);
        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).Select(a => a.Location).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        Check("no managed module loaded from original or candidate development directories", loaded.All(p => !p.Contains("\\Codex\\", StringComparison.OrdinalIgnoreCase) && !p.Contains("\\GameTranslator\\Work\\", StringComparison.OrdinalIgnoreCase)));
        File.WriteAllText(Path.Combine(Output, Phase + ".json"), JsonSerializer.Serialize(new { passed = Errors.Count == 0, checks = Passed.Count, Passed, Errors, loadedModules = loaded, applicationRoot = AppContext.BaseDirectory, apiCallsRequestedByHarness = 0, syntheticCredentialOnly = true, operatingSystemIsExistingHost = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{Phase}: {Passed.Count} PASS, {Errors.Count} failures");
        foreach (var error in Errors) Console.Error.WriteLine(error);
        return Errors.Count == 0 ? 0 : 1;
    }

    private static async Task Exercise(Form form, string data)
    {
        await Task.Delay(700);
        Check("main form starts with all external payloads and OCR absent", form.IsHandleCreated && !form.IsDisposed);
        var states = ((System.Collections.IEnumerable)Call(Type("RuntimePayloadProvider"), "GetOverview")!).Cast<object>().ToArray();
        var expectedDependencyState = Phase == "invalid-root" ? "Invalid" : "Missing";
        Check("all eight pinned local dependencies show " + expectedDependencyState, states.Length == 8 && states.All(x => Value(x, "State")!.ToString() == expectedDependencyState));
        Call(form, "ShowPage", "设置");
        var categories = (int)Value(form, "SettingsTabCountForSmoke")!;
        int profile = -1, dependency = -1;
        for (int index = 0; index < categories; index++)
        {
            string category = (string)Call(form, "SelectSettingsTabForSmoke", index)!;
            Check("settings page opens: " + category, !form.IsDisposed);
            if (category == "翻译方案") profile = index;
            if (category == "运行依赖") dependency = index;
        }
        Check("dependency page exists", dependency >= 0);
        Call(form, "SelectSettingsTabForSmoke", dependency);
        await (Task)Call(form, "RefreshRuntimeDependencyOverviewAsync")!;
        await Until(() => !Field<bool>(form, "_dependencyOverviewChecking"));
        Check("dependency overview visibly reports " + (Phase == "invalid-root" ? "Invalid" : "Missing"), Field<Label>(form, "_runtimeDependencyOverview").Text.Contains(Phase == "invalid-root" ? "Invalid" : "Missing"));
        Check("dependency overview identifies omitted cover helper", Field<Label>(form, "_runtimeDependencyOverview").Text.Contains("封面截图组件 — Not included in this alpha"));
        Check("unbundled cover helper reports unavailable", !(bool)Type("GameWindowCover").GetProperty("Available", Flags)!.GetValue(null)!);
        foreach (bool automatic in new[] { false, true })
        {
            var capture = (Task)Call(Type("GameWindowCover"), "CaptureAsync", "invalid-non-game-path", automatic, CancellationToken.None, null)!;
            Check((automatic ? "automatic" : "manual") + " missing cover helper returns before any window wait", capture.IsCompletedSuccessfully);
            await capture;
            var result = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
            Check((automatic ? "automatic" : "manual") + " capture reports explicit alpha omission", Value(result, "Image") is null && ((string)Value(result, "Message")!).Contains("Not included in this alpha") && ((string)Value(result, "Diagnostic")!).Contains("code=helper-missing"));
        }
        if (Phase == "invalid-root")
        {
            Check("invalid explicit root keeps status and retry controls available", Descendants(form).Any(c => c.Name == "CheckRuntimeDependencies" && c.Visible));
            SavePage(form, "dependency-page.png");
            Call(form, "ShowPage", "内嵌翻译");
            Check("invalid explicit root does not prevent library navigation", (string)Value(form, "CurrentMainPageForSmoke")! == "内嵌翻译");
            return;
        }
        SavePage(form, "dependency-page.png");
        Call(form, "ShowPage", "主页");
        await (Task)Call(form, "RefreshOcrAvailabilityAsync")!;
        Check("Screenshot Translator page opens without OCR", (string)Value(form, "CurrentMainPageForSmoke")! == "主页");
        Check("OCR unavailable state is visible", Field<Label>(form, "_homeOcrStatus").Text.Contains("Missing"));
        Check("OCR-dependent capture/import actions disabled", !Field<Button>(form, "_screenshotCaptureButton").Enabled && !Field<Button>(form, "_screenshotImportButton").Enabled);
        SavePage(form, "screenshot-page.png");
        Call(form, "ShowPage", "内嵌翻译");
        Check("game library opens", (string)Value(form, "CurrentMainPageForSmoke")! == "内嵌翻译");
        var fixtureRoot = Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!, "fixtures");
        string mono = CreateFixture(fixtureRoot, "MonoFixture", true), unknown = CreateFixture(fixtureRoot, "UnknownFixture", false);
        if (Phase is "first" or "cover")
        {
            await (Task)Call(form, "ImportLibraryPathsAsync", new[] { mono }, false)!;
            Check("EXE added to library", await LibraryContains(form, mono));
            var target = Descendants(form).First(c => c.AllowDrop && !c.IsDisposed);
            var payload = new DataObject(DataFormats.FileDrop, new[] { unknown });
            var drop = new DragEventArgs(payload, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);
            typeof(Control).GetMethod("OnDragDrop", Flags)!.Invoke(target, new object[] { drop });
            await UntilAsync(() => LibraryContains(form, unknown));
            Check("real library file-drop handler imports second EXE", await LibraryContains(form, unknown));
        }
        else Check("library entries survive restart", await LibraryContains(form, mono) && await LibraryContains(form, unknown));
        await (Task)Call(form, "SelectFusionGameAsync", mono, null, false)!;
        var game = Field<object>(form, "_selectedGame");
        Check("engine recognition uses actual synthetic Mono layout", (string)Value(game, "AdapterId")! == "unity-mono");
        Call(form, "OpenGameSession", game, false, false);
        Call(form, "RenderGameState");
        await Until(() => Field<object?>(form, "_selectedDependenciesReady") is bool);
        Check("selected engine shows Missing before installation", Field<Label>(form, "_selectedRuntimeDependency").Text.Contains("Missing"));
        Check("missing runtime disables new install/translation launch", !Field<Button>(form, "_installGameButton").Enabled && !Field<Button>(form, "_launchGameButton").Enabled);
        var before = Snapshot(Path.GetDirectoryName(mono)!);
        bool denied = false;
        try { Call(Type("UnityEmbeddedAdapter"), "Install", mono); }
        catch (Exception ex) { denied = Unwrap(ex).Message.Contains("依赖") || Unwrap(ex).Message.Contains("Missing"); }
        Check("actual installer rejects missing payload", denied);
        Check("failed installation leaves every fixture byte unchanged", before.SequenceEqual(Snapshot(Path.GetDirectoryName(mono)!)));
        await ExerciseCoverAvailability(form, mono);
        if (Phase == "cover") return;

        File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "Selecting translation settings after artwork checks\n");
        Call(form, "SelectSettingsTabForSmoke", profile);
        File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "Translation settings selected\n");
        if (Phase == "first")
        {
            Field<TextBox>(form, "_apiUrlBox").Text = "http://127.0.0.1:9/v1";
            Field<TextBox>(form, "_apiKeyBox").Text = SyntheticKey;
            Field<TextBox>(form, "_modelBox").Text = "offline-test-model";
            Field<TextBox>(form, "_schemeName").Text = "Local validation";
            var target = Field<ComboBox>(form, "_targetLanguageBox"); target.SelectedIndex = Math.Min(1, target.Items.Count - 1);
            var draft = Call(form, "ReadProfileDraft")!;
            Check("synthetic profile is complete before the real save action", !((System.Collections.IEnumerable)Call(Type("FusionConfiguration"), "Missing", draft)!).Cast<object>().Any());
            Check("real profile save button is available", Field<Button>(form, "_saveProfileButton").Enabled && Field<Button>(form, "_saveProfileButton").Visible);
            File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "Saving synthetic local translation profile\n");
            Field<Button>(form, "_saveProfileButton").PerformClick();
            string profileSaveStatus = Field<Label>(form, "_profileSaveStatus").Text;
            Check("API settings save through real button (" + profileSaveStatus + ")", profileSaveStatus.StartsWith("保存成功"));
            Call(form, "SaveSettings", true);
            var current = Value(form, "CurrentSettings")!;
            File.WriteAllText(Path.Combine(Output, "expected-language.txt"), (string)Value(current, "TargetLanguage")!);
        }
        var effective = Value(form, "CurrentSettings")!;
        Check("saved synthetic API key is readable by this local user", (string)Value(effective, "ApiKey")! == SyntheticKey);
        Check("target language persists", (string)Value(effective, "TargetLanguage")! == File.ReadAllText(Path.Combine(Output, "expected-language.txt")));
        Check("no plaintext synthetic key in saved JSON", Directory.GetFiles(data, "*.json").All(p => !File.ReadAllText(p).Contains(SyntheticKey)));
        Check("DPAPI-protected credential exists", File.ReadAllText(Path.Combine(data, "fusion-settings.json")).Contains("dpapi:v1:"));
        Check("no original development paths in saved configuration", Directory.GetFiles(data, "*.json").All(p => !File.ReadAllText(p).Contains("\\\\Codex\\\\", StringComparison.OrdinalIgnoreCase) && !File.ReadAllText(p).Contains("ST-Release", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task ExerciseCoverAvailability(Form form, string gamePath)
    {
        var task = (Task)Call(form, "RecentStore")!; await task;
        var store = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var record = Call(store, "Find", gamePath)!;
        File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "Opening isolated artwork dialog\n");
        using var dialog = (Form)Activator.CreateInstance(Type("GameArtworkDialog"), Flags, null,
            new object[] { store, record, Field<object>(form, "_appliedDesktopSettings") }, null)!;
        File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "Artwork dialog constructed\n");
        dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(form);
        File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "Artwork dialog shown\n");
        var capture = Descendants(dialog).OfType<Button>().Single(c => c.Name == "CoverCaptureAction");
        var explanation = Descendants(dialog).OfType<Label>().Single(c => c.Name == "CoverCaptureAvailability");
        Check("omitted helper disables manual cover capture", !capture.Enabled);
        Check("manual cover capture has visible alpha omission", explanation.Visible && explanation.Text.Contains("Not included in this alpha"));
        Check("local image selection remains available", Descendants(dialog).OfType<Button>().Single(c => c.Text == "从文件选择").Enabled);
        await (Task)Call(dialog, "Run", (Func<Task>)(() => Task.CompletedTask))!;
        Check("finishing other artwork actions does not re-enable unavailable capture", !capture.Enabled);
        string helper = (string)Type("GameWindowCover").GetProperty("HelperPath", Flags)!.GetValue(null)!;
        if (File.Exists(helper)) throw new InvalidOperationException("Refusing to overwrite a supplied cover helper.");
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        try
        {
            // Inert presence sentinel; never launched or shipped. Tests only the existing File.Exists boundary.
            File.WriteAllText(helper, "inert cover helper availability fixture; never executed");
            Call(dialog, "RefreshCaptureAvailability");
            Check("locally supplied helper preserves manual capture availability", capture.Enabled && !explanation.Visible);
        }
        finally { File.Delete(helper); }
        Call(dialog, "RefreshCaptureAvailability");
        Check("removing local helper returns manual capture to unavailable", !capture.Enabled && explanation.Visible);
        Check("availability checks do not mark artwork dirty", !Field<bool>(dialog, "dirty"));
        dialog.Close();
        File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "Artwork dialog closed\n");
    }

    private static string CreateFixture(string root, string name, bool mono)
    {
        string directory = Path.Combine(root, name); Directory.CreateDirectory(directory);
        string exe = Path.Combine(directory, name + ".exe");
        if (!File.Exists(exe))
        {
            byte[] bytes = new byte[512]; using var stream = new MemoryStream(bytes); using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0x5a4d); stream.Position = 60; writer.Write(128); stream.Position = 128; writer.Write(0x4550); writer.Write((ushort)0x8664);
            File.WriteAllBytes(exe, bytes);
        }
        if (mono)
        {
            string managed = Path.Combine(directory, name + "_Data", "Managed"); Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(managed, "Assembly-CSharp.dll"), "inert test sentinel; not a game assembly");
            File.WriteAllText(Path.Combine(directory, "UnityPlayer.dll"), "inert detection sentinel; never loaded");
            File.WriteAllText(Path.Combine(directory, name + "_Data", "globalgamemanagers"), "2021.3.0f1");
        }
        return exe;
    }

    private static async Task<bool> LibraryContains(Form form, string exe)
    {
        var task = (Task)Call(form, "RecentStore")!; await task;
        var store = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return Call(store, "Find", exe) is not null;
    }
    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p).Select(p => Path.GetRelativePath(root, p) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    private static async Task Until(Func<bool> predicate) { for (int n = 0; n < 100; n++) { if (predicate()) return; await Task.Delay(50); } throw new TimeoutException("Expected UI completion did not arrive."); }
    private static async Task UntilAsync(Func<Task<bool>> predicate) { for (int n = 0; n < 100; n++) { if (await predicate()) return; await Task.Delay(50); } throw new TimeoutException("Expected async UI completion did not arrive."); }
    private static IEnumerable<Control> Descendants(Control root) { yield return root; foreach (Control child in root.Controls) foreach (var nested in Descendants(child)) yield return nested; }
    private static void SavePage(Form form, string name) { using var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height); Call(form, "DrawCurrentPageForSmoke", image); image.Save(Path.Combine(Output, Phase + "-" + name)); }
    private static Type Type(string name) => App.GetType("ScreenshotTranslationUiTester." + name, true)!;
    private static object? Call(object target, string name, params object?[] args)
    {
        var matches = (target as Type ?? target.GetType()).GetMethods(Flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length).ToArray();
        if (matches.Length != 1) throw new MissingMethodException($"Expected one method {name} with {args.Length} arguments; found {matches.Length}.");
        return matches[0].Invoke(target is Type ? null : target, args);
    }
    private static object? Value(object target, string name) => target.GetType().GetProperty(name, Flags)!.GetValue(target);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static Exception Unwrap(Exception exception) => exception is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : exception;
    private static void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException("FAIL: " + name); Passed.Add(name); File.AppendAllText(Path.Combine(Output, Phase + "-progress.txt"), "PASS: " + name + "\n"); }
}
