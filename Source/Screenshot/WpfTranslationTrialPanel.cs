using W=System.Windows;
using C=System.Windows.Controls;

namespace ScreenshotTranslationUiTester;

internal sealed class WpfTranslationTrialPanel:C.StackPanel,IDisposable
{
    private readonly Func<TranslationTrialConfiguration> getConfiguration;
    private TranslationTrialConfiguration configuration=new(new(),new());
    private readonly C.TextBox input=NativeDialogControls.Input("",true),terms=NativeDialogControls.Input("",true);
    private readonly C.ComboBox compare=NativeDialogControls.Choice(["API 方案","提示词预设","词库"]);
    private readonly C.ComboBox profileA=new(),profileB=new();
    private readonly C.CheckBox temporary=new(){Content="本次使用临时术语"};
    private readonly C.TextBlock status=NativeDialogControls.Text(""),hint=NativeDialogControls.Text(""),summary=NativeDialogControls.Text("");
    private readonly C.Button start,stop;
    private readonly C.Grid sides=new();
    private readonly Side a=new("A"),b=new("B");
    private CancellationTokenSource? operation;
    private bool refreshing,disposed;
    private string loadedIdentity="";
    private int generation;
    private sealed record Profile(string Id,string Label){public override string ToString()=>Id.Length==0?UiLanguage.Text(Label):Label;}
    private sealed record PresetChoice(TranslationPreset? Value,string Label){public override string ToString()=>Value is null?UiLanguage.Text(Label):TranslationLibrary.DisplayName(Value);}
    private sealed class Side
    {
        internal readonly C.StackPanel Body=new(),Options=new(),Packs=new();
        internal readonly C.ComboBox Preset=new();
        internal readonly C.TextBox Output=NativeDialogControls.Input(UiLanguage.Text("尚未测试"),true,true);
        internal string? OutputStatus="尚未测试";
        internal TranslationTrialResult? Result;
        internal readonly C.TextBlock Metrics=NativeDialogControls.Text(""),Hits=NativeDialogControls.Text(""),Heading;
        internal readonly C.Button Retry;
        internal readonly Dictionary<string,C.CheckBox> PackChecks=new();
        internal TranslationTrialRequest? Request;
        internal void SetStatus(string value){Result=null;OutputStatus=value;Output.Text=UiLanguage.Text(value);}
        internal Side(string name)
        {
            Heading=NativeDialogControls.Text("方案 "+name,18);Heading.FontWeight=W.FontWeights.SemiBold;
            Preset.MinHeight=34;Preset.Margin=new(0,6,0,8);
            Options.Children.Add(NativeDialogControls.Text("本次提示词"));Options.Children.Add(Preset);
            Options.Children.Add(new C.Expander{Header="本次词库",Content=Packs,Margin=new(0,0,0,10)});
            Output.MinHeight=145;Output.MaxHeight=350;Output.VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto;
            Retry=NativeDialogControls.Button("重试这一侧",()=>{});Retry.Visibility=W.Visibility.Collapsed;
            foreach(var view in new W.UIElement[]{Heading,Options,Output,Metrics,Hits,Retry})Body.Children.Add(view);
        }
    }
    internal WpfTranslationTrialPanel(Func<TranslationTrialConfiguration> getConfiguration)
    {
        this.getConfiguration=getConfiguration;
        Identify(this,"TranslationTrial");
        Children.Add(NativeDialogControls.Text("粘贴几句对白，使用已保存的 API 方案试译。试用不会修改正式翻译设置。"));
        input.MaxLength=4000;input.MinHeight=130;input.MaxHeight=250;input.Margin=new(0,12,0,12);Identify(input,"TrialInput");Children.Add(input);
        var toolbar=new C.WrapPanel();Children.Add(toolbar);
        AddField(toolbar,"对比内容",compare,170);AddField(toolbar,"方案 A",profileA,240);AddField(toolbar,"方案 B（可选）",profileB,240);
        Identify(compare,"TrialCompare");Identify(profileA,"TrialProfileA");Identify(profileB,"TrialProfileB");
        hint.Margin=new(0,8,0,8);Children.Add(hint);Children.Add(summary);
        terms.MinHeight=90;terms.MaxHeight=180;terms.MaxLength=12000;Identify(terms,"TrialTerms");Identify(temporary,"TrialUseTerms");
        Children.Add(new C.Expander{Header="临时术语",Content=NativeDialogControls.Column(temporary,NativeDialogControls.Text("每行填写：原文 = 译文。只用于本次试用；正式术语在游戏的“译文与术语”管理。"),terms),Margin=new(0,8,0,12)});
        start=NativeDialogControls.Button("开始试用",()=>_=Run(null),true);stop=NativeDialogControls.Button("停止",Cancel);stop.IsEnabled=false;
        Identify(start,"TrialStart");Identify(stop,"TrialStop");
        Children.Add(NativeDialogControls.Row(start,stop));status.Margin=new(0,8,0,12);Children.Add(status);
        sides.ColumnDefinitions.Add(new());sides.ColumnDefinitions.Add(new(){Width=new(16)});sides.ColumnDefinitions.Add(new());
        sides.RowDefinitions.Add(new(){Height=W.GridLength.Auto});sides.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        AddSide(a,0);AddSide(b,2);Children.Add(sides);sides.SizeChanged+=(_,_)=>ArrangeSides();
        input.TextChanged+=(_,_)=>Invalidate();terms.TextChanged+=(_,_)=>Invalidate();temporary.Click+=(_,_)=>Invalidate();
        compare.SelectionChanged+=(_,_)=>{if(!refreshing){if(compare.SelectedIndex>0)profileB.SelectedIndex=0;RefreshMode();Invalidate();}};
        profileA.SelectionChanged+=(_,_)=>Invalidate();profileB.SelectionChanged+=(_,_)=>{RefreshMode();Invalidate();};
        foreach(var side in new[]{a,b}){side.Preset.SelectionChanged+=(_,_)=>Invalidate();side.Retry.Click+=(_,_)=>_=Run(side);}
        UiLanguage.LanguageChanged+=LanguageChanged;
    }
    private void LanguageChanged(object? sender,EventArgs e)
    {
        if(disposed)return;
        refreshing=true;
        try
        {
            RefreshChoice(profileB);
            foreach(var side in new[]{a,b})
            {
                RefreshChoice(side.Preset);
                foreach(var p in TranslationLibrary.Packs())if(side.PackChecks.TryGetValue(p.Id,out var box))box.Content=UiLanguage.Format($"{TranslationLibrary.DisplayName(p)}（{p.Terms.Count} 条）");
                RefreshResult(side);
            }
            RefreshSummary();RefreshMode();WpfUiLanguage.Dialog(this);
        }
        finally{refreshing=false;}
    }
    // WPF caches SelectionBoxItem text produced by ToString. Refreshing the
    // item view alone leaves the previous language visible in a closed combo.
    private static void RefreshChoice(C.ComboBox box)
    {
        var selected=box.SelectedItem;var items=box.ItemsSource;
        box.ItemsSource=null;box.ItemsSource=items;box.SelectedItem=selected;
    }
    private void RefreshSummary()
    {
        var preset=TranslationLibrary.Presets().FirstOrDefault(p=>p.Id==TranslationLibrary.PresetId(configuration.Content));
        summary.Text=UiLanguage.Format($"已保存：{(preset is null?UiLanguage.Text("自定义提示词"):TranslationLibrary.DisplayName(preset))} · 目标 {configuration.Content.TargetLanguage}");
    }
    private static void RefreshResult(Side side)
    {
        if(side.OutputStatus is {} state)side.Output.Text=UiLanguage.Text(state);
        if(side.Result is {} result)
        {
            side.Output.Text=result.Success?result.Text:UiOperationText.ErrorText(result.Error);
            side.Metrics.Text=UiLanguage.Format($"{UiLanguage.Text(result.Success?"完成":result.Cancelled?"已停止":"未完成")} · 总耗时 {result.Seconds:F2} 秒")+(result.Success?UiLanguage.Format($" · {result.Requests} 次请求"):"")+"\n"+UiLanguage.Text("当前接口一次返回完整译文，不单独统计开始出字。");
        }
        if(side.Request is {} request)
            side.Hits.Text=request.Hits.Length==0?UiLanguage.Text("本次未命中词条。"):UiLanguage.Text("本次命中：")+"\n"+string.Join("\n",request.Hits.Select(h=>$"{h.Source} → {h.Target} · {HitName(h)}"));
    }
    private static string HitName(TranslationTrialHit hit)
    {
        if(hit.PackId is "__temporary" or "__unknown")return UiLanguage.Text(hit.Pack);
        var pack=TranslationLibrary.Packs().FirstOrDefault(p=>p.Id==hit.PackId);
        return pack is null?hit.Pack:TranslationLibrary.DisplayName(pack);
    }
    private static void AddField(C.Panel parent,string title,C.ComboBox box,double width)
    {box.Width=width;box.MinHeight=34;var p=NativeDialogControls.Column(NativeDialogControls.Text(title),box);p.Margin=new(0,0,12,8);parent.Children.Add(p);}
    private void AddSide(Side side,int col)
    {
        Identify(side.Preset,"TrialPreset"+(col==0?"A":"B"));Identify(side.Output,"TrialOutput"+(col==0?"A":"B"));
        var card=new C.Border{Child=side.Body,Padding=new(16),CornerRadius=new(8),BorderThickness=new(1)};
        card.SetResourceReference(C.Border.BorderBrushProperty,"Fusion.Border");card.SetResourceReference(C.Border.BackgroundProperty,"Fusion.Secondary");C.Grid.SetColumn(card,col);sides.Children.Add(card);
    }
    internal void Reload()
    {
        if(disposed||operation is not null)return;
        try
        {
            configuration=getConfiguration();
            // Include connections in the private in-memory identity, never in UI/logs.
            string identity=System.Text.Json.JsonSerializer.Serialize(configuration);
            if(identity==loadedIdentity)return;loadedIdentity=identity;
            refreshing=true;
            var previousA=(profileA.SelectedItem as Profile)?.Id;var previousB=(profileB.SelectedItem as Profile)?.Id;
            var choices=configuration.Profiles.Select(p=>new Profile(p.Key,configuration.Names?.GetValueOrDefault(p.Key)??p.Key)).ToArray();
            profileA.ItemsSource=choices;profileA.SelectedItem=choices.FirstOrDefault(p=>p.Id==previousA)??choices.FirstOrDefault(p=>p.Id==configuration.PreferredProfile)??choices.FirstOrDefault();
            profileB.ItemsSource=new[]{new Profile("","只试一个方案")}.Concat(choices).ToArray();profileB.SelectedIndex=0;
            if(previousB is not null)profileB.SelectedItem=profileB.Items.Cast<Profile>().FirstOrDefault(p=>p.Id==previousB)??profileB.Items[0];
            var presets=new[]{new PresetChoice(null,"使用已保存的提示词")}.Concat(TranslationLibrary.Presets().Select(p=>new PresetChoice(p,p.Name))).ToArray();
            foreach(var side in new[]{a,b})
            {
                side.Preset.ItemsSource=presets;side.Preset.SelectedIndex=0;side.Packs.Children.Clear();side.PackChecks.Clear();
                foreach(var p in TranslationLibrary.Packs())
                {
                    var box=new C.CheckBox{Content=UiLanguage.Format($"{TranslationLibrary.DisplayName(p)}（{p.Terms.Count} 条）"),IsChecked=configuration.Content.GlossaryPackIds.Contains(p.Id),Margin=new(0,3,0,3)};
                    box.Click+=(_,_)=>Invalidate();side.Packs.Children.Add(box);side.PackChecks.Add(p.Id,box);
                }
            }
            RefreshSummary();WpfUiLanguage.Dialog(this);
            refreshing=false;RefreshMode();Invalidate();
        }
        catch(Exception ex){refreshing=false;status.Text=UiOperationText.Error(ex);}
    }
    private bool HasB=>compare.SelectedIndex>0||(profileB.SelectedItem is Profile p&&p.Id.Length>0);
    private void RefreshMode()
    {
        profileB.IsEnabled=compare.SelectedIndex==0&&operation is null;
        if(profileB.Parent is W.FrameworkElement profileBField)profileBField.Visibility=compare.SelectedIndex==0?W.Visibility.Visible:W.Visibility.Collapsed;
        a.Heading.Text=compare.SelectedIndex==1?"提示词 A":compare.SelectedIndex==2?"词库 A":"方案 A";
        b.Heading.Text=compare.SelectedIndex==1?"提示词 B":compare.SelectedIndex==2?"词库 B":"方案 B";
        b.Body.Visibility=HasB?W.Visibility.Visible:W.Visibility.Collapsed;
        a.Options.Visibility=compare.SelectedIndex==0?W.Visibility.Collapsed:W.Visibility.Visible;
        b.Options.Visibility=a.Options.Visibility;
        a.Preset.IsEnabled=b.Preset.IsEnabled=compare.SelectedIndex==1;
        a.Packs.IsEnabled=b.Packs.IsEnabled=compare.SelectedIndex==2;
        hint.Text=compare.SelectedIndex switch{1=>"两侧使用方案 A 和相同词库，只比较提示词。",2=>"两侧使用方案 A 和已保存的提示词；分别勾选词库，取消全部可比较未启用的效果。",_=>"两侧沿用相同的已保存提示词和词库，只比较 API 方案。"};
        ArrangeSides();
    }
    private void ArrangeSides()
    {
        if(sides.Children.Count!=2)return;bool stacked=sides.ActualWidth<850||!HasB;
        C.Grid.SetColumnSpan(sides.Children[0],stacked?3:1);C.Grid.SetColumn(sides.Children[1],stacked?0:2);C.Grid.SetRow(sides.Children[1],stacked?1:0);C.Grid.SetColumnSpan(sides.Children[1],stacked?3:1);
        ((W.FrameworkElement)sides.Children[1]).Visibility=HasB?W.Visibility.Visible:W.Visibility.Collapsed;
        ((W.FrameworkElement)sides.Children[1]).Margin=stacked?new(0,16,0,0):new(0);
    }
    private void Invalidate()
    {
        if(refreshing||operation is not null)return;generation++;
        foreach(var side in new[]{a,b}){side.Request=null;side.SetStatus("尚未测试");side.Metrics.Text="";side.Hits.Text="";side.Retry.Visibility=W.Visibility.Collapsed;}
        status.Text="点击开始后会发送试用文本，并产生对应 API 用量。";
    }
    private TranslationTrialRequest Prepare(Side side)
    {
        var profile=(side==b&&compare.SelectedIndex==0?profileB:profileA).SelectedItem as Profile;
        var preset=compare.SelectedIndex==1?(side.Preset.SelectedItem as PresetChoice)?.Value:null;
        var packs=compare.SelectedIndex==2?side.PackChecks.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray():configuration.Content.GlossaryPackIds;
        return TranslationTrial.Prepare(configuration,profile?.Id??"",input.Text,preset,packs,temporary.IsChecked==true?TranslationTrial.ParseTerms(terms.Text):new Dictionary<string,string>());
    }
    private async Task Run(Side? retry)
    {
        if(disposed||operation is not null)return;
        var selected=retry is not null?new[]{retry}:HasB?new[]{a,b}:new[]{a};
        try{foreach(var side in selected)side.Request=Prepare(side);}
        catch(Exception ex){status.Text=UiOperationText.Error(ex);return;}
        using var run=new CancellationTokenSource();operation=run;int revision=generation;SetBusy(true);
        try
        {
            await Task.WhenAll(selected.Select(async side=>{
                var request=side.Request!;side.Retry.Visibility=W.Visibility.Collapsed;side.SetStatus("正在请求…");side.Metrics.Text="";RefreshResult(side);
                var result=await TranslationTrial.RunAsync(request,run.Token);
                if(disposed||revision!=generation)return;
                side.OutputStatus=null;side.Result=result;RefreshResult(side);
                side.Retry.Visibility=result.Success?W.Visibility.Collapsed:W.Visibility.Visible;
            }));
            if(!disposed)status.Text="本次试用结束；结果未写入游戏缓存或正式术语。";
        }
        finally{operation=null;if(!disposed)SetBusy(false);}
    }
    private void SetBusy(bool busy)
    {
        start.IsEnabled=!busy;stop.IsEnabled=busy;input.IsReadOnly=busy;terms.IsReadOnly=busy;temporary.IsEnabled=!busy;compare.IsEnabled=!busy;profileA.IsEnabled=!busy;
        foreach(var side in new[]{a,b}){side.Options.IsEnabled=!busy;side.Retry.IsEnabled=!busy;}
        RefreshMode();
    }
    internal void Cancel()=>operation?.Cancel();
    public void Dispose(){disposed=true;generation++;UiLanguage.LanguageChanged-=LanguageChanged;Cancel();}
    private static void Identify(W.DependencyObject view,string id)=>W.Automation.AutomationProperties.SetAutomationId(view,id);
}
