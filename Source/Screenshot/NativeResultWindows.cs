using System.Text;
using W = System.Windows;
using WC = System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

internal sealed class NativeSavedHistoryWindow : NativeDialogWindow
{
    internal NativeSavedHistoryWindow(SessionHistoryItem item) : base("历史保存结果 · 原图缺失",1000,720)
    {
        if(item.OriginalPng.Length!=0)throw new ArgumentException("Use normal history preview when source exists",nameof(item));
        var grid=new WC.Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new(){Width=new(300)});
        var image=new WC.Image{Stretch=System.Windows.Media.Stretch.Uniform};
        if(item.TranslatedImage is {Length:>0}){using var stream=new MemoryStream(item.TranslatedImage,false);using var decoded=Image.FromStream(stream,true,true);using var bitmap=new Bitmap(decoded);image.Source=WpfDrawing.Image(bitmap);}
        grid.Children.Add(image);var text=NativeDialogControls.Input(item.TranslationText??item.TranslationSummary??"",true,true);WC.Grid.SetColumn(text,1);grid.Children.Add(text);
        var root=new WC.DockPanel{Margin=new(16)};var notice=NativeDialogControls.Text("原图已缺失。这里保留当时的译图和文字；重新识别或翻译需要重新打开原图。");WC.DockPanel.SetDock(notice,WC.Dock.Top);root.Children.Add(notice);root.Children.Add(grid);Content=root;
    }
}

internal sealed class NativeRuntimeTranslationWindow : NativeDialogWindow
{
    internal RpgTranslationSession Session {get;}
    private readonly WC.TextBox text=NativeDialogControls.Input("",true,true);
    private readonly WC.TextBlock state=NativeDialogControls.Text("");
    private readonly System.Windows.Threading.DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    internal NativeRuntimeTranslationWindow(string game,RpgTranslationSession session):base(game+" · 备用译文窗",620,480)
    {
        Session=session;ShowInTaskbar=true;MinWidth=360;MinHeight=240;
        var root=new WC.DockPanel{Margin=new(16)};
        void Top(W.UIElement view){WC.DockPanel.SetDock(view,WC.Dock.Top);root.Children.Add(view);}
        Top(NativeDialogControls.Text("显示暂不能替换到游戏里的文字；关闭此窗后仍会翻译和保存，关闭游戏翻译才会停止。"));
        var pinned=new WC.CheckBox{Content=UiLanguage.Text("保持窗口在最前面"),Margin=new(0,0,0,10)};pinned.Checked+=(_,_)=>Topmost=true;pinned.Unchecked+=(_,_)=>Topmost=false;Top(pinned);Top(state);root.Children.Add(text);Content=root;
        Loaded+=(_,_)=>{session.SetReadOnlyWindowVisible(true);RefreshText();timer.Start();};timer.Tick+=(_,_)=>RefreshText();Closed+=(_,_)=>{timer.Stop();session.SetReadOnlyWindowVisible(false);};
    }
    private void RefreshText()
    {
        var sources=Session.ReadOnlyTexts;var output=new StringBuilder();int translated=0;
        foreach(var source in sources){string value=Session.Lookup(source);if(value.Length>0){translated++;output.AppendLine(value);output.AppendLine(source);}else{output.AppendLine(source);output.AppendLine(UiLanguage.Text(Session.Enabled?"正在等待译文…":"翻译已停止"));}output.AppendLine();}
        state.Text=UiLanguage.Text(!Session.Enabled?"翻译已停止，已有译文仍可查看。":sources.Length==0?"等待游戏中可读取的文字…":$"最近读取 {sources.Length} 条 · 已有译文 {translated} 条");
        string next=output.ToString();if(text.Text==next)return;int selection=text.SelectionStart,length=text.SelectionLength;double scroll=text.VerticalOffset;text.Text=next;text.Select(Math.Min(selection,next.Length),Math.Min(length,Math.Max(0,next.Length-selection)));text.ScrollToVerticalOffset(scroll);
    }
}

internal static class NativeToast
{
    internal static void Show(string message)
    {
        var dispatcher=W.Application.Current?.Dispatcher;if(dispatcher is null)return;
        if(!dispatcher.CheckAccess()){dispatcher.BeginInvoke(()=>Show(message));return;}
        var form=new NativeDialogWindow("Fusion",280,72){WindowStyle=W.WindowStyle.None,ResizeMode=W.ResizeMode.NoResize,ShowActivated=false,Topmost=true,WindowStartupLocation=W.WindowStartupLocation.Manual,Opacity=.94};
        var area=W.SystemParameters.WorkArea;form.Left=area.Right-form.Width-24;form.Top=area.Bottom-form.Height-24;
        form.Content=new NativeLocalizedTextBlock{Text=message,TextWrapping=W.TextWrapping.Wrap,VerticalAlignment=W.VerticalAlignment.Center,HorizontalAlignment=W.HorizontalAlignment.Center,Margin=new(14)};
        var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(1400)};timer.Tick+=(_,_)=>{timer.Stop();form.Close();};form.Closed+=(_,_)=>timer.Stop();form.Show();timer.Start();
    }
}
