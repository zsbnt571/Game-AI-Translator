using System.Text.Json;
using W = System.Windows;
using WC = System.Windows.Controls;
using WA = System.Windows.Automation;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeTranslationLibraryDialog : NativeDialogWindow
{
    private readonly TranslationContentEditorState draft=new();
    private readonly TranslationContentEditorState destination;
    private TranslationLibraryDocument library;
    private readonly WC.WrapPanel cards=new();
    private readonly WC.TextBlock summary=NativeDialogControls.Text(""), notice=NativeDialogControls.Text("");
    private readonly WC.TextBox search=NativeDialogControls.Input();
    private readonly WC.ComboBox filter=NativeDialogControls.Choice(["全部","已选择","我的"]);
    private WC.Button presetTab=null!,packTab=null!;
    private bool packs;
    internal NativeTranslationLibraryDialog(TranslationContentEditorState state):base("提示词预设与词库",1050,780)
    {
        MinWidth=640;MinHeight=480;destination=state;draft.Load(state.Snapshot());library=TranslationLibrary.Load();
        var root=new WC.Grid{Margin=new(22)};
        root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,W.GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        var title=NativeDialogControls.Text("提示词预设与词库",23);title.FontWeight=W.FontWeights.Bold;
        search.MinWidth=280;search.MaxWidth=440;search.ToolTip="搜索名称或说明";Identify(search,"TranslationLibrarySearch");filter.Width=135;
        presetTab=NativeDialogControls.Button("提示词预设",()=>{packs=false;Render();});Identify(presetTab,"LibraryPresets");
        packTab=NativeDialogControls.Button("词库",()=>{packs=true;Render();});Identify(packTab,"LibraryPacks");
        var add=NativeDialogControls.Button("新建",()=>Try(()=>Edit(null,null)));Identify(add,"LibraryNew");
        var import=NativeDialogControls.Button("导入词库",()=>Try(Import));Identify(import,"LibraryImport");
        var top=NativeDialogControls.Column(title,NativeDialogControls.Row(presetTab,packTab),NativeDialogControls.Text("搜索名称或说明",12),NativeDialogControls.Row(search,filter,add,import));
        root.Children.Add(top);
        var scroll=new WC.ScrollViewer{Content=cards,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Disabled};WC.Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var apply=NativeDialogControls.Button("使用选择",()=>{destination.Load(draft.Snapshot());Finish(System.Windows.Forms.DialogResult.OK);},true);Identify(apply,"LibraryApply");
        var footer=NativeDialogControls.Column(summary,notice,NativeDialogControls.Row(apply,ResultButton("取消",System.Windows.Forms.DialogResult.Cancel)));
        WC.Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        search.TextChanged+=(_,_)=>Render();filter.SelectionChanged+=(_,_)=>Render();UiLanguage.LanguageChanged+=RefreshLanguage;Closed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshLanguage;Render();
    }
    private void RefreshLanguage(object? sender,EventArgs e)=>Render();
    private static void Identify(W.DependencyObject element,string id)=>WA.AutomationProperties.SetAutomationId(element,id);
    private void Try(Action action){try{action();}catch(Exception e){notice.Text=UiOperationText.Error(e);}}
    private bool Visible(string name,string description,bool selected,bool user)
        =>(filter.SelectedIndex!=1||selected)&&(filter.SelectedIndex!=2||user)&&(string.IsNullOrWhiteSpace(search.Text)||(name+" "+description).Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase));
    private void Render()
    {
        cards.Children.Clear();notice.Text="";
        WpfThemeResources.SetButton(presetTab,"提示词预设",null,!packs);WpfThemeResources.SetButton(packTab,"词库",null,packs);
        var chosen=TranslationLibrary.PresetId(draft.Snapshot());
        if(!packs)
        {
            foreach(var p in TranslationLibrary.Presets(library))
            {
                if(p.LegacyStyle==TranslationStyle.Literal&&p.Id!=chosen)continue;
                bool selected=p.Id==chosen,user=library.Presets.Any(x=>x.Id==p.Id);
                if(!Visible(TranslationLibrary.DisplayName(p),TranslationLibrary.DisplayDescription(p),selected,user))continue;
                var choose=NativeDialogControls.Button(selected?"已选择":"选择",()=>{draft.Choose(p);Render();},selected);Identify(choose,"Preset_"+p.Id);
                var detail=NativeDialogControls.Button("查看",()=>Details(p,null));
                var copy=NativeDialogControls.Button(user?"编辑":"复制自定义",()=>Try(()=>Edit(p,null)));
                Card(TranslationLibrary.DisplayName(p),TranslationLibrary.DisplayDescription(p),p.Source,NativeDialogControls.Row(choose,detail,copy),selected);
            }
        }
        else
        {
            var settings=new ApiSettings();var content=draft.Snapshot();settings.SourceLanguage=content.SourceLanguage;settings.SourceLanguages=content.SourceLanguages;settings.TargetLanguage=content.TargetLanguage;
            foreach(var p in TranslationLibrary.Packs(library))
            {
                bool selected=draft.PackIds.Contains(p.Id),user=library.Packs.Any(x=>x.Id==p.Id),compatible=TranslationLibrary.Compatible(p,settings);
                if(!Visible(TranslationLibrary.DisplayName(p),TranslationLibrary.DisplayDescription(p),selected,user))continue;
                var check=new WC.CheckBox{Content=selected?"已选用":"选用",IsChecked=selected,Margin=new(0,4,18,8),MinHeight=30};Identify(check,"Pack_"+p.Id);
                check.Click+=(_,_)=>{draft.PackIds=check.IsChecked==true?draft.PackIds.Append(p.Id).ToArray():draft.PackIds.Where(x=>x!=p.Id).ToArray();Render();};
                var detail=NativeDialogControls.Button("查看词条",()=>Details(null,p));
                var edit=NativeDialogControls.Button(user?"编辑":"复制自定义",()=>Try(()=>Edit(null,p)));
                var actions=NativeDialogControls.Row(check,detail,edit);
                if(selected)actions.Children.Add(NativeDialogControls.Button("优先使用",()=>{draft.PackIds=new[]{p.Id}.Concat(draft.PackIds.Where(x=>x!=p.Id)).ToArray();Render();}));
                Card(TranslationLibrary.DisplayName(p),UiLanguage.Format($"{p.SourceLanguage} → {p.TargetLanguage} · {p.Terms.Count} 条")+"\n"+TranslationLibrary.DisplayDescription(p)+(compatible?"":"\n"+UiLanguage.Text("当前语言不匹配，暂不生效。")),p.Source,actions,selected);
            }
        }
        if(cards.Children.Count==0)cards.Children.Add(NativeDialogControls.Text("没有符合条件的内容。"));
        var allPacks=TranslationLibrary.Packs(library).ToDictionary(p=>p.Id);
        string packNames=string.Join(" → ",draft.PackIds.Select(id=>allPacks.TryGetValue(id,out var p)?TranslationLibrary.DisplayName(p):UiLanguage.Format($"不可用词库（{id}）")));
        var selectedPreset=TranslationLibrary.Presets(library).FirstOrDefault(p=>p.Id==chosen);
        summary.Text=UiLanguage.Format($"提示词：{(selectedPreset is null?UiLanguage.Text("已保存的自定义预设"):TranslationLibrary.DisplayName(selectedPreset))}\n词库：{(packNames.Length==0?UiLanguage.Text("未选用"):packNames)}");
        if(packs)
        {
            var seen=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);int conflicts=0;
            foreach(var p in draft.PackIds.Where(allPacks.ContainsKey).Select(id=>allPacks[id]))foreach(var t in p.Terms)
                if(!seen.TryAdd(t.Key,t.Value)&&seen[t.Key]!=t.Value)conflicts++;
            notice.Text=UiLanguage.Format($"已有 {allPacks.Count} 个词库 · 选中 {draft.PackIds.Length} 个。勾选 → 使用选择 → 返回保存翻译内容。\n同词冲突 {conflicts} 项：按上方顺序优先；单游戏术语始终优先。只发送当前文本命中的词条。");
        }
        else notice.Text="选择一个预设，可独立搭配多个词库。返回“翻译内容”后保存生效。";
        WpfUiLanguage.Dialog(cards);
    }
    private void Card(string name,string description,string source,W.UIElement actions,bool selected)
    {
        var title=NativeDialogControls.Text(name,17,localize:false);title.FontWeight=W.FontWeights.SemiBold;
        var note=NativeDialogControls.Text(source,12);note.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");
        var panel=NativeDialogControls.Column(title,NativeDialogControls.Text(description,localize:false),note,actions);
        var card=new WC.Border{Child=panel,Width=450,Padding=new(16),Margin=new(0,0,12,12),CornerRadius=new(8),BorderThickness=new(selected?2:1)};
        card.SetResourceReference(WC.Border.BackgroundProperty,"Fusion.Secondary");card.SetResourceReference(WC.Border.BorderBrushProperty,selected?"Fusion.Accent":"Fusion.Border");
        cards.Children.Add(card);
    }
    private void Details(TranslationPreset? preset,TranslationTermPack? pack)
    {
        using var dialog=new NativeDialogWindow(preset is not null?TranslationLibrary.DisplayName(preset):TranslationLibrary.DisplayName(pack!),820,650);
        var body=NativeDialogControls.Column(NativeDialogControls.Text(preset is not null?TranslationLibrary.DisplayDescription(preset):TranslationLibrary.DisplayDescription(pack!),localize:false));
        if(preset is not null)body.Children.Add(NativeDialogControls.Input(preset.Instructions,true,true));
        else
        {
            var grid=new WC.DataGrid{ItemsSource=pack!.Terms.Select(t=>new{Source=t.Key,Target=t.Value}).ToArray(),IsReadOnly=true,AutoGenerateColumns=false,Height=340,CanUserAddRows=false};grid.Columns.Add(new WC.DataGridTextColumn{Header=UiLanguage.Text("原文"),Binding=new System.Windows.Data.Binding("Source")});grid.Columns.Add(new WC.DataGridTextColumn{Header=UiLanguage.Text("译文"),Binding=new System.Windows.Data.Binding("Target")});body.Children.Add(grid);
            body.Children.Add(NativeDialogControls.Text(UiLanguage.Format($"{pack.SourceLanguage} → {pack.TargetLanguage} · {pack.Terms.Count} 条")+" · "+UiLanguage.Text(ScopeName(pack.Scope))));
            body.Children.Add(NativeDialogControls.Button("导出 JSON",()=>Try(()=>{
                var save=new Microsoft.Win32.SaveFileDialog{Filter=UiLanguage.Text("词库")+" JSON|*.json",FileName=pack.Name+".json"};
                if(save.ShowDialog(dialog)==true)File.WriteAllText(save.FileName,JsonSerializer.Serialize(pack,new JsonSerializerOptions{WriteIndented=true}));
            })));
        }
        string url=preset?.SourceUrl??pack!.SourceUrl;
        if(!string.IsNullOrEmpty(url))body.Children.Add(NativeDialogControls.Button("查看来源",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true})));
        body.Children.Add(dialog.ResultButton("关闭",System.Windows.Forms.DialogResult.OK));dialog.Content=new WC.ScrollViewer{Content=body,Margin=new(22),VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};dialog.ShowDialog(this);
    }
    private static string ScopeName(string scope)=>scope=="label"?"完整标签（推荐）":scope=="entity"?"句中术语（按语境）":"保留原文";
    private void Import()
    {
        var file=new Microsoft.Win32.OpenFileDialog{Filter=UiLanguage.Text("词库")+"|*.json;*.csv;*.tsv",Multiselect=false};
        if(file.ShowDialog(this)!=true)return;
        var terms=TranslationLibrary.ReadTerms(file.FileName);
        var pack=new TranslationTermPack("import-"+Guid.NewGuid().ToString("N"),Path.GetFileNameWithoutExtension(file.FileName),"导入后可查看、编辑和选用。","en","zh-CN",terms,"我的导入");
        // Exported Fusion pack metadata is retained; arbitrary file paths and
        // remote resources are never followed during import.
        if(Path.GetExtension(file.FileName).Equals(".json",StringComparison.OrdinalIgnoreCase))
        {
            try{var prior=JsonSerializer.Deserialize<TranslationTermPack>(File.ReadAllText(file.FileName));if(prior?.Terms is not null)pack=prior with{Id=pack.Id,Terms=terms,Source="我的导入"};}catch(JsonException){}
        }
        Edit(null,pack,importing:true);
    }
    private void Edit(TranslationPreset? preset,TranslationTermPack? pack,bool importing=false)
    {
        bool isPack=pack is not null||(preset is null&&packs);
        using var dialog=new NativeDialogWindow(isPack?"编辑词库":"编辑提示词预设",850,730);
        var name=NativeDialogControls.Input(preset?.Name??pack?.Name??"");
        var description=NativeDialogControls.Input(preset?.Description??pack?.Description??"");
        var text=NativeDialogControls.Input(isPack?JsonSerializer.Serialize(pack?.Terms??new Dictionary<string,string>{{"New Game","新游戏"}},new JsonSerializerOptions{WriteIndented=true}):preset?.Instructions??"",true);text.Height=275;
        var source=NativeDialogControls.Choice(TranslationLanguagePolicy.Choices.Select(x=>x.Code));source.SelectedItem=pack?.SourceLanguage??"en";
        var target=NativeDialogControls.Choice(["zh-CN","zh-TW","en","ja","ko","es","fr","de","ru"]);target.SelectedItem=pack?.TargetLanguage??"zh-CN";
        var scope=NativeDialogControls.Choice(["完整标签（推荐）","句中术语（按语境）","保留原文"]);scope.SelectedIndex=pack?.Scope=="entity"?1:pack?.Scope=="literal"?2:0;
        var message=NativeDialogControls.Text( importing?"先核对原文语言、目标语言和词条；保存后才加入我的词库。":"自定义内容保存在软件 data/translation-library 中。");
        var body=NativeDialogControls.Column(NativeDialogControls.Text("名称"),name,NativeDialogControls.Text("说明"),description);
        if(isPack)body.Children.Add(NativeDialogControls.Row(source,target,scope));
        body.Children.Add(NativeDialogControls.Text(isPack?"词条（JSON 原文与译文）":"提示词内容"));body.Children.Add(text);body.Children.Add(message);
        var save=NativeDialogControls.Button("保存到我的资料",()=>{
            try
            {
                if(isPack)
                {
                    var data=TranslationLibrary.ReadTermsFromJson(text.Text);
                    if(scope.SelectedIndex==2)data=data.ToDictionary(t=>t.Key,t=>t.Key,StringComparer.Ordinal);
                    string id=pack is not null&&library.Packs.Any(p=>p.Id==pack.Id)?pack.Id:"user-pack-"+Guid.NewGuid().ToString("N");
                    var entry=new TranslationTermPack(id,name.Text.Trim(),description.Text.Trim(),source.SelectedItem?.ToString()??"en",target.SelectedItem?.ToString()??"zh-CN",data,"我的词库",pack?.SourceUrl??"",scope.SelectedIndex==1?"entity":scope.SelectedIndex==2?"literal":"label");
                    var next=new TranslationLibraryDocument{Presets=[..library.Presets],Packs=[..library.Packs.Where(p=>p.Id!=id),entry]};TranslationLibrary.Save(next);library=next;
                }
                else
                {
                    string id=preset is not null&&library.Presets.Any(p=>p.Id==preset.Id)?preset.Id:"user-preset-"+Guid.NewGuid().ToString("N");
                    var entry=new TranslationPreset(id,name.Text.Trim(),description.Text.Trim(),text.Text.Trim(),"我的预设",preset?.SourceUrl??"");
                    var next=new TranslationLibraryDocument{Presets=[..library.Presets.Where(p=>p.Id!=id),entry],Packs=[..library.Packs]};TranslationLibrary.Save(next);library=next;
                    if(draft.PresetId==id)draft.Choose(entry);
                }
                dialog.Finish(System.Windows.Forms.DialogResult.OK);
            }catch(Exception ex){message.Text=UiOperationText.Error(ex);}
        },true);Identify(save,"LibraryItemSave");
        body.Children.Add(NativeDialogControls.Row(save,dialog.ResultButton("取消",System.Windows.Forms.DialogResult.Cancel)));
        dialog.Content=new WC.ScrollViewer{Content=body,Margin=new(22),VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};dialog.ShowDialog(this);Render();
    }
}
