using System.Diagnostics;
using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal sealed partial class WpfDesktopSettingsPage
{
    private void DataMigration(WC.Button chooseLocation)
    {
        Section("从旧版导入数据");
        Row("", Text("先关闭旧版软件。粘贴旧版软件文件夹或 data 路径，按回车或点击“检查目录”；也可以浏览选择。检查通过后再复制。"));

        var applicationRoot = AppContext.BaseDirectory;
        var currentRoot = AppDataPaths.Root;
        bool explicitRoot = Environment.GetCommandLineArgs().Any(a => a.StartsWith("--data-root=", StringComparison.OrdinalIgnoreCase));
        bool busy = false, disposed = false, updatingSource = false;
        PortableDataMigration.MigrationPlan? plan = null;
        PortableDataMigration.PendingMigration? pending = null;
        CancellationTokenSource? cancellation = null;
        Func<string> status = () => UiLanguage.Text("尚未选择旧版数据。");
        var source = new WC.TextBox { MinHeight = 36, TextWrapping = W.TextWrapping.Wrap, AcceptsReturn = false };
        var destination = new WC.TextBox { IsReadOnly = true, MinHeight = 36, TextWrapping = W.TextWrapping.Wrap };
        Identify(source, "MigrationSource"); Identify(destination, "MigrationDestination");
        Row("旧版数据目录", source); Row("导入后的目录", destination);
        var summary = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap };
        summary.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text");
        Identify(summary, "MigrationSummary"); Row("检查结果", summary);
        var progress = new WC.ProgressBar { Minimum = 0, Maximum = 100, Height = 6, Margin = new(0, 0, 0, 8), Visibility = W.Visibility.Collapsed };
        Identify(progress, "MigrationProgress"); Row("", progress);
        var message = new WC.TextBlock { TextWrapping = W.TextWrapping.Wrap };
        message.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted"); Identify(message, "MigrationStatus"); Row("", message);
        var select = Button("选择旧版目录", () => { }); Identify(select, "MigrationChoose");
        var inspect = Button("检查目录", () => { }); Identify(inspect, "MigrationInspect");
        var import = Button("复制并在下次启动使用", () => { }); Identify(import, "MigrationImport");
        var cancel = Button("取消复制", () => cancellation?.Cancel()); Identify(cancel, "MigrationCancel");
        var undo = Button("取消待生效迁移", () => { }); Identify(undo, "MigrationUndo");
        var previous = Button("打开原数据目录", () => host.NativeOpenFolder(pending?.PreviousRoot ?? currentRoot)); Identify(previous, "MigrationPrevious");
        Row("", Flow(select, inspect, import, cancel, undo, previous));
        var details = new WC.StackPanel();
        foreach (string description in new[]
        {
            "复制到新目录，下次正常启动时使用。不会合并两个游戏库；旧目录和当前数据完整保留，可取消待生效迁移。",
            "所选数据目录中的设置、翻译方案、游戏库、图片、历史、译文和缓存。游戏文件、游戏原存档及目录外的文件不迁移。",
            "游戏和外部图片路径保持不变。API 密钥受原电脑和 Windows 账户保护，无法解密时会停止导入并提示处理。共享翻译记忆位于目录外，不包含在本次迁移中。"
        })
        {
            var paragraph = Text(description); paragraph.Margin = new(0, 8, 0, 8); details.Children.Add(paragraph);
        }
        var scope = new WC.Expander { Content = details, Margin = new(0, 4, 0, 12) };
        scope.SetResourceReference(WC.Control.ForegroundProperty, "Fusion.Text");
        refresh.Add(() => scope.Header = UiLanguage.Text("迁移范围与保留方式")); section.Children.Add(scope);

        void SetSource(string value)
        {
            if (source.Text == value) return;
            updatingSource = true;
            try { source.Text = value; }
            finally { updatingSource = false; }
        }

        void Update()
        {
            if (disposed) return;
            if (pending is not null) SetSource(pending.SourceRoot);
            destination.Text = plan?.DestinationRoot ?? pending?.DestinationRoot ?? "";
            summary.Text = plan is null ? UiLanguage.Text("选择目录后，先检查文件数量和所需空间。") :
                UiLanguage.Format($"共 {plan.FileCount:N0} 个文件，{plan.TotalBytes / 1048576d:N1} MB；将复制所选目录内的全部数据。");
            message.Text = status();
            select.IsEnabled = !busy && pending is null && !explicitRoot;
            source.IsReadOnly = busy || pending is not null || explicitRoot;
            inspect.IsEnabled = !busy && pending is null && !explicitRoot && !string.IsNullOrWhiteSpace(source.Text);
            import.IsEnabled = !busy && plan is not null && pending is null && !explicitRoot;
            chooseLocation.IsEnabled = !busy && pending is null && !explicitRoot;
            cancel.Visibility = busy ? W.Visibility.Visible : W.Visibility.Collapsed;
            undo.Visibility = pending is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
            undo.IsEnabled = !busy;
            previous.Visibility = pending is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
        }
        void Failure(Exception ex)
        {
            status = () => MigrationError(ex);
        }
        async Task Run(Func<CancellationToken, Task> action)
        {
            if (busy || disposed) return;
            busy = true; cancellation = new();
            progress.Visibility = W.Visibility.Visible; progress.IsIndeterminate = true; Update();
            try { await action(cancellation.Token); }
            catch (OperationCanceledException) { plan = null; status = () => UiLanguage.Text("已取消；原数据和当前使用位置未改变。未完成的副本不会启用。"); }
            catch (Exception ex) { plan = null; Failure(ex); }
            finally
            {
                cancellation.Dispose(); cancellation = null; busy = false;
                if (!disposed)
                {
                    // A durable pending receipt can precede a failed pointer write.
                    // Recover its cancel action without requiring another restart.
                    if (!explicitRoot)
                    {
                        try { pending = PortableDataMigration.GetPending(applicationRoot, currentRoot); }
                        catch (Exception ex) { Failure(ex); }
                    }
                    progress.Visibility = W.Visibility.Collapsed; Update();
                }
            }
        }
        async Task InspectSource()
        {
            if (busy || pending is not null || explicitRoot) return;
            string selection = source.Text.Trim();
            if (selection.Length >= 2 && selection[0] == '"' && selection[^1] == '"') selection = selection[1..^1];
            SetSource(selection);
            if (string.IsNullOrWhiteSpace(selection))
            {
                plan = null; status = () => UiLanguage.Text("请输入或选择旧版软件文件夹或 data 路径。"); Update(); return;
            }
            plan = null; status = () => UiLanguage.Text("正在检查旧版数据和可用空间……");
            await Run(async token =>
            {
                var result = await Task.Run(() => PortableDataMigration.Inspect(selection, currentRoot, applicationRoot, token), token);
                if (disposed) return;
                plan = result;
                SetSource(result.SourceRoot);
                status = () => UiLanguage.Text("检查完成。请确认上方来源和目标目录，再开始复制。当前数据目录会保留。另一个旧版程序应保持关闭。");
            });
        }
        source.TextChanged += (_, _) =>
        {
            if (updatingSource || disposed || busy || pending is not null || explicitRoot) return;
            plan = null;
            status = () => UiLanguage.Text("路径已更改。按回车或点击“检查目录”，检查通过后才能导入。");
            Update();
        };
        source.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Return) return;
            e.Handled = true; await InspectSource();
        };
        inspect.Click += async (_, _) => await InspectSource();
        select.Click += async (_, _) =>
        {
            if (busy || pending is not null || explicitRoot) return;
            string? selection;
            try { selection = host.NativeChooseMigrationSource(); }
            catch (Exception ex) { Failure(ex); Update(); return; }
            if (selection is null) return;
            SetSource(selection); await InspectSource();
        };
        import.Click += async (_, _) =>
        {
            if (busy || plan is null || pending is not null || explicitRoot) return;
            var selectedPlan = plan;
            status = () => UiLanguage.Text("正在复制并校验数据；完成前不会切换使用位置……");
            await Run(async token =>
            {
                var timer = Stopwatch.StartNew();
                var reporter = new Progress<PortableDataMigration.MigrationProgress>(value =>
                {
                    if (disposed || !busy || pending is not null || (timer.ElapsedMilliseconds < 100 && value.CopiedFiles < value.TotalFiles)) return;
                    timer.Restart(); progress.IsIndeterminate = false;
                    progress.Value = value.TotalBytes > 0 ? Math.Clamp(100d * value.CopiedBytes / value.TotalBytes, 0, 100) : 0;
                    status = () => UiLanguage.Format($"正在复制并校验：{value.CopiedFiles:N0} / {value.TotalFiles:N0} 个文件，{value.CopiedBytes / 1048576d:N1} / {value.TotalBytes / 1048576d:N1} MB。");
                    Update();
                });
                pending = await PortableDataMigration.ImportAsync(selectedPlan, reporter, token);
                status = () => UiLanguage.Text("复制和校验已完成。请关闭软件，再从这个新版目录正常启动，即可使用导入的数据。无需再点“保存设置”；本次运行仍使用原数据。");
            });
        };
        undo.Click += async (_, _) =>
        {
            if (busy || pending is null) return;
            status = () => UiLanguage.Text("正在取消待生效迁移……");
            await Run(async _ =>
            {
                await Task.Run(() => PortableDataMigration.CancelPending(applicationRoot, currentRoot));
                pending = null; plan = null;
                status = () => UiLanguage.Text("已取消下次启动的数据切换。原目录和已复制的数据均保留。");
            });
        };
        if (explicitRoot) status = () => UiLanguage.Text("当前通过指定数据目录的启动参数运行。请从新版文件夹正常启动软件后使用数据迁移。");
        else
        {
            try
            {
                pending = PortableDataMigration.GetPending(applicationRoot, currentRoot);
                if (pending is not null) status = () => UiLanguage.Text("已有复制完成的迁移等待生效。关闭软件后，从此目录正常启动即可使用导入的数据；也可以取消。");
            }
            catch (Exception ex) { Failure(ex); }
        }
        refresh.Add(Update);
        detach.Add(() => { disposed = true; cancellation?.Cancel(); });
        Update();
    }

    private static string MigrationError(Exception exception)
    {
        string message = exception is PortableDataMigration.MigrationException failure ? failure.Code switch
        {
            "SourceMissing" => "找不到旧版数据目录。请选择仍然存在的旧版软件文件夹或 data 文件夹。",
            "UnrecognizedSource" => "未识别为 Fusion 数据目录。请选择旧版软件文件夹或其中的 data 文件夹。",
            "InvalidPath" or "ReparsePoint" => "请选择本机普通文件夹；不支持网络路径、链接或重定向目录。",
            "InvalidConfiguration" => "旧版的数据配置无法读取或内容无效。原数据未改动；请先在旧版中检查配置，再重新导入。",
            "SourcePending" => "旧版还有未完成的数据位置切换。请先完成旧版切换并关闭旧版，再重试。",
            "Overlap" => "来源、当前数据和目标目录不能相同，也不能互相包含。请重新选择旧版目录。",
            "DestinationExists" => "目标目录已存在。请重新选择旧版目录，生成新的迁移目标。",
            "SourceChanged" or "SourceBusy" => "旧版数据正在使用或在检查后发生了变化。请关闭旧版软件，重新选择目录并检查。",
            "VerificationFailed" => "复制后的数据校验未通过，未切换使用位置。请检查磁盘后重新选择目录再试。",
            "NotEnoughSpace" => "目标磁盘空间不足。请释放空间后重新检查。",
            "Busy" => "另一个数据操作正在进行，请等待它完成后再试。",
            "PendingExists" => "已有数据迁移或位置切换等待生效。请先完成或取消已有操作。",
            "LocationChanged" => "数据位置设置已被其他操作修改。未覆盖新设置，请重新打开软件后检查。",
            "AlreadyApplied" => "这次迁移已经生效。原数据仍保留；如需恢复，可再次导入原数据目录。",
            "CredentialsUnavailable" => "无法解密旧版的 API 密钥，尚未切换数据。请在原电脑和 Windows 账户中打开旧版，清空密钥并保存，再导入；完成后重新填写密钥。",
            "NoPending" => "没有等待生效的数据迁移。",
            "CurrentRootUnavailable" => "当前数据目录不可用。请检查目录权限和磁盘连接。",
            "TooManyFiles" => "目录包含的文件过多。请确认选中了 Fusion 的数据目录。",
            _ => "无法完成数据迁移。请检查目录权限、磁盘空间，并确认旧版软件已关闭。"
        } : "无法完成数据迁移。请检查目录权限、磁盘空间，并确认旧版软件已关闭。";
        return UiLanguage.Text(message);
    }
}
