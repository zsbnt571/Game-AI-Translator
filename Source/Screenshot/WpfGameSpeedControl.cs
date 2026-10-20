using System.Globalization;
using W = System.Windows;
using C = System.Windows.Controls;
using I = System.Windows.Input;

namespace ScreenshotTranslationUiTester;

// All routes edit one preview value. Only a completed interaction sends a command.
internal sealed class WpfGameSpeedControl : C.StackPanel
{
    internal const double MinimumRate=.25,MaximumRate=100,StepRate=.25;
    private readonly C.Slider slider=new(){Minimum=MinimumRate,Maximum=MaximumRate,SmallChange=StepRate,LargeChange=.5,TickFrequency=StepRate,IsSnapToTickEnabled=true,MinWidth=140,VerticalAlignment=W.VerticalAlignment.Center};
    private readonly C.TextBlock caption=new NativeLocalizedTextBlock{VerticalAlignment=W.VerticalAlignment.Center,Margin=new(0,0,12,0)};
    private readonly C.TextBlock current=new(){MinWidth=52,VerticalAlignment=W.VerticalAlignment.Center,TextAlignment=W.TextAlignment.Center};
    private readonly C.TextBlock reason=new NativeLocalizedTextBlock{TextWrapping=W.TextWrapping.Wrap,Margin=new(0,5,0,0)};
    private readonly C.Button minus,plus;
    private readonly C.WrapPanel choices=new(){Margin=new(0,6,0,0)};
    private readonly List<(double Rate,C.Button View)> presets=[];
    private double confirmed=1,preview=1;
    private bool available,supported=true,syncing,pointerEditing,keyEditing,busy;
    private double minimum=MinimumRate,maximum=MaximumRate,step=StepRate;
    private bool integerOnly;
    private int generation;
    private object? context;
    internal Func<double,Task>? Commit;
    internal double CurrentRate=>confirmed;
    internal double PreviewRate=>preview;

    internal WpfGameSpeedControl()
    {
        W.Automation.AutomationProperties.SetAutomationId(this,"GameSpeed");
        caption.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Text");current.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Text");reason.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");
        var line=new C.Grid();line.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});line.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});line.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});line.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});line.ColumnDefinitions.Add(new(){Width=W.GridLength.Auto});
        minus=WpfThemeResources.Button("−",null,async()=>await CommitPreview(preview-step));plus=WpfThemeResources.Button("+",null,async()=>await CommitPreview(preview+step));minus.Width=plus.Width=36;minus.Height=plus.Height=34;slider.Margin=new(12,0,12,0);
        foreach(var (view,index) in new (W.FrameworkElement,int)[]{(caption,0),(minus,1),(slider,2),(current,3),(plus,4)}){C.Grid.SetColumn(view,index);line.Children.Add(view);}
        Children.Add(line);SetPresets([.25,.5,1,2,4,10,25,50,100]);
        Children.Add(choices);Children.Add(reason);
        W.Automation.AutomationProperties.SetAutomationId(slider,"GameSpeedSlider");W.Automation.AutomationProperties.SetAutomationId(minus,"GameSpeedDecrease");W.Automation.AutomationProperties.SetAutomationId(plus,"GameSpeedIncrease");
        W.Automation.AutomationProperties.SetName(minus,"降低游戏速度");WpfUiLanguage.Property(minus,W.Automation.AutomationProperties.NameProperty);
        W.Automation.AutomationProperties.SetName(plus,"提高游戏速度");WpfUiLanguage.Property(plus,W.Automation.AutomationProperties.NameProperty);
        slider.ValueChanged+=(_,_)=>{if(syncing)return;preview=Normalize(slider.Value);Render();};
        slider.PreviewMouseLeftButtonDown+=(_,_)=>{if(available&&!busy)pointerEditing=true;};
        slider.AddHandler(C.Primitives.Thumb.DragStartedEvent,new C.Primitives.DragStartedEventHandler((_,_)=>{if(available&&!busy)pointerEditing=true;}));
        slider.PreviewMouseLeftButtonUp+=async(_,_)=>{if(!pointerEditing)return;pointerEditing=false;await CommitPreview(preview);};
        slider.AddHandler(C.Primitives.Thumb.DragCompletedEvent,new C.Primitives.DragCompletedEventHandler(async(_,e)=>{if(!pointerEditing)return;pointerEditing=false;if(e.Canceled)Restore();else await CommitPreview(preview);}));
        slider.PreviewKeyDown+=(_,e)=>{if(available&&!busy&&Adjusts(e.Key))keyEditing=true;};
        slider.PreviewKeyUp+=async(_,e)=>{if(!keyEditing||!Adjusts(e.Key))return;keyEditing=false;await CommitPreview(preview);};
        slider.LostKeyboardFocus+=async(_,_)=>{if(!keyEditing)return;keyEditing=false;await CommitPreview(preview);};
        Unloaded+=(_,_)=>{generation++;pointerEditing=keyEditing=busy=false;Restore();};
        SetState(1,false);
    }
    private void SetPresets(IEnumerable<double> rates)
    {
        double[] values=rates.Where(rate=>double.IsFinite(rate)&&rate>=minimum&&rate<=maximum).Distinct().ToArray();if(presets.Select(item=>item.Rate).SequenceEqual(values))return;
        presets.Clear();choices.Children.Clear();foreach(double rate in values){var button=WpfThemeResources.Button(Format(rate),null,async()=>await CommitPreview(rate));button.MinWidth=54;button.Height=32;button.Margin=new(0,3,6,0);W.Automation.AutomationProperties.SetAutomationId(button,"GameSpeedPreset"+rate.ToString(CultureInfo.InvariantCulture));presets.Add((rate,button));choices.Children.Add(button);}
    }
    internal void SetState(double rate,bool enabled,string? unavailableReason=null,object? contextKey=null)
    {
        if(!Equals(context,contextKey)){context=contextKey;generation++;pointerEditing=keyEditing=busy=false;}
        confirmed=Normalize(rate);available=enabled;reason.Text=UiLanguage.Text(unavailableReason??"");reason.Visibility=reason.Text.Length>0?W.Visibility.Visible:W.Visibility.Collapsed;
        if(!enabled)pointerEditing=keyEditing=false;
        if(!pointerEditing&&!keyEditing&&!busy)preview=confirmed;
        Render();
    }
    internal void SetState(ModificationSpeedState state,object? contextKey=null)
    {
        supported=state.Supported;integerOnly=state.IntegerOnly;minimum=state.Minimum;maximum=state.Maximum;step=state.Step;
        syncing=true;try{slider.Minimum=minimum;slider.Maximum=maximum;slider.SmallChange=step;slider.LargeChange=step*2;slider.TickFrequency=step;}finally{syncing=false;}
        SetPresets(state.Presets);SetState(state.Value,state.Supported&&state.Enabled&&!state.Busy,state.Reason,contextKey??state.ContextKey);
    }
    private static bool Adjusts(I.Key key)=>key is I.Key.Left or I.Key.Right or I.Key.Up or I.Key.Down or I.Key.Home or I.Key.End or I.Key.PageUp or I.Key.PageDown;
    private double Normalize(double value)=>double.IsFinite(value)?Math.Clamp(Math.Round(value,2),minimum,maximum):Math.Clamp(1,minimum,maximum);
    private static string Format(double value)=>value.ToString("0.##",CultureInfo.InvariantCulture)+"×";
    private void Restore(){preview=confirmed;Render();}
    private void Render()
    {
        syncing=true;try{slider.Value=preview;slider.IsEnabled=available&&!busy;minus.IsEnabled=available&&!busy&&preview>minimum;plus.IsEnabled=available&&!busy&&preview<maximum;caption.Text="游戏速度";current.Text=supported?Format(preview):"—";W.Automation.AutomationProperties.SetName(slider,"游戏速度 "+current.Text);WpfUiLanguage.Property(slider,W.Automation.AutomationProperties.NameProperty);foreach(var (rate,button) in presets){button.IsEnabled=available&&!busy;WpfThemeResources.SetButton(button,Format(rate),null,Math.Abs(preview-rate)<.001);}}finally{syncing=false;}
    }
    private async Task CommitPreview(double rate)
    {
        if(!available||busy||Commit is null){Restore();return;}
        preview=Normalize(integerOnly?Math.Round(rate,MidpointRounding.AwayFromZero):rate);if(Math.Abs(preview-confirmed)<.001){Render();return;}
        int ticket=generation;busy=true;Render();
        try{await Commit(preview);}
        catch(Exception ex){if(ticket==generation){reason.Text=ex.Message;reason.Visibility=W.Visibility.Visible;}}
        finally{if(ticket==generation){busy=false;Restore();}}
    }
}
