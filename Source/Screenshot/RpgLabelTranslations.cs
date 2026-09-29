using System.Collections.Concurrent;
using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester;

// A separate, bounded name queue; never enables the game's translation hooks.
internal sealed class RpgLabelTranslations:IDisposable
{
    private readonly ConcurrentDictionary<string,string> names=new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> pending=new();
    private readonly ConcurrentDictionary<string,byte> requested=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim signal=new(0);
    private readonly Task worker;
    private readonly bool allLanguages;
    internal event Action? Changed;
    internal string Status {get;private set;}="";
    internal string Lookup(string source)=>names.GetValueOrDefault(source,"");
    internal bool NeedsRequest(string source)=>source.Length<=240&&!names.ContainsKey(source)&&!requested.ContainsKey(source);
    internal void Request(string source)
    {
        if(string.IsNullOrWhiteSpace(source)||source.Length>240||!Regex.IsMatch(source,allLanguages?@"\p{L}":@"[A-Za-z\u3040-\u30ff]"))return;
        if(!allLanguages&&Regex.IsMatch(source,@"[\u4e00-\u9fff]")&&!Regex.IsMatch(source,@"[\u3040-\u30ff]"))return;
        if(pending.Count>=512)return;
        if(names.ContainsKey(source)||!requested.TryAdd(source,0))return;
        pending.Enqueue(source);signal.Release();
    }
    internal RpgLabelTranslations(string exe,ApiSettings source,Func<string,ApiSettings,CancellationToken,Task<string>> translate,bool allLanguages=false)
    {
        this.allLanguages=allLanguages;
        var settings=ApiSettingsSnapshot.Copy(source);settings.TargetLanguage="zh-CN";
        var single=ApiSettingsSnapshot.Copy(settings);var batch=ApiSettingsSnapshot.Copy(settings);RpgTranslationPrompts.Configure(settings,single,batch);
        worker=Task.Run(async()=>
        {
            string path=Path.Combine(RpgMakerDataAdapter.Storage(exe),"labels",RpgTranslationCache.Id(settings,RpgTranslationCache.Endpoint(settings.ApiUrl))+".json");
            bool dirty=false;
            try
            {
                // Read only matching Chinese-language game caches; never overwrite them.
                if(source.TargetLanguage.StartsWith("zh",StringComparison.OrdinalIgnoreCase)||source.TargetLanguage.Contains("中文"))
                    foreach(var p in RpgTranslationCache.Load(exe,source).Entries)names[p.Key]=p.Value;
                var saved=RpgTranslationCache.Read(path)??RpgTranslationCache.Read(path+".previous");
                if(saved is not null)foreach(var p in saved)names[p.Key]=p.Value;
                Changed?.Invoke();
                bool configured=!string.IsNullOrWhiteSpace(settings.ApiUrl)&&!string.IsNullOrWhiteSpace(settings.Model)&&(settings.AllowEmptyApiKey||!string.IsNullOrWhiteSpace(settings.ApiKey));
                while(!lifetime.IsCancellationRequested)
                {
                    await signal.WaitAsync(lifetime.Token);
                    var items=new List<string>();
                    while(items.Count<24&&pending.TryDequeue(out var text))if(!names.ContainsKey(text))items.Add(text);
                    if(items.Count==0)continue;
                    if(!configured){Status="设置翻译方案后可补齐修改页中文名";continue;}
                    Status="正在补齐修改页中文名…";
                    var result=await RpgTranslationBatch.Translate(items.ToArray(),single,batch,translate,lifetime.Token);
                    foreach(var p in result){names[p.Key]=p.Value;dirty=true;}
                    Status=result.Count<items.Count?"部分中文名未能翻译，可点击刷新重试":"";
                    if(dirty){PortableDataStorage.WriteJson(path,names.ToDictionary(p=>p.Key,p=>p.Value));dirty=false;}
                    Changed?.Invoke();
                }
            }
            catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
            catch(Exception ex){Status="中文名读取失败："+SafeDiagnosticOutput.ExceptionSummary(ex);}
        });
    }
    internal void RetryMissing(){foreach(var key in requested.Keys)if(!names.ContainsKey(key))requested.TryRemove(key,out _);}
    public void Dispose(){lifetime.Cancel();}
}
