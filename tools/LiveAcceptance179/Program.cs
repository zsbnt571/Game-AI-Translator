using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScreenshotTranslationUiTester;

internal static class Program
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    private static readonly ConcurrentQueue<object> Pairs=new();
    private static int httpRequests,providerJobs;
    private static string output="";
    private static void Save(string name,object value)=>File.WriteAllText(Path.Combine(output,name+".json"),JsonSerializer.Serialize(value,Json));
    private static void Say(string message)=>Console.WriteLine(DateTimeOffset.Now.ToString("HH:mm:ss")+" "+message);
    private static string Required(string[] args,string key)=>args.FirstOrDefault(a=>a.StartsWith(key+"=",StringComparison.Ordinal))?[(key.Length+1)..]??throw new ArgumentException("Missing "+key);
    [STAThread]
    private static int Main(string[] args)
    {
        try{return Run(args).GetAwaiter().GetResult();}
        catch(Exception ex){Console.Error.WriteLine(SafeDiagnosticOutput.ExceptionSummary(ex));return 1;}
    }
    private static async Task<int> Run(string[] args)
    {
        string mode=Required(args,"--mode"),exe=Path.GetFullPath(Required(args,"--game"));
        output=Path.GetFullPath(Required(args,"--output"));Directory.CreateDirectory(output);
        string data=Path.GetFullPath(Required(args,"--data-root"));
        if(!Directory.Exists(data))throw new IOException("Prepare an isolated data directory first.");
        AppDataPaths.Initialize(args);
        var game=GameDetector.Detect(exe);
        Save("identity",new{version=BuildIdentity.BuildVersion,game.Engine,game.AdapterId,game.Support,Mode=mode});
        Say("Detected "+game.Engine+" / "+game.AdapterId);
        if(mode=="inspect")return 0;
        string root=Path.GetDirectoryName(exe)!;
        string marker=Path.Combine(root,GameTestCopyEnvironment.Marker);
        if(!File.Exists(marker)||File.ReadAllText(marker).Trim()!="FusionTranslationTestCopy/1")throw new IOException("A marked isolated game copy is required.");
        void Install(){if(game.AdapterId=="renpy")RenpyGameAdapter.Install(exe);else if(game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz")RpgMakerDataAdapter.Install(exe);else EmbeddedGameAdapters.Install(game);}
        void Restore(){if(game.AdapterId=="renpy")RenpyGameAdapter.Restore(exe);else if(game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz")RpgMakerDataAdapter.Restore(exe);else EmbeddedGameAdapters.Restore(game);}
        if(mode=="restore"){Restore();Save("restore",new{completed=true});return 0;}
        if(mode=="install"){Install();Save("install",new{completed=true});return 0;}
        if(!args.Contains("--allow-api"))throw new InvalidOperationException("Real API requests require --allow-api and user authorization.");
        var fusion=new FusionConfiguration();var basis=ConfigurationManager.Load(AppDataPaths.SettingsPath,false);
        string original=args.FirstOrDefault(a=>a.StartsWith("--profile-game="))?[15..]??exe;
        var settings=fusion.GameSettings(basis,original);
        SafeDiagnosticOutput.RegisterCredential(settings.ApiKey);
        settings.RequestKind=TranslationRequestKind.GameText;
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        TranslationService.RequestStartingForDiagnostics=()=>
        {
            if(Interlocked.Increment(ref httpRequests)>24){deadline.Cancel();throw new OperationCanceledException("Acceptance HTTP request budget reached.",deadline.Token);}
        };
        var service=new TranslationService();
        var options=game.AdapterId is "rpg-maker-mv" or "rpg-maker-mz" or "renpy"?EmbeddedTranslationOptions.ForLegacy(exe):EmbeddedGameAdapters.Options(game);
        RpgTextCatalogResult? catalogue=null;
        if(options.ConfiguredDesktopCatalog is {} configured)catalogue=await configured(settings,deadline.Token);
        else if(options.DesktopCatalog is {} read)catalogue=await read(deadline.Token);
        if(catalogue is not null)
        {
            Save("catalogue",new{Total=catalogue.Texts.Length,catalogue.Files,catalogue.ReadCompleted,catalogue.SelectedLanguage,catalogue.ScriptSourceLanguage,Unlocalized=catalogue.UnlocalizedSourceTexts?.Count??0,Unreadable=catalogue.Unreadable.Length});
            var selected=catalogue.Texts.Where(t=>t.Length is >=4 and <=500&&TranslationLanguagePolicy.ShouldTranslate(t,settings)).Take(12).ToArray();
            catalogue=catalogue with{Texts=selected,UnlocalizedSourceTexts=catalogue.UnlocalizedSourceTexts?.Intersect(selected).ToHashSet(StringComparer.Ordinal),TrustedSourceTexts=catalogue.TrustedSourceTexts?.Intersect(selected).ToHashSet(StringComparer.Ordinal)};
            var bounded=catalogue;
            options=options with{DesktopCatalog=_=>Task.FromResult(bounded),ConfiguredDesktopCatalog=null,RemoteCatalog=false};
        }
        if(mode=="api")
        {
            if(catalogue is null)throw new InvalidOperationException("This engine needs a live bridge for its catalogue.");
            var selected=catalogue.Texts.Take(6).ToArray();
            var single=ApiSettingsSnapshot.Copy(settings);var batch=ApiSettingsSnapshot.Copy(settings);RpgTranslationPrompts.Configure(settings,single,batch);
            var watch=Stopwatch.StartNew();int requests=0;
            var result=await RpgTranslationBatch.Translate(selected,single,batch,async(text,profile,ct)=>
            {var reply=await service.TranslatePlainAsync(text,profile,ct);Interlocked.Add(ref requests,reply.RequestCount);return reply.Text;},deadline.Token,syntax:options.Syntax);
            Save("api",new{selected=selected.Length,translated=result.Count,missing=selected.Length-result.Count,requestCount=requests,elapsedMs=watch.ElapsedMilliseconds,FailureCodes=(result as RpgTranslationBatchResult)?.Failures.Values.Distinct().ToArray()});
            Save("private-samples",selected.Select(text=>new{Source=text,Translation=result.GetValueOrDefault(text)}));
            Say("Game translation API completed: "+result.Count+" / "+selected.Length+", HTTP "+requests);return result.Count==selected.Length?0:1;
        }
        if(mode!="live")throw new ArgumentException("Unknown mode.");
        Install();Save("install",new{completed=true});
        using IEmbeddedTranslationConnection connection=game.AdapterId is "renpy" or "rpg-maker-mv" or "rpg-maker-mz"?new RpgGameDataConnection(exe):EmbeddedGameAdapters.Connect(game);
        using var process=await connection.LaunchAsync(true,deadline.Token);connection.Launched(process.Id);
        Save("launched",new{process.Id,game.Engine,game.AdapterId});Say("Game launched: PID "+process.Id);
        await connection.AcceptAsync(deadline.Token);Say("Bridge connected.");
        using var translation=new RpgTranslationSession(connection,exe,settings,async(text,profile,ct)=>
        {
            // This is an acceptance budget, not a product queue limit. Keep the
            // remaining jobs cancellably waiting instead of consuming more API.
            if(Interlocked.Increment(ref providerJobs)>10)await Task.Delay(Timeout.Infinite,ct);
            using var request=CancellationTokenSource.CreateLinkedTokenSource(ct,deadline.Token);
            var result=await service.TranslatePlainAsync(text,profile,request.Token);
            Pairs.Enqueue(new{Source=text,Translation=result.Text});return result.Text;
        },options);
        await translation.StartAsync();
        void Snapshot(string name)
        {
            var report=new TranslationDiagnosticReport(DateTimeOffset.Now,BuildIdentity.BuildVersion,game.Engine,connection.Connected,translation.Enabled,translation.RestorePending,0,translation.Snapshot);
            File.WriteAllText(Path.Combine(output,name+".json"),report.Json());
            Save("api-use",new{httpRequests,providerJobs,Completed=Pairs.Count});Save("private-samples",Pairs.ToArray());
        }
        Save("ready",new{ready=true,Instruction="Inspect the real game. Create disable-request, then stop-request in this output directory to stop this acceptance session."});
        while(!deadline.IsCancellationRequested&&connection.Connected&&!File.Exists(Path.Combine(output,"stop-request")))
        {
            Snapshot("latest");
            if(File.Exists(Path.Combine(output,"disable-request"))&&translation.Enabled)
            {await translation.StopAsync();Snapshot("disabled");Say("Translation disabled; restore pending: "+translation.RestorePending);}
            await Task.Delay(1000,deadline.Token);
        }
        if(translation.Enabled&&connection.Connected)await translation.StopAsync();
        Snapshot("final");Say("Session ended. Close the game, then run restore mode to verify component removal.");return 0;
    }
}
