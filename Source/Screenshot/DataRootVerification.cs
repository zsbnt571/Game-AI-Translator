using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;

namespace ScreenshotTranslationUiTester;

/// <summary>Explicit non-interactive lifecycle check of the real GUI data owners.</summary>
internal static class DataRootVerification
{
    internal static int Run(string[] args)
    {
        if(!AppDataPaths.HasExplicitRoot || !AppDataPaths.IsDataRootVerification || !AppDataPaths.DisableGlobalInput)
            throw new InvalidOperationException("A dedicated data root and disabled global input are required.");
        var readback=args.Contains("--verify-data-root-readback",StringComparer.OrdinalIgnoreCase);
        var marker=Path.Combine(AppDataPaths.Root,"verification-owned.json");
        if(!readback && (File.Exists(marker)||File.Exists(AppDataPaths.SettingsPath)||Directory.EnumerateFileSystemEntries(AppDataPaths.HistoryRoot).Any()))
            throw new InvalidOperationException("Verification requires a fresh data root and never overwrites settings or History.");
        if(readback && !File.Exists(marker))throw new InvalidOperationException("Readback requires this verification's ownership marker.");
        if(!readback)File.WriteAllText(marker,JsonSerializer.Serialize(new{Purpose="Synthetic DATA-ROOT verification only",BuildIdentity.BuildId}));
        var output=Path.Combine(AppDataPaths.Root,"verification");
        Directory.CreateDirectory(output);
        var result=new Dictionary<string,object?>
        {
            ["BuildId"]=BuildIdentity.BuildId,["ProcessId"]=Environment.ProcessId,["Readback"]=readback,
            ["DataRoot"]=AppDataPaths.Root,["AppLogRoot"]=AppLog.Root,["ProductLogsRoot"]=AppDataPaths.ProductLogsRoot,
            ["CacheRoot"]=AppDataPaths.CacheRoot,["CacheStorage"]="OCR and translation memory only; Python/tool caches use CacheRoot",
            ["InstanceId"]=AppDataPaths.InstanceId,["MutexName"]=SingleInstanceCoordinator.DiagnosticMutexName,
            ["PipeName"]=SingleInstanceCoordinator.DiagnosticPipeName,["DefaultStableId"]=BuildIdentity.StableApplicationId,
            ["Environment"]=AppDataPaths.IsolatedEnvironment(),["RealApiRequests"]=0,["UserDataMigration"]=false
        };
        var exit=1;var foregroundBefore=GetForegroundWindow();var clipboardBefore=GetClipboardSequenceNumber();
        using var context=new ApplicationContext();
        MainForm? main=null;
        EventHandler? started=null;
        started=async(_,_) =>
        {
            Application.Idle-=started;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(100));
            try
            {
                using var instance=new SingleInstanceCoordinator();
                if(!instance.IsPrimary)throw new InvalidOperationException("Dedicated verification instance is already active.");
                result["PrimaryInstance"]=true;
                RunPathContracts();
                result["PathContractsPassed"]=10;
                main=new MainForm(null,new(CaptureFlashDiagnosticMode.CaptureOnly,false,false));
                _=main.Handle;
                result["Gui"]=main.VerifyDataRootServicesForSmoke(readback);
                // Exercise the real History/Preview highlighter, then verify its actual persisted owner.
                var diagnosticsRoot=Path.GetFullPath(RealPathDiagnosticTrace.Root);
                var expectedDiagnostics=Path.Combine(AppDataPaths.LogsRoot,"diagnostics");
                var ownershipPath=Path.Combine(diagnosticsRoot,"highlight","ownership.jsonl");
                result["Diagnostics"]=new{Root=diagnosticsRoot,ExpectedRoot=expectedDiagnostics,
                    OwnershipPath=ownershipPath,OwnershipExists=File.Exists(ownershipPath)};
                if(!string.Equals(diagnosticsRoot,expectedDiagnostics,StringComparison.OrdinalIgnoreCase)||
                   !File.Exists(ownershipPath)||new FileInfo(ownershipPath).Length==0)
                    throw new InvalidOperationException("History highlight diagnostics escaped the data root or were not persisted.");
                var runtimeArg=Argument(args,"--verify-runtime-root=");
                var runtime=runtimeArg??AppContext.BaseDirectory;
                result["RuntimeSource"]=readback?"NOT USED IN READBACK":runtimeArg is null?"BUNDLED ACTUAL PACKAGE":"REGISTERED SHARED TEST RUNTIME";
                result["RuntimeApplicationRoot"]=runtime;
                if(!readback)
                {
                    var python=Path.Combine(runtime,"runtime","python","python.exe");
                    result["PythonEnvironment"]=await VerifyPythonAsync(python,timeout.Token);
                    var image=Argument(args,"--verify-ocr-image=");
                    if(image is not null)
                    {
                        int? pid;
                        await using(var manager=new OcrRuntimeManager(runtime,new OcrService()))
                        {
                            using var bitmap=new Bitmap(image);
                            var ocr=await manager.RecognizeWithFallbackAsync(OcrEngineKind.Rapid,bitmap,"英语",timeout.Token,allowAutomaticWindowsFallback:false);
                            pid=manager.ActiveWorkerPid;
                            var sourceSha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(image)));
                            File.WriteAllText(Path.Combine(output,"FRESH-OCR.json"),JsonSerializer.Serialize(ocr,new JsonSerializerOptions{WriteIndented=true}));
                            result["Rapid"]=new{ocr.Success,ocr.EngineRequested,ocr.EngineActual,ocr.FallbackUsed,ocr.CacheHit,
                                SourcePath=Path.GetFullPath(image),SourceSHA256=sourceSha,SourceWidth=bitmap.Width,SourceHeight=bitmap.Height,
                                FullOcrPath=Path.Combine(output,"FRESH-OCR.json"),
                                Blocks=ocr.Blocks.Count,WorkerPid=pid,manager.RuntimeRoot,manager.LogsRoot};
                            if(!ocr.Success||ocr.EngineActual!=OcrEngineKind.Rapid||ocr.FallbackUsed||pid is null)
                                throw new InvalidOperationException("Fresh Rapid OCR failed.");
                        }
                        result["RapidWorkerExited"]=!Alive(pid.Value);
                        if(Alive(pid.Value))throw new InvalidOperationException("Rapid worker survived disposal.");
                    }
                }
                result["ForegroundPreserved"]=foregroundBefore==GetForegroundWindow();
                result["ClipboardSequencePreserved"]=clipboardBefore==GetClipboardSequenceNumber();
                // A user can independently change focus/clipboard during this test; report observations, never restore them.
                result["Pass"]=true;exit=0;
            }
            catch(Exception ex)
            {
                result["Pass"]=false;result["Error"]=SafeDiagnosticOutput.ExceptionSummary(ex);
                AppLog.Write("data-root","Hidden verification failed",ex);
            }
            finally
            {
                try{main?.ExitForSmoke();main?.Dispose();result["MainFormDisposed"]=main?.IsDisposed??true;}
                catch(Exception ex){exit=1;result["Pass"]=false;result["CloseError"]=SafeDiagnosticOutput.ExceptionSummary(ex);}
                result["ExitCode"]=exit;result["FinishedUtc"]=DateTimeOffset.UtcNow;
                try{File.WriteAllText(Path.Combine(output,readback?"READBACK.json":"RESULT.json"),
                    JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));}
                catch(Exception ex){exit=1;AppLog.Write("data-root","Unable to persist verification result",ex);}
                context.ExitThread();
            }
        };
        Application.Idle+=started;
        Application.Run(context);
        Application.Idle-=started;
        return exit;
    }

    private static async Task<JsonElement> VerifyPythonAsync(string python,CancellationToken token)
    {
        if(!File.Exists(python))throw new FileNotFoundException("Registered Python runtime missing.",python);
        var info=new ProcessStartInfo(python){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,
            RedirectStandardError=true,WorkingDirectory=AppDataPaths.TempRoot};
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import os,json,tempfile,pathlib; keys=['TEMP','TMP','PYTHONPYCACHEPREFIX','PYTHONDONTWRITEBYTECODE','PIP_CACHE_DIR','XDG_CACHE_HOME']; d={k:os.environ.get(k) for k in keys}; d.update(pid=os.getpid(),tempfile=tempfile.gettempdir()); p=pathlib.Path(os.environ['XDG_CACHE_HOME'])/'python-verification.json'; p.write_text(json.dumps(d),encoding='utf-8'); print(json.dumps(d))");
        AppDataPaths.ConfigureChildEnvironment(info);
        using var process=Process.Start(info)??throw new InvalidOperationException("Unable to start environment probe.");
        var stdout=process.StandardOutput.ReadToEndAsync(token);var stderr=process.StandardError.ReadToEndAsync(token);
        try{await process.WaitForExitAsync(token);}
        catch{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}throw;}
        var text=await stdout;var error=await stderr;
        if(process.ExitCode!=0)throw new InvalidOperationException("Python environment probe failed: "+SafeDiagnosticOutput.Redact(error));
        using var json=JsonDocument.Parse(text);
        if(!string.Equals(json.RootElement.GetProperty("tempfile").GetString(),AppDataPaths.TempRoot,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Python tempfile escaped data root.");
        return json.RootElement.Clone();
    }

    private static void RunPathContracts()
    {
        foreach(var invalid in new[]{"relative","E:relative",@"E:\",@"\\server\share\folder",@"\\?\E:\folder",@"E:\test\..\outside",@"E:\test:stream",@"E:\test. "})
        {
            try{AppDataPaths.ValidateExplicitRoot(invalid);}
            catch(ArgumentException){continue;}
            throw new InvalidOperationException("Unsafe data root accepted: "+invalid);
        }
        if(ConfigurationManager.ResolveSettingsPath()!=AppDataPaths.SettingsPath)
            throw new InvalidOperationException("Inherited test settings overrode explicit data root.");
        try{AppDataPaths.ConstrainSettingsPath(Path.Combine(Path.GetPathRoot(AppDataPaths.Root)!,"outside-settings.json"));}
        catch(ArgumentException){return;}
        throw new InvalidOperationException("Settings escaped explicit root.");
    }
    private static bool Alive(int pid){try{using var p=Process.GetProcessById(pid);return !p.HasExited;}catch(ArgumentException){return false;}}
    private static string? Argument(string[] args,string prefix)=>args.FirstOrDefault(x=>x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
}
