using System.Diagnostics;
using W = System.Windows;
using WC = System.Windows.Controls;
using WA = System.Windows.Automation;
using WM = System.Windows.Media;
using WI = System.Windows.Media.Imaging;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeGameMediaDialog : NativeDialogWindow
{
    private readonly RecentGame game;
    private readonly GameMediaStore store;
    private readonly GameMediaController controller;
    private readonly bool ownsController;
    private readonly WC.WrapPanel gallery=new();
    private readonly WC.TextBlock status=NativeDialogControls.Text(""),count=NativeDialogControls.Text("");
    private readonly WC.Button screenshot,start,stop,settings;
    private readonly WC.TextBlock shortcuts=NativeDialogControls.Text("");
    private readonly WC.TextBlock location;
    private bool busy;
    private long galleryRevision=-1;
    internal NativeGameMediaDialog(RecentGame game,GameMediaController? shared=null):base(UiLanguage.Format($"截图与录像 · {game.Name}"),1060,760)
    {
        this.game=game;ownsController=shared is null;controller=shared??new(()=>Task.FromResult(new[]{game}),_=>{});store=new(game.Id,gameName:game.Name);MinWidth=640;MinHeight=440;
        var header=NativeDialogControls.Text(game.Name,22,localize:false);header.FontWeight=W.FontWeights.SemiBold;
        screenshot=Button("保存截图","GameMediaScreenshot",()=>Run(CaptureAsync),true);
        start=Button("开始录像","GameMediaRecord",()=>Run(StartAsync));
        stop=Button("停止并保存","GameMediaStop",()=>Run(StopAsync),true);stop.IsEnabled=false;
        var folder=Button("打开保存文件夹 ▾","GameMediaFolder",()=>Task.CompletedTask);
        var folders=new WC.ContextMenu();
        foreach(var item in new[]{("截图文件夹",store.ScreenshotDirectory),("录像文件夹",store.VideoDirectory)})
        {var choice=new WC.MenuItem();WpfUiLanguage.Property(choice,WC.HeaderedItemsControl.HeaderProperty,item.Item1);choice.Click+=async(_,_)=>await Run(()=>{Open(item.Item2);return Task.CompletedTask;});folders.Items.Add(choice);}
        folder.ContextMenu=folders;folder.Click+=(_,_)=>{folders.PlacementTarget=folder;folders.IsOpen=true;};
        var refresh=Button("刷新","GameMediaRefresh",()=>Run(()=>{RefreshGallery();return Task.CompletedTask;}));
        settings=Button("快捷键设置","GameMediaSettings",()=>Run(()=>{using var dialog=new NativeGameMediaSettingsDialog(controller.Preferences,controller.SavePreferences);dialog.ShowDialog(this);return Task.CompletedTask;}));
        WA.AutomationProperties.SetAutomationId(status,"GameMediaStatus");
        var info=NativeDialogControls.Text("截图保存为 PNG。录像保存为 MP4，当前仅录制游戏窗口画面（无声音）。游戏最小化、退出或调整尺寸时，录像会停止并保存。");
        location=NativeDialogControls.Text("默认保存到软件 data 文件夹：截图在 screenshot，录像在 video，按游戏名称归档。",12);
        location.ToolTip=UiLanguage.Format($"截图：{store.ScreenshotDirectory}\n录像：{store.VideoDirectory}");WA.AutomationProperties.SetAutomationId(location,"GameMediaLocation");
        WA.AutomationProperties.SetAutomationId(shortcuts,"GameMediaShortcuts");
        var top=NativeDialogControls.Column(header,NativeDialogControls.Row(screenshot,start,stop,folder,settings,refresh),info,shortcuts,status,count);
        var body=new WC.Grid{Margin=new(22)};
        body.RowDefinitions.Add(new(){Height=W.GridLength.Auto});body.RowDefinitions.Add(new(){Height=new(1,W.GridUnitType.Star)});body.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        body.Children.Add(top);
        var scroll=new WC.ScrollViewer{Content=gallery,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Disabled};WC.Grid.SetRow(scroll,1);body.Children.Add(scroll);
        WC.Grid.SetRow(location,2);body.Children.Add(location);Content=body;
        Loaded+=(_,_)=>{RefreshGallery();UpdateButtons();controller.Changed+=ControllerChanged;};
        UiLanguage.LanguageChanged+=LanguageChanged;
        Closed+=(_,_)=>{UiLanguage.LanguageChanged-=LanguageChanged;controller.Changed-=ControllerChanged;if(ownsController)controller.Dispose();};
        status.Text=controller.Status;
    }
    private void LanguageChanged(object? sender,EventArgs e){Title=UiLanguage.Format($"截图与录像 · {game.Name}");location.ToolTip=UiLanguage.Format($"截图：{store.ScreenshotDirectory}\n录像：{store.VideoDirectory}");RefreshGallery();ControllerChanged();}
    private void ControllerChanged()
    {
        if(IsDisposed)return;status.Text=controller.Status;UpdateButtons();
        if(!controller.Busy&&galleryRevision!=controller.GalleryRevision)RefreshGallery();
    }
    private static WC.Button Button(string text,string id,Func<Task> action,bool primary=false)
    {
        var button=WpfThemeResources.Button(text,null,async()=>await action(),primary);button.Height=36;
        WA.AutomationProperties.SetAutomationId(button,id);return button;
    }
    private async Task Run(Func<Task> action)
    {
        if(busy)return;busy=true;UpdateButtons();
        try{await action();}
        catch(Exception ex){status.Text=UiOperationText.Error(ex);}
        finally{busy=false;if(!IsDisposed)UpdateButtons();}
    }
    private void UpdateButtons()
    {
        screenshot.IsEnabled=!busy&&!controller.Busy;start.IsEnabled=!busy&&!controller.Busy&&!controller.Recording;
        stop.IsEnabled=!busy&&!controller.Busy&&controller.Recording;settings.IsEnabled=!busy;
        shortcuts.Text=controller.ShortcutStatus;
    }
    private Task CaptureAsync()=>controller.CaptureAsync(game);
    private Task StartAsync()=>controller.StartAsync(game);
    private Task StopAsync()=>controller.StopAsync();
    private void RefreshGallery()
    {
        galleryRevision=controller.GalleryRevision;
        var items=store.Read();count.Text=UiLanguage.Format($"{items.Count(x=>x.Kind=="screenshot")} 张截图 · {items.Count(x=>x.Kind=="video")} 段录像");gallery.Children.Clear();
        if(items.Count==0){gallery.Children.Add(NativeDialogControls.Text("还没有截图或录像。保存后会显示在这里。"));return;}
        foreach(var entry in items.Take(300))
        {
            var panel=new WC.StackPanel{Width=278};
            var thumb=entry.Kind=="video"?entry.ThumbnailFile:entry.FileName;
            var preview=new WC.Image{Height=154,Stretch=WM.Stretch.Uniform};
            if(thumb is not null)
            {
                try
                {
                    var image=new WI.BitmapImage();image.BeginInit();image.CacheOption=WI.BitmapCacheOption.OnLoad;image.DecodePixelWidth=600;
                    using var file=File.OpenRead(store.ExistingFilePath(thumb));image.StreamSource=file;image.EndInit();image.Freeze();preview.Source=image;
                }catch(Exception ex)when(ex is IOException or NotSupportedException or ArgumentException){}
            }
            panel.Children.Add(preview);
            var label=NativeDialogControls.Text((entry.Kind=="video"?UiLanguage.Format($"▶ 录像 · {entry.DurationSeconds:0.0} 秒"):UiLanguage.Text("截图"))+" · "+entry.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm"),13);label.Margin=new(8);panel.Children.Add(label);
            var button=new WC.Button{Content=panel,Margin=new(0,0,12,12),Padding=new(0),ToolTip=entry.FileName};
            WA.AutomationProperties.SetName(button,UiLanguage.Text(entry.Kind=="video"?"播放录像":"打开截图")+" "+entry.FileName);
            button.Click+=async(_,_)=>await Run(()=>{Open(store.ExistingFilePath(entry.FileName));return Task.CompletedTask;});gallery.Children.Add(button);
        }
        if(items.Count>300)gallery.Children.Add(NativeDialogControls.Text("这里显示最近 300 项，全部文件可在保存文件夹中查看。"));
    }
    private static void Open(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
}

public sealed partial class MainForm
{
    private async Task OpenGameMediaAsync()
    {
        if(_selectedGame is not {} game)return;
        await _rememberSelectedTask;var store=await RecentStore();
        if(IsDisposed||store.Find(game.ExePath) is not {} record)return;
        using var dialog=new NativeGameMediaDialog(record,MediaController);dialog.ShowDialog(this);
    }
}
