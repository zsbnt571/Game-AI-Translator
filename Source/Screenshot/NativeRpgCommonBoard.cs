using System.Text.Json;
using System.Text.Json.Nodes;
using W = System.Windows;
using WC = System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal sealed class NativeRpgCommonBoard : NativeRpgElement
{
    private readonly Dictionary<string, Card> cards = [];
    private readonly Dictionary<string, Section> sections=[];
    private readonly WC.StackPanel grouped=new();
    private readonly WC.StackPanel attributes=new();
    private readonly Dictionary<string,WC.StackPanel> attributePanels=[];
    private readonly WpfGameSpeedControl speed=new(){Visibility=W.Visibility.Collapsed,Margin=new(0,8,0,12)};
    private JsonElement? speedRow;
    private string speedContextKey="";
    private bool speedBusy;
    internal void SetSpeedContext(string contextKey,bool busy){if(speedContextKey==contextKey&&speedBusy==busy)return;speedContextKey=contextKey;speedBusy=busy;RefreshSpeed();}
    private readonly ResponsiveEqualColumnsPanel numbers = new(420, 2), switches = new(280, 3);
    private readonly NativeLocalizedTextBlock numbersTitle = new() { Text = "数值调整", FontWeight = W.FontWeights.SemiBold, Margin = new(0, 8, 0, 12) }, switchesTitle = new() { Text = "常用开关", FontWeight = W.FontWeights.SemiBold, Margin = new(0, 18, 0, 12) };
    private bool available;
    private readonly bool showTitles;
    private readonly bool fieldLayout;
    internal Func<JsonObject, Task>? Execute;
    internal Action<string[]>? RequestNames;
    internal NativeRpgCommonBoard(bool showTitles=true,bool fieldLayout=false) : base(new WC.StackPanel())
    {
        this.showTitles=showTitles;this.fieldLayout=fieldLayout;numbersTitle.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");switchesTitle.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");
        var panel = (WC.StackPanel)View; panel.Children.Add(speed);panel.Children.Add(numbersTitle); panel.Children.Add(numbers); panel.Children.Add(switchesTitle); panel.Children.Add(switches);panel.Children.Add(attributes);panel.Children.Add(grouped);
        speed.Commit=async rate=>{if(available&&speedRow is not null&&Execute is not null)await Execute(new(){["op"]="set",["key"]="option:speed",["value"]=rate});};
        UiLanguage.LanguageChanged+=RefreshLanguage;
    }
    private sealed record SectionPosition(string Selected,bool Expanded,double ListOffset,double DetailOffset);
    private readonly Dictionary<string,SectionPosition> sectionPositions=[];
    internal void ClearRows() { foreach (var card in cards.Values) card.Dispose(); cards.Clear(); numbers.Children.Clear(); switches.Children.Clear();attributePanels.Clear();attributes.Children.Clear();foreach(var section in sections.Values)section.Dispose();sections.Clear();sectionPositions.Clear();grouped.Children.Clear();speedRow=null;RefreshSpeed(); Arrange(); }
    internal bool SetRows(JsonElement[] rows)
    {
        bool added = false;
        speedRow=rows.FirstOrDefault(row=>Field(row,"key")=="option:speed") is {ValueKind:JsonValueKind.Object} speedValue?speedValue:null;
        RefreshSpeed();rows=rows.Where(row=>Field(row,"key")!="option:speed").ToArray();
        var sectionRows=rows.Where(row=>Field(row,"sectionKey").Length>0).GroupBy(row=>Field(row,"sectionKey")).ToArray();
        var sectionKeys=sectionRows.Select(section=>section.Key).ToHashSet();
        foreach(var key in sections.Keys.Where(key=>!sectionKeys.Contains(key)).ToArray()){sectionPositions[key]=sections[key].CapturePosition();grouped.Children.Remove(sections[key].View);sections[key].Dispose();sections.Remove(key);}
        foreach(var group in sectionRows)
        {
            bool created=false;
            if(!sections.TryGetValue(group.Key,out var section)){section=new(group.Key,request=>available&&Execute is not null?Execute(request):Task.CompletedTask,nameKeys=>RequestNames?.Invoke(nameKeys),sectionPositions.GetValueOrDefault(group.Key));sections.Add(group.Key,section);grouped.Children.Add(section.View);added=true;created=true;}
            section.Update(group.ToArray());section.SetAvailability(available);
            if(created)section.View.IsExpanded=sectionPositions.TryGetValue(group.Key,out var position)?position.Expanded:group.First().TryGetProperty("sectionExpanded",out var expanded)&&expanded.ValueKind==JsonValueKind.True;
        }
        rows=rows.Where(row=>Field(row,"sectionKey").Length==0).ToArray();
        var keys = rows.Select(row => row.GetProperty("key").GetString()!).ToHashSet();
        foreach (string key in cards.Keys.Where(key => !keys.Contains(key)).ToArray()) { var card = cards[key]; RemoveFromParent(card.View);card.Dispose(); cards.Remove(key); }
        if(fieldLayout)
        {
            var groupKeys=rows.Select(row=>Field(row,"attributeGroup")).ToHashSet();
            foreach(string key in attributePanels.Keys.Where(key=>!groupKeys.Contains(key)).ToArray()){attributes.Children.Remove(attributePanels[key]);attributePanels.Remove(key);}
        }
        rows=rows.OrderBy(row=>row.TryGetProperty("attributeOrder",out var order)?order.GetInt32():0).ToArray();
        foreach (var row in rows)
        {
            string key = row.GetProperty("key").GetString()!;
            if (!cards.TryGetValue(key, out var card)) { card = new Card(key, request => available && Execute is not null ? Execute(request) : Task.CompletedTask,fieldLayout); cards.Add(key, card); added = true; }
            card.Update(row); card.SetAvailability(available);
            if(fieldLayout)
            {
                string group=Field(row,"attributeGroup");
                if(!attributePanels.TryGetValue(group,out var host))
                {
                    host=new();attributePanels.Add(group,host);attributes.Children.Add(host);
                    if(Field(row,"attributeTitle") is {Length:>0} caption)
                    {
                        var label=new NativeLocalizedTextBlock{Text=caption,FontWeight=W.FontWeights.SemiBold,Margin=new(0,8,0,12)};
                        label.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");
                        W.Automation.AutomationProperties.SetAutomationId(label,"AttributeGroup:"+group);host.Children.Add(label);
                    }
                }
                if(!host.Children.Contains(card.View)){RemoveFromParent(card.View);host.Children.Add(card.View);}
                continue;
            }
            var target = card.IsBoolean ? switches : numbers; var previous = card.IsBoolean ? numbers : switches; previous.Children.Remove(card.View); if (!target.Children.Contains(card.View)) target.Children.Add(card.View);
        }
        if(fieldLayout)
        {
            int groupIndex=0;
            foreach(var group in rows.GroupBy(row=>Field(row,"attributeGroup")))
            {
                var host=attributePanels[group.Key];
                if(attributes.Children.IndexOf(host)!=groupIndex){attributes.Children.Remove(host);attributes.Children.Insert(groupIndex,host);}
                int index=host.Children.Count>0&&host.Children[0] is WC.TextBlock?1:0;
                foreach(var row in group)
                {
                    var card=cards[Field(row,"key")].View;
                    if(host.Children.IndexOf(card)!=index){host.Children.Remove(card);host.Children.Insert(index,card);}
                    index++;
                }
                groupIndex++;
            }
        }
        Arrange(); return added;
    }
    internal void SetAvailability(bool enabled, string message) { available = enabled; View.ToolTip = message;WpfUiLanguage.Property(View,W.FrameworkElement.ToolTipProperty); foreach (var card in cards.Values) card.SetAvailability(enabled);foreach(var section in sections.Values)section.SetAvailability(enabled);RefreshSpeed(); }
    private void RefreshLanguage(object? sender,EventArgs e)
    {
        if(IsDisposed)return;
        if(!View.Dispatcher.CheckAccess()){View.Dispatcher.BeginInvoke((Action)(()=>RefreshLanguage(sender,e)));return;}
        // Change presentation in place. Re-applying rows would normalize an
        // unfinished numeric draft and reconstruct the selected role's editors.
        foreach(var section in sections.Values)section.RefreshLanguage();
    }
    public override void Dispose()
    {
        if(IsDisposed)return;
        UiLanguage.LanguageChanged-=RefreshLanguage;ClearRows();base.Dispose();
    }
    private void RefreshSpeed()
    {
        speed.Visibility=speedRow is null?W.Visibility.Collapsed:W.Visibility.Visible;
        speed.SetState(speedRow is {} row?ModificationSpeedState.FromRpgRow(row,available,speedBusy,speedContextKey):new ModificationSpeedState(false,false,false,1,1,4,1,[1,2,3,4],"",speedContextKey,true));
    }
    private void Arrange()
    {
        numbersTitle.Visibility = showTitles&&numbers.Children.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
        switchesTitle.Visibility = showTitles&&switches.Children.Count > 0 ? W.Visibility.Visible : W.Visibility.Collapsed;
    }
    private static string Field(JsonElement row,string name)=>row.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
    private static void RemoveFromParent(W.FrameworkElement element){if(element.Parent is WC.Panel parent)parent.Children.Remove(element);}
    // Only the selected object's editors are constructed. The object list is
    // virtualized; a large actor database must not allocate thousands of
    // editable cards just because the user opens its category.
    private sealed class Section : IDisposable
    {
        internal readonly WC.Expander View=new(){Margin=new(0,6,0,10),HorizontalContentAlignment=W.HorizontalAlignment.Stretch};
        private readonly WC.StackPanel content=new();
        private readonly WC.DataGrid objects=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,CanUserSortColumns=false,SelectionMode=WC.DataGridSelectionMode.Single,SelectionUnit=WC.DataGridSelectionUnit.FullRow,HeadersVisibility=WC.DataGridHeadersVisibility.Column,RowHeaderWidth=0,VerticalAlignment=W.VerticalAlignment.Top};
        private readonly WC.ScrollViewer details=new(){VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=WC.ScrollBarVisibility.Disabled,Height=WpfModificationLayout.DetailHeight};
        private readonly WC.TextBlock selectedName=new(){FontSize=20,FontWeight=W.FontWeights.SemiBold,TextWrapping=W.TextWrapping.Wrap,Margin=new(0,0,0,8)};
        private readonly WC.TextBlock selectedOriginal=new(){TextWrapping=W.TextWrapping.Wrap,Margin=new(0,0,0,12)};
        private readonly NativeLocalizedTextBlock explanation=new(){TextWrapping=W.TextWrapping.Wrap,Margin=new(0,0,0,10)};
        private readonly WC.StackPanel detailPanel=new();
        private readonly WpfModificationColumns browser=new();
        private readonly Func<JsonObject,Task> send;
        private readonly Action<string[]> requestNames;
        private JsonElement[] rows=[];
        private bool loaded,available,syncing;
        private string activeKey="";
        private SectionPosition? restorePosition;
        private int selectionRevision;
        private bool disposed;
        private NativeRpgCommonBoard? board;
        private sealed class ObjectRows(string key) : System.ComponentModel.INotifyPropertyChanged
        {
            internal string Key {get;}=key;
            public string Title {get;private set;}="";
            public string Original {get;private set;}="";
            public string Value {get;private set;}="";
            public string Lock {get;private set;}="";
            internal JsonElement[] Rows=[];
            internal void Update(string title,string original,JsonElement[] rows)
            {
                Rows=rows;
                var level=rows.FirstOrDefault(row=>Field(row,"caption")=="等级");
                string summary=level.ValueKind==JsonValueKind.Object?UiLanguage.Text("等级")+" "+level.GetProperty("value").ToString():UiLanguage.Format($"{rows.Length} 项属性");
                int locked=rows.Count(row=>row.TryGetProperty("locked",out var value)&&value.ValueKind==JsonValueKind.True);
                string lockText=locked==0?"—":locked+" "+UiLanguage.Text("项");
                if(Title==title&&Original==original&&Value==summary&&Lock==lockText)return;
                Title=title;Original=original;Value=summary;Lock=lockText;PropertyChanged?.Invoke(this,new(null));
            }
            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }
        internal Section(string key,Func<JsonObject,Task> send,Action<string[]> requestNames,SectionPosition? position=null)
        {
            restorePosition=position;activeKey=position?.Selected??"";
            this.send=send;this.requestNames=requestNames;W.Automation.AutomationProperties.SetAutomationId(View,"CommonSection:"+key);View.Content=content;
            selectedName.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");selectedOriginal.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");explanation.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");
            W.Automation.AutomationProperties.SetAutomationId(selectedName,"CommonSelectedName:"+key);
            W.Automation.AutomationProperties.SetAutomationId(selectedOriginal,"CommonSelectedOriginal:"+key);
            explanation.Text=key=="recollection"?"按字段名称识别的候选。请先核对用途和当前值；登记数值不一定表示已解锁。":"按字段名称归类的属性；不代表当前队伍。";
            detailPanel.Children.Add(selectedName);detailPanel.Children.Add(selectedOriginal);detailPanel.Children.Add(explanation);details.Content=detailPanel;
            View.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");
            objects.SetResourceReference(WC.Control.ForegroundProperty,"Fusion.Text");
            objects.SetResourceReference(WC.Control.BackgroundProperty,"Fusion.Secondary");
            objects.SetResourceReference(WC.Control.BorderBrushProperty,"Fusion.Border");
            W.Automation.AutomationProperties.SetAutomationId(objects,"CommonObjects:"+key);
            objects.Columns.Add(new WC.DataGridTemplateColumn{Header=UiLanguage.Text("名称 / Name"),CellTemplate=WpfModificationLayout.Names("Title","Original"),Width=new(70,WC.DataGridLengthUnitType.Star)});
            objects.Columns.Add(new WC.DataGridTextColumn{Header=UiLanguage.Text("当前值"),Binding=new System.Windows.Data.Binding("Value"),Width=new(18,WC.DataGridLengthUnitType.Star)});
            objects.Columns.Add(new WC.DataGridTextColumn{Header=UiLanguage.Text("锁定"),Binding=new System.Windows.Data.Binding("Lock"),Width=new(12,WC.DataGridLengthUnitType.Star)});
            foreach(var column in objects.Columns)WpfUiLanguage.Property(column,WC.DataGridColumn.HeaderProperty);
            WpfModificationLayout.Table(objects);
            WC.ScrollViewer.SetCanContentScroll(objects,true);
            browser.Children.Add(objects);browser.Children.Add(details);WC.Grid.SetColumn(details,1);
            objects.SelectionChanged+=(_,_)=>{if(!syncing&&objects.SelectedItem is ObjectRows selected)Select(selected);};
            objects.AddHandler(WC.ScrollViewer.ScrollChangedEvent,new WC.ScrollChangedEventHandler((_,e)=>RequestVisibleNames((int)e.VerticalOffset)));
            View.Expanded+=(_,_)=>{loaded=true;Refresh();};
        }
        internal void Update(JsonElement[] value)
        {
            rows=value;RefreshHeader();
            if(loaded)Refresh();
        }
        private void RefreshHeader()
        {
            if(rows.Length==0)return;
            int objectCount=rows.Select(row=>Field(row,"groupKey")).Where(key=>key.Length>0).Distinct().Count();
            View.Header=UiLanguage.Text(Field(rows[0],"sectionTitle"))+" · "+(objectCount>0?UiLanguage.Format($"{objectCount} 个对象 · {rows.Length} 项属性"):rows.Length.ToString());
        }
        internal void RefreshLanguage()
        {
            RefreshHeader();
            foreach(var item in objects.Items.Cast<ObjectRows>())item.Update(item.Title,item.Original,item.Rows);
        }
        private void Refresh()
        {
            var existing=objects.Items.Cast<ObjectRows>().ToDictionary(item=>item.Key);
            var next=rows.GroupBy(row=>Field(row,"groupKey")).Select(group=>
            {
                var item=existing.GetValueOrDefault(group.Key)??new ObjectRows(group.Key);
                var first=group.First();string original=Field(first,"groupOriginal"),title=Field(first,"groupTitle");
                item.Update(title,original.Length>0?original:title,group.ToArray());return item;
            }).ToArray();
            bool hasObjects=next.Any(group=>group.Key.Length>0);
            if(hasObjects)
            {
                if(!browser.Children.Contains(details)){content.Children.Remove(details);browser.Children.Add(details);}
                if(!content.Children.Contains(browser)){content.Children.Clear();content.Children.Add(browser);}
                syncing=true;
                try{if(!objects.Items.Cast<ObjectRows>().SequenceEqual(next))objects.ItemsSource=next;objects.SelectedItem=next.FirstOrDefault(group=>group.Key==activeKey)??next.FirstOrDefault();}
                finally{syncing=false;}
                if(objects.SelectedItem is ObjectRows selected)Select(selected);
                RequestVisibleNames(0);
                if(restorePosition is {} position)
                {
                    int revision=selectionRevision;
                    View.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,(Action)(()=>
                    {if(disposed||revision!=selectionRevision||!ReferenceEquals(restorePosition,position))return;WpfModificationLayout.TableScroll(objects)?.ScrollToVerticalOffset(position.ListOffset);if(activeKey==position.Selected)details.ScrollToVerticalOffset(position.DetailOffset);restorePosition=null;}));
                }
            }
            else if(next.FirstOrDefault() is {} only)
            {if(!content.Children.Contains(details)){browser.Children.Remove(details);content.Children.Clear();content.Children.Add(details);details.Margin=new(0);}Select(only);}
        }
        private void Select(ObjectRows selected)
        {
            if(activeKey!=selected.Key){selectionRevision++;restorePosition=null;details.ScrollToTop();}
            activeKey=selected.Key;
            selectedName.Text=Field(selected.Rows[0],"groupTitle");
            selectedOriginal.Text=selected.Original;
            selectedOriginal.Visibility=selectedOriginal.Text.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
            selectedName.ToolTip=selected.Original;
            selectedName.Visibility=selectedName.Text.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
            if(board is null){board=new(false,true){Execute=send};detailPanel.Children.Add(board.View);}
            W.Automation.AutomationProperties.SetAutomationId(details,"CommonObject:"+activeKey);
            board.SetRows(selected.Rows.Select(row=>{var value=JsonNode.Parse(row.GetRawText())!.AsObject();value.Remove("sectionKey");return JsonSerializer.SerializeToElement(value);}).ToArray());
            board.SetAvailability(available,"");
            requestNames(selected.Rows.Take(32).Select(row=>Field(row,"key")).ToArray());
        }
        private void RequestVisibleNames(int first)=>requestNames(objects.Items.Cast<ObjectRows>().Skip(Math.Max(0,first)).Take(32).Select(item=>Field(item.Rows[0],"key")).ToArray());
        internal void SetAvailability(bool value){available=value;board?.SetAvailability(value,"");}
        internal SectionPosition CapturePosition()=>restorePosition??new(activeKey,View.IsExpanded,WpfModificationLayout.TableScroll(objects)?.VerticalOffset??0,details.VerticalOffset);
        public void Dispose(){disposed=true;selectionRevision++;board?.Dispose();board=null;details.Content=null;objects.ItemsSource=null;content.Children.Clear();}
    }
    private sealed class Card : IDisposable
    {
        internal readonly WC.Grid View = new() { Margin = new(0, 0, 16, 12), MinHeight = 44 };
        private readonly NativeLocalizedTextBlock title = new() { VerticalAlignment = W.VerticalAlignment.Center, TextTrimming = W.TextTrimming.CharacterEllipsis, Margin = new(0, 0, 12, 0) };
        private readonly WC.TextBlock source = new() { FontSize=12,TextTrimming=W.TextTrimming.CharacterEllipsis,Margin=new(0,3,12,0),Visibility=W.Visibility.Collapsed };
        private readonly NativeRpgNumber number = new() { Width = 112 };
        private readonly NativeRpgToggle toggle = new();
        private readonly NativeRpgButton apply, lockButton;
        private readonly string key;
        private readonly Func<JsonObject, Task> send;
        private readonly bool fieldLayout;
        private JsonElement row;
        private bool updating, editable, canLock, locked;
        internal bool IsBoolean { get; private set; }
        internal Card(string key, Func<JsonObject, Task> send,bool fieldLayout=false)
        {
            this.key = key; this.send = send;this.fieldLayout=fieldLayout; W.Automation.AutomationProperties.SetAutomationId(View, "CommonOperation:" + key);
            title.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");source.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");
            View.ColumnDefinitions.Add(new() { Width = new(1, W.GridUnitType.Star) }); View.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto }); var caption=new WC.StackPanel{VerticalAlignment=W.VerticalAlignment.Center};caption.Children.Add(title);caption.Children.Add(source);View.Children.Add(caption);
            var actions = new WC.WrapPanel(); WC.Grid.SetColumn(actions, 1); View.Children.Add(actions);
            if(fieldLayout)
            {
                View.ColumnDefinitions.RemoveAt(1);
                View.RowDefinitions.Add(new(){Height=W.GridLength.Auto});View.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
                WC.Grid.SetColumn(actions,0);WC.Grid.SetRow(actions,1);caption.Margin=new(0,0,0,4);
            }
            apply = new("应用", "check", async (_, _) => await Apply(), !fieldLayout);
            lockButton = new("锁定", "", async (_, _) => { if (editable && canLock) await Send(locked ? "unlock" : "lock", JsonNode.Parse(row.GetProperty("value").GetRawText())!); });
            W.Automation.AutomationProperties.SetLabeledBy(toggle.View,title);
            foreach(var input in ((WC.Grid)number.View).Children.OfType<WC.TextBox>())
                W.Automation.AutomationProperties.SetLabeledBy(input,title);
            foreach (var control in new NativeRpgElement[] { toggle, lockButton, number, apply }) { control.Margin = new(0, 2, 6, 2); actions.Children.Add(control.View); }
            toggle.CheckedChanged += async (_, _) => { if (!updating && editable) await Send("set", JsonValue.Create(toggle.Checked)!); };
            number.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Apply(); } };
        }
        private Task Apply() => editable ? Send("set", JsonValue.Create(number.Value)!) : Task.CompletedTask;
        private Task Send(string operation, JsonNode value) => send(new() { ["op"] = operation, ["key"] = key, ["value"] = value });
        internal void Update(JsonElement value)
        {
            row = value.Clone(); updating = true;
            try
            {
                string full = row.GetProperty("name").GetString() ?? key;
                bool productCaption=row.TryGetProperty("caption",out var caption);
                title.Localize=productCaption||key is "gold"||key.StartsWith("option:",StringComparison.Ordinal);
                title.Text=productCaption?caption.GetString()??full:full.Split(" / ", 2)[0];title.ToolTip=full;
                source.Text=row.TryGetProperty("source",out var original)?original.GetString()??"":"";
                string raw=Field(row,"original") is {Length:>0} originalName?originalName:full;
                if(fieldLayout)
                {
                    var path=source.Text.Split(" / ",StringSplitOptions.TrimEntries);
                    string translated=System.Text.RegularExpressions.Regex.Replace(path[^1],@"\s*\[CDB [^\]]+\]\s*$","");
                    string originalField=System.Text.RegularExpressions.Regex.Replace(raw.Split(" / ",StringSplitOptions.TrimEntries)[^1],@"\s*\[CDB [^\]]+\]\s*$","");
                    if(translated.Length>0&&!string.Equals(translated,originalField,StringComparison.Ordinal)){title.Localize=false;title.Text=translated;}
                    source.Text=originalField;
                }
                if(!fieldLayout&&string.Equals(source.Text,title.Text,StringComparison.Ordinal))source.Text=string.Equals(raw,title.Text,StringComparison.Ordinal)?"":raw;
                source.ToolTip=raw;source.Visibility=source.Text.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
                IsBoolean = row.GetProperty("type").GetString() == "boolean"; canLock = row.GetProperty("canLock").GetBoolean(); locked = row.GetProperty("locked").GetBoolean();
                toggle.Visible = IsBoolean; number.Visible = apply.Visible = !IsBoolean; lockButton.Visible = canLock;
                if (IsBoolean) toggle.Checked = row.GetProperty("value").GetBoolean();
                else
                {
                    decimal minimum=row.TryGetProperty("min",out var min)?min.GetDecimal():0;
                    decimal maximum=row.TryGetProperty("max",out var max)?max.GetDecimal():int.MaxValue;
                    // Bounds setters normalize the text. A name-only refresh
                    // must not erase an in-progress "-" or decimal separator.
                    if(number.Minimum!=minimum||number.Maximum!=maximum)
                    {number.Minimum=decimal.MinValue;number.Maximum=decimal.MaxValue;number.Minimum=minimum;number.Maximum=maximum;}
                    if(!number.ContainsFocus)number.Value=Math.Clamp(row.GetProperty("value").GetDecimal(),number.Minimum,number.Maximum);
                }
                lockButton.Text = locked ? "解锁" : "锁定"; lockButton.Primary = locked;
            }
            finally { updating = false; }
        }
        internal void SetAvailability(bool enabled) { editable = enabled; number.Enabled = toggle.Enabled = apply.Enabled = enabled; lockButton.Enabled = enabled && canLock; }
        public void Dispose() { number.Dispose(); toggle.Dispose(); apply.Dispose(); lockButton.Dispose(); }
    }
}
