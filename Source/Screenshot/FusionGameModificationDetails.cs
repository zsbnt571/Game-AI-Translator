using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private string? _dataRestoreEventCommandsKey;
    private static string ReadableDataKey(string key)
    {
        var p=key.Split(':');
        if(p.Length==4&&p[0]=="event"&&p[3] is "A" or "B" or "C" or "D")return "独立开关 "+p[3];
        var kind=p[0] switch{"item"=>"道具","weapon"=>"武器","armor"=>"防具","actor"=>"角色","event"=>"地图事件","commonEvent"=>"公共事件","variable"=>"变量","switch"=>"开关","gold"=>"队伍金币","option"=>"常用设置",_=>""};
        return kind.Length>0?kind+(p.Length>1&&int.TryParse(p[1],out int id)?$"  #{id:0000}":""):key;
    }
    private void AddDataDetail(Control control)
    {
        control.Margin=new(0,4,0,6);
        UiTheme.Apply(control,_appliedDesktopSettings,true);FontManager.ApplyUi(control,_appliedDesktopSettings);ConfigureFusionInputs(control);
        control.Scale(new SizeF(DeviceDpi/96f,DeviceDpi/96f));CompactModificationControls(control);WorkspaceAdd(_dataObjectEditor!,control);
    }
    private Label DataDetailLabel(string text)=>new(){Text=text,AutoSize=true,MaximumSize=new(Math.Max(180,(_dataObjectEditor?.Width??320)-20),0)};
    private async Task ShowSelectedMapTileAsync()
    {
        if(_dataScope.SelectedIndex!=7)return;
        string signature=MapDetailSignature();
        if(_dataMapDetailSignature==signature&&_dataObjectGeneration==SelectedDataSession()?.Generation&&_dataObjectEditor?.Controls.Count>0)
        {if(_dataEventConditionsRefresh is {} refresh)await refresh();return;}
        _dataMapDetailSignature=signature;ResetDataDetails();_dataObjectGeneration=SelectedDataSession()?.Generation??-1;_dataMapSelection.Text=_dataMap.SelectionText;
        _dataSelectedName.Text=_dataMap.SelectedTile is { } p?$"格子 {p.X}, {p.Y}":"地图浏览";
        _dataDescription.Text=_dataMap.TileDetails;_dataOriginalName.Text=_dataMap.SelectionText;
        if(_dataMap.SelectedTile is null)return;
        AddDataDetail(IconButton("传送到此格","play",async(_,_)=>await TeleportGameDataAsync(),true,130));
        var events=_dataMap.SelectedEvents;
        if(events.Length==0){AddDataDetail(DataDetailLabel("此格没有事件。"));return;}
        var chooser=new GamePlanBox{Width=240,DropDownStyle=ComboBoxStyle.DropDownList};
        foreach(var ev in events)chooser.Items.Add($"事件 {ev.GetProperty("id"):000} · {ev.GetProperty("name")}");
        BindDataNames(chooser,()=>events.Select(e=>e.GetProperty("name").GetString()??"").ToArray(),(i,name)=>$"事件 {events[i].GetProperty("id"):000} · {name}");
        int revision=_dataDetailRevision;
        var host=ModificationColumn();host.AutoSize=true;AddDataDetail(chooser);AddDataDetail(host);
        Task selectionLoad=Task.CompletedTask;
        chooser.SelectedIndexChanged+=(_,_)=>
        {
            if(_dataUpdatingNames||chooser.SelectedIndex<0||host.IsDisposed)return;revision=++_dataDetailRevision;
            foreach(Control c in host.Controls.Cast<Control>().ToArray())c.Dispose();host.RowCount=0;host.RowStyles.Clear();
            selectionLoad=PopulateEventDetailAsync(_dataMap.MapId,events[chooser.SelectedIndex].GetProperty("id").GetInt32(),false,host,revision);
        };
        chooser.SelectedIndex=0;await selectionLoad;
    }
    private async Task ShowGameDataObjectAsync(JsonElement item)
    {
        ResetDataDetails();_dataSelected=item;_dataObjectGeneration=SelectedDataSession()?.Generation??-1;
        string key=item.GetProperty("key").GetString()!,name=item.GetProperty("name").GetString()??"",alias=DataChinese(item);
        _dataSelectedName.Text=alias.Length>0?alias:name;_dataOriginalName.Text=alias.Length>0?name:"";_dataKeyLabel.Text=ReadableDataKey(key);_dataDescription.Text="";
        int revision=_dataDetailRevision;var parts=key.Split(':');
        if(item.GetProperty("type").GetString()=="actor")await PopulateActorDetailAsync(int.Parse(parts[1]),revision);
        else await PopulateEventDetailAsync(parts[0]=="event"?int.Parse(parts[1]):_dataSelectedMapId,int.Parse(parts[^1]),parts[0]=="commonEvent",_dataObjectEditor!,revision,parts[0]=="interpreter");
    }
    private async Task PopulateActorDetailAsync(int id,int revision)
    {
        if(SelectedDataSession() is not { } state||!state.Enabled)return;
        try
        {
            var detail=await state.Connection.RequestAsync(new(){["op"]="actorDetails",["actorId"]=id,["generation"]=state.Generation});
            if(revision!=_dataDetailRevision)return;
            var fields=await state.Connection.RequestAsync(new(){["op"]="describe",["keys"]=JsonNode.Parse(detail.GetProperty("fields").GetRawText()),["generation"]=state.Generation});
            if(revision!=_dataDetailRevision)return;
            string sectionKey=_dataViewPath+"|actor:"+id;
            var host=ModificationColumn();host.AutoSize=true;
            var tabs=ModificationRow();string[] labels=["属性","技能","状态"];
            for(int i=0;i<labels.Length;i++)
            {
                int index=i;var tab=IconButton(labels[i],"",(_,_)=>Render(index),false,68);tab.TabStyle=true;tabs.Controls.Add(tab);
            }
            AddDataDetail(tabs);AddDataDetail(host);Render(_dataDetailSelections.GetValueOrDefault(sectionKey));
            void Render(int section)
            {
                if(revision!=_dataDetailRevision)return;
                _dataDetailSelections[sectionKey]=section;
                foreach(Control c in host.Controls.Cast<Control>().ToArray())c.Dispose();host.Controls.Clear();host.RowCount=0;host.RowStyles.Clear();
                foreach(GameActionButton b in tabs.Controls)b.Primary=tabs.Controls.IndexOf(b)==section;
                tabs.Invalidate(true);
                if(section==0)foreach(var field in fields.GetProperty("rows").EnumerateArray())AddInlineDataField(host,field);
                else
                {
                    string scope=section==1?"skills":"states";var entries=detail.GetProperty(scope).EnumerateArray().Select(e=>e.Clone()).ToArray();
                    var query=new TextBox{PlaceholderText="搜索名称或编号",Width=200};
                    var choice=new GamePlanBox{Width=245,DropDownStyle=ComboBoxStyle.DropDownList};
                    var value=new WorkspaceSwitch{Text=section==1?"已学会":"生效中",Height=36,Width=115,AccessibleName=section==1?"技能是否已学会":"状态是否生效"};
                    var visible=new List<JsonElement>();
                    void Filter()
                    {
                        visible=entries.Where(e=>(e.GetProperty("name")+" "+e.GetProperty("key")+" "+(e.TryGetProperty("chinese",out var c)?c.ToString():"")).Contains(query.Text,StringComparison.OrdinalIgnoreCase)).ToList();
                        choice.Items.Clear();choice.Items.AddRange(visible.Select(e=>(e.TryGetProperty("chinese",out var c)&&c.GetString()?.Length>0?c+" · ":"")+e.GetProperty("name")+"  #"+e.GetProperty("key").ToString().Split(':')[^1]).ToArray());
                        BindDataNames(choice,()=>visible.Select(e=>e.GetProperty("name").GetString()??"").ToArray(),(i,name)=>name+"  #"+visible[i].GetProperty("key").ToString().Split(':')[^1]);
                        if(choice.Items.Count>0)choice.SelectedIndex=0;
                    }
                    choice.SelectedIndexChanged+=(_,_)=>{if(!_dataUpdatingNames&&choice.SelectedIndex>=0)value.Checked=visible[choice.SelectedIndex].GetProperty("enabled").GetBoolean();};
                    query.TextChanged+=(_,_)=>Filter();
                    DynamicDetailAdd(host,SearchField(query));DynamicDetailAdd(host,choice);DynamicDetailAdd(host,value);
                    DynamicDetailAdd(host,IconButton("应用修改","check",async(_,_)=>{if(choice.SelectedIndex>=0)await ExecuteGameDataAsync(new(){["op"]="set",["key"]=visible[choice.SelectedIndex].GetProperty("key").GetString(),["value"]=value.Checked});},true,100));Filter();
                }
            }
        }
        catch(Exception ex){if(revision==_dataDetailRevision)_dataDescription.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
    private void DynamicDetailAdd(TableLayoutPanel host,Control control)
    {
        if(control is NumericUpDown number)control=new WorkspaceNumberField(number);
        UiTheme.Apply(control,_appliedDesktopSettings,true);FontManager.ApplyUi(control,_appliedDesktopSettings);ConfigureFusionInputs(control);
        control.Scale(new SizeF(DeviceDpi/96f,DeviceDpi/96f));CompactModificationControls(control);WorkspaceAdd(host,control);
    }
    private void AddInlineDataField(TableLayoutPanel host,JsonElement row)
    {
        string key=row.GetProperty("key").GetString()!,name=row.GetProperty("name").GetString()??"";
        int dot=name.IndexOf(" · ",StringComparison.Ordinal);if(dot>=0)name=name[(dot+3)..];
        var field=ModificationColumn();field.AutoSize=true;WorkspaceAdd(field,DataDetailLabel(name));
        bool isBool=row.GetProperty("type").GetString()=="boolean";
        var check=new WorkspaceSwitch{Text="开启",Height=36,Width=115,AccessibleName=name,Checked=isBool&&row.GetProperty("value").GetBoolean()};
        var number=new WorkspaceNumberBox{Width=115,Minimum=isBool?0:row.GetProperty("min").GetDecimal(),Maximum=isBool?1:row.GetProperty("max").GetDecimal()};
        if(!isBool)number.Value=Math.Clamp(row.GetProperty("value").GetDecimal(),number.Minimum,number.Maximum);
        var set=IconButton("应用","check",async(_,_)=>await ExecuteGameDataAsync(new(){["op"]="set",["key"]=key,["value"]=isBool?JsonValue.Create(check.Checked):JsonValue.Create(number.Value)}),false,66);
        var controls=ModificationRow(isBool?check:new WorkspaceNumberField(number),set);
        if(row.GetProperty("canLock").GetBoolean())controls.Controls.Add(IconButton(row.GetProperty("locked").GetBoolean()?"解锁":"锁定","",async(_,_)=>await ExecuteGameDataAsync(new(){["op"]=row.GetProperty("locked").GetBoolean()?"unlock":"lock",["key"]=key,["value"]=isBool?JsonValue.Create(check.Checked):JsonValue.Create(number.Value)}),false,62));
        WorkspaceAdd(field,controls);DynamicDetailAdd(host,field);
    }
    private async Task PopulateEventDetailAsync(int mapId,int eventId,bool common,TableLayoutPanel host,int revision,bool running=false)
    {
        if(SelectedDataSession() is not { } state||!state.Enabled)return;
        try
        {
            string selectionKey=_dataViewPath+"|event:"+running+":"+common+":"+mapId+":"+eventId;
            bool restoreCommands=_dataRestoreEventCommandsKey==selectionKey;_dataRestoreEventCommandsKey=null;
            var request=new JsonObject{["op"]="eventDetails",["mapId"]=mapId,["eventId"]=eventId,["common"]=common,["generation"]=state.Generation};
            if(running)request["runtimeToken"]=eventId;
            var detail=await state.Connection.RequestAsync(request);
            if(revision!=_dataDetailRevision||host.IsDisposed)return;
            var pages=detail.GetProperty("pages").EnumerateArray().Select(p=>p.Clone()).ToArray();
            int selectedPage=0;
            var commandPageTitle=WorkspaceLabel("");commandPageTitle.Name="EventCommandPageTitle";
            var list=new GameEventCommandList{Name="EventCommands"};
            JsonObject Operation(string op,int index=0)=>new(){["op"]=op,["mapId"]=mapId,["eventId"]=eventId,["common"]=common,["page"]=selectedPage,["index"]=index};
            var run=IconButton(running?"跳到此指令":"执行本页", "play",async(_,_)=>
            {
                if(running){if(list.SelectedIndex<0)return;await ExecuteGameDataAsync(new(){["op"]="interpreterGoto",["runtimeToken"]=eventId,["index"]=list.SelectedIndex});}
                else await ExecuteGameDataAsync(Operation("eventRun"));
            },true,92);
            var from=IconButton("从此执行","",async(_,_)=>{if(list.SelectedIndex>=0)await ExecuteGameDataAsync(Operation("eventRun",list.SelectedIndex));},false,92);
            var overview=ModificationColumn();overview.AutoSize=true;overview.Name="EventPageOverview";overview.Visible=!running;
            var conditions=DataDetailLabel("");conditions.Name="EventPageSummary";
            DynamicDetailAdd(overview,conditions);
            var visibleConditions=ModificationColumn();visibleConditions.AutoSize=true;
            DynamicDetailAdd(overview,visibleConditions);DynamicDetailAdd(host,overview);
            Task pageLoad=Task.CompletedTask;
            var advanced=ModificationColumn();advanced.Name="EventAdvanced";advanced.AutoSize=true;advanced.Visible=running;
            var conditionEditor=ModificationColumn();conditionEditor.AutoSize=true;conditionEditor.Visible=false;
            void ShowCommands(int page)
            {
                selectedPage=page;pageLoad=LoadEventPageAsync();overview.Visible=false;advanced.Visible=true;
                _dataDetailScroll!.ScrollOffset=0;
            }
            var back=IconButton("返回全部事件页","back",(_,_)=>{advanced.Visible=false;overview.Visible=true;_dataDetailScroll!.ScrollOffset=0;},false,132);
            back.Name="EventOverviewBack";back.Visible=!running;
            DynamicDetailAdd(advanced,ModificationRow(back));
            var toolbar=ModificationRow();if(!running)toolbar.Controls.Add(commandPageTitle);toolbar.Controls.Add(run);
            DynamicDetailAdd(advanced,toolbar);
            if(running)DynamicDetailAdd(advanced,DataDetailLabel("正在运行 · 选择指令后可跳转执行位置"));
            var emptyNotice=DataDetailLabel("此页没有可执行的指令。");emptyNotice.Name="EventEmptyNotice";DynamicDetailAdd(advanced,emptyNotice);
            DynamicDetailAdd(advanced,list);
            if(!running)DynamicDetailAdd(advanced,ModificationRow(from));
            var editor=ModificationColumn();editor.AutoSize=true;DynamicDetailAdd(advanced,editor);
            DynamicDetailAdd(host,advanced);
            if(!running)BindEventConditions(state,Operation("eventConditions"),visibleConditions,conditions,()=>revision==_dataDetailRevision&&!host.IsDisposed,ShowCommands,index=>index>=0&&index<pages.Length&&pages[index].GetProperty("list").EnumerateArray().Any(c=>c.GetProperty("code").GetInt32()!=0));
            if(!running)
            {
                DynamicDetailAdd(advanced,ModificationRow(IconButton("独立开关与位置","",(_,_)=>conditionEditor.Visible=!conditionEditor.Visible,false,136)));
                DynamicDetailAdd(advanced,conditionEditor);
            }
            async Task LoadEventPageAsync()
            {
                if(selectedPage<0||selectedPage>=pages.Length)return;var page=pages[selectedPage];int loadingPage=selectedPage;
                commandPageTitle.Text=$"第 {selectedPage+1} 页的指令";
                _dataDetailSelections[selectionKey]=selectedPage;
                var commands=page.GetProperty("list").EnumerateArray().ToArray();
                bool hasContent=commands.Any(c=>c.GetProperty("code").GetInt32() is not (0 or 108 or 408));
                run.Visible=running||hasContent;from.Visible=!running&&hasContent;emptyNotice.Visible=!running&&!hasContent;
                list.Visible=commands.Length>0;editor.Visible=!running&&commands.Length>0;
                list.SetItems(commands.Select(c=>$"{c.GetProperty("code").GetInt32():000}  "+EventCommandLabel(c)).ToArray());
                int selected=running&&detail.TryGetProperty("currentIndex",out var current)?current.GetInt32():_dataDetailSelections.GetValueOrDefault(selectionKey+":"+selectedPage);
                if(commands.Length>0)list.SelectedIndex=Math.Clamp(selected,0,commands.Length-1);
                foreach(Control c in conditionEditor.Controls.Cast<Control>().ToArray())c.Dispose();conditionEditor.RowStyles.Clear();conditionEditor.RowCount=0;
                if(running)return;
                var keys=common?new List<string>():detail.GetProperty("fields").EnumerateArray().Select(k=>k.GetString()!).ToList();
                if(_dataEventConditionsRefresh is { } refreshConditions)await refreshConditions();
                if(keys.Count>0)try
                {
                    var values=await state.Connection.RequestAsync(new(){["op"]="describe",["keys"]=new JsonArray(keys.Distinct().Select(k=>(JsonNode?)JsonValue.Create(k)).ToArray()),["generation"]=state.Generation});
                    if(revision!=_dataDetailRevision||conditionEditor.IsDisposed||selectedPage!=loadingPage)return;
                    foreach(var row in values.GetProperty("rows").EnumerateArray())AddInlineDataField(conditionEditor,row);
                }
                catch(Exception ex){if(revision==_dataDetailRevision&&!conditionEditor.IsDisposed)DynamicDetailAdd(conditionEditor,DataDetailLabel(SafeDiagnosticOutput.ExceptionSummary(ex)));}
            }
            list.SelectedIndexChanged+=(_,_)=>
            {
                using var batch=new UiLayoutBatch(editor);
                foreach(Control c in editor.Controls.Cast<Control>().ToArray())c.Dispose();editor.RowStyles.Clear();editor.RowCount=0;
                if(list.SelectedIndex<0||selectedPage<0||running)return;
                int pageIndex=selectedPage,commandIndex=list.SelectedIndex;
                _dataDetailSelections[selectionKey+":"+pageIndex]=commandIndex;
                var command=JsonNode.Parse(pages[pageIndex].GetProperty("list")[commandIndex].GetRawText())!.AsObject();
                var parameters=command["parameters"]!.AsArray();
                if(parameters.Count==0){DynamicDetailAdd(editor,DataDetailLabel("此指令没有可编辑参数。"));return;}
                var inputs=new List<(int Index,Control Input)>();
                for(int i=0;i<parameters.Count;i++)
                {
                    JsonNode? node=parameters[i];string kind=node?.GetValueKind().ToString()??"Null";
                    string label=command["code"]?.GetValue<int>() is 401 or 405?"文本":$"参数 {i+1}";
                    Control input;
                    if(kind=="String")input=new TextBox{Text=node!.GetValue<string>(),Multiline=true,Height=58,MinimumSize=new(0,58),Width=245,ScrollBars=ScrollBars.Vertical};
                    else if(kind is "True" or "False")input=new WorkspaceSwitch{Checked=node!.GetValue<bool>(),Height=28,Width=100,AccessibleName=label};
                    else if(kind=="Number")input=new WorkspaceNumberBox{Width=125,Minimum=int.MinValue,Maximum=int.MaxValue,DecimalPlaces=node!.GetValue<decimal>()%1==0?0:3,Value=Math.Clamp(node.GetValue<decimal>(),int.MinValue,int.MaxValue)};
                    else input=new TextBox{Text=node?.ToJsonString()??"null",Tag="json",Multiline=true,Height=58,MinimumSize=new(0,58),Width=245,ScrollBars=ScrollBars.Vertical};
                    inputs.Add((i,input));
                    if(input is NumericUpDown number)DynamicDetailAdd(editor,ModificationRow(WorkspaceLabel(label),new WorkspaceNumberField(number)));
                    else if(input is CheckBox)DynamicDetailAdd(editor,ModificationRow(WorkspaceLabel(label),input));
                    else{DynamicDetailAdd(editor,DataDetailLabel(label));DynamicDetailAdd(editor,input);}
                }
                DynamicDetailAdd(editor,ModificationRow(IconButton("保存指令修改","check",async(_,_)=>
                {
                    try
                    {
                        foreach(var (i,input) in inputs)parameters[i]=input is TextBox text?(Equals(text.Tag,"json")?JsonNode.Parse(text.Text):JsonValue.Create(text.Text)):input is CheckBox toggle?JsonValue.Create(toggle.Checked):JsonValue.Create(((NumericUpDown)input).Value);
                        var save=Operation("eventCommandSet",commandIndex);save["page"]=pageIndex;save["command"]=command.DeepClone();_dataRestoreEventCommandsKey=selectionKey;await ExecuteGameDataAsync(save);
                    }
                    catch(JsonException){_dataFeedback.Text="参数格式无效，请检查括号、引号和逗号。";}
                },true,118)));
            };
            if(pages.Length>0){selectedPage=Math.Clamp(_dataDetailSelections.GetValueOrDefault(selectionKey),0,pages.Length-1);pageLoad=LoadEventPageAsync();}
            await pageLoad;
            if(!running&&restoreCommands)ShowCommands(selectedPage);
        }
        catch(Exception ex){if(revision==_dataDetailRevision&&!host.IsDisposed)DynamicDetailAdd(host,DataDetailLabel(SafeDiagnosticOutput.ExceptionSummary(ex)));}
    }
    private static string EventCommandLabel(JsonElement c)
    {
        int code=c.GetProperty("code").GetInt32();
        string title=code switch{0=>"结束",101=>"显示文字",401=>"对白",102=>"显示选项",402=>"选项分支",403=>"取消分支",404=>"结束选项",103=>"输入数字",104=>"选择物品",105=>"滚动文字",405=>"滚动文字内容",111=>"条件分支",411=>"否则",412=>"结束条件",112=>"循环",113=>"跳出循环",115=>"结束事件处理",117=>"公共事件",118=>"标签",119=>"跳转到标签",121=>"控制开关",122=>"控制变量",123=>"独立开关",124=>"控制计时器",125=>"增减金币",126=>"增减道具",127=>"增减武器",128=>"增减防具",129=>"更换队伍成员",201=>"场所移动",202=>"设置载具位置",203=>"设置事件位置",204=>"滚动地图",205=>"设置移动路线",505=>"移动路线内容",206=>"载具乘降",211=>"更改透明状态",212=>"显示动画",213=>"显示气泡图标",214=>"暂时消除事件",216=>"更改队伍跟随",217=>"集合队伍",221=>"淡出画面",222=>"淡入画面",223=>"更改画面色调",224=>"闪烁画面",225=>"震动画面",230=>"等待",231=>"显示图片",232=>"移动图片",233=>"旋转图片",234=>"更改图片色调",235=>"消除图片",236=>"设置天气",241=>"播放背景音乐",242=>"淡出背景音乐",243=>"保存背景音乐",244=>"恢复背景音乐",245=>"播放环境音",246=>"淡出环境音",249=>"播放音乐效果",250=>"播放音效",251=>"停止音效",261=>"播放视频",301=>"战斗处理",302=>"商店处理",303=>"输入姓名",351=>"打开菜单",352=>"打开存档界面",353=>"游戏结束",354=>"返回标题画面",355=>"脚本",655=>"脚本续行",356=>"插件命令 (MV)",357=>"插件指令 (MZ)",108=>"注释",408=>"注释续行",_=>"指令 "+code};
        string parameters=string.Join(" · ",c.GetProperty("parameters").EnumerateArray().Select(p=>p.ToString()));
        return new string(' ',Math.Clamp(c.GetProperty("indent").GetInt32(),0,20)*2)+title+(parameters.Length>0?"\n"+parameters:"");
    }
    private async void BackupRpgSaves()
    {
        if(_selectedGame is not { } game)return;
        try
        {
            var install=RpgMakerDataAdapter.Detect(game.ExePath)??throw new IOException("未识别游戏目录。");
            string source=Path.Combine(install.DataRoot,"save");if(!Directory.Exists(source))throw new IOException("尚未找到此游戏的存档目录。");
            string folder=Path.Combine(RpgMakerDataAdapter.Storage(game.ExePath),"save-backups");Directory.CreateDirectory(folder);
            string output=Path.Combine(folder,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..6]+".zip");
            await Task.Run(()=>ZipFile.CreateFromDirectory(source,output,CompressionLevel.Fastest,false));
            _dataFeedback.Text="存档已备份："+Path.GetFileName(output);
        }
        catch(Exception ex){_dataFeedback.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
    private async Task SaveRpgLocksAsync()
    {
        if(_selectedGame is not { } game||SelectedDataSession() is not { } state)return;
        using var dialog=new SaveFileDialog{Title="保存此游戏的锁定配置",Filter="锁定配置 (*.json)|*.json",FileName=Path.GetFileNameWithoutExtension(game.ExePath)+"-locks.json"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try
        {
            var response=await state.Connection.RequestAsync(new(){["op"]="exportLocks",["generation"]=state.Generation});
            PortableDataStorage.WriteJson(dialog.FileName,new{format="fusion-rpg-locks-v1",game=RecentGameStore.Normalize(game.ExePath),entries=response.GetProperty("entries")});
            _dataFeedback.Text="锁定配置已保存。";
        }
        catch(Exception ex){_dataFeedback.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
    private async Task LoadRpgLocksAsync()
    {
        if(_selectedGame is not { } game)return;
        using var dialog=new OpenFileDialog{Title="读取此游戏的锁定配置",Filter="锁定配置 (*.json)|*.json"};
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try
        {
            if(new FileInfo(dialog.FileName).Length>1024*1024)throw new IOException("配置文件过大。");
            var config=JsonNode.Parse(await File.ReadAllTextAsync(dialog.FileName))!.AsObject();
            if(config["format"]?.ToString()!="fusion-rpg-locks-v1"||!SameGame(config["game"]?.ToString(),game.ExePath))throw new IOException("此配置属于另一个游戏，未应用。");
            await ExecuteGameDataAsync(new(){["op"]="importLocks",["entries"]=config["entries"]?.DeepClone()});
        }
        catch(Exception ex){_dataFeedback.Text=SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
}

