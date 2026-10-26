using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private sealed partial class NativeRpgController
    {

    private Func<Task>? _dataEventConditionsRefresh;

    private void BindEventConditions(GameDataSession session,JsonObject request,NativeRpgColumn host,NativeRpgLabel summary,Func<bool> current,Action<int> showCommands,Func<int,bool> canInspect)
    {
        Task? pending=null;string signature="";
        var bindings=new List<Action<JsonElement>>();
        var headings=new List<NativeRpgLabel>();
        var editors=new List<(NativeRpgElement Host,NativeRpgElement Field,NativeRpgButton Chip)>();
        int generation=session.Generation;request["generation"]=generation;request["allPages"]=true;
        void ToggleEditor(NativeRpgElement field)
        {
            bool open=!field.Visible;
            
            foreach(var entry in editors)
            {
                bool selected=open&&ReferenceEquals(entry.Field,field);
                entry.Field.Visible=selected;entry.Chip.Primary=selected;entry.Chip.Invalidate();
            }
            foreach(var group in editors.GroupBy(e=>e.Host))group.Key.Visible=group.Any(e=>open&&ReferenceEquals(e.Field,field));
        }
        _dataEventConditionsRefresh=async()=>
        {
            if(pending is {IsCompleted:false})await pending;
            if(!current()||!session.Enabled||session.Generation!=generation)return;
            var task=Refresh();pending=task;await task;
        };
        async Task Refresh()
        {
            try
            {
                var result=await session.Connection.RequestAsync(request);
                if(!current()||session.Generation!=generation||host.IsDisposed)return;
                var pages=result.GetProperty("pages").EnumerateArray().Select(p=>p.Clone()).ToArray();
                var rows=pages.SelectMany(p=>p.GetProperty("rows").EnumerateArray()).ToArray();
                int? active=result.TryGetProperty("activePage",out var activeValue)&&activeValue.ValueKind==JsonValueKind.Number?activeValue.GetInt32():null;
                string next=string.Join("|",pages.Select(p=>p.GetProperty("index")+":"+p.GetProperty("commandCount")+":"+string.Join(",",p.GetProperty("rows").EnumerateArray().Select(r=>r.GetProperty("key")+":"+r.GetProperty("type")))));
                if(next!=signature||headings.Count!=pages.Length)
                {
                    
                    foreach(NativeRpgElement child in host.Controls.Cast<NativeRpgElement>().ToArray())child.Dispose();
                    bindings.Clear();headings.Clear();editors.Clear();
                    foreach(var page in pages)
                    {
                        int index=page.GetProperty("index").GetInt32(),count=page.GetProperty("commandCount").GetInt32();
                        var section=ModificationColumn();section.Name="EventPage:"+index;section.Margin=new(0,2,0,2);
                        var heading=WorkspaceLabel("");heading.Name="EventPageHeading:"+index;headings.Add(heading);
                        var header=ModificationRow(heading);header.Margin=new System.Windows.Thickness(0);
                        if(count>0||canInspect(index))
                        {
                            var open=IconButton("查看指令","",(_,_)=>showCommands(index),false,88);open.Name="EventPageCommands:"+index;header.Controls.Add(open);
                        }
                        WorkspaceAdd(section,header);DynamicDetailAdd(host,section);
                        var fields=page.GetProperty("rows").EnumerateArray().ToArray();
                        var chips=ModificationRow();chips.Name="EventConditionChips:"+index;chips.Margin=new System.Windows.Thickness(0);
                        DynamicDetailAdd(section,chips);
                        var editorHost=ModificationColumn();editorHost.Visible=false;DynamicDetailAdd(section,editorHost);
                        if(fields.Length==0)chips.Controls.Add(WorkspaceLabel("无条件"));
                        foreach(var row in fields)
                        {
                            string key=row.GetProperty("key").GetString()!,type=row.GetProperty("type").GetString()!;
                            var update=AddEventConditionField(editorHost,row,index);
                            var field=editorHost.Controls[editorHost.Controls.Count-1];field.Visible=false;
                            var chip=IconButton("","",(_,_)=>ToggleEditor(field),false,100);chip.Name="ConditionChip:"+index+":"+key;
                            chip.Margin=new(0,2,6,2);chips.Controls.Add(chip);editors.Add((editorHost,field,chip));
                            DynamicDetailAdd((NativeRpgColumn)field,IconButton("收起","",(_,_)=>ToggleEditor(field),false,60));
                            bindings.Add(value=>
                            {
                                update(value);
                                string original=value.GetProperty("name").GetString()??"",chinese=DataChinese(value),name=chinese.Length>0?chinese:original;
                                string shown=key.StartsWith("event:")?"":name.Length>16?name[..16]+"…":name;
                                string actual=type=="unavailable"?UiLanguage.Text("未读取"):ConditionPlainValue(value.GetProperty("value"));
                                string text=ReadableDataKey(key)+(shown.Length>0?" · "+shown:"")+"："+actual;
                                if(chip.Text!=text)chip.Text=text;
                                string requirement=ConditionPlainValue(value.GetProperty("target"));
                                if(value.GetProperty("condition").GetString()=="atLeast")requirement=UiLanguage.FormatTemplate("至少 {0}",requirement);
                                string tip=UiLanguage.FormatTemplate("{0}\n当前：{1}；出现要求：{2}\n点击修改当前值",ReadableDataKey(key)+" · "+name+(chinese.Length>0&&chinese!=original?"\n"+original:""),actual,requirement);
                                chip.AccessibleName=text;chip.View.ToolTip=tip;
                            });
                        }
                        var divider=new NativeRpgColumn{Height=1,Margin=new(0,6,0,4)};
                        divider.View.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty,"Fusion.Border");
                        WorkspaceAdd(section,divider,SizeType.Absolute,1);
                    }
                    signature=next;
                }
                summary.Text="";summary.Visible=false;
                for(int i=0;i<pages.Length;i++)
                {
                    int index=pages[i].GetProperty("index").GetInt32(),count=pages[i].GetProperty("commandCount").GetInt32();
                    string heading=$"第 {index+1} 页"+(active==index?" · 正在使用":"")+(count>0?$" · {count} 条指令":"");
                    if(headings[i].Text!=heading)headings[i].Text=heading;
                }
                for(int i=0;i<rows.Length;i++)bindings[i](rows[i]);
            }
            catch(Exception ex){if(current()&&!summary.IsDisposed){summary.Text="事件读取失败："+SafeDiagnosticOutput.ExceptionSummary(ex);summary.Visible=true;}}
        }
    }

    private static string ConditionPlainValue(JsonElement value)=>value.ValueKind switch
    {
        JsonValueKind.True=>UiLanguage.Text("开"),JsonValueKind.False=>UiLanguage.Text("关"),JsonValueKind.Null=>UiLanguage.Text("未读取"),_=>value.ToString()
    };

    private Action<JsonElement> AddEventConditionField(NativeRpgColumn host,JsonElement initial,int page)
    {
        string key=initial.GetProperty("key").GetString()!,type=initial.GetProperty("type").GetString()!,id=page+":"+key;
        var field=ModificationColumn();field.Name="Condition:"+id;field.Margin=new(0,2,0,8);
        var title=DataDetailLabel("");title.Name="ConditionName:"+id;
        var reference=DataDetailLabel("");reference.Name="ConditionReference:"+id;
        var status=DataDetailLabel("");status.Name="ConditionValue:"+id;
        var requirement=DataDetailLabel("");requirement.Name="ConditionRequirement:"+id;
        WorkspaceAdd(field,title);WorkspaceAdd(field,reference);
        bool updating=false,dirty=false;decimal? actualNumber=null;
        NativeRpgToggle? toggle=null;NativeRpgNumber? number=null;
        if(type=="boolean")
        {
            toggle=new NativeRpgToggle{Height=28,Width=100,AccessibleName=UiLanguage.FormatTemplate("条件值 {0}",key)};
            WpfUiLanguage.Property(toggle.View,System.Windows.Automation.AutomationProperties.NameProperty);
            toggle.CheckedChanged+=async(_,_)=>{
                if(updating||_dataLoading||_dataWriting)return;
                await ExecuteGameDataAsync(new(){["op"]="set",["key"]=key,["value"]=toggle.Checked});
            };
            WorkspaceAdd(field,ModificationRow(WorkspaceLabel("当前值"),toggle));
        }
        else if(type=="number")
        {
            number=new NativeRpgNumber{Width=110,Minimum=initial.GetProperty("min").GetDecimal(),Maximum=initial.GetProperty("max").GetDecimal(),AccessibleName=UiLanguage.FormatTemplate("条件值 {0}",key)};
            WpfUiLanguage.Property(number.View,System.Windows.Automation.AutomationProperties.NameProperty);
            number.ValueChanged+=(_,_)=>{if(!updating)dirty=true;};
            async Task Apply()
            {
                decimal submitted=number.Value;
                await ExecuteGameDataAsync(new(){["op"]="set",["key"]=key,["value"]=submitted});
                if(!number.IsDisposed&&number.Value==submitted&&actualNumber==submitted){dirty=false;status.Visible=false;}
            }
            number.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await Apply();}};
            WorkspaceAdd(field,ModificationRow(WorkspaceLabel("当前值"),number,IconButton("应用","check",async(_,_)=>await Apply(),true,70)));
        }
        WorkspaceAdd(field,status);WorkspaceAdd(field,requirement);DynamicDetailAdd(host,field);
        return row=>{
            string original=row.GetProperty("name").GetString()??"",chinese=DataChinese(row);
            string heading=ReadableDataKey(key)+" · "+(chinese.Length>0?chinese:original);
            string detail=chinese.Length>0&&chinese!=original?original:"";
            if(title.Text!=heading)title.Text=heading;
            if(reference.Text!=detail)reference.Text=detail;reference.Visible=detail.Length>0;
            string Plain(JsonElement value)=>value.ValueKind==JsonValueKind.True?UiLanguage.SwitchText(true):value.ValueKind==JsonValueKind.False?UiLanguage.SwitchText(false):value.ValueKind==JsonValueKind.Null?UiLanguage.Text("未读取到"):value.ToString();
            string text,neededText="";
            if(type=="unavailable")text="暂无法读取："+row.GetProperty("error").GetString();
            else
            {
                string needed=row.GetProperty("condition").GetString()=="atLeast"?UiLanguage.FormatTemplate("至少 {0}",Plain(row.GetProperty("target"))):Plain(row.GetProperty("target"));
                text=UiLanguage.FormatTemplate("当前值：{0}",Plain(row.GetProperty("value")));
                neededText=UiLanguage.FormatTemplate("要求：{0} · {1}",needed,UiLanguage.Text(row.GetProperty("met").GetBoolean()?"已满足":"未满足"));
                if(type=="readonly")text+=UiLanguage.Text("（文本变量）");
            }
            if(status.Text!=text)status.Text=text;
            // A separate live value is needed only while a numeric draft differs or for a read-only field.
            status.Visible=type is "readonly" or "unavailable"||dirty;
            if(requirement.Text!=neededText)requirement.Text=neededText;
            requirement.Visible=neededText.Length>0;
            updating=true;
            try
            {
                if(toggle is not null){toggle.Checked=row.GetProperty("value").GetBoolean();toggle.Text=UiLanguage.SwitchText(toggle.Checked);}
                if(number is not null){actualNumber=row.GetProperty("value").GetDecimal();if(!dirty&&!number.ContainsFocus)number.Value=Math.Clamp(actualNumber.Value,number.Minimum,number.Maximum);}
            }
            finally{updating=false;}
        };
    }
    }
}
