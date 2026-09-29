namespace ScreenshotTranslationUiTester;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            DesktopStages.Start();
            args = FusionRuntime.Prepare(args);
            AppDataPaths.Initialize(args);
            FusionRuntime.Initialize();
            Run(args);
        }
        catch(Exception ex)
        {
            // Fail closed on startup/verification errors instead of falling back to user data or generating a crash dump.
            if(AppDataPaths.IsInitialized)AppLog.Write("startup","Application startup failed",ex);
            try{Console.Error.WriteLine(SafeDiagnosticOutput.ExceptionSummary(ex));}catch{}
            if(!args.Any(x=>x.StartsWith("--verify",StringComparison.OrdinalIgnoreCase)||x.Contains("tests",StringComparison.OrdinalIgnoreCase)))
                MessageBox.Show("软件未能启动："+SafeDiagnosticOutput.ExceptionSummary(ex)+"\n请检查软件目录及 data-location.json 的读写权限。未改用 C 盘的数据目录。","FUSION R1",MessageBoxButtons.OK,MessageBoxIcon.Error);
            Environment.ExitCode=1;
        }
    }

    private static void Run(string[] args)
    {
        var profileVerification=ArgumentValue(args,"--fusion-profile-verification=");
        if(profileVerification is not null){ApplicationConfiguration.Initialize();Environment.ExitCode=FusionProfileVerification.RunAsync(profileVerification).GetAwaiter().GetResult();return;}
        if(args.Contains("--fusion-test-surface"))
        {ApplicationConfiguration.Initialize();Application.Run(new FusionTestSurface());return;}
        var fusionFixtures=ArgumentValue(args,"--fusion-export-fixtures=");
        if(fusionFixtures is not null){FusionTestSurface.Export(fusionFixtures);return;}
        var fusionVerification=ArgumentValue(args,"--fusion-verification=");
        if(fusionVerification is not null){Environment.ExitCode=FusionVerification.RunAsync(fusionVerification).GetAwaiter().GetResult();return;}
        if(AppDataPaths.IsDataRootVerification)
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Environment.ExitCode=DataRootVerification.Run(args);
            return;
        }
        var scopedProduction=ArgumentValue(args,"--scoped-production-chain=");
        if(!string.IsNullOrWhiteSpace(scopedProduction))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=ScopedProductionChainHarness.Run(scopedProduction,
                ArgumentValue(args,"--cases=")??throw new ArgumentException("--cases is required"),
                ArgumentValue(args,"--application-root=")??throw new ArgumentException("--application-root is required"),
                ArgumentValue(args,"--worker-wrapper=")??throw new ArgumentException("--worker-wrapper is required"),
                ArgumentValue(args,"--purpose=")??"Scoped production correctness");return;
        }
        var scopedVisualTests=ArgumentValue(args,"--scoped-visual-tests=");
        if(!string.IsNullOrWhiteSpace(scopedVisualTests))
        { Environment.ExitCode=ScopedVisualSelfTests.Run(scopedVisualTests,ArgumentValue(args,"--scope=")??"FIELDS");return; }
        var boundaryTests=ArgumentValue(args,"--translation-boundary-tests=");
        if(!string.IsNullOrWhiteSpace(boundaryTests))
        { Environment.ExitCode=TranslationBoundarySelfTests.Run(boundaryTests);return; }
        var historyVisualReplay=ArgumentValue(args,"--history-visual-replay=");
        if(!string.IsNullOrWhiteSpace(historyVisualReplay))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=HistoryVisualReplayHarness.Run(historyVisualReplay,
                ArgumentValue(args,"--history-manifest=")??throw new ArgumentException("--history-manifest is required"),
                ArgumentValue(args,"--settings=")??throw new ArgumentException("--settings is required"));return;
        }
        var pathCancelTests=ArgumentValue(args,"--path-cancel-tests=");
        if(!string.IsNullOrWhiteSpace(pathCancelTests))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=PreviewRenderCancellationSelfTests.Run(pathCancelTests,
                ArgumentValue(args,"--path-cancel-fixture=")??throw new ArgumentException("--path-cancel-fixture is required"));return;
        }
        var materialRecoveryTests=ArgumentValue(args,"--material-recovery-tests=");
        if(!string.IsNullOrWhiteSpace(materialRecoveryTests))
        {
            Environment.ExitCode=MaterialRecoverySelfTests.Run(materialRecoveryTests);return;
        }
        var safeLogTests=ArgumentValue(args,"--safe-log-output-tests=");
        if(!string.IsNullOrWhiteSpace(safeLogTests))
        {
            Environment.ExitCode=SecurityLogRedactionSelfTests.Run(safeLogTests);return;
        }
        var finalProductFixWave=ArgumentValue(args,"--final-product-fix-wave-tests=");
        if(!string.IsNullOrWhiteSpace(finalProductFixWave))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=FinalProductFixWaveSelfTests.Run(finalProductFixWave,
                ArgumentValue(args,"--source-root=")??AppContext.BaseDirectory);return;
        }
        var whiteSpeckAttribution=ArgumentValue(args,"--white-speck-attribution=");
        if(!string.IsNullOrWhiteSpace(whiteSpeckAttribution))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=WhiteSpeckAttributionHarness.Run(
                whiteSpeckAttribution,
                ArgumentValue(args,"--input=")??throw new ArgumentException("--input is required"),
                ArgumentValue(args,"--settings=")??throw new ArgumentException("--settings is required"));return;
        }
        var apiLatencyBounding=ArgumentValue(args,"--api-latency-bounding=");
        if(!string.IsNullOrWhiteSpace(apiLatencyBounding))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=ApiLatencyBoundingHarness.RunAsync(
                apiLatencyBounding,
                ArgumentValue(args,"--pairing-manifest=")??throw new ArgumentException("--pairing-manifest is required"),
                ArgumentValue(args,"--settings=")??throw new ArgumentException("--settings is required"),
                ArgumentValue(args,"--translation-replay-root=")??throw new ArgumentException("--translation-replay-root is required"),
                int.TryParse(ArgumentValue(args,"--repeats="),out var apiRepeats)?apiRepeats:3).GetAwaiter().GetResult();return;
        }
        var rendererProductPerformance=ArgumentValue(args,"--renderer-product-performance=");
        if(!string.IsNullOrWhiteSpace(rendererProductPerformance))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=CorePipelineV2.RendererProductPerformanceHarness.Run(
                rendererProductPerformance,
                ArgumentValue(args,"--pairing-manifest=")??throw new ArgumentException("--pairing-manifest is required"),
                ArgumentValue(args,"--settings=")??throw new ArgumentException("--settings is required"),
                ArgumentValue(args,"--translation-replay-root=")??throw new ArgumentException("--translation-replay-root is required"),
                ArgumentValue(args,"--diagnostic-reference-root=")??throw new ArgumentException("--diagnostic-reference-root is required"),
                int.TryParse(ArgumentValue(args,"--iterations="),out var productIterations)?productIterations:3);return;
        }
        var sourceStyleBundleTests=ArgumentValue(args,"--source-style-bundle-tests=");
        if(!string.IsNullOrWhiteSpace(sourceStyleBundleTests))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=SourceStyleBundleSelfTests.Run(sourceStyleBundleTests,ArgumentValue(args,"--source-style-real-fixture="));return;
        }
        var megaVisualE2e=ArgumentValue(args,"--mega-visual-e2e=");
        if(!string.IsNullOrWhiteSpace(megaVisualE2e))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=CorePipelineV2.MegaVisualFidelityE2EHarness.RunAsync(
                megaVisualE2e,
                ArgumentValue(args,"--pairing-manifest=")??throw new ArgumentException("--pairing-manifest is required"),
                ArgumentValue(args,"--settings=")??throw new ArgumentException("--settings is required"),
                ArgumentValue(args,"--application-root=")??AppContext.BaseDirectory,
                ArgumentValue(args,"--run-label=")??"MEGA",
                ArgumentValue(args,"--translation-replay-root="),
                ArgumentValue(args,"--pair-filter="),
                ArgumentValue(args,"--r2-mode="),
                ArgumentValue(args,"--translation-overrides=")).GetAwaiter().GetResult();
            return;
        }
        if(args.Any(x=>x.Equals("--cursor-capture-test-surface",StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();Application.Run(new CursorCaptureTestSurfaceForm());return;
        }
        var historyRepair=ArgumentValue(args,"--history-restore-repair-tests=");
        if(!string.IsNullOrWhiteSpace(historyRepair)){ApplicationConfiguration.Initialize();Environment.ExitCode=HistoryRestoreRepairSelfTests.Run(historyRepair);return;}
        var cursorRepair=ArgumentValue(args,"--cursor-exclusion-repair-tests=");
        if(!string.IsNullOrWhiteSpace(cursorRepair)){ApplicationConfiguration.Initialize();Environment.ExitCode=CursorExclusionRepairSelfTests.Run(cursorRepair);return;}
        var dxgiCursorPolicy=ArgumentValue(args,"--dxgi-cursor-surface-policy-tests=");
        if(!string.IsNullOrWhiteSpace(dxgiCursorPolicy)){ApplicationConfiguration.Initialize();Environment.ExitCode=DxgiCursorSurfacePolicySelfTests.Run(dxgiCursorPolicy);return;}
        var earlyPublish=ArgumentValue(args,"--early-translation-publish-tests=");
        if(!string.IsNullOrWhiteSpace(earlyPublish)){ApplicationConfiguration.Initialize();Environment.ExitCode=EarlyTranslationPublishSelfTests.Run(earlyPublish);return;}
        var previewViewport=ArgumentValue(args,"--preview-viewport-tests=");
        if(!string.IsNullOrWhiteSpace(previewViewport)){ApplicationConfiguration.Initialize();Environment.ExitCode=PreviewViewportSelfTests.Run(previewViewport);return;}
        var snapshotOwnership=ArgumentValue(args,"--snapshot-ownership-tests=");
        if(!string.IsNullOrWhiteSpace(snapshotOwnership)){ApplicationConfiguration.Initialize();Environment.ExitCode=SnapshotOwnershipFixSelfTests.Run(snapshotOwnership);return;}
        var visionDefaultOffGate=ArgumentValue(args,"--vision-default-off-gate-tests=");
        if(!string.IsNullOrWhiteSpace(visionDefaultOffGate))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=VisionDefaultOffGateSelfTests.Run(visionDefaultOffGate,
                ArgumentValue(args,"--source=")??throw new ArgumentException("--source is required"));return;
        }
        var continuationLayoutProduct=ArgumentValue(args,"--continuation-layout-product-tests=");
        if(!string.IsNullOrWhiteSpace(continuationLayoutProduct))
        {
            ApplicationConfiguration.Initialize();
            var projectRoot=ArgumentValue(args,"--project-root=")??throw new ArgumentException("--project-root is required");
            Environment.ExitCode=CorePipelineV2.ContinuationLayoutProductSelfTests.Run(projectRoot,continuationLayoutProduct);return;
        }
        var titleRecoveryProductGate=ArgumentValue(args,"--title-recovery-product-gate=");
        if(!string.IsNullOrWhiteSpace(titleRecoveryProductGate))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=CorePipelineV2.TaskATitleRecoveryProductGateHarness.RunAsync(
                ArgumentValue(args,"--project-root=")??throw new ArgumentException("--project-root is required"),
                titleRecoveryProductGate,
                ArgumentValue(args,"--application-root=")??AppContext.BaseDirectory).GetAwaiter().GetResult();return;
        }
        var task4E2e=ArgumentValue(args,"--phase2-task4-layout-e2e=");
        if(!string.IsNullOrWhiteSpace(task4E2e))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode=CorePipelineV2.Task4LayoutProductE2EHarness.RunAsync(
                ArgumentValue(args,"--phase2-task4-mode=")??throw new ArgumentException("--phase2-task4-mode is required"),
                ArgumentValue(args,"--corpus-root=")??throw new ArgumentException("--corpus-root is required"),
                ArgumentValue(args,"--project-root=")??throw new ArgumentException("--project-root is required"),
                task4E2e,
                ArgumentValue(args,"--settings=")??throw new ArgumentException("--settings is required"),
                ArgumentValue(args,"--application-root=")??AppContext.BaseDirectory,
                ArgumentValue(args,"--bad-card=")??throw new ArgumentException("--bad-card is required")).GetAwaiter().GetResult();return;
        }
        var coreApi=ArgumentValue(args,"--core-pipeline-v2-deepseek=");
        if(!string.IsNullOrWhiteSpace(coreApi))
        {
            ApplicationConfiguration.Initialize();
            var raw=ArgumentValue(args,"--core-pipeline-v2-raw=")??throw new ArgumentException("--core-pipeline-v2-raw is required");
            var output=ArgumentValue(args,"--core-pipeline-v2-output=")??throw new ArgumentException("--core-pipeline-v2-output is required");
            var settings=ArgumentValue(args,"--core-pipeline-v2-settings=")??throw new ArgumentException("--core-pipeline-v2-settings is required");
            var mode=ArgumentValue(args,"--core-pipeline-v2-mode=")??"all";
            var fixture=ArgumentValue(args,"--core-pipeline-v2-fixture=");
            Environment.ExitCode=CorePipelineV2.CorePipelineV2DeepSeekRunner.RunAsync(coreApi,raw,output,settings,mode,fixture).GetAwaiter().GetResult();return;
        }
        var coreTests=ArgumentValue(args,"--core-pipeline-v2-tests=");
        if(!string.IsNullOrWhiteSpace(coreTests)){ApplicationConfiguration.Initialize();Environment.ExitCode=CorePipelineV2.CorePipelineV2SelfTests.Run(coreTests);return;}
        var coreTranslationContract=ArgumentValue(args,"--core-v2-translation-contract-tests=");
        if(!string.IsNullOrWhiteSpace(coreTranslationContract)){ApplicationConfiguration.Initialize();Environment.ExitCode=CoreV2TranslationContractSelfTests.Run(coreTranslationContract);return;}
        var coreCorpus=ArgumentValue(args,"--core-pipeline-v2-corpus=");
        if(!string.IsNullOrWhiteSpace(coreCorpus))
        {
            ApplicationConfiguration.Initialize();
            var raw=ArgumentValue(args,"--core-pipeline-v2-raw=")??throw new ArgumentException("--core-pipeline-v2-raw is required");
            var output=ArgumentValue(args,"--core-pipeline-v2-output=")??throw new ArgumentException("--core-pipeline-v2-output is required");
            var replay=ArgumentValue(args,"--core-pipeline-v2-translation-replay=");
            var typographySettings=ArgumentValue(args,"--core-pipeline-v2-settings=");
            Environment.ExitCode=CorePipelineV2.CorePipelineCorpusRunner.Run(coreCorpus,raw,output,replay,typographySettings);return;
        }
        var architectureV2=ArgumentValue(args,"--renderer-architecture-v2-tests=");if(!string.IsNullOrWhiteSpace(architectureV2)){ApplicationConfiguration.Initialize();Environment.ExitCode=RendererArchitectureV2SelfTests.Run(architectureV2);return;}
        var allocationContract=ArgumentValue(args,"--translation-allocation-contract-tests=");if(!string.IsNullOrWhiteSpace(allocationContract)){ApplicationConfiguration.Initialize();Environment.ExitCode=TranslationAllocationContractV1SelfTests.Run(allocationContract);return;}
        var ownershipLayout=ArgumentValue(args,"--ownership-layout-tests=");if(!string.IsNullOrWhiteSpace(ownershipLayout)){ApplicationConfiguration.Initialize();Environment.ExitCode=OwnershipLayoutSelfTests.Run(ownershipLayout,ArgumentValue(args,"--april-source="),ArgumentValue(args,"--runtime-root="));return;}
        var blocker06131=ArgumentValue(args,"--0506131-blocker-tests=");if(!string.IsNullOrWhiteSpace(blocker06131)){ApplicationConfiguration.Initialize();Environment.ExitCode=FinalBlocker06131Tests.Run(blocker06131);return;}
        var stabilization0613=ArgumentValue(args,"--050613-stabilization-tests=");if(!string.IsNullOrWhiteSpace(stabilization0613)){ApplicationConfiguration.Initialize();Environment.ExitCode=FinalStabilization0613Tests.Run(stabilization0613);return;}
        var repair06126=ArgumentValue(args,"--0506126-targeted-tests=");if(!string.IsNullOrWhiteSpace(repair06126)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair06126TargetedTests.Run(repair06126);return;}
        var postConfirm06123=ArgumentValue(args,"--0506123-post-confirm-e2e=");if(!string.IsNullOrWhiteSpace(postConfirm06123)){ApplicationConfiguration.Initialize();Environment.ExitCode=PostConfirmE2EHarness06123.Run(postConfirm06123);return;}
        var repair06123=ArgumentValue(args,"--0506123-targeted-tests=");if(!string.IsNullOrWhiteSpace(repair06123)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair06123TargetedTests.Run(repair06123);return;}
        var repair06122=ArgumentValue(args,"--0506122-targeted-tests=");if(!string.IsNullOrWhiteSpace(repair06122)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair06122TargetedTests.Run(repair06122,ArgumentValue(args,"--faranna-source="));return;}
        var repair06121=ArgumentValue(args,"--0506121-targeted-tests=");if(!string.IsNullOrWhiteSpace(repair06121)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair06121TargetedTests.Run(repair06121);return;}
        var repair0612=ArgumentValue(args,"--050612-targeted-tests=");if(!string.IsNullOrWhiteSpace(repair0612)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair0612TargetedTests.Run(repair0612);return;}
        var cleanup0612=ArgumentValue(args,"--050612-cleanup-trace=");if(!string.IsNullOrWhiteSpace(cleanup0612)){ApplicationConfiguration.Initialize();Environment.ExitCode=CleanupTrace0612.Run(ArgumentValue(args,"--source=")!,ArgumentValue(args,"--recognition=")!,cleanup0612);return;}
        var blindD0612=ArgumentValue(args,"--050612-blind-holdout-d=");if(!string.IsNullOrWhiteSpace(blindD0612)){ApplicationConfiguration.Initialize();Environment.ExitCode=BlindHoldout0611.Run(blindD0612,"D");return;}
        var repair0611=ArgumentValue(args,"--050611-repair-tests=");if(!string.IsNullOrWhiteSpace(repair0611)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair0611SelfTests.Run(repair0611);return;}
        var realScreenshot0611=ArgumentValue(args,"--050611-real-screenshot-e2e=");if(!string.IsNullOrWhiteSpace(realScreenshot0611)){ApplicationConfiguration.Initialize();Environment.ExitCode=RealScreenshotE2EHarness0611.Run(realScreenshot0611);return;}
        var blind0611=ArgumentValue(args,"--050611-blind-holdout=");if(!string.IsNullOrWhiteSpace(blind0611)){ApplicationConfiguration.Initialize();Environment.ExitCode=BlindHoldout0611.Run(blind0611);return;}
        var blindB0611=ArgumentValue(args,"--050611-blind-holdout-b=");if(!string.IsNullOrWhiteSpace(blindB0611)){ApplicationConfiguration.Initialize();Environment.ExitCode=BlindHoldout0611.Run(blindB0611,"B");return;}
        var blindC0611=ArgumentValue(args,"--050611-blind-holdout-c=");if(!string.IsNullOrWhiteSpace(blindC0611)){ApplicationConfiguration.Initialize();Environment.ExitCode=BlindHoldout0611.Run(blindC0611,"C");return;}
        var repair0610=ArgumentValue(args,"--050610-repair-tests=");if(!string.IsNullOrWhiteSpace(repair0610)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair0610SelfTests.Run(repair0610);return;}
        var holdout0610=ArgumentValue(args,"--050610-holdout=");if(!string.IsNullOrWhiteSpace(holdout0610)){ApplicationConfiguration.Initialize();Environment.ExitCode=HoldoutGeneralization0610.Run(holdout0610);return;}
        var holdoutDiscovery0610=ArgumentValue(args,"--050610-holdout-discovery=");if(!string.IsNullOrWhiteSpace(holdoutDiscovery0610)){ApplicationConfiguration.Initialize();Environment.ExitCode=HoldoutGeneralization0610.RunDiscovery(holdoutDiscovery0610);return;}
        var e2e0610=ArgumentValue(args,"--050610-real-exe-e2e=");if(!string.IsNullOrWhiteSpace(e2e0610)){ApplicationConfiguration.Initialize();Environment.ExitCode=RealExeE2EHarness0610.Run(e2e0610);return;}
        var repair069=ArgumentValue(args,"--05069-repair-tests=");if(!string.IsNullOrWhiteSpace(repair069)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair069SelfTests.Run(repair069);return;}
        var repair068=ArgumentValue(args,"--05068-repair-tests=");if(!string.IsNullOrWhiteSpace(repair068)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair068SelfTests.Run(repair068);return;}
        var repair067=ArgumentValue(args,"--05067-repair-tests=");if(!string.IsNullOrWhiteSpace(repair067)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair067SelfTests.Run(repair067);return;}
        var repair066=ArgumentValue(args,"--05066-repair-tests=");if(!string.IsNullOrWhiteSpace(repair066)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair066SelfTests.Run(repair066);return;}
        var repair065=ArgumentValue(args,"--05065-repair-tests=");if(!string.IsNullOrWhiteSpace(repair065)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair065SelfTests.Run(repair065);return;}
        var repair064=ArgumentValue(args,"--05064-repair-tests=");if(!string.IsNullOrWhiteSpace(repair064)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair064SelfTests.Run(repair064);return;}
        var repair063=ArgumentValue(args,"--05063-repair-tests=");if(!string.IsNullOrWhiteSpace(repair063)){ApplicationConfiguration.Initialize();Environment.ExitCode=Repair063SelfTests.Run(repair063);return;}
        var phase3TextColor=ArgumentValue(args,"--phase3-text-color=");if(!string.IsNullOrWhiteSpace(phase3TextColor)){ApplicationConfiguration.Initialize();var source=ArgumentValue(args,"--source=")??throw new ArgumentException("--source is required");Environment.ExitCode=TextColorPhase3Diagnostics.Run(source,phase3TextColor);return;}
        var finalMajorRework=ArgumentValue(args,"--major-rework-final-tests=");if(!string.IsNullOrWhiteSpace(finalMajorRework)){ApplicationConfiguration.Initialize();Environment.ExitCode=MajorReworkFinalSelfTests.Run(finalMajorRework);return;}
        var phase2Background=ArgumentValue(args,"--phase2-background-integration=");if(!string.IsNullOrWhiteSpace(phase2Background)){ApplicationConfiguration.Initialize();var source=ArgumentValue(args,"--source=")??throw new ArgumentException("--source is required");Environment.ExitCode=BackgroundIntegrationPhase2Diagnostics.Run(source,phase2Background);return;}
        var phase1Region=ArgumentValue(args,"--phase1-region-rework=");if(!string.IsNullOrWhiteSpace(phase1Region)){ApplicationConfiguration.Initialize();var source=ArgumentValue(args,"--source=")??throw new ArgumentException("--source is required");var legacy=ArgumentValue(args,"--legacy-audit=");Environment.ExitCode=RegionStructureReworkDiagnostics.Run(source,phase1Region,legacy);return;}
        var effectiveDump=ArgumentValue(args,"--translation-image-effective-dump=");if(!string.IsNullOrWhiteSpace(effectiveDump)){ApplicationConfiguration.Initialize();Environment.ExitCode=TranslationImageTextRenderingDiagnostics.DumpEffectiveConfiguration(effectiveDump);return;}
        var oldNewApril=ArgumentValue(args,"--translation-image-old-new-april=");if(!string.IsNullOrWhiteSpace(oldNewApril)){ApplicationConfiguration.Initialize();var source=ArgumentValue(args,"--april-source=")??throw new ArgumentException("--april-source is required");Environment.ExitCode=TranslationImageTextRenderingDiagnostics.RunOldNewApril(oldNewApril,source);return;}
        var textRenderingDiagnostics=ArgumentValue(args,"--translation-image-text-ab=");if(!string.IsNullOrWhiteSpace(textRenderingDiagnostics)){ApplicationConfiguration.Initialize();Environment.ExitCode=TranslationImageTextRenderingDiagnostics.Run(textRenderingDiagnostics,ArgumentValue(args,"--complex-background="));return;}
        var closeLifecycle=ArgumentValue(args,"--preview-close-lifecycle=");if(!string.IsNullOrWhiteSpace(closeLifecycle)){ApplicationConfiguration.Initialize();var countText=ArgumentValue(args,"--iterations=");Environment.ExitCode=PreviewCloseLifecycleDiagnostics.Run(closeLifecycle,int.TryParse(countText,out var count)?count:1);return;}
        var typographyTests=ArgumentValue(args,"--typography-foundation-tests=");if(!string.IsNullOrWhiteSpace(typographyTests)){ApplicationConfiguration.Initialize();Environment.ExitCode=TypographyFoundationSelfTests.Run(typographyTests);return;}
        var inputTests0505=ArgumentValue(args,"--0505-input-tests=");if(!string.IsNullOrWhiteSpace(inputTests0505)){ApplicationConfiguration.Initialize();Environment.ExitCode=InputInteraction0505SelfTests.Run(inputTests0505);return;}
        var performance0504=ArgumentValue(args,"--0504-worker-performance=");if(!string.IsNullOrWhiteSpace(performance0504)){ApplicationConfiguration.Initialize();var image=ArgumentValue(args,"--performance-image=")??throw new ArgumentException("--performance-image is required");Environment.ExitCode=RuntimePerformance0504.Run(performance0504,image);return;}
        var repairTests0503=ArgumentValue(args,"--0503-repair-tests=");if(!string.IsNullOrWhiteSpace(repairTests0503)){ApplicationConfiguration.Initialize();Environment.ExitCode=ManualAcceptanceRepair3SelfTests.Run(repairTests0503);return;}
        var repairTests0502=ArgumentValue(args,"--0502-repair-tests=");if(!string.IsNullOrWhiteSpace(repairTests0502)){ApplicationConfiguration.Initialize();Environment.ExitCode=ManualAcceptanceRepair2SelfTests.Run(repairTests0502);return;}
        var repairTests=ArgumentValue(args,"--0501-repair-tests=");if(!string.IsNullOrWhiteSpace(repairTests)){ApplicationConfiguration.Initialize();Environment.ExitCode=ManualAcceptanceRepairSelfTests.Run(repairTests);return;}
        var exitHarness=ArgumentValue(args,"--0501-exit-harness=");if(!string.IsNullOrWhiteSpace(exitHarness)){Run0501ExitHarness(exitHarness);return;}
        var startupHealthTests=ArgumentValue(args,"--startup-health-tests=");if(!string.IsNullOrWhiteSpace(startupHealthTests)){ApplicationConfiguration.Initialize();Environment.ExitCode=StartupHealthSelfTests.RunAsync(startupHealthTests,ArgumentValue(args,"--application-root=")??AppContext.BaseDirectory).GetAwaiter().GetResult();return;}
        var productizationTests=ArgumentValue(args,"--050-product-tests=");if(!string.IsNullOrWhiteSpace(productizationTests)){ApplicationConfiguration.Initialize();Environment.ExitCode=ProductizationSelfTests.Run(productizationTests);return;}
        var toolbarOutput = ArgumentValue(args, "--toolbar-ui-smoke=");
        if (!string.IsNullOrWhiteSpace(toolbarOutput)) { RunToolbarUiSmoke(toolbarOutput); return; }
        var themeUiOutput = ArgumentValue(args, "--theme-ui-smoke=");
        if (!string.IsNullOrWhiteSpace(themeUiOutput))
        {
            RunThemeUiSmoke(themeUiOutput); return;
        }
        var pageBenchmark=ArgumentValue(args,"--page-host-benchmark=");if(!string.IsNullOrWhiteSpace(pageBenchmark)){RunPageHostBenchmark(pageBenchmark);return;}
        var settingsUiOutput = ArgumentValue(args, "--settings-ui-smoke=");
        if (!string.IsNullOrWhiteSpace(settingsUiOutput))
        {
            RunSettingsUiSmoke(settingsUiOutput); return;
        }
        var repeatLivePreviewImage = ArgumentValue(args, "--0411-live-repeat=");
        if (!string.IsNullOrWhiteSpace(repeatLivePreviewImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "0411-live-repeat");
            var modelText = ArgumentValue(args, "--visual-model=") ?? nameof(VisualModelKind.PPDocLayoutS);
            Environment.ExitCode = Run0411LivePreviewRepeat(repeatLivePreviewImage, output, Enum.Parse<VisualModelKind>(modelText, true)); return;
        }
        var livePreviewImage = ArgumentValue(args, "--041-live-preview=");
        if (!string.IsNullOrWhiteSpace(livePreviewImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "0411-live-preview");
            var modelText = ArgumentValue(args, "--visual-model=") ?? nameof(VisualModelKind.PPDocLayoutS);
            Environment.ExitCode = Run0411LivePreview(livePreviewImage, output, Enum.Parse<VisualModelKind>(modelText, true)); return;
        }
        var performanceImage = ArgumentValue(args, "--041-performance=");
        if (!string.IsNullOrWhiteSpace(performanceImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "041-performance");
            Environment.ExitCode = Run041Performance(performanceImage, output); return;
        }
        var rendererSmokeImage = ArgumentValue(args, "--renderer-v2-smoke=");
        if (!string.IsNullOrWhiteSpace(rendererSmokeImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "renderer-v2");
            Environment.ExitCode = RunRendererV2Smoke(rendererSmokeImage, output); return;
        }
        var editorSmokeImage = ArgumentValue(args, "--region-editor-v2-smoke=");
        if (!string.IsNullOrWhiteSpace(editorSmokeImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "region-editor-v2");
            RunRegionEditorV2Smoke(editorSmokeImage, output); return;
        }
        var v2PreviewImage = ArgumentValue(args, "--vision-v2-preview-smoke=");
        if (!string.IsNullOrWhiteSpace(v2PreviewImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "vision-v2-preview");
            RunVisionV2PreviewSmoke(v2PreviewImage, output); return;
        }
        var v2RealImage = ArgumentValue(args, "--vision-v2-real-smoke=");
        if (!string.IsNullOrWhiteSpace(v2RealImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "vision-v2-real");
            var modelText = ArgumentValue(args, "--visual-model=") ?? nameof(VisualModelKind.PPDocLayoutS);
            var engineText = ArgumentValue(args, "--ocr-engine=") ?? nameof(OcrEngineKind.Rapid);
            Environment.ExitCode = RunVisionV2RealSmoke(v2RealImage, output,
                Enum.Parse<VisualModelKind>(modelText, true), Enum.Parse<OcrEngineKind>(engineText, true));
            return;
        }
        var v2TestOutput = ArgumentValue(args, "--vision-v2-tests=");
        if (!string.IsNullOrWhiteSpace(v2TestOutput))
        {
            ApplicationConfiguration.Initialize();
            Environment.ExitCode = VisionV2SelfTests.Run(v2TestOutput);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Write("ui", "Unhandled UI exception", e.Exception);
            AppDialog.Show(null, "游戏 AI 截图翻译器",
                "程序遇到无法继续的界面错误。诊断已保存，程序将安全退出。",
                AppDialogKind.Error, e.Exception.ToString());
            Application.Exit();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write("ui", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        var diagnostics = CaptureFlashDiagnosticOptions.Parse(args);
        CaptureFlashDiagnosticLog.Start(diagnostics);
        var repair3SmokeImage = ArgumentValue(args, "--repair3-ocr-smoke=");
        if (!string.IsNullOrWhiteSpace(repair3SmokeImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ??
                Path.Combine(AppContext.BaseDirectory, "reports", "repair3-real-ui-smoke");
            RunRepair3OcrSmoke(repair3SmokeImage, output);
            return;
        }
        var escSmoke = ArgumentValue(args, "--repair2-esc-smoke=");
        if (!string.IsNullOrWhiteSpace(escSmoke))
        {
            RunRepair2EscSmoke(escSmoke);
            return;
        }
        var smokeImage = ArgumentValue(args, "--repair2-ocr-smoke=");
        if (!string.IsNullOrWhiteSpace(smokeImage))
        {
            var output = ArgumentValue(args, "--smoke-output=") ?? Path.Combine(AppContext.BaseDirectory, "reports", "real-ui-smoke");
            RunRepair2OcrSmoke(smokeImage, output);
            return;
        }
        var singleHarness=ArgumentValue(args,"--0503-single-instance-harness=");
        using var instance=new SingleInstanceCoordinator();
        if(!instance.IsPrimary){instance.NotifyPrimary();return;}
        using var main=new MainForm(null,diagnostics);
        var uiChecks=ArgumentValue(args,"--ui-repair-checks=");if(uiChecks is not null)main.Shown+=async(_,_)=>await main.RunUiRepairChecksAsync(uiChecks);
        instance.Start(()=>{if(!main.IsDisposed)main.BeginInvoke(new Action(()=>{main.RestoreFromExternalLaunch();if(!string.IsNullOrWhiteSpace(singleHarness)){File.WriteAllText(singleHarness,"ACTIVATED");main.ExitForSmoke();}}));});
        if(!string.IsNullOrWhiteSpace(singleHarness))main.Shown+=(_,_)=>{main.TrayHideForSmoke();File.WriteAllText(singleHarness+".ready","READY");};
        Application.Run(main);
    }

    private static void Run0501ExitHarness(string scenario)
    {
        ApplicationConfiguration.Initialize();var config=Path.Combine(Path.GetTempPath(),$"st-0501-exit-{Guid.NewGuid():N}.json");ConfigurationManager.Save(config,new ApiSettings{CloseMainWindowBehavior=scenario=="tray"?CloseMainWindowBehavior.MinimizeToTray:CloseMainWindowBehavior.Exit,VisualModel=VisualModelKind.Off});using var main=new MainForm(config);PreviewForm? preview=null;var timer=new System.Windows.Forms.Timer{Interval=350};main.Shown+=(_,_)=>{if(scenario is "preview-off" or "preview-on" or "dialog"){using var image=new Bitmap(160,90);preview=main.OpenPreviewForSmoke(image);preview.TopMost=scenario is "preview-on" or "dialog";}if(scenario=="tray")main.Close();if(scenario=="dialog"&&preview is not null)main.BeginInvoke(new Action(()=>AppDialog.Show(preview,"退出测试","可见模态窗口",AppDialogKind.Confirmation)));timer.Tick+=(_,_)=>{timer.Stop();main.ExitForSmoke();};timer.Start();};Application.Run(main);try{File.Delete(config);File.Delete(config+".bak");}catch{}
    }

    private static string? ArgumentValue(IEnumerable<string> args, string prefix) =>
        args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];

    private static void RunToolbarUiSmoke(string output)
    {
        Environment.SetEnvironmentVariable("SCREENSHOT_TRANSLATOR_V2_FROZEN_TEST", "1");
        ApplicationConfiguration.Initialize(); Directory.CreateDirectory(output);
        foreach (var percent in new[] { 100, 125, 150 })
        {
            using var image = new Bitmap(900, 600); using (var g = Graphics.FromImage(image)) { g.Clear(Color.FromArgb(45, 48, 54)); g.DrawString("April Preview", new Font("Arial", 28), Brushes.White, 30, 30); }
            using var form = new PreviewForm(image, PreviewMode.OcrOnly, new ApiSettings { VisualModel = VisualModelKind.Off }, new OcrService(), new TranslationService());
            if (percent != 100) form.Scale(new SizeF(percent / 100f, percent / 100f)); form.Show(); Application.DoEvents();
            using var shot = CaptureWindow(form); shot.Save(Path.Combine(output, $"preview-toolbar-{percent}.png"));
            form.SetTextViewForSmoke(TextViewMode.Hidden);Application.DoEvents();using(var hidden=CaptureWindow(form))hidden.Save(Path.Combine(output,$"preview-compact-hidden-{percent}.png"));
            form.SetTextViewForSmoke(TextViewMode.RawOcr);form.SetChromeModeForSmoke(PreviewChromeMode.Full);Application.DoEvents();using(var full=CaptureWindow(form))full.Save(Path.Combine(output,$"preview-full-toolbar-{percent}.png"));
            File.WriteAllLines(Path.Combine(output, $"preview-toolbar-{percent}-controls.txt"), DescribeControls(form)); form.Close();
        }
        File.WriteAllText(Path.Combine(output, "toolbar-ui-smoke.txt"), "RealApiCalls=0\r\nActiveOperations=0\r\nWorkerResidue=NONE\r\n");
    }

    private static void RunThemeUiSmoke(string output)
    {
        RunSettingsUiSmoke(output);
        using(var translation=new TranslationSettingsDialog(new ApiSettings()))
        {
            translation.Show();translation.BringToFront();Application.DoEvents();File.WriteAllLines(Path.Combine(output,"dialog-translation-controls.txt"),DescribeControls(translation));using var shot=CaptureWindow(translation);shot.Save(Path.Combine(output,"dialog-translation-settings.png"));translation.Close();
        }
        using(var appearance=new AppearanceSettingsDialog(new ApiSettings()))
        {
            appearance.Show();appearance.BringToFront();Application.DoEvents();using var shot=CaptureWindow(appearance);shot.Save(Path.Combine(output,"dialog-appearance-settings.png"));appearance.Close();
        }
    }

    private static void RunPageHostBenchmark(string output){ApplicationConfiguration.Initialize();var config=Path.Combine(Path.GetDirectoryName(output)??AppContext.BaseDirectory,"page-host-benchmark.json");ConfigurationManager.Save(config,new ApiSettings{VisualModel=VisualModelKind.Off,CloseMainWindowBehavior=CloseMainWindowBehavior.Exit});using var main=new MainForm(config);main.Show();var lines=new List<string>();var timer=System.Diagnostics.Stopwatch.StartNew();for(var stepNumber=0;stepNumber<1000;stepNumber++){var page=MainForm.MainPageNamesForSmoke[stepNumber%MainForm.MainPageNamesForSmoke.Length];var step=System.Diagnostics.Stopwatch.StartNew();var ok=main.NavigateWithoutEventsForSmoke(page);lines.Add($"{stepNumber}:{page}:ok={ok}:ms={step.ElapsedMilliseconds}:host={main.MainHostControlCountForSmoke}");}lines.Add($"TOTAL={timer.ElapsedMilliseconds}");File.WriteAllLines(output,lines);main.Close();}

    private static IReadOnlyList<string> DescribeControls(Control root){var lines=new List<string>();void Scan(Control c,int depth){lines.Add($"{new string(' ',depth*2)}{c.GetType().Name} text={c.Text} bounds={c.Bounds} visible={c.Visible} dock={c.Dock}");foreach(Control child in c.Controls)Scan(child,depth+1);}Scan(root,0);return lines;}
    private static Bitmap CaptureWindow(Form form){var shot=new Bitmap(form.ClientSize.Width,form.ClientSize.Height);form.DrawToBitmap(shot,form.ClientRectangle);using var graphics=Graphics.FromImage(shot);void Scan(Control c,Point origin){var here=new Point(origin.X+c.Left,origin.Y+c.Top);if(c is FlowLayoutPanel&&c.Controls.Cast<Control>().Any(x=>x is Button)){using var layer=new Bitmap(c.Width,c.Height);c.DrawToBitmap(layer,c.ClientRectangle);graphics.DrawImageUnscaled(layer,here);}foreach(Control child in c.Controls)Scan(child,here);}foreach(Control child in form.Controls)Scan(child,Point.Empty);return shot;}

    private static void RunSettingsUiSmoke(string output)
    {
        ApplicationConfiguration.Initialize();Directory.CreateDirectory(output);var config=Path.Combine(output,"settings-smoke.json");ConfigurationManager.Save(config,new ApiSettings{CloseMainWindowBehavior=CloseMainWindowBehavior.Exit});using var form=new MainForm(config);var timer=new System.Windows.Forms.Timer{Interval=900};form.Shown+=(_,_)=>{timer.Tick+=(_,_)=>{timer.Stop();for(var i=0;i<form.SettingsTabCountForSmoke;i++){var name=form.SelectSettingsTabForSmoke(i);Application.DoEvents();using var shot=new Bitmap(form.ClientSize.Width,form.ClientSize.Height);form.DrawCurrentPageForSmoke(shot);shot.Save(Path.Combine(output,$"settings-{i+1:D2}-{name.Replace('/','-')}.png"));}File.WriteAllLines(Path.Combine(output,"visible-text.txt"),form.SettingsVisibleTextsForSmoke());File.WriteAllText(Path.Combine(output,"settings-ui-smoke.txt"),$"Tabs={form.SettingsTabCountForSmoke}{Environment.NewLine}RealApiCalls=0{Environment.NewLine}ActiveOperations=0{Environment.NewLine}WorkerResidue=NONE");form.Close();};timer.Start();};Application.Run(form);
    }

    private static int Run0411LivePreview(string imagePath, string output, VisualModelKind model)
    {
        ApplicationConfiguration.Initialize(); Directory.CreateDirectory(output); var external=System.Diagnostics.Stopwatch.StartNew();
        using var source=new Bitmap(imagePath); using var form=new PreviewForm(source,PreviewMode.OcrAndTranslate,
            new ApiSettings { OcrEngine=OcrEngineKind.Rapid, VisualModel=model, OcrLanguage="英语", TranslationCacheEnabled=false },
            new OcrService(),new TranslationService());
        form.SetTranslationExecutorForSmoke(async (items,_,progress,token)=>
        {
            progress?.Report(new(TranslationStage.SendingRequest,1,0,"正在请求翻译 API"));
            await Task.Delay(300,token); progress?.Report(new(TranslationStage.RequestFinished,1,300,"翻译 API 已完成"));
            var values=items.ToDictionary(x=>x.Id,x=>$"译文：{x.Text}",StringComparer.Ordinal);
            return new(values,1,300,[],false,string.Join("\n",values.Values),true);
        });
        form.Shown += async (_,_) =>
        {
            try
            {
                var deadline=DateTime.UtcNow.AddMinutes(3); while(!form.FinalPreviewReadyForSmoke&&DateTime.UtcNow<deadline)await Task.Delay(25);
                if(!form.FinalPreviewReadyForSmoke)throw new TimeoutException("Live Preview did not become paint-ready.");
                external.Stop(); using var shot=new Bitmap(form.ClientSize.Width,form.ClientSize.Height); form.DrawToBitmap(shot,form.ClientRectangle); shot.Save(Path.Combine(output,"live-preview-final.png"));
                if(form.LastTimingPathForSmoke is { } timing&&File.Exists(timing))File.Copy(timing,Path.Combine(output,"wallclock-internal.json"),true);
                File.WriteAllText(Path.Combine(output,"wallclock-external.txt"),$"ExternalWallClockMs={external.ElapsedMilliseconds}{Environment.NewLine}InternalWallClockMs={form.EndToEndWallClockMsForSmoke}{Environment.NewLine}DifferenceMs={Math.Abs(external.ElapsedMilliseconds-form.EndToEndWallClockMsForSmoke)}{Environment.NewLine}VisionInferenceCount={form.RecognitionRunCountForSmoke}{Environment.NewLine}RealApiCalls=0{Environment.NewLine}ActiveOperations=0");
            }
            catch(Exception ex){File.WriteAllText(Path.Combine(output,"live-preview-error.txt"),ex.ToString());Environment.ExitCode=2;}
            finally{form.Close();}
        };
        Application.Run(form); return Environment.ExitCode;
    }

    private static int Run0411LivePreviewRepeat(string imagePath, string output, VisualModelKind model)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(output);
        var ocrService = new OcrService();
        var translationService = new TranslationService();
        var ocr = new OcrRuntimeManager(AppContext.BaseDirectory, ocrService);
        var vision = new VisionRuntimeManager(AppContext.BaseDirectory);
        using var session = new SessionServices();
        var rows = new List<object>();
        try
        {
            for (var run = 1; run <= 3; run++)
            {
                var label = run == 1 ? "cold" : "warm";
                var runDirectory = Path.Combine(output, $"run-{run}-{label}");
                Directory.CreateDirectory(runDirectory);
                var external = System.Diagnostics.Stopwatch.StartNew();
                using var source = new Bitmap(imagePath);
                using var form = new PreviewForm(source, PreviewMode.OcrAndTranslate,
                    new ApiSettings { OcrEngine = OcrEngineKind.Rapid, VisualModel = model, OcrLanguage = "鑻辫",
                        OcrCacheEnabled = false, TranslationCacheEnabled = false },
                    ocrService, translationService, ocr, vision, session);
                Exception? failure = null;
                form.SetTranslationExecutorForSmoke(async (items, _, progress, token) =>
                {
                    progress?.Report(new(TranslationStage.SendingRequest, 1, 0, "姝ｅ湪璇锋眰缈昏瘧 API"));
                    await Task.Delay(300, token);
                    progress?.Report(new(TranslationStage.RequestFinished, 1, 300, "缈昏瘧 API 宸插畬鎴?"));
                    var values = items.ToDictionary(x => x.Id, x => $"译文：{x.Text}", StringComparer.Ordinal);
                    return new(values, 1, 300, [], false, string.Join("\n", values.Values), true);
                });
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var deadline = DateTime.UtcNow.AddMinutes(3);
                        while (!form.FinalPreviewReadyForSmoke && DateTime.UtcNow < deadline) await Task.Delay(25);
                        if (!form.FinalPreviewReadyForSmoke) throw new TimeoutException($"Live Preview run {run} did not become paint-ready.");
                        external.Stop();
                        using var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                        form.DrawToBitmap(shot, form.ClientRectangle);
                        shot.Save(Path.Combine(runDirectory, "live-preview-final.png"));
                        if (form.LastTimingPathForSmoke is { } timing && File.Exists(timing))
                            File.Copy(timing, Path.Combine(runDirectory, "wallclock-internal.json"), true);
                        var row = new
                        {
                            run, temperature = label, externalWallClockMs = external.ElapsedMilliseconds,
                            internalWallClockMs = form.EndToEndWallClockMsForSmoke,
                            differenceMs = Math.Abs(external.ElapsedMilliseconds - form.EndToEndWallClockMsForSmoke),
                            visionInferenceCount = form.RecognitionRunCountForSmoke,
                            visionWorkerPid = form.VisionWorkerPidForSmoke, ocrWorkerPid = form.OcrWorkerPidForSmoke,
                            realApiCalls = 0, activeOperations = 0
                        };
                        rows.Add(row);
                        File.WriteAllText(Path.Combine(runDirectory, "wallclock-external.json"),
                            System.Text.Json.JsonSerializer.Serialize(row, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
                if (failure is not null) throw failure;
            }
            File.WriteAllText(Path.Combine(output, "cold-warm-warm-summary.json"),
                System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "cold-warm-warm-error.txt"), ex.ToString());
            return 2;
        }
        finally
        {
            vision.DisposeAsync().AsTask().GetAwaiter().GetResult();
            ocr.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int Run041Performance(string imagePath, string output)
    {
        Directory.CreateDirectory(output); using var image = new Bitmap(imagePath);
        var ocr = new OcrRuntimeManager(AppContext.BaseDirectory, new OcrService()); var vision = new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            var rows = new List<object>(); long generation = 0;
            var combinations = new[] { (VisualModelKind.PPDocLayoutS, OcrEngineKind.Rapid), (VisualModelKind.PPDocLayoutM, OcrEngineKind.Rapid), (VisualModelKind.PPDocLayoutS, OcrEngineKind.Paddle) };
            foreach (var combination in combinations)
                for (var run = 1; run <= 3; run++)
                {
                    var settings = new ApiSettings { VisualModel = combination.Item1, OcrEngine = combination.Item2, OcrLanguage = "英语" };
                    var result = new RecognitionPipelineV2(ocr, vision).RunAsync(image, settings, ++generation, CancellationToken.None).GetAwaiter().GetResult();
                    var render = RegionRendererV2.Render(image, result.Document.Regions, new RenderSettings()); using (render.Bitmap) { }
                    var d = result.Document.Diagnostics!;
                    rows.Add(new { visualModel=combination.Item1.ToString(), ocrEngine=combination.Item2.ToString(), run,
                        d.VisionMs, d.OcrMs, d.FusionMs, d.GroupingMs, d.RoleMs, rendererMs=render.ElapsedMs,
                        recognitionTotalMs=d.TotalMs, visionWorkerPid=vision.ActiveWorkerPid, ocrWorkerPid=ocr.ActiveWorkerPid,
                        modelDetail=result.Visual.ModelDiagnostics.Detail, regions=d.RecognitionRegionCount, units=d.TranslationUnitCount });
                }
            File.WriteAllText(Path.Combine(output, "performance-warm-runs.json"), System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "performance-error.txt"), ex.ToString()); return 2; }
        finally { vision.DisposeAsync().AsTask().GetAwaiter().GetResult(); ocr.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void RunRegionEditorV2Smoke(string imagePath, string output)
    {
        ApplicationConfiguration.Initialize(); Directory.CreateDirectory(output);
        using var image = new Bitmap(imagePath); var ocr = new OcrRuntimeManager(AppContext.BaseDirectory, new OcrService());
        var vision = new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            var settings = new ApiSettings { OcrEngine = OcrEngineKind.Rapid, VisualModel = VisualModelKind.PPDocLayoutS, OcrLanguage = "英语" };
            var result = new RecognitionPipelineV2(ocr, vision).RunAsync(image, settings, 1, CancellationToken.None).GetAwaiter().GetResult();
            SaveCoverageEvidence(image, result.Document, output);
            using var form = new RecognitionRegionEditorForm(image, result.Document.Regions,
                (_, _) => Task.FromResult<IReadOnlyList<OcrEngineBlock>>([]), (_, _) => Task.FromResult<VisualRegion?>(null));
            form.Shown += (_, _) => { using var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height); form.DrawToBitmap(shot, form.ClientRectangle); shot.Save(Path.Combine(output, "region-editor-v2.png")); File.WriteAllText(Path.Combine(output, "region-editor-v2.txt"), $"Regions={form.ResultRegions.Count}{Environment.NewLine}ActiveOperations=0{Environment.NewLine}RealApiCalls=0"); form.Close(); };
            Application.Run(form);
        }
        finally { vision.DisposeAsync().AsTask().GetAwaiter().GetResult(); ocr.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static int RunRendererV2Smoke(string imagePath, string output)
    {
        Directory.CreateDirectory(output); using var image = new Bitmap(imagePath);
        var ocr = new OcrRuntimeManager(AppContext.BaseDirectory, new OcrService()); var vision = new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            image.Save(Path.Combine(output, "source.png"));
            var settings = new ApiSettings { OcrEngine = OcrEngineKind.Rapid, VisualModel = VisualModelKind.PPDocLayoutS, OcrLanguage = "英语" };
            var result = new RecognitionPipelineV2(ocr, vision).RunAsync(image, settings, 1, CancellationToken.None).GetAwaiter().GetResult();
            SaveCoverageEvidence(image, result.Document, output);
            result.Document.TranslationGeneration = 1;
            static string FixtureText(RegionRoleType role)=>role switch
            {
                RegionRoleType.Button=>"继续",RegionRoleType.Header=>"标题",RegionRoleType.CharacterName=>"角色名",
                RegionRoleType.Metadata=>"时间信息",RegionRoleType.Dialogue=>"这是对话译文",_=>"这是正文译文"
            };
            var allocations=result.Document.TranslationUnits.ToDictionary(x=>x.Id,x=>x.StableSourceIds.Select((id,index)=>
                new TranslationAllocationSegment(x.Id,[id],FixtureText(x.RoleType),index,1,$"{x.Id}-A{index+1:000}")).ToArray(),StringComparer.Ordinal);
            var translations=allocations.ToDictionary(x=>x.Key,x=>string.Concat(x.Value.Select(y=>y.TranslatedText)),StringComparer.Ordinal);
            TranslationMappingV2.Apply(result.Document,new TranslationBatchResult(translations,0,0,[],true,Allocations:allocations),1);
            var variants = new (string Name, RenderSettings Settings)[]
            {
                ("auto", new RenderSettings { BackgroundStrategy = TranslationOverlayBackgroundStyle.Automatic }),
                ("semitransparent-alpha-40", new RenderSettings { BackgroundStrategy = TranslationOverlayBackgroundStyle.LightOverlay, Background = true, BackgroundOpacity = 40 }),
                ("semitransparent-alpha-120", new RenderSettings { BackgroundStrategy = TranslationOverlayBackgroundStyle.LightOverlay, Background = true, BackgroundOpacity = 120 }),
                ("semitransparent-alpha-220", new RenderSettings { BackgroundStrategy = TranslationOverlayBackgroundStyle.LightOverlay, Background = true, BackgroundOpacity = 220 }),
                ("solid", new RenderSettings { BackgroundStrategy = TranslationOverlayBackgroundStyle.Solid, Background = true, BackgroundOpacity = 255 }),
                ("font-yahei", new RenderSettings { BackgroundStrategy = TranslationOverlayBackgroundStyle.LightOverlay, Background = true, BackgroundOpacity = 120, FontFamily = "Microsoft YaHei UI" }),
                ("font-simsun", new RenderSettings { BackgroundStrategy = TranslationOverlayBackgroundStyle.LightOverlay, Background = true, BackgroundOpacity = 120, FontFamily = "SimSun" })
            };
            var renderedVariants = new List<(string Name, RegionRenderResult Result)>();
            foreach (var variant in variants)
            {
                var variantResult = RegionRendererV2.Render(image, result.Document, variant.Settings);
                variantResult.Bitmap.Save(Path.Combine(output, $"renderer-v2-{variant.Name}.png"));
                renderedVariants.Add((variant.Name, variantResult));
            }
            var rendered = renderedVariants[0].Result;
            rendered.Bitmap.Save(Path.Combine(output, "renderer-v2-in-place.png"));
            var coverage = result.Document.CoverageSummary!;
            File.WriteAllText(Path.Combine(output, "renderer-v2.txt"), $"Regions={result.Document.Regions.Count}{Environment.NewLine}TranslationUnits={result.Document.TranslationUnits.Count}{Environment.NewLine}OCRSourceLines={coverage.TotalSourceLines}{Environment.NewLine}Assigned={coverage.Assigned}{Environment.NewLine}Preserved={coverage.Preserved}{Environment.NewLine}Ignored={coverage.Ignored}{Environment.NewLine}Unaccounted={coverage.Unaccounted}{Environment.NewLine}RequiredEraseLines={coverage.RequiredEraseLines}{Environment.NewLine}ValidEraseGeometry={coverage.ValidEraseGeometry}{Environment.NewLine}MissingEraseGeometry={coverage.MissingEraseGeometry}{Environment.NewLine}CoverageFinalizeMs={result.Document.Diagnostics?.CoverageFinalizeMs}{Environment.NewLine}Warnings={rendered.Warnings.Count}{Environment.NewLine}RendererMs={rendered.ElapsedMs}{Environment.NewLine}RealApiCalls=0{Environment.NewLine}ActiveOperations=0");
            File.WriteAllText(Path.Combine(output, "renderer-v2-diagnostics.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                source = Path.GetFileName(imagePath), visualModel = settings.VisualModel.ToString(), ocrEngine = settings.OcrEngine.ToString(),
                rendered.ElapsedMs, rendered.MaskMs, rendered.LayoutMs, rendered.DrawMs,
                coverage, sourceLines = result.Document.SourceLineCoverage, coverageErrors = RecognitionCoverageValidator.Validate(result.Document),
                warnings = rendered.Warnings, regions = rendered.Diagnostics,
                variants = renderedVariants.Select(x => new { x.Name, x.Result.ElapsedMs, x.Result.Warnings, regions = x.Result.Diagnostics }),
                realApiCalls = 0, activeOperations = 0
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            foreach (var variant in renderedVariants) variant.Result.Bitmap.Dispose();
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "renderer-v2-error.txt"), ex.ToString()); return 2; }
        finally { vision.DisposeAsync().AsTask().GetAwaiter().GetResult(); ocr.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void SaveCoverageEvidence(Bitmap source, RecognitionDocumentV2 document, string output)
    {
        static void Label(Graphics g, string text, PointF at, Color color)
        {
            using var font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(text, font); using var back = new SolidBrush(Color.FromArgb(190, Color.Black));
            g.FillRectangle(back, at.X, Math.Max(0, at.Y - size.Height), size.Width, size.Height);
            using var brush = new SolidBrush(color); g.DrawString(text, font, brush, at.X, Math.Max(0, at.Y - size.Height));
        }
        using (var lines = new Bitmap(source)) using (var g = Graphics.FromImage(lines))
        {
            using var pen = new Pen(Color.Lime, 2);
            foreach (var line in document.SourceLineCoverage.Where(x => x.Polygon.Length >= 3))
            { g.DrawPolygon(pen, line.Polygon); var b = GeometryV2.Bounds(line.Polygon); Label(g, line.SourceLineId, new(b.Left, b.Top), Color.Lime); }
            lines.Save(Path.Combine(output, "source-line-geometry.png"));
        }
        using (var semantic = new Bitmap(source)) using (var g = Graphics.FromImage(semantic))
        {
            using var pen = new Pen(Color.Cyan, 3);
            foreach (var region in document.Regions.Where(x => x.Polygon.Length >= 3))
            { g.DrawPolygon(pen, region.Polygon); var b = region.BoundingBox; Label(g, $"{region.RegionId} {region.RoleType}", new(b.Left, b.Top), Color.Cyan); }
            semantic.Save(Path.Combine(output, "semantic-regions.png"));
        }
        using (var coverage = new Bitmap(source)) using (var g = Graphics.FromImage(coverage))
        {
            foreach (var line in document.SourceLineCoverage.Where(x => x.Polygon.Length >= 3))
            {
                var color = line.Disposition switch { SourceLineDisposition.AssignedToTranslationUnit => Color.Lime,
                    SourceLineDisposition.PreserveOriginal => Color.DeepSkyBlue, SourceLineDisposition.IgnoredWithReason => Color.Gold, _ => Color.Red };
                using var pen = new Pen(color, line.Disposition == SourceLineDisposition.Unaccounted ? 5 : 3);
                g.DrawPolygon(pen, line.Polygon); var b = GeometryV2.Bounds(line.Polygon); Label(g, $"{line.SourceLineId} {line.Disposition}", new(b.Left, b.Top), color);
            }
            coverage.Save(Path.Combine(output, "coverage-disposition.png"));
        }
    }

    private static int RunVisionV2RealSmoke(string imagePath, string output, VisualModelKind model, OcrEngineKind engine)
    {
        Directory.CreateDirectory(output);
        WindowsOcrIsolationDiagnostics.Reset();
        using var image = new Bitmap(imagePath);
        var ocr = new OcrRuntimeManager(AppContext.BaseDirectory, new OcrService());
        var vision = new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            var settings = new ApiSettings { OcrEngine = engine, VisualModel = model, OcrLanguage = "英语" };
            var result = new RecognitionPipelineV2(ocr, vision).RunAsync(image, settings, 1, CancellationToken.None).GetAwaiter().GetResult();
            using var overlay = new Bitmap(image); using (var g = Graphics.FromImage(overlay))
            {
                using var pen = new Pen(Color.Lime, 2); using var font = new Font("Segoe UI", 10, FontStyle.Bold);
                foreach (var r in result.Document.Regions) { if (r.Polygon.Length >= 3) g.DrawPolygon(pen, r.Polygon); var label = $"{r.RegionId} {r.RoleType} #{r.ReadingOrder}"; var size = g.MeasureString(label, font); g.FillRectangle(Brushes.Black, r.BoundingBox.Left, Math.Max(0, r.BoundingBox.Top - size.Height), size.Width, size.Height); g.DrawString(label, font, Brushes.Lime, r.BoundingBox.Left, Math.Max(0, r.BoundingBox.Top - size.Height)); }
            }
            overlay.Save(Path.Combine(output, $"{model}-{engine}-regions.png"));
            var summary = new { model = result.Visual.ModelDiagnostics, pipeline = result.Document.Diagnostics, ocr = new { result.Ocr.EngineActual, result.Ocr.ModelName, blocks = result.Ocr.Blocks.Count, result.Ocr.TotalMilliseconds,
                    sourceBlocks = result.Ocr.Blocks.Select(x => new { x.Id, text = string.IsNullOrWhiteSpace(x.CorrectedText) ? x.RawText : x.CorrectedText, x.BoundingBox, polygon = x.Polygon.Select(p => new[] { p.X, p.Y }), x.ReadingOrder, x.Confidence }) },
                visualRegions = result.Visual.VisualRegions.Select(x => new { x.VisualRegionId, role = x.VisualRoleHint.ToString(), x.Confidence, x.BoundingBox, polygon = x.Polygon.Select(p => new[] { p.X, p.Y }), x.ReadingOrderHint, x.ParentContainerHint, x.SourceModel, x.OptionalTextHint }),
                regions = result.Document.Regions.Select(r => new { r.RegionId, polygon = r.Polygon.Select(p => new[] { p.X, p.Y }), sourceLinePolygons = r.SourceLinePolygons.Select(line => line.Select(p => new[] { p.X, p.Y })), bounds = r.BoundingBox, r.SourceBlockIds, r.OcrText, r.StructuredText, role = r.RoleType.ToString(), r.RoleConfidence, r.ReadingOrder, r.GroupId, r.TranslationUnitId, detectionSources = r.DetectionSources.Select(x => x.ToString()) }),
                translationUnits = result.Document.TranslationUnits, realApiCalls = 0, activeOperations = 0,
                windowsOcrInstances = WindowsOcrIsolationDiagnostics.Instances,
                windowsOcrRecognizeCalls = WindowsOcrIsolationDiagnostics.RecognizeCalls,
                visionWorkerPidBeforeDispose = vision.ActiveWorkerPid, ocrWorkerPidBeforeDispose = ocr.ActiveWorkerPid };
            File.WriteAllText(Path.Combine(output, $"{model}-{engine}-regions.json"), System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, $"{model}-{engine}-error.txt"), ex.ToString()); return 2; }
        finally { vision.DisposeAsync().AsTask().GetAwaiter().GetResult(); ocr.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void RunVisionV2PreviewSmoke(string imagePath, string output)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(output); using var source = new Bitmap(imagePath);
        using var form = new PreviewForm(source, PreviewMode.OcrOnly, new ApiSettings { OcrEngine = OcrEngineKind.Rapid,
            VisualModel = VisualModelKind.PPDocLayoutS, OcrLanguage = "英语" }, new OcrService(), new TranslationService());
        form.Shown += async (_, _) =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddMinutes(2);
                while (form.RecognitionRegionCountForSmoke == 0 && DateTime.UtcNow < deadline) await Task.Delay(200);
                if (form.RecognitionRegionCountForSmoke == 0) throw new TimeoutException("Live Preview V2 did not produce regions.");
                using var shot = new Bitmap(form.ClientSize.Width, form.ClientSize.Height); form.DrawToBitmap(shot, form.ClientRectangle);
                shot.Save(Path.Combine(output, "live-preview-v2.png"));
                File.WriteAllText(Path.Combine(output, "live-preview-v2.txt"), $"Regions={form.RecognitionRegionCountForSmoke}{Environment.NewLine}VisionWorkerPidDuringRun={form.VisionWorkerPidForSmoke}{Environment.NewLine}RealApiCalls=0");
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "live-preview-v2-error.txt"), ex.ToString()); Environment.ExitCode = 2; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    private static void RunRepair2OcrSmoke(string imagePath, string output)
    {
        var runtime = new OcrRuntimeManager(AppContext.BaseDirectory, new OcrService());
        using var form = new OcrComparisonForm(runtime, OcrComparisonMode.ExperimentalWindows);
        form.Shown += async (_, _) =>
        {
            try
            {
                form.LoadImageForSmoke(imagePath);
                foreach (var kind in Enum.GetValues<OcrEngineKind>())
                {
                    await form.RunEngineForSmokeAsync(kind);
                    form.SaveUiSmokeScreenshot(Path.Combine(output, kind.ToString().ToLowerInvariant() + ".png"), kind);
                }
                File.WriteAllText(Path.Combine(output, "smoke-summary.txt"),
                    string.Join(Environment.NewLine, Enum.GetValues<OcrEngineKind>().Select(kind =>
                        $"{kind}: {form.OutputForSmoke(kind).Split('\n').FirstOrDefault()}")) +
                    $"{Environment.NewLine}ActiveOperations={form.ActiveOperationCountForSmoke}");
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "smoke-error.txt"), ex.ToString());
                Environment.ExitCode = 2;
            }
            finally { form.Close(); }
        };
        Application.Run(form);
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void RunRepair3OcrSmoke(string imagePath, string output)
    {
        Directory.CreateDirectory(output);
        var runtime = new OcrRuntimeManager(AppContext.BaseDirectory, new OcrService());
        using var loaded = new Bitmap(imagePath);
        using var primary = new OcrComparisonForm(runtime, loaded,
            OcrComparisonMode.Standard, autoRunPrimary: true);
        primary.Shown += async (_, _) =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while ((primary.ResultsForSmoke.Count < 2 || primary.ActiveOperationCountForSmoke != 0) &&
                       DateTime.UtcNow < deadline)
                    await Task.Delay(100);
                if (primary.ResultsForSmoke.Count < 2 || primary.ActiveOperationCountForSmoke != 0)
                    throw new TimeoutException("Rapid/Paddle automatic OCR did not complete in 90 seconds.");
                primary.SaveUiSmokeScreenshot(Path.Combine(output, "rapid-screenshot-direct.png"),
                    OcrEngineKind.Rapid);
                primary.SaveUiSmokeScreenshot(Path.Combine(output, "paddle-screenshot-direct.png"),
                    OcrEngineKind.Paddle);
                var rapidResult = primary.ResultsForSmoke[OcrEngineKind.Rapid];
                var paddleResult = primary.ResultsForSmoke[OcrEngineKind.Paddle];
                File.WriteAllText(Path.Combine(output, "primary-summary.txt"),
                    $"ImageSession={primary.ImageSessionForSmoke}{Environment.NewLine}" +
                    $"RapidRequest={primary.RequestIdForSmoke(OcrEngineKind.Rapid)}{Environment.NewLine}" +
                    $"PaddleRequest={primary.RequestIdForSmoke(OcrEngineKind.Paddle)}{Environment.NewLine}" +
                    $"Rapid={primary.OutputForSmoke(OcrEngineKind.Rapid).Split('\n').FirstOrDefault()}{Environment.NewLine}" +
                    $"Paddle={primary.OutputForSmoke(OcrEngineKind.Paddle).Split('\n').FirstOrDefault()}{Environment.NewLine}" +
                    $"RapidBlocks={rapidResult.Blocks.Count}{Environment.NewLine}" +
                    $"RapidConfidenceAvailable={rapidResult.ConfidenceAvailableBlockCount}{Environment.NewLine}" +
                    $"RapidConfidenceUnavailable={rapidResult.ConfidenceUnavailableBlockCount}{Environment.NewLine}" +
                    $"RapidMinConfidence={rapidResult.Blocks.Where(x => x.Confidence.HasValue).Select(x => x.Confidence!.Value).DefaultIfEmpty(-1).Min():F4}{Environment.NewLine}" +
                    $"RapidLowConfidence={rapidResult.LowConfidenceBlockCount}{Environment.NewLine}" +
                    $"RapidRetryCount={rapidResult.PreprocessRetryCount}{Environment.NewLine}" +
                    $"RapidAmbiguous={rapidResult.AmbiguousBlockCount}{Environment.NewLine}" +
                    $"PaddleBlocks={paddleResult.Blocks.Count}{Environment.NewLine}" +
                    $"PaddleConfidenceAvailable={paddleResult.ConfidenceAvailableBlockCount}{Environment.NewLine}" +
                    $"PaddleConfidenceUnavailable={paddleResult.ConfidenceUnavailableBlockCount}{Environment.NewLine}" +
                    $"PaddleMinConfidence={paddleResult.Blocks.Where(x => x.Confidence.HasValue).Select(x => x.Confidence!.Value).DefaultIfEmpty(-1).Min():F4}{Environment.NewLine}" +
                    $"PaddleLowConfidence={paddleResult.LowConfidenceBlockCount}{Environment.NewLine}" +
                    $"PaddleRetryCount={paddleResult.PreprocessRetryCount}{Environment.NewLine}" +
                    $"PaddleAmbiguous={paddleResult.AmbiguousBlockCount}{Environment.NewLine}" +
                    $"CharacterConfidenceAvailable=False{Environment.NewLine}" +
                    $"CharacterTopKAvailable=False{Environment.NewLine}" +
                    $"CtcLogitsAvailable=False{Environment.NewLine}" +
                    $"WindowsTab={primary.HasEngineTabForSmoke(OcrEngineKind.Windows)}{Environment.NewLine}" +
                    $"ActiveOperations={primary.ActiveOperationCountForSmoke}");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "primary-error.txt"), ex.ToString());
                Environment.ExitCode = 2;
            }
            finally { primary.Close(); }
        };
        Application.Run(primary);

        using var experimental = new OcrComparisonForm(runtime, loaded,
            OcrComparisonMode.ExperimentalWindows, autoRunPrimary: false);
        experimental.Shown += async (_, _) =>
        {
            try
            {
                await experimental.RunEngineForSmokeAsync(OcrEngineKind.Windows);
                experimental.SaveUiSmokeScreenshot(Path.Combine(output, "windows-experimental.png"),
                    OcrEngineKind.Windows);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "windows-error.txt"), ex.ToString());
                Environment.ExitCode = 3;
            }
            finally { experimental.Close(); }
        };
        Application.Run(experimental);
        runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void RunRepair2EscSmoke(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var overlay = new CaptureOverlay(reusableLifecycle: true);
        using var frame = new Bitmap(640, 360);
        using (var graphics = Graphics.FromImage(frame)) graphics.Clear(Color.DarkSlateBlue);
        overlay.PrewarmHandle();
        overlay.PrepareReusableCaptureForTest(frame, System.Diagnostics.Stopwatch.GetTimestamp());
        using var escapeTimer = new System.Windows.Forms.Timer { Interval = 350 };
        using var failSafeTimer = new System.Windows.Forms.Timer { Interval = 3500 };
        var forced = false;
        var focusAtSend = false;
        var foregroundAtSend = false;
        var containsFocusAtSend = false;
        var hotKeyRegistered = false;
        var hotKeyError = 0;
        escapeTimer.Tick += (_, _) =>
        {
            escapeTimer.Stop();
            foregroundAtSend = NativeMethods.GetForegroundWindow() == overlay.Handle;
            containsFocusAtSend = overlay.ContainsFocus;
            focusAtSend = foregroundAtSend && containsFocusAtSend;
            (hotKeyRegistered, hotKeyError) = overlay.GetEscapeHotKeyStateForTest();
            NativeMethods.keybd_event((byte)Keys.Escape, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event((byte)Keys.Escape, 0, 0x0002, UIntPtr.Zero);
        };
        failSafeTimer.Tick += (_, _) => { failSafeTimer.Stop(); forced = true; overlay.Close(); };
        escapeTimer.Start(); failSafeTimer.Start();
        var result = overlay.ShowDialog();
        failSafeTimer.Stop();
        var passed = !forced && result == DialogResult.Cancel;
        File.WriteAllText(outputPath,
            $"passed={passed}{Environment.NewLine}forcedClose={forced}{Environment.NewLine}" +
            $"foregroundAtSend={foregroundAtSend}{Environment.NewLine}containsFocusAtSend={containsFocusAtSend}{Environment.NewLine}" +
            $"focusAtSend={focusAtSend}{Environment.NewLine}hotKeyRegistered={hotKeyRegistered}{Environment.NewLine}" +
            $"hotKeyError={hotKeyError}{Environment.NewLine}dialogResult={result}");
        if (!passed) Environment.ExitCode = 3;
    }
}
