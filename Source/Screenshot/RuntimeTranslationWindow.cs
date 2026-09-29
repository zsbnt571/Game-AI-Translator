using System.Text;

namespace ScreenshotTranslationUiTester;

// An optional display-only fallback. Its session is pinned when opened so
// switching the library selection cannot send one game's text to another.
internal sealed class RuntimeTranslationWindow : Form
{
    internal RpgTranslationSession Session {get;}
    private readonly RichTextBox text=new(){Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.None,DetectUrls=false};
    private readonly Label state=new(){Dock=DockStyle.Top,AutoSize=true,Padding=new(0,8,0,8)};
    private readonly System.Windows.Forms.Timer refresh=new(){Interval=500};
    internal RuntimeTranslationWindow(string game,RpgTranslationSession session,ApiSettings appearance)
    {
        Session=session;Text=game+" · 备用译文窗";Size=new(620,480);MinimumSize=new(360,240);
        StartPosition=FormStartPosition.CenterScreen;Padding=new(16);ShowInTaskbar=true;
        var note=new Label{Dock=DockStyle.Top,AutoSize=true,Text="显示暂不能替换到游戏里的文字；关闭此窗后停止补译新文字。",Padding=new(0,0,0,8)};
        var top=new CheckBox{Dock=DockStyle.Top,AutoSize=true,Text="保持窗口在最前面",Padding=new(0,0,0,8)};
        top.CheckedChanged+=(_,_)=>TopMost=top.Checked;
        Controls.Add(text);Controls.Add(state);Controls.Add(top);Controls.Add(note);
        FontManager.ApplyUi(this,appearance);UiTheme.Apply(this,appearance);
        Resize+=(_,_)=>note.MaximumSize=new(Math.Max(200,ClientSize.Width-Padding.Horizontal),0);
        Shown+=(_,_)=>{session.EnableReadOnlyTranslation(true);RefreshText();refresh.Start();};
        refresh.Tick+=(_,_)=>RefreshText();
        FormClosed+=(_,_)=>{session.EnableReadOnlyTranslation(false);refresh.Stop();refresh.Dispose();};
    }
    private void RefreshText()
    {
        var sources=Session.ReadOnlyTexts;var output=new StringBuilder();int translated=0;
        foreach(var source in sources)
        {
            string value=Session.Lookup(source);
            if(value.Length>0){translated++;output.AppendLine(value);output.AppendLine(source);}
            else{output.AppendLine(source);output.AppendLine(Session.Enabled?"正在等待译文…":"翻译已停止");}
            output.AppendLine();
        }
        state.Text=!Session.Enabled?"翻译已停止，已有译文仍可查看。":sources.Length==0?"等待游戏中可读取的文字…":$"最近读取 {sources.Length} 条 · 已有译文 {translated} 条";
        string valueText=output.ToString();if(text.Text!=valueText){int selection=text.SelectionStart;text.Text=valueText;text.SelectionStart=Math.Min(selection,text.TextLength);}
    }
}

public sealed partial class MainForm
{
    private RuntimeTranslationWindow? _runtimeTranslationWindow;
    private void OpenRuntimeTranslationWindow()
    {
        if(_selectedGame is not {} game||SelectedTranslationSession()?.Translation is not {Enabled:true} session)return;
        if(_runtimeTranslationWindow is {IsDisposed:false} previous)
        {if(ReferenceEquals(previous.Session,session)){previous.Activate();return;}previous.Close();}
        var window=new RuntimeTranslationWindow(game.Name,session,UiSettings);_runtimeTranslationWindow=window;
        window.FormClosed+=(_,_)=>{if(ReferenceEquals(_runtimeTranslationWindow,window))_runtimeTranslationWindow=null;};
        window.Show();
    }
}
