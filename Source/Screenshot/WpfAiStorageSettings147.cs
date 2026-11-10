using W=System.Windows;
using C=System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

internal sealed partial class WpfDesktopSettingsPage
{
    private C.TextBlock AiStorageTextView147(string key,bool muted=true)
    {
        var view=Text("",muted,false);refresh.Add(()=>view.Text=AiStorageText147.Text(key));return view;
    }
    private C.Button AiStorageButton147(string key,string id,Action action)
    {
        var button=Button("",action);button.MinHeight=36;button.Margin=new(8,0,0,0);Identify(button,id);
        refresh.Add(()=>{button.Content=AiStorageText147.Text(key);W.Automation.AutomationProperties.SetName(button,AiStorageText147.Text(key));});return button;
    }
    private void AiStorageSettings147()
    {
        Section("");Identify(section,"AiStorageSection147");var heading=((C.WrapPanel)section.Children[0]).Children.OfType<C.TextBlock>().First();refresh.Add(()=>heading.Text=AiStorageText147.Text("Section"));
        AiStorageInfo147? info=null;string error="",notice="";bool choosing=false;
        var locations=new Dictionary<string,C.TextBox>();
        var opens=new Dictionary<string,C.Button>();
        void Row147(string key,W.FrameworkElement value)
        {
            var row=new WpfSettingsFieldRow{Margin=new(0,0,0,12)};row.ColumnDefinitions.Add(new(){Width=new(206)});row.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
            var label=AiStorageTextView147(key,false);label.Margin=new(0,6,16,0);row.Children.Add(label);C.Grid.SetColumn(value,1);row.Children.Add(value);section.Children.Add(row);
        }
        string Path147(string key)=>info is null?"":key switch{"Root"=>info.Root,"Tasks"=>info.Tasks,"Conversations"=>info.Conversations,"Projects"=>info.Projects,_=>info.Harness};
        var change=AiStorageButton147("ChangeRoot","AiStorageChange147",()=>
        {
            if(choosing)return;choosing=true;error="";
            try{string? selected=host.NativeChooseAiStorageLocation();if(!string.IsNullOrWhiteSpace(selected)){bool repair=info?.ErrorCode=="ai_storage_unavailable";host.NativeRequestAiStorageLocation(selected);notice=repair?"Located":"";}}
            catch(Exception ex){error=ex is AiToolsException e?e.Code:"ai_storage_unavailable";}
            finally{choosing=false;Refresh();}
        });
        foreach(string key in new[]{"Root","Tasks","Conversations","Projects","Harness"})
        {
            var grid=new C.Grid();grid.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
            var path=new C.TextBox{IsReadOnly=true,MinHeight=36,VerticalContentAlignment=W.VerticalAlignment.Center,TextWrapping=W.TextWrapping.NoWrap,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Hidden};Identify(path,"AiStorage"+key+"147");locations[key]=path;grid.Children.Add(path);
            var open=AiStorageButton147("Open","AiStorageOpen"+key+"147",()=>{string target=Path147(key);if(target.Length>0)host.NativeOpenFolder(target);});opens[key]=open;C.Grid.SetColumn(open,1);grid.Children.Add(open);
            if(key=="Root")
            {
                // Keep the full path readable even when localized actions need more space.
                grid.Children.Remove(path);grid.Children.Remove(open);var root=new C.StackPanel();root.Children.Add(path);
                var actions=new C.WrapPanel{Margin=new(0,8,0,0)};open.Margin=new(0,0,8,8);change.Margin=new(0,0,8,8);actions.Children.Add(open);actions.Children.Add(change);root.Children.Add(actions);Row147(key,root);
            }
            else Row147(key,grid);
        }
        var status=Text("",false,false);Identify(status,"AiStorageStatus147");status.Margin=new(0,2,0,10);W.Automation.AutomationProperties.SetLiveSetting(status,W.Automation.AutomationLiveSetting.Polite);section.Children.Add(status);
        var help=AiStorageTextView147("RetentionHelp");help.Margin=new(0,0,0,8);section.Children.Add(help);var external=AiStorageTextView147("ExternalHelp");external.Margin=new(0,0,0,8);section.Children.Add(external);
        refresh.Add(()=>
        {
            try{info=host.NativeAiStorageInfo;}catch(Exception ex){info=null;error=ex is AiToolsException e?e.Code:"ai_storage_unavailable";}
            foreach(var pair in locations){string path=Path147(pair.Key);pair.Value.Text=path;pair.Value.ToolTip=path;W.Automation.AutomationProperties.SetName(pair.Value,AiStorageText147.Text(pair.Key));opens[pair.Key].IsEnabled=path.Length>0&&string.IsNullOrEmpty(info?.ErrorCode);}
            change.IsEnabled=!choosing&&info is {IsIsolated:false}&&string.IsNullOrEmpty(info.PendingRoot);
            string action=info?.ErrorCode=="ai_storage_unavailable"?"LocateRoot":"ChangeRoot";change.Content=AiStorageText147.Text(action);W.Automation.AutomationProperties.SetName(change,AiStorageText147.Text(action));
            string currentError=error.Length>0?error:info?.ErrorCode??"";
            status.Text=currentError.Length>0?AiStorageText147.Text(currentError):info?.IsIsolated==true?AiStorageText147.Text("Isolated"):info?.PendingRoot is {Length:>0} target?AiStorageText147.Text("Pending")+"\n"+target:AiStorageText147.Text(notice.Length>0?notice:"DefaultHelp");
        });
    }
}
