using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Retained cards: refreshing a value never recreates a focused editor.
internal sealed class GameCommonBoard : Panel
{
    private readonly WorkspaceScrollView scroll=new(){Dock=DockStyle.Fill};
    private readonly Panel canvas=new(){AutoSize=false};
    private readonly List<Card> cards=[];
    private readonly Label numbersTitle=new(){Text="数值调整",TextAlign=ContentAlignment.MiddleLeft},switchesTitle=new(){Text="常用开关",TextAlign=ContentAlignment.MiddleLeft};
    private bool arranging,available;
    internal Func<JsonObject,Task>? Execute;
    internal int CardCount=>cards.Count;
    internal int ContentHeight=>canvas.Height;
    internal event Action? ContentHeightChanged;
    internal GameCommonBoard()
    {
        DoubleBuffered=true;Name="CommonOperations";scroll.Controls.Add(canvas);Controls.Add(scroll);
        canvas.Controls.AddRange([numbersTitle,switchesTitle]);
        canvas.SizeChanged+=(_,_)=>Arrange();
        scroll.SizeChanged+=(_,_)=>Arrange();
    }
    internal void ClearRows()
    {
        if(cards.Count==0)return;
        foreach(var card in cards)card.Dispose();cards.Clear();numbersTitle.Visible=switchesTitle.Visible=false;canvas.Height=0;ContentHeightChanged?.Invoke();
    }
    internal bool SetRows(JsonElement[] rows)
    {
        bool created=false;var keys=rows.Select(r=>r.GetProperty("key").GetString()!).ToHashSet();
        foreach(var card in cards.Where(c=>!keys.Contains(c.Key)).ToArray()){card.Dispose();cards.Remove(card);}
        foreach(var row in rows)
        {
            string key=row.GetProperty("key").GetString()!;var card=cards.FirstOrDefault(c=>c.Key==key);
            if(card is null){card=new Card(key,Send);cards.Add(card);canvas.Controls.Add(card.Surface);created=true;}
            card.Update(row);card.SetAvailability(available);
        }
        Arrange();return created;
    }
    internal void SetAvailability(bool enabled,string message)
    {
        available=enabled;string next=enabled?"开关点击即生效；数值输入后按回车或点击应用。":message;
        AccessibleDescription=next;
        foreach(var card in cards)card.SetAvailability(enabled);Arrange();
    }
    private async Task Send(JsonObject request)
    {
        if(!available||Execute is null)return;
        await Execute(request);
    }
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);Arrange();}
    private void Arrange()
    {
        if(arranging||canvas.Width<=0)return;arranging=true;
        try
        {
            int S(int n)=>n*DeviceDpi/96;
            int y=S(4),gap=S(24),height=S(44),rowGap=S(10);bool any=false;
            foreach(bool boolean in new[]{false,true})
            {
                var group=cards.Where(c=>c.IsBoolean==boolean).ToArray();var heading=boolean?switchesTitle:numbersTitle;heading.Visible=group.Length>0;if(group.Length==0)continue;
                if(any)y+=S(22);
                heading.SetBounds(0,y,canvas.Width,S(28));y+=S(34);
                int columns=Math.Clamp((canvas.Width+gap)/(S(boolean?245:380)+gap),1,boolean?3:2);
                int width=Math.Min(S(boolean?340:490),(canvas.Width-gap*(columns-1))/columns);
                for(int i=0;i<group.Length;i++)
                {
                    group[i].Surface.SetBounds((i%columns)*(width+gap),y+(i/columns)*(height+rowGap),width,height);
                }
                y+=((group.Length+columns-1)/columns)*(height+rowGap)-rowGap;any=true;
            }
            int next=any?y+S(12):0;if(canvas.Height!=next){canvas.Height=next;ContentHeightChanged?.Invoke();}
        }
        finally{arranging=false;}
    }
    protected override void Dispose(bool disposing){if(disposing)foreach(var card in cards)card.Dispose();base.Dispose(disposing);}
    private sealed class Card : IDisposable
    {
        internal readonly string Key;
        internal readonly Panel Surface=new(){Name="CommonOperation"};
        private readonly Label title=new(){AutoSize=false,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft};
        private readonly ToolTip tip=new();
        private readonly WorkspaceSwitch toggle=new(){Width=112,Height=36};
        private readonly WorkspaceNumberBox number=new(){Width=104};
        private readonly WorkspaceNumberField field;
        private readonly GameActionButton apply=new("应用"){MinimumLogicalWidth=64,LogicalHeight=34,Primary=true},lockButton=new("锁定"){MinimumLogicalWidth=64,LogicalHeight=34};
        private readonly Func<JsonObject,Task> send;
        private JsonElement row;
        private bool updating,isBoolean,canLock,locked,editable;
        internal bool IsBoolean=>isBoolean;
        internal Card(string key,Func<JsonObject,Task> execute)
        {
            Key=key;send=execute;Surface.Name="CommonOperation:"+key;field=new(number);
            foreach(Control control in new Control[]{title,toggle,field,apply,lockButton})Surface.Controls.Add(control);
            toggle.AccessibleName=key;number.AccessibleName=key;Surface.Layout+=(_,_)=>Arrange();
            toggle.CheckedChanged+=async(_,_)=>{if(!updating&&editable)await Send("set",JsonValue.Create(toggle.Checked)!);};
            apply.Click+=async(_,_)=>await Apply();
            number.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await Apply();}};
            lockButton.Click+=async(_,_)=>{if(editable&&canLock)await Send(locked?"unlock":"lock",JsonNode.Parse(row.GetProperty("value").GetRawText())!);};
        }
        private Task Apply()=>editable?Send("set",JsonValue.Create(number.Value)!):Task.CompletedTask;
        private Task Send(string operation,JsonNode value)=>send(new(){["op"]=operation,["key"]=Key,["value"]=value});
        internal void Update(JsonElement value)
        {
            row=value.Clone();updating=true;
            try
            {
                var parts=(row.GetProperty("name").GetString()??Key).Split(" / ",2);title.Text=parts[0];tip.SetToolTip(title,row.GetProperty("name").GetString());
                isBoolean=row.GetProperty("type").GetString()=="boolean";canLock=row.GetProperty("canLock").GetBoolean();locked=row.GetProperty("locked").GetBoolean();
                toggle.Visible=isBoolean;field.Visible=apply.Visible=!isBoolean;lockButton.Visible=canLock;
                if(isBoolean)toggle.Checked=row.GetProperty("value").GetBoolean();
                else
                {
                    number.Minimum=decimal.MinValue;number.Maximum=decimal.MaxValue;
                    number.Minimum=row.TryGetProperty("min",out var min)?min.GetDecimal():0;number.Maximum=row.TryGetProperty("max",out var max)?max.GetDecimal():int.MaxValue;
                    decimal actual=Math.Clamp(row.GetProperty("value").GetDecimal(),number.Minimum,number.Maximum);
                    if(!number.ContainsFocus)number.Value=actual;
                }
                lockButton.Text=locked?"解锁":"锁定";lockButton.Primary=locked;
                apply.RefreshMetrics();lockButton.RefreshMetrics();Arrange();
            }
            finally{updating=false;}
        }
        internal void SetAvailability(bool enabled)
        {
            editable=enabled;toggle.Enabled=field.Enabled=apply.Enabled=enabled;lockButton.Enabled=enabled&&canLock;
        }
        private void Arrange()
        {
            int S(int n)=>n*Surface.DeviceDpi/96;int width=Surface.ClientSize.Width;int height=GameActionButton.RowHeight(apply,34);int y=Math.Max(0,(Surface.Height-height)/2);
            apply.Height=lockButton.Height=height;
            toggle.SetBounds(width-S(96),y,S(96),height);
            lockButton.Location=new(width-lockButton.Width,y);
            apply.Location=new(lockButton.Left-S(8)-apply.Width,y);
            field.SetBounds(apply.Left-S(8)-S(112),y,S(112),height);
            int textWidth=Math.Max(1,(isBoolean?toggle.Left:field.Left)-S(12));
            title.SetBounds(0,0,textWidth,Surface.Height);
        }
        public void Dispose(){tip.Dispose();Surface.Dispose();}
    }
}
