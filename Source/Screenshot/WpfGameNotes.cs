using W=System.Windows;
using C=System.Windows.Controls;
using D=System.Windows.Documents;
using I=System.Windows.Input;
using M=System.Windows.Media;
using System.Windows.Threading;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfGameNotes : C.StackPanel,IDisposable
{
    private readonly GameNotesStore store=new();
    private readonly IGameDetailsHost host;
    private readonly C.RichTextBox editor=new(){VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,MinHeight=320,MaxHeight=650,Padding=new(12),AcceptsTab=false,IsUndoEnabled=true,UndoLimit=100};
    private readonly C.TextBlock status=new(){TextWrapping=W.TextWrapping.Wrap,Margin=new(12,0,0,0),VerticalAlignment=W.VerticalAlignment.Center};
    private readonly C.Button save;
    private readonly C.WrapPanel toolbar=new();
    private readonly System.Collections.Generic.List<(WpfNotesToolButton Button,string Label,string Id)> tools=[];
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(700)};
    private readonly Dictionary<string,(GameNoteDocument Document,GameNoteParagraph[] Draft)> drafts=new(StringComparer.Ordinal);
    private GameNoteSelection? selected;
    private GameNoteDocument? document;
    private bool loading,dirty,failed;
    private string cleanSignature="";
    internal WpfGameNotes(IGameDetailsHost host)
    {
        this.host=host;
        W.Automation.AutomationProperties.SetAutomationId(editor,"GameNotesEditor");
        W.Automation.AutomationProperties.SetAutomationId(status,"GameNotesSaveStatus");
        Tool("加粗","GameNotesBold",()=>Command(D.EditingCommands.ToggleBold));
        Tool("斜体","GameNotesItalic",()=>Command(D.EditingCommands.ToggleItalic));
        Tool("下划线","GameNotesUnderline",()=>Command(D.EditingCommands.ToggleUnderline));
        Separate();
        Tool("高亮","GameNotesHighlight",Highlight);
        Separate();
        Tool("项目列表","GameNotesBullets",()=>Command(D.EditingCommands.ToggleBullets));
        Tool("编号列表","GameNotesNumbering",()=>Command(D.EditingCommands.ToggleNumbering));
        Separate();
        Tool("撤销","GameNotesUndo",()=>{editor.Focus();if(editor.CanUndo)editor.Undo();});
        Tool("重做","GameNotesRedo",()=>{editor.Focus();if(editor.CanRedo)editor.Redo();});
        save=WpfThemeResources.Button("保存笔记",null,Flush);save.HorizontalAlignment=W.HorizontalAlignment.Left;save.ToolTip="Ctrl+S";
        W.Automation.AutomationProperties.SetAutomationId(save,"GameNotesSave");
        var footer=new C.WrapPanel{Margin=new(0,10,0,0)};footer.Children.Add(save);footer.Children.Add(status);
        var toolFrame=new C.Border{Child=toolbar,Padding=new(3),CornerRadius=new(6),BorderThickness=new(1),Margin=new(0,0,0,10),HorizontalAlignment=W.HorizontalAlignment.Left};
        toolFrame.SetResourceReference(C.Border.BorderBrushProperty,"Fusion.Border");
        Children.Add(toolFrame);Children.Add(editor);Children.Add(footer);
        editor.SetResourceReference(C.Control.BackgroundProperty,"Fusion.Secondary");
        editor.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");
        editor.SetResourceReference(C.Control.BorderBrushProperty,"Fusion.Border");
        editor.SetResourceReference(C.RichTextBox.CaretBrushProperty,"Fusion.Text");
        editor.SelectionChanged+=(_,_)=>RefreshTools();
        editor.PreviewMouseMove+=(_,e)=>{
            var uri=LinkAt(e.GetPosition(editor));editor.ToolTip=uri is null?null:uri.AbsoluteUri+"\n"+UiLanguage.Text("Ctrl+单击打开链接");
            editor.Cursor=uri is not null&&I.Keyboard.Modifiers.HasFlag(I.ModifierKeys.Control)?I.Cursors.Hand:I.Cursors.IBeam;
        };
        editor.PreviewMouseLeftButtonDown+=(_,e)=>{
            if(!I.Keyboard.Modifiers.HasFlag(I.ModifierKeys.Control))return;
            var uri=LinkAt(e.GetPosition(editor));if(uri is null)return;OpenLink(uri);e.Handled=true;
        };
        editor.PreviewMouseRightButtonDown+=(_,e)=>{
            var uri=LinkAt(e.GetPosition(editor));if(uri is null){editor.ContextMenu=null;return;}
            var menu=new C.ContextMenu();var open=new C.MenuItem{Header=UiLanguage.Text("打开链接")};open.Click+=(_,_)=>OpenLink(uri);
            var copy=new C.MenuItem{Header=UiLanguage.Text("复制链接")};copy.Click+=(_,_)=>{try{W.Clipboard.SetText(uri.AbsoluteUri);}catch(System.Runtime.InteropServices.ExternalException){}};
            menu.Items.Add(open);menu.Items.Add(copy);editor.ContextMenu=menu;
        };
        editor.TextChanged+=(_,_)=>{
            if(loading||document is null)return;
            dirty=Signature(Capture())!=cleanSignature;failed=false;timer.Stop();if(dirty)timer.Start();Present();
        };
        editor.PreviewTextInput+=(_,e)=>{if(Plain().Length-editor.Selection.Text.Length+e.Text.Length>GameNotesStore.MaxLength)e.Handled=true;};
        // Paste plain text; format using the toolbar. Never accept embedded objects.
        W.DataObject.AddPastingHandler(editor,(_,e)=>{
            if(!e.DataObject.GetDataPresent(W.DataFormats.UnicodeText)){e.CancelCommand();return;}
            string text=e.DataObject.GetData(W.DataFormats.UnicodeText) as string??"";
            if(Plain().Length-editor.Selection.Text.Length+text.Length>GameNotesStore.MaxLength){e.CancelCommand();return;}
            e.FormatToApply=W.DataFormats.UnicodeText;
        });
        editor.LostKeyboardFocus+=(_,_)=>Flush();
        editor.PreviewKeyDown+=(_,e)=>{if(e.Key==I.Key.S&&I.Keyboard.Modifiers.HasFlag(I.ModifierKeys.Control)){Flush();e.Handled=true;}};
        timer.Tick+=(_,_)=>Flush();
        IsVisibleChanged+=(_,_)=>{if(!IsVisible)Flush();};Unloaded+=(_,_)=>Flush();
    }
    private void Tool(string label,string id,Action action)
    {
        var button=new WpfNotesToolButton(id,()=>{action();RefreshTools();});toolbar.Children.Add(button);tools.Add((button,label,id));
    }
    private void Separate(){var line=new C.Border{Width=1,Height=18,Margin=new(5,6,5,6)};line.SetResourceReference(C.Border.BackgroundProperty,"Fusion.Border");toolbar.Children.Add(line);}
    private void RefreshTools()
    {
        var selection=editor.Selection;
        var list=selection.Start.Paragraph?.Parent as D.ListItem;
        foreach(var tool in tools)
        {
            string shortcut=tool.Id switch{"GameNotesBold"=>"Ctrl+B","GameNotesItalic"=>"Ctrl+I","GameNotesUnderline"=>"Ctrl+U","GameNotesUndo"=>"Ctrl+Z","GameNotesRedo"=>"Ctrl+Y",_=>""};
            tool.Button.Caption(tool.Label,shortcut);
            tool.Button.IsEnabled=document is not null&&(tool.Id!="GameNotesUndo"||editor.CanUndo)&&(tool.Id!="GameNotesRedo"||editor.CanRedo);
            tool.Button.IsChecked=tool.Id switch{
                "GameNotesBold"=>selection.GetPropertyValue(D.TextElement.FontWeightProperty) is W.FontWeight weight&&weight>=W.FontWeights.Bold,
                "GameNotesItalic"=>selection.GetPropertyValue(D.TextElement.FontStyleProperty) is W.FontStyle style&&style==W.FontStyles.Italic,
                "GameNotesUnderline"=>selection.GetPropertyValue(D.Inline.TextDecorationsProperty) is W.TextDecorationCollection decorations&&decorations.Any(d=>d.Location==W.TextDecorationLocation.Underline),
                "GameNotesHighlight"=>selection.GetPropertyValue(D.TextElement.BackgroundProperty) is M.SolidColorBrush brush&&brush.Color.A>0,
                "GameNotesBullets"=>list?.Parent is D.List bullets&&bullets.MarkerStyle!=W.TextMarkerStyle.Decimal,
                "GameNotesNumbering"=>list?.Parent is D.List numbered&&numbered.MarkerStyle==W.TextMarkerStyle.Decimal,_=>false};
        }
    }
    private Uri? LinkAt(W.Point point)
    {
        var pointer=editor.GetPositionFromPoint(point,false);var paragraph=pointer?.Paragraph;
        if(pointer is null||paragraph is null)return null;
        string text=new D.TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text;
        return GameNoteLinks.At(text,new D.TextRange(paragraph.ContentStart,pointer).Text.Length);
    }
    private void OpenLink(Uri uri)
    {
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
        catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or InvalidOperationException){status.Text=UiLanguage.Text("无法打开链接，请复制网址后重试。");}
    }
    private void Command(I.RoutedUICommand command){editor.Focus();if(command.CanExecute(null,editor))command.Execute(null,editor);}
    private void Highlight()
    {
        editor.Focus();if(editor.Selection.IsEmpty)return;
        bool remove=editor.Selection.GetPropertyValue(D.TextElement.BackgroundProperty) is M.SolidColorBrush b&&b.Color.A>0;
        editor.BeginChange();
        try{
            editor.Selection.ApplyPropertyValue(D.TextElement.BackgroundProperty,remove?M.Brushes.Transparent:WpfGameNotesFormatting.HighlightBrush);
            editor.Selection.ApplyPropertyValue(D.TextElement.ForegroundProperty,remove?editor.Foreground:M.Brushes.White);
        }finally{editor.EndChange();}
    }
    private GameNoteParagraph[] Capture()=>WpfGameNotesFormatting.Read(editor.Document);
    private string Plain()=>GameNotesStore.FormattedText(Capture());
    private static string Signature(GameNoteParagraph[] value)=>JsonSerializer.Serialize(value);
    internal void Refresh()
    {
        var next=host.NativeGameNotes;
        if(next?.Id!=selected?.Id)
        {
            Flush();
            if(selected is not null&&document is not null&&dirty)drafts[selected.Id]=(document,Capture());
            selected=next;document=null;dirty=false;failed=false;loading=true;
            try{
                if(next is null)editor.Document=WpfGameNotesFormatting.Build("",null);
                else if(drafts.TryGetValue(next.Id,out var retained)){document=retained.Document;editor.Document=WpfGameNotesFormatting.Build(GameNotesStore.FormattedText(retained.Draft),retained.Draft);dirty=true;failed=true;}
                else{document=store.Read(next.Id,next.LegacyNote);editor.Document=WpfGameNotesFormatting.Build(document.Text,document.Paragraphs);}
                cleanSignature=dirty?"":Signature(Capture());
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {editor.Document=WpfGameNotesFormatting.Build("",null);failed=true;}
            finally{loading=false;}
        }
        Present();
    }
    internal void Flush()
    {
        timer.Stop();if(!dirty||document is null||selected is null)return;
        try{
            var rows=Capture();document=store.Save(selected.Id,GameNotesStore.FormattedText(rows),document.Revision,rows);
            cleanSignature=Signature(rows);dirty=false;failed=false;drafts.Remove(selected.Id);host.NativeGameNotesSaved();
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){failed=true;}
        Present();
    }
    private void Present()
    {
        W.Automation.AutomationProperties.SetName(editor,UiLanguage.Text("游戏笔记"));
        WpfThemeResources.SetButton(save,"保存笔记",null);editor.IsReadOnly=document is null;save.IsEnabled=dirty&&document is not null;RefreshTools();
        status.Text=UiLanguage.Text(failed?(document is null?"笔记读取失败，原文件保留。":"笔记未保存，请复制内容备份后重试。"):
            dirty?"正在保存…":document?.Revision.Length>0?"已保存":"");
        status.ToolTip=UiLanguage.Text("自动保存，也可按 Ctrl+S；最多 100,000 字符。");
    }
    public void Dispose()=>Flush();
}
