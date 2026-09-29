using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private Func<Task>? _dataEventConditionsRefresh;

    private void BindEventConditions(GameDataSession session,JsonObject request,TableLayoutPanel host,Label summary,Func<bool> current,Action<int> showCommands,Func<int,bool> canInspect)
    {
        Task? pending=null;string signature="";
        var bindings=new List<Action<JsonElement>>();
        var headings=new List<Label>();
        var editors=new List<(Control Host,Control Field,GameActionButton Chip)>();
        int generation=session.Generation;request["generation"]=generation;request["allPages"]=true;
        void ToggleEditor(Control field)
        {
            bool open=!field.Visible;
            using var layout=new UiLayoutBatch(host);
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
                    using var batch=new UiLayoutBatch(host);
                    foreach(Control child in host.Controls.Cast<Control>().ToArray())child.Dispose();
                    host.RowStyles.Clear();host.RowCount=0;bindings.Clear();headings.Clear();editors.Clear();
                    foreach(var page in pages)
                    {
                        int index=page.GetProperty("index").GetInt32(),count=page.GetProperty("commandCount").GetInt32();
                        var section=ModificationColumn();section.Name="EventPage:"+index;section.Margin=new(0,2,0,2);
                        var heading=WorkspaceLabel("");heading.Name="EventPageHeading:"+index;headings.Add(heading);
                        var header=ModificationRow(heading);header.Margin=Padding.Empty;
                        if(count>0||canInspect(index))
                        {
                            var open=IconButton("查看指令","",(_,_)=>showCommands(index),false,88);open.Name="EventPageCommands:"+index;header.Controls.Add(open);
                        }
                        WorkspaceAdd(section,header);DynamicDetailAdd(host,section);
                        var fields=page.GetProperty("rows").EnumerateArray().ToArray();
                        var chips=ModificationRow();chips.Name="EventConditionChips:"+index;chips.Margin=Padding.Empty;
                        DynamicDetailAdd(section,chips);
                        var editorHost=ModificationColumn();editorHost.Visible=false;DynamicDetailAdd(section,editorHost);
                        if(fields.Length==0)chips.Controls.Add(WorkspaceLabel("无条件"));
                        foreach(var row in fields)
                        {
                            string key=row.GetProperty("key").GetString()!,type=row.GetProperty("type").GetString()!;
                            var update=AddEventConditionField(editorHost,row,index);
                            var field=editorHost.Controls[editorHost.Controls.Count-1];field.Visible=false;
                            var chip=IconButton("","",(_,_)=>ToggleEditor(field),false,100);chip.Name="ConditionChip:"+index+":"+key;
                            chip.LogicalHeight=28;chip.Margin=new(0,2,6,2);chips.Controls.Add(chip);editors.Add((editorHost,field,chip));
                            DynamicDetailAdd((TableLayoutPanel)field,IconButton("收起","",(_,_)=>ToggleEditor(field),false,60));
                            void CapWidth(){chip.MaximumSize=new(Math.Max(100,chips.ClientSize.Width-chip.Margin.Horizontal),0);}
                            chips.SizeChanged+=(_,_)=>CapWidth();CapWidth();
                            bindings.Add(value=>
                            {
                                update(value);
                                string original=value.GetProperty("name").GetString()??"",chinese=DataChinese(value),name=chinese.Length>0?chinese:original;
                                string shown=key.StartsWith("event:")?"":name.Length>16?name[..16]+"…":name;
                                string actual=type=="unavailable"?"未读取":ConditionPlainValue(value.GetProperty("value"));
                                string text=ReadableDataKey(key)+(shown.Length>0?" · "+shown:"")+"："+actual;
                                if(chip.Text!=text)chip.Text=text;
                                string requirement=(value.GetProperty("condition").GetString()=="atLeast"?"至少 ":"")+ConditionPlainValue(value.GetProperty("target"));
                                string tip=ReadableDataKey(key)+" · "+name+(chinese.Length>0&&chinese!=original?"\n"+original:"")+"\n当前："+actual+"；出现要求："+requirement+"\n点击修改当前值";
                                chip.AccessibleName=text;chip.AccessibleDescription=tip;_fusionTips.SetToolTip(chip,tip);
                            });
                        }
                        var divider=new Panel{Height=1,Dock=DockStyle.Top,Margin=new(0,6,0,4)};
                        divider.Paint+=(_,e)=>{using var pen=new Pen(UiTheme.Current.Border);e.Graphics.DrawLine(pen,0,0,divider.Width,0);};
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
        JsonValueKind.True=>"开",JsonValueKind.False=>"关",JsonValueKind.Null=>"未读取",_=>value.ToString()
    };

    private Action<JsonElement> AddEventConditionField(TableLayoutPanel host,JsonElement initial,int page)
    {
        string key=initial.GetProperty("key").GetString()!,type=initial.GetProperty("type").GetString()!,id=page+":"+key;
        var field=ModificationColumn();field.AutoSize=true;field.Name="Condition:"+id;field.Margin=new(0,2,0,8);
        var title=DataDetailLabel("");title.Name="ConditionName:"+id;
        var reference=DataDetailLabel("");reference.Name="ConditionReference:"+id;
        var status=DataDetailLabel("");status.Name="ConditionValue:"+id;
        var requirement=DataDetailLabel("");requirement.Name="ConditionRequirement:"+id;
        WorkspaceAdd(field,title);WorkspaceAdd(field,reference);
        bool updating=false,dirty=false;decimal? actualNumber=null;
        WorkspaceSwitch? toggle=null;WorkspaceNumberBox? number=null;
        if(type=="boolean")
        {
            toggle=new WorkspaceSwitch{Height=28,Width=100,AccessibleName="条件值 "+key};
            toggle.CheckedChanged+=async(_,_)=>{
                if(updating||_dataLoading||_dataWriting)return;
                await ExecuteGameDataAsync(new(){["op"]="set",["key"]=key,["value"]=toggle.Checked});
            };
            WorkspaceAdd(field,ModificationRow(WorkspaceLabel("当前值"),toggle));
        }
        else if(type=="number")
        {
            number=new WorkspaceNumberBox{Width=110,Minimum=initial.GetProperty("min").GetDecimal(),Maximum=initial.GetProperty("max").GetDecimal(),AccessibleName="条件值 "+key};
            number.ValueChanged+=(_,_)=>{if(!updating)dirty=true;};
            async Task Apply()
            {
                decimal submitted=number.Value;
                await ExecuteGameDataAsync(new(){["op"]="set",["key"]=key,["value"]=submitted});
                if(!number.IsDisposed&&number.Value==submitted&&actualNumber==submitted){dirty=false;status.Visible=false;}
            }
            number.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await Apply();}};
            WorkspaceAdd(field,ModificationRow(WorkspaceLabel("当前值"),new WorkspaceNumberField(number),IconButton("应用","check",async(_,_)=>await Apply(),true,70)));
        }
        WorkspaceAdd(field,status);WorkspaceAdd(field,requirement);DynamicDetailAdd(host,field);
        return row=>{
            string original=row.GetProperty("name").GetString()??"",chinese=DataChinese(row);
            string heading=ReadableDataKey(key)+" · "+(chinese.Length>0?chinese:original);
            string detail=chinese.Length>0&&chinese!=original?original:"";
            if(title.Text!=heading)title.Text=heading;
            if(reference.Text!=detail)reference.Text=detail;reference.Visible=detail.Length>0;
            string Plain(JsonElement value)=>value.ValueKind==JsonValueKind.True?"开启":value.ValueKind==JsonValueKind.False?"关闭":value.ValueKind==JsonValueKind.Null?"未读取到":value.ToString();
            string text,neededText="";
            if(type=="unavailable")text="暂无法读取："+row.GetProperty("error").GetString();
            else
            {
                string needed=row.GetProperty("condition").GetString()=="atLeast"?"至少 "+Plain(row.GetProperty("target")):Plain(row.GetProperty("target"));
                text="当前值："+Plain(row.GetProperty("value"));
                neededText="要求："+needed+" · "+(row.GetProperty("met").GetBoolean()?"已满足":"未满足");
                if(type=="readonly")text+="（文本变量）";
            }
            if(status.Text!=text)status.Text=text;
            // A separate live value is needed only while a numeric draft differs or for a read-only field.
            status.Visible=type is "readonly" or "unavailable"||dirty;
            if(requirement.Text!=neededText)requirement.Text=neededText;
            requirement.Visible=neededText.Length>0;
            updating=true;
            try
            {
                if(toggle is not null){toggle.Checked=row.GetProperty("value").GetBoolean();toggle.Text=toggle.Checked?"开启":"关闭";}
                if(number is not null){actualNumber=row.GetProperty("value").GetDecimal();if(!dirty&&!number.ContainsFocus)number.Value=Math.Clamp(actualNumber.Value,number.Minimum,number.Maximum);}
            }
            finally{updating=false;}
        };
    }
}
