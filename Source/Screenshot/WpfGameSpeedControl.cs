using System.Globalization;
using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// All routes edit one preview value. Only a completed interaction sends a command.
internal sealed class WpfGameSpeedControl : C.StackPanel
{
    internal const double MinimumRate=.25,MaximumRate=100,StepRate=.25;
    private readonly C.Slider slider=new(){Minimum=MinimumRate,Maximum=MaximumRate,SmallChange=StepRate,LargeChange=.5,TickFrequency=StepRate,IsSnapToTickEnabled=true,Width=240,Height=34,VerticalAlignment=W.VerticalAlignment.Center};
    private readonly C.TextBlock caption=new NativeLocalizedTextBlock{Margin=new(0,0,0,6),FontWeight=W.FontWeights.SemiBold};
    private readonly C.TextBox input=new(){Width=76,Height=34,VerticalAlignment=W.VerticalAlignment.Center,VerticalContentAlignment=W.VerticalAlignment.Center,TextAlignment=W.TextAlignment.Center,MaxLength=24};
    private readonly C.TextBlock unit=new(){Text="×",VerticalAlignment=W.VerticalAlignment.Center,Margin=new(4,0,6,0)};
    private readonly C.TextBlock inputError=new NativeLocalizedTextBlock{TextWrapping=W.TextWrapping.Wrap,Margin=new(0,5,0,0),Visibility=W.Visibility.Collapsed};
    private readonly C.TextBlock reason=new NativeLocalizedTextBlock{TextWrapping=W.TextWrapping.Wrap,Margin=new(0,5,0,0),MinHeight=36};
    private readonly C.TextBlock feedback=new NativeLocalizedTextBlock{TextWrapping=W.TextWrapping.Wrap,Margin=new(0,4,0,0),MinHeight=20};
    private readonly C.Button details;
    private readonly C.StackPanel detailsPanel=new(){Margin=new(0,6,0,0),Visibility=W.Visibility.Collapsed};
    private C.TextBox? detailsText;
    private C.TextBlock? copyFeedback;
    private string backendLabel="",diagnostics="",statusFeedback="";
    private bool rateConfirmed,backendBusy;
    private readonly C.Button minus,plus,apply,reset;
    private readonly C.WrapPanel choices=new(){Margin=new(0,2,0,0)};
    private readonly List<(double Rate,C.Button View)> presets=[];
    private double confirmed=1,preview=1;
    private bool available,supported=true,syncing,pointerEditing,keyEditing,textEditing,busy;
    private double minimum=MinimumRate,maximum=MaximumRate,step=StepRate;
    private bool integerOnly;
    private int generation;
    private object? context;
    internal Func<double,Task>? Commit;
    internal Action<string> CopyDiagnostics=W.Clipboard.SetText;
    internal double CurrentRate=>confirmed;
    internal double PreviewRate=>preview;

    internal WpfGameSpeedControl()
    {
        MaxWidth=680;HorizontalAlignment=W.HorizontalAlignment.Left;
        W.Automation.AutomationProperties.SetAutomationId(this,"GameSpeed");
        caption.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Text");unit.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Text");reason.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");feedback.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");inputError.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Text");
        // Keep the number and its +/- controls together; the remaining groups
        // wrap independently instead of stretching the track across the page.
        var line=new C.WrapPanel();var valueGroup=new C.StackPanel{Orientation=C.Orientation.Horizontal,Margin=new(0,0,8,6)};
        minus=WpfThemeResources.Button("−",null,async()=>await AdjustInput(-step));plus=WpfThemeResources.Button("+",null,async()=>await AdjustInput(step));minus.Width=plus.Width=36;minus.Height=plus.Height=34;minus.Margin=new(0,0,6,0);
        apply=WpfThemeResources.Button("应用",null,async()=>await CommitInput(),true);
        reset=WpfThemeResources.Button("恢复 1×",null,async()=>{if(minimum<=1&&maximum>=1)await CommitPreview(1);});
        foreach(var button in new[]{apply,reset}){button.Height=34;button.Margin=new(0,0,8,6);button.VerticalAlignment=W.VerticalAlignment.Center;}
        foreach(var view in new W.UIElement[]{minus,input,unit,plus})valueGroup.Children.Add(view);
        slider.Margin=new(4,0,0,6);slider.Template=CreateSliderTemplate();slider.SetResourceReference(C.Control.FocusVisualStyleProperty,W.SystemParameters.FocusVisualStyleKey);
        foreach(var view in new W.UIElement[]{valueGroup,apply,reset,slider})line.Children.Add(view);
        Children.Add(caption);Children.Add(line);SetPresets([.25,.5,1,2,4,10,25,50,100]);
        details=WpfThemeResources.Button("速度详情",null,ToggleDetails);details.HorizontalAlignment=W.HorizontalAlignment.Left;details.Margin=new(0,3,0,0);details.Visibility=W.Visibility.Collapsed;
        W.Automation.AutomationProperties.SetAutomationId(details,"GameSpeedDetails");W.Automation.AutomationProperties.SetAutomationId(feedback,"GameSpeedFeedback");
        Children.Add(inputError);Children.Add(choices);Children.Add(feedback);Children.Add(reason);Children.Add(details);Children.Add(detailsPanel);
        W.Automation.AutomationProperties.SetAutomationId(slider,"GameSpeedSlider");W.Automation.AutomationProperties.SetAutomationId(minus,"GameSpeedDecrease");W.Automation.AutomationProperties.SetAutomationId(plus,"GameSpeedIncrease");
        W.Automation.AutomationProperties.SetName(minus,"降低游戏速度");WpfUiLanguage.Property(minus,W.Automation.AutomationProperties.NameProperty);
        W.Automation.AutomationProperties.SetName(plus,"提高游戏速度");WpfUiLanguage.Property(plus,W.Automation.AutomationProperties.NameProperty);
        W.Automation.AutomationProperties.SetAutomationId(input,"GameSpeedInput");W.Automation.AutomationProperties.SetAutomationId(apply,"GameSpeedApply");
        W.Automation.AutomationProperties.SetAutomationId(reset,"GameSpeedReset");W.Automation.AutomationProperties.SetName(reset,"恢复正常游戏速度");WpfUiLanguage.Property(reset,W.Automation.AutomationProperties.NameProperty);
        W.Automation.AutomationProperties.SetName(input,"游戏速度倍率");WpfUiLanguage.Property(input,W.Automation.AutomationProperties.NameProperty);
        input.ToolTip="输入倍率后按 Enter 或点击应用；按 Esc 恢复当前倍率。";WpfUiLanguage.Property(input,W.FrameworkElement.ToolTipProperty);
        W.Automation.AutomationProperties.SetName(apply,"应用游戏速度");WpfUiLanguage.Property(apply,W.Automation.AutomationProperties.NameProperty);
        input.TextChanged+=(_,_)=>{if(syncing)return;textEditing=true;ClearInputError();UpdateStepButtons();};
        input.PreviewKeyDown+=async(_,e)=>
        {
            if(e.Key==I.Key.Enter){e.Handled=true;await CommitInput();}
            else if(e.Key==I.Key.Escape){e.Handled=true;textEditing=false;ClearInputError();Restore();input.SelectAll();}
        };
        input.LostKeyboardFocus+=(_,_)=>{if(textEditing&&available&&!busy)ValidateInput(out _);};
        slider.ValueChanged+=(_,_)=>{if(syncing)return;textEditing=false;ClearInputError();preview=Normalize(slider.Value);Render();};
        slider.PreviewMouseLeftButtonDown+=(_,_)=>{if(available&&!busy)pointerEditing=true;};
        slider.AddHandler(C.Primitives.Thumb.DragStartedEvent,new C.Primitives.DragStartedEventHandler((_,_)=>{if(available&&!busy)pointerEditing=true;}));
        slider.PreviewMouseLeftButtonUp+=async(_,_)=>{if(!pointerEditing)return;pointerEditing=false;await CommitPreview(preview);};
        slider.AddHandler(C.Primitives.Thumb.DragCompletedEvent,new C.Primitives.DragCompletedEventHandler(async(_,e)=>{if(!pointerEditing)return;pointerEditing=false;if(e.Canceled)Restore();else await CommitPreview(preview);}));
        slider.PreviewKeyDown+=(_,e)=>{if(available&&!busy&&Adjusts(e.Key))keyEditing=true;};
        slider.PreviewKeyUp+=async(_,e)=>{if(!keyEditing||!Adjusts(e.Key))return;keyEditing=false;await CommitPreview(preview);};
        slider.LostKeyboardFocus+=async(_,_)=>{if(!keyEditing)return;keyEditing=false;await CommitPreview(preview);};
        Unloaded+=(_,_)=>{generation++;pointerEditing=keyEditing=textEditing=busy=false;ClearInputError();Restore();};
        SetState(1,false);
    }
    private void ToggleDetails()
    {
        bool show=detailsPanel.Visibility!=W.Visibility.Visible;
        if(show&&detailsText is null)
        {
            detailsText=new C.TextBox{IsReadOnly=true,TextWrapping=W.TextWrapping.Wrap,AcceptsReturn=true,MinHeight=90,MaxHeight=240,
                VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled,Padding=new(8)};
            W.Automation.AutomationProperties.SetAutomationId(detailsText,"GameSpeedDiagnosticText");
            W.Automation.AutomationProperties.SetName(detailsText,"速度诊断详情");WpfUiLanguage.Property(detailsText,W.Automation.AutomationProperties.NameProperty);
            var copy=WpfThemeResources.Button("复制详情",null,()=>
            {
                try{CopyDiagnostics(detailsText.Text);copyFeedback!.Text="已复制。";}
                catch(Exception){copyFeedback!.Text="复制失败，请选中文字后手动复制。";}
            });
            copy.HorizontalAlignment=W.HorizontalAlignment.Left;copy.Margin=new(0,6,0,0);W.Automation.AutomationProperties.SetAutomationId(copy,"GameSpeedCopyDiagnostics");
            copyFeedback=new NativeLocalizedTextBlock{TextWrapping=W.TextWrapping.Wrap,Margin=new(0,4,0,0)};copyFeedback.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");
            var snapshotHint=new NativeLocalizedTextBlock{Text="显示最近一次变速诊断；重新展开可读取最新记录。",TextWrapping=W.TextWrapping.Wrap,Margin=new(0,4,0,0)};
            snapshotHint.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");
            detailsPanel.Children.Add(detailsText);detailsPanel.Children.Add(snapshotHint);detailsPanel.Children.Add(copy);detailsPanel.Children.Add(copyFeedback);
        }
        detailsPanel.Visibility=show?W.Visibility.Visible:W.Visibility.Collapsed;
        WpfThemeResources.SetButton(details,show?"收起速度详情":"速度详情",null);
        if(show)RefreshDetails();
    }
    private void RefreshDetails()
    {
        if(detailsText is null||detailsPanel.Visibility!=W.Visibility.Visible)return;
        // Opening the disclosure captures a report. Heartbeats never move its
        // selection/scroll position; reopening obtains the latest evidence.
        detailsText.Text=diagnostics;WpfUiLanguage.Property(detailsText,C.TextBox.TextProperty);
        if(copyFeedback is not null)copyFeedback.Text="";
    }
    private static C.ControlTemplate CreateSliderTemplate()=>(C.ControlTemplate)W.Markup.XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Slider">
          <Grid Background="Transparent">
            <Track x:Name="PART_Track" Height="22" VerticalAlignment="Center" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{TemplateBinding Value}" IsDirectionReversed="{TemplateBinding IsDirectionReversed}">
              <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False" Background="{DynamicResource Fusion.Accent}"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" CornerRadius="2" Background="{TemplateBinding Background}" VerticalAlignment="Center"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False" Background="{DynamicResource Fusion.Border}"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Height="4" CornerRadius="2" Background="{TemplateBinding Background}" VerticalAlignment="Center"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
              <Track.Thumb><Thumb Width="22" Height="22" Focusable="False" Cursor="Hand"><Thumb.Template><ControlTemplate TargetType="Thumb"><Grid Background="Transparent"><Ellipse Width="16" Height="16" Fill="{DynamicResource Fusion.Main}" Stroke="{DynamicResource Fusion.Accent}" StrokeThickness="2"/></Grid></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
            </Track>
          </Grid>
          <ControlTemplate.Triggers><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5"/></Trigger></ControlTemplate.Triggers>
        </ControlTemplate>
        """);
    private void SetPresets(IEnumerable<double> rates)
    {
        double[] values=rates.Where(rate=>double.IsFinite(rate)&&rate>=minimum&&rate<=maximum).Distinct().ToArray();if(presets.Select(item=>item.Rate).SequenceEqual(values))return;
        presets.Clear();choices.Children.Clear();foreach(double rate in values){var button=WpfThemeResources.Button(Format(rate),null,async()=>await CommitPreview(rate));button.MinWidth=54;button.Height=34;button.Margin=new(0,0,6,6);W.Automation.AutomationProperties.SetAutomationId(button,"GameSpeedPreset"+rate.ToString(CultureInfo.InvariantCulture));presets.Add((rate,button));choices.Children.Add(button);}
    }
    internal void SetState(double rate,bool enabled,string? unavailableReason=null,object? contextKey=null)
    {
        if(!Equals(context,contextKey)){context=contextKey;generation++;pointerEditing=keyEditing=textEditing=busy=false;ClearInputError();detailsPanel.Visibility=W.Visibility.Collapsed;WpfThemeResources.SetButton(details,"速度详情",null);if(detailsText is not null)detailsText.Clear();if(copyFeedback is not null)copyFeedback.Text="";}
        // Keep the raw diagnostic for live language changes. NativeLocalizedTextBlock
        // translates it, including nested product-owned diagnostic envelopes.
        confirmed=Normalize(rate);available=enabled;
        string message=unavailableReason??"";if(reason.Text!=UiLanguage.Text(message))reason.Text=message;
        reason.Visibility=message.Length>0?W.Visibility.Visible:W.Visibility.Hidden;
        // Backend polling can temporarily disable actions during an unrelated
        // request. That is not revocation and must not discard typed text.
        if(!enabled&&!backendBusy){pointerEditing=keyEditing=textEditing=false;ClearInputError();}
        if(!pointerEditing&&!keyEditing&&!textEditing&&!busy)preview=confirmed;
        Render();
    }
    internal void SetState(ModificationSpeedState state,object? contextKey=null)
    {
        supported=state.Supported;integerOnly=state.IntegerOnly;minimum=state.Minimum;maximum=state.Maximum;step=state.Step;
        backendLabel=state.BackendLabel;rateConfirmed=state.RateConfirmed;backendBusy=state.Busy;diagnostics=state.Diagnostics;statusFeedback=state.Feedback;
        details.Visibility=diagnostics.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        if(diagnostics.Length==0)detailsPanel.Visibility=W.Visibility.Collapsed;
        syncing=true;try{slider.Minimum=minimum;slider.Maximum=maximum;slider.SmallChange=step;slider.LargeChange=step*2;slider.TickFrequency=step;}finally{syncing=false;}
        SetPresets(state.Presets);SetState(state.Value,state.Supported&&state.Enabled&&!state.Busy,state.Reason,contextKey??state.ContextKey);
    }
    private static bool Adjusts(I.Key key)=>key is I.Key.Left or I.Key.Right or I.Key.Up or I.Key.Down or I.Key.Home or I.Key.End or I.Key.PageUp or I.Key.PageDown;
    private double Normalize(double value)=>double.IsFinite(value)?Math.Clamp(Math.Round(value,2),minimum,maximum):Math.Clamp(1,minimum,maximum);
    private static string Format(double value)=>value.ToString("0.##",CultureInfo.InvariantCulture)+"×";
    private static CultureInfo InputCulture=>UiLanguage.NumericInputCulture;
    private void ClearInputError(){inputError.Text="";inputError.Visibility=W.Visibility.Collapsed;}
    private bool TryReadInput(out double rate)
    {
        string text=input.Text.Trim();if(text.EndsWith('×'))text=text[..^1].TrimEnd();
        const NumberStyles style=NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint;
        return (double.TryParse(text,style,InputCulture,out rate)||double.TryParse(text,style,CultureInfo.InvariantCulture,out rate))&&double.IsFinite(rate);
    }
    private bool ValidateInput(out double rate)
    {
        string error="";
        if(!TryReadInput(out rate))
            error="请输入有效的速度倍率。";
        else if(rate<minimum||rate>maximum)
            error=UiLanguage.Format($"请输入 {minimum:0.##} 到 {maximum:0.##} 之间的倍率。");
        else if(integerOnly&&rate!=Math.Truncate(rate))
            error="此引擎仅支持整数倍率。";
        inputError.Text=error;inputError.Visibility=error.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        return error.Length==0;
    }
    private async Task AdjustInput(double delta)
    {
        double rate=preview;
        if(textEditing&&!ValidateInput(out rate))return;
        await CommitPreview(rate+delta);
    }
    private void UpdateStepButtons()
    {
        double rate=preview;
        if(textEditing&&TryReadInput(out double draft))rate=draft;
        minus.IsEnabled=available&&!busy&&rate>minimum;plus.IsEnabled=available&&!busy&&rate<maximum;
    }
    private async Task CommitInput()
    {
        if(!available||busy||Commit is null)return;
        if(!ValidateInput(out double rate))return;
        await CommitPreview(rate);
    }
    private void Restore(){preview=confirmed;Render();}
    private void Render()
    {
        string confirmation=busy?"正在请求 "+Format(preview)+"…":backendBusy?"正在确认速度设置…":statusFeedback.Length>0?statusFeedback:rateConfirmed?"上次确认 "+Format(confirmed)+"；实际推进效果请以游戏为准。":
            available?"尚未设置倍率。":"";
        feedback.Text=backendLabel.Length==0?confirmation:backendLabel+(confirmation.Length>0?" · "+confirmation:"");
        feedback.Visibility=feedback.Text.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        syncing=true;try{slider.Value=preview;slider.IsEnabled=input.IsEnabled=apply.IsEnabled=available&&!busy;reset.IsEnabled=available&&!busy&&minimum<=1&&maximum>=1;UpdateStepButtons();caption.Text="游戏速度";if(!textEditing)input.Text=supported?preview.ToString("0.##",InputCulture):"—";W.Automation.AutomationProperties.SetName(slider,UiLanguage.Format($"游戏速度 {(supported?Format(preview):"—")}"));WpfUiLanguage.Property(slider,W.Automation.AutomationProperties.NameProperty);foreach(var (rate,button) in presets){button.IsEnabled=available&&!busy;WpfThemeResources.SetButton(button,Format(rate),null,Math.Abs(preview-rate)<.001);}}finally{syncing=false;}
    }
    private async Task CommitPreview(double rate)
    {
        if(busy)return;
        if(!available||Commit is null){Restore();return;}
        textEditing=false;ClearInputError();
        preview=Normalize(integerOnly?Math.Round(rate,MidpointRounding.AwayFromZero):rate);if(rateConfirmed&&statusFeedback.Length==0&&Math.Abs(preview-confirmed)<.001){Render();return;}
        int ticket=generation;busy=true;Render();
        try{await Commit(preview);}
        catch(Exception ex){if(ticket==generation){reason.Text=ex.Message;reason.Visibility=W.Visibility.Visible;}}
        finally{if(ticket==generation){busy=false;Restore();}}
    }
}
