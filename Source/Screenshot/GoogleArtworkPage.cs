using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Text.Json;
using System.Windows.Threading;
using W=System.Windows;

namespace ScreenshotTranslationUiTester;

// Google's image result page needs JavaScript. Use a normal, isolated WebView
// to render that public page, then show the extracted results in our native UI.
// This profile never reads the user's browser, credentials or browsing history.
internal static class GoogleArtworkPage
{
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowStyle(IntPtr window,int index);
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowStyle(IntPtr window,int index,IntPtr style);
    private static readonly SemaphoreSlim slot=new(1);
    internal static async Task<string> ReadAsync(string url,CancellationToken token)
    {
        if(!AllowedNavigation(url))throw new IOException("Invalid image search URL.");
        await slot.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread=new Thread(()=>Run(url,token,result)){IsBackground=true,Name="Fusion image search"};
            thread.SetApartmentState(ApartmentState.STA);thread.Start();
            return await result.Task.ConfigureAwait(false);
        }
        finally{slot.Release();}
    }
    private static void Run(string url,CancellationToken token,TaskCompletionSource<string> result)
    {
        var dispatcher=Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        WebView2? web=null;W.Window? window=null;
        async void Render()
        {
            string? html=null;Exception? failure=null;
            try
            {
                token.ThrowIfCancellationRequested();
                web=new WebView2();
                window=new W.Window{Content=web,Width=1024,Height=768,Left=-32000,Top=-32000,ShowActivated=false,ShowInTaskbar=false,WindowStyle=W.WindowStyle.None};
                window.SourceInitialized+=(_,_)=>{var handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;SetWindowStyle(handle,-20,new(GetWindowStyle(handle,-20).ToInt64()|0x08000000));};
                window.Show();
                var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(AppDataPaths.CacheRoot,"artwork-browser")).WaitAsync(token);
                await web.EnsureCoreWebView2Async(environment).WaitAsync(token);
                var core=web.CoreWebView2;
                core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;
                core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;
                core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
                core.NewWindowRequested+=(_,e)=>e.Handled=true;
                core.DownloadStarting+=(_,e)=>e.Cancel=true;
                core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>
                {
                    if(!AllowedResource(e.Request.Uri))e.Response=environment.CreateWebResourceResponse(Stream.Null,403,"Blocked","");
                };
                var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                core.NavigationStarting+=(_,e)=>{if(!AllowedNavigation(e.Uri)){e.Cancel=true;ready.TrySetException(new ArtworkSourceRestrictedException("Image source requires verification."));}};
                core.NavigationCompleted+=(_,e)=>{if(e.IsSuccess)ready.TrySetResult();else ready.TrySetException(new IOException("Image source could not load."));};
                core.Navigate(url);await ready.Task.WaitAsync(token);
                // Result cards may be hydrated after NavigationCompleted. Stop
                // promptly for a challenge, cancellation, or a populated page.
                for(int attempt=0;attempt<12;attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    html=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.documentElement.outerHTML").WaitAsync(token))??"";
                    if(html.Length>4_000_000)throw new IOException("Image result page is too large.");
                    ArtworkSearchService.EnsureReadable(html);
                    bool cards=System.Text.RegularExpressions.Regex.IsMatch(html,@"<div\b[^>]*\bdata-lpage\s*=",System.Text.RegularExpressions.RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(200));
                    bool metadata=System.Text.RegularExpressions.Regex.IsMatch(html,"\\[0,\"[^\"\\s]{1,100}\",\\[\"https://",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(200));
                    if(cards&&metadata||html.Contains("href=\"/imgres?"))break;
                    await Task.Delay(250,token);
                }
            }
            catch(Exception ex){failure=ex;}
            finally
            {
                try{web?.Dispose();window?.Close();}catch(Exception ex){failure??=ex;}
                if(failure is OperationCanceledException)result.TrySetCanceled(token);
                else if(failure is not null)result.TrySetException(failure is IOException?failure:new IOException("Google image search is unavailable.",failure));
                else result.TrySetResult(html??"");
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
        dispatcher.BeginInvoke((Action)Render);Dispatcher.Run();
    }
    internal static bool AllowedNavigation(string value)=>Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host=="www.google.com"&&uri.AbsolutePath=="/search";
    internal static bool AllowedResource(string value)
    {
        if(!ArtworkWebSources.PublicUri(value,out var uri))return false;
        return new[]{"google.com","gstatic.com","googleusercontent.com","googleapis.com"}.Any(host=>uri!.Host==host||uri.Host.EndsWith("."+host,StringComparison.OrdinalIgnoreCase));
    }
}
