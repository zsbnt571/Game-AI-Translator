using GameAiTranslator.Runtime;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private readonly Label _runtimeDependencyOverview = new() { AutoSize = true, MaximumSize = new(850, 0), Name = "RuntimeDependencyOverview" };
    private readonly Label _selectedRuntimeDependency = new() { AutoSize = true, MaximumSize = new(620, 0), Name = "SelectedRuntimeDependency" };
    private string? _dependencyGameIdentity;
    private bool? _selectedDependenciesReady;
    private bool _dependencyOverviewChecking;
    private Button? _screenshotCaptureButton, _screenshotImportButton;
    private bool _ocrAvailabilityChecking;

    private TableLayoutPanel BuildRuntimeDependencySettings()
    {
        var table = SettingsTable();
        AddSettingRow(table, "本地运行依赖", Info("依赖是可选的。缺失时仍可使用设置和游戏库；对应安装或 OCR 功能暂不可用。不会自动下载，也不会搜索其他目录。"));
        string payloadRoot;
        try { payloadRoot = RuntimePayloadProvider.ResolveRoot(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { payloadRoot = "Invalid：" + SafeDiagnosticOutput.ExceptionSummary(ex); }
        AddSettingRow(table, "引擎依赖目录", Info(payloadRoot));
        AddSettingRow(table, "OCR 目录", Info(FusionRuntime.Root));
        var refresh = MakeButton("检查本地依赖", Point.Empty, new(150, 36));
        refresh.Name = "CheckRuntimeDependencies";
        refresh.Click += async (_, _) => await RefreshRuntimeDependencyOverviewAsync();
        AddSettingRow(table, "", refresh);
        _runtimeDependencyOverview.Text = "正在检查本地依赖…";
        AddSettingRow(table, "状态", _runtimeDependencyOverview);
        AddSettingRow(table, "状态说明", Info("Ready：校验通过；Missing：缺失；Invalid version：版本错误；Wrong architecture：位数错误；Wrong backend：后端错误；Hash mismatch：内容不符；Not required：当前功能不需要。\n依赖校验不代表所有真实游戏均兼容。恢复已有安装不要求运行 ZIP 仍然存在。"));
        table.VisibleChanged += async (_, _) => { if (table.Visible) await RefreshRuntimeDependencyOverviewAsync(); };
        return table;
    }

    private async Task RefreshRuntimeDependencyOverviewAsync()
    {
        if (_dependencyOverviewChecking || IsDisposed) return;
        _dependencyOverviewChecking = true;
        try
        {
            var engines = await Task.Run(() => RuntimePayloadProvider.GetOverview());
            var ocr = await OcrDependencyChecker.CheckAsync(AppContext.BaseDirectory, CancellationToken.None, FusionRuntime.Root);
            if (IsDisposed || Disposing) return;
            _runtimeDependencyOverview.Text = string.Join("\n\n", engines.Select(x =>
                $"{x.DisplayName} — {DependencyStateText(x.State)}\n{x.Message}\n{x.ExpectedPath}"))
                + $"\n\nOCR — {ocr.State}\n{ocr.Detail}"
                + "\n\nUnreal 文本目录工具 — " + (File.Exists(UnrealCatalogPath) ? "已提供（运行时仍需校验本地依赖）" : "Missing：本版未包含此可选工具；Unreal 资源提取不可用。")
                + "\n\n封面截图组件 — " + (GameWindowCover.Available ? "已提供" : GameWindowCover.MissingMessage);
            _dependencyGameIdentity = null;
            ApplyOcrAvailability(ocr);
            RenderGameState();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _runtimeDependencyOverview.Text = "依赖检查未完成：" + SafeDiagnosticOutput.ExceptionSummary(ex);
        }
        finally { _dependencyOverviewChecking = false; }
    }

    private static string UnrealCatalogPath => Path.Combine(AppContext.BaseDirectory, "tools", "unreal-catalog", "FusionUnrealCatalog.exe");
    private static string DependencyStateText(DependencyState state) => state.ToString() switch
    {
        "InvalidVersion" => "Invalid version", "WrongArchitecture" => "Wrong architecture",
        "WrongBackend" => "Wrong backend", "HashMismatch" => "Hash mismatch", "NotRequired" => "Not required",
        var value => value
    };

    private Control BuildSelectedGameDependencyStatus()
    {
        var row = WorkspaceColumn();
        WorkspaceAdd(row, _selectedRuntimeDependency);
        var refresh = IconButton("检查运行依赖", "refresh", (_, _) =>
        { _dependencyGameIdentity = null; RenderGameState(); }, false, 145);
        refresh.Name = "CheckSelectedGameDependencies";
        WorkspaceAdd(row, refresh);
        return row;
    }

    private static DependencyStatus[] RequiredGameDependencies(GameInfo game)
    {
        string? id = game.AdapterId switch
        {
            "mgi" => "unity-legacy", "cloud-meadow" => "unity-specialized",
            "unity-mono" => game.Architecture.Contains("32") || game.Architecture == "x86" ? "unity-mono-x86" : "unity-mono-x64",
            "unity-il2cpp" => "unity-il2cpp-x64", "unreal" => "unreal-runtime", _ => null
        };
        return id is null ? [] : [RuntimePayloadProvider.Check(id)];
    }

    private void ApplyOcrAvailability(OcrDependencyReport report)
    {
        if (IsDisposed) return;
        if (_screenshotCaptureButton is not null) _screenshotCaptureButton.Enabled = report.Ready;
        if (_screenshotImportButton is not null) _screenshotImportButton.Enabled = report.Ready;
        if (_homeOcrStatus is not null) _homeOcrStatus.Text = $"OCR — {report.State}：{report.Detail}";
    }

    private async Task RefreshOcrAvailabilityAsync()
    {
        if (_ocrAvailabilityChecking || IsDisposed) return;
        _ocrAvailabilityChecking = true;
        try { ApplyOcrAvailability(await OcrDependencyChecker.CheckAsync(AppContext.BaseDirectory, CancellationToken.None, FusionRuntime.Root)); }
        catch (Exception ex) { if (!IsDisposed && _homeOcrStatus is not null) _homeOcrStatus.Text = "OCR — CheckFailed：" + SafeDiagnosticOutput.ExceptionSummary(ex); }
        finally { _ocrAvailabilityChecking = false; }
    }

    private void ApplySelectedGameDependencyState(GameInfo? game)
    {
        if (game is null) { _selectedRuntimeDependency.Text = "Not required：请选择游戏查看依赖。"; return; }
        string identity = game.ExePath + "|" + game.AdapterId + "|" + game.Architecture;
        if (_dependencyGameIdentity != identity)
        {
            _dependencyGameIdentity = identity;
            _selectedDependenciesReady = null;
            _selectedRuntimeDependency.Text = "正在检查运行依赖…";
            _ = RefreshSelectedGameDependenciesAsync(game, identity);
        }
        if (_selectedDependenciesReady != true)
        {
            if (_installGameButton is not null) _installGameButton.Enabled = false;
            if (_launchGameButton is not null) _launchGameButton.Enabled = false;
        }
        // Do not gate plain launch, an existing live connection, or restore actions.
    }

    private async Task RefreshSelectedGameDependenciesAsync(GameInfo game, string identity)
    {
        try
        {
            var states = await Task.Run(() => RequiredGameDependencies(game));
            if (IsDisposed || Disposing || _dependencyGameIdentity != identity) return;
            _selectedDependenciesReady = states.All(x => x.State is DependencyState.Ready or DependencyState.NotRequired);
            _selectedRuntimeDependency.Text = states.Length == 0 ? "Not required：此适配不需要外部运行 ZIP。" :
                string.Join("\n", states.Select(x => $"{x.DisplayName} — {DependencyStateText(x.State)}：{x.Message}"));
            if (game.AdapterId == "unreal" && !File.Exists(UnrealCatalogPath))
            {
                // The existing adapter can reuse an already valid resource catalogue.
                // Do not block that path or restore with a new global preflight.
                _selectedRuntimeDependency.Text += "\nUnreal 文本目录工具 — Missing：无法新建资源目录；已有有效目录仍由原安装器判断。";
            }
        }
        catch (Exception ex)
        {
            if (IsDisposed || _dependencyGameIdentity != identity) return;
            _selectedDependenciesReady = false;
            _selectedRuntimeDependency.Text = "依赖不可用：" + SafeDiagnosticOutput.ExceptionSummary(ex);
        }
        if (!IsDisposed) RenderGameState();
    }
}
