using W=System.Windows;
using C=System.Windows.Controls;
using I=System.Windows.Input;
namespace ScreenshotTranslationUiTester;

internal sealed partial class WpfAiToolsWorkspace
{
    private readonly WpfAiOverlay144 taskMenu146=new(),taskEdit146=new();
    private readonly C.StackPanel taskMenuBody146=new(),taskEditBody146=new();
    private readonly C.TextBlock taskMenuTitle146=Label(15,true),taskEditTitle146=Label(16,true),taskEditHelp146=Label(),taskEditError146=Label();
    private readonly C.TextBox taskName146=new(){MinHeight=36,MaxLength=200};
    private C.Button? taskPin146,taskDelete146,taskConfirm146;
    private W.FrameworkElement? taskAnchor146;
    private string menuTask146="",editKind146=""; private bool taskActionNavigation146;
    private C.Button TaskButton146(string key,Action action,string id)
    {
        var b=WpfThemeResources.Button(AiWorkspaceText146.Text(key),null,action);b.MinHeight=36;b.Margin=new(0,3,0,3);b.HorizontalAlignment=W.HorizontalAlignment.Stretch;b.HorizontalContentAlignment=W.HorizontalAlignment.Left;Id(b,id);return b;
    }
    private void InitializeWorkspace146()
    {
        var row=new W.FrameworkElementFactory(typeof(C.DockPanel));
        var more=new W.FrameworkElementFactory(typeof(C.Button));more.SetValue(C.DockPanel.DockProperty,C.Dock.Right);more.SetValue(C.ContentControl.ContentProperty,"…");more.SetValue(W.FrameworkElement.WidthProperty,28d);more.SetValue(W.FrameworkElement.HeightProperty,28d);more.SetValue(C.Control.PaddingProperty,new W.Thickness(0));more.SetValue(C.Control.BorderThicknessProperty,new W.Thickness(0));more.SetValue(C.Control.BackgroundProperty,System.Windows.Media.Brushes.Transparent);more.SetResourceReference(C.Control.ForegroundProperty,"Fusion.Text");more.SetBinding(W.FrameworkElement.TagProperty,new System.Windows.Data.Binding("Task.Id"));more.SetBinding(W.FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("OptionsLabel"));more.SetBinding(W.Automation.AutomationProperties.NameProperty,new System.Windows.Data.Binding("OptionsLabel"));more.SetBinding(W.Automation.AutomationProperties.AutomationIdProperty,new System.Windows.Data.Binding("OptionsId"));more.AddHandler(I.Mouse.PreviewMouseDownEvent,new I.MouseButtonEventHandler((sender,e)=>{if(e.ChangedButton!=I.MouseButton.Left)return;e.Handled=true;if(sender is C.Button button&&button.Tag is string id)OpenTaskMenu146(id,button);}));more.AddHandler(C.Button.ClickEvent,new W.RoutedEventHandler((sender,e)=>{e.Handled=true;if(sender is C.Button button&&button.Tag is string id)OpenTaskMenu146(id,button);}));row.AppendChild(more);
        var text=new W.FrameworkElementFactory(typeof(C.StackPanel));var title=new W.FrameworkElementFactory(typeof(C.TextBlock));title.SetBinding(C.TextBlock.TextProperty,new System.Windows.Data.Binding("Title"));title.SetValue(C.TextBlock.TextTrimmingProperty,W.TextTrimming.CharacterEllipsis);title.SetBinding(W.FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("Hint"));text.AppendChild(title);var hint=new W.FrameworkElementFactory(typeof(C.TextBlock));hint.SetBinding(C.TextBlock.TextProperty,new System.Windows.Data.Binding("GameName"));hint.SetValue(C.TextBlock.FontSizeProperty,12d);hint.SetValue(W.FrameworkElement.MarginProperty,new W.Thickness(0,3,0,0));hint.SetResourceReference(C.TextBlock.ForegroundProperty,"Fusion.Muted");hint.SetValue(C.TextBlock.TextTrimmingProperty,W.TextTrimming.CharacterEllipsis);text.AppendChild(hint);row.AppendChild(text);tasks.ItemTemplate=new W.DataTemplate{VisualTree=row};
        taskMenuBody146.Children.Add(taskMenuTitle146);taskMenuBody146.Children.Add(TaskButton146("RenameTask",()=>OpenTaskEditor146("rename"),"AiTaskRename"));taskPin146=TaskButton146("PinTask",ToggleTaskPin146,"AiTaskPin");taskMenuBody146.Children.Add(taskPin146);taskDelete146=TaskButton146("DeleteTask",()=>OpenTaskEditor146("delete"),"AiTaskDelete");taskMenuBody146.Children.Add(taskDelete146);SetupPopup(taskMenu146,tasks,taskMenuBody146,"AiTaskMenu");var menuSurface=(C.Border)taskMenu146.Child!;menuSurface.Width=260;menuSurface.Tag=260d;menuSurface.Padding=new(8);taskMenuTitle146.Margin=new(8,4,8,8);taskMenuTitle146.TextWrapping=W.TextWrapping.NoWrap;taskMenuTitle146.TextTrimming=W.TextTrimming.CharacterEllipsis;foreach(var action in taskMenuBody146.Children.OfType<C.Button>()){action.SetResourceReference(W.FrameworkElement.StyleProperty,"Fusion.NavButton");action.Background=System.Windows.Media.Brushes.Transparent;action.BorderBrush=System.Windows.Media.Brushes.Transparent;action.BorderThickness=new(0);action.Margin=new(0);action.Height=36;action.Padding=new(8,0,8,0);}
        taskEditBody146.Children.Add(taskEditTitle146);taskEditHelp146.Margin=new(0,10,0,10);taskEditBody146.Children.Add(taskEditHelp146);Id(taskName146,"AiTaskRenameInput");taskName146.Margin=new(0,8,0,12);taskEditBody146.Children.Add(taskName146);taskEditError146.Margin=new(0,6,0,8);Id(taskEditError146,"AiTaskActionError");taskEditBody146.Children.Add(taskEditError146);var actions=new C.WrapPanel();taskConfirm146=TaskButton146("Save",ConfirmTaskAction146,"AiTaskConfirm");var cancel=TaskButton146("Cancel",CloseTaskActions146,"AiTaskCancel");taskConfirm146.Margin=new(0,0,8,0);actions.Children.Add(taskConfirm146);actions.Children.Add(cancel);taskEditBody146.Children.Add(actions);SetupPopup(taskEdit146,tasks,taskEditBody146,"AiTaskEditor");
        tasks.PreviewMouseRightButtonDown+=(_,e)=>{var item=C.ItemsControl.ContainerFromElement(tasks,e.OriginalSource as W.DependencyObject) as C.ListBoxItem;if(item?.DataContext is TaskRow task){e.Handled=true;OpenTaskMenu146(task.Task.Id,item);}};
        tasks.PreviewKeyDown+=(_,e)=>{var item=C.ItemsControl.ContainerFromElement(tasks,I.Keyboard.FocusedElement as W.DependencyObject) as C.ListBoxItem;var task=item?.DataContext as TaskRow??tasks.SelectedItem as TaskRow;if(task is null)return;if(e.Key==I.Key.F2){e.Handled=true;menuTask146=task.Task.Id;taskAnchor146=(W.FrameworkElement?)item??tasks;OpenTaskEditor146("rename");}else if(e.Key==I.Key.Apps||e.Key==I.Key.F10&&I.Keyboard.Modifiers.HasFlag(I.ModifierKeys.Shift)){e.Handled=true;OpenTaskMenu146(task.Task.Id,(W.FrameworkElement?)item??tasks);}};
        taskName146.PreviewKeyDown+=(_,e)=>{if(e.Key==I.Key.Enter){e.Handled=true;ConfirmTaskAction146();}};
        taskMenu146.Opened+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{if(taskMenu146.IsOpen)taskMenuBody146.Children.OfType<C.Button>().First().Focus();}));
        taskEdit146.Opened+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{if(!taskEdit146.IsOpen)return;if(editKind146=="rename"){taskName146.Focus();taskName146.SelectAll();}else taskConfirm146.Focus();}));
        taskMenu146.Closed+=(_,_)=>RestoreTaskFocus146();taskEdit146.Closed+=(_,_)=>RestoreTaskFocus146();
    }
    private void RestoreTaskFocus146(){taskActionNavigation146=true;try{if(taskAnchor146 is {IsVisible:true,IsEnabled:true} anchor)anchor.Focus();else if(tasks.IsVisible)tasks.Focus();}finally{taskActionNavigation146=false;}}
    private void OpenTaskMenu146(string id,W.FrameworkElement anchor)
    {
        if(!controller.Tasks.Any(t=>t.Id==id))return;ClosePopups(false);menuTask146=id;taskAnchor146=anchor;taskMenu146.PlacementTarget=anchor;RefreshTaskActions146();taskMenu146.IsOpen=true;
    }
    private void OpenTaskEditor146(string kind)
    {
        var task=controller.Tasks.FirstOrDefault(t=>t.Id==menuTask146);if(task is null||kind=="delete"&&controller.IsTaskRunning(task.Id))return;taskMenu146.IsOpen=false;editKind146=kind;taskEdit146.PlacementTarget=taskAnchor146??tasks;taskName146.Text=task.Title;taskEditError146.Text="";RefreshTaskActions146();taskEdit146.IsOpen=true;
    }
    private void ToggleTaskPin146(){var task=controller.Tasks.FirstOrDefault(t=>t.Id==menuTask146);if(task is null)return;Run(()=>controller.SetTaskPinned(task.Id,!task.IsPinned));CloseTaskActions146();}
    private void ConfirmTaskAction146()
    {
        try{if(editKind146=="rename")controller.RenameTask(menuTask146,taskName146.Text);else if(editKind146=="delete")controller.DeleteTask(menuTask146);CloseTaskActions146();Refresh();}
        catch(Exception ex){taskEditError146.Text=AiWorkspaceText146.Text(ErrorCode(ex));RefreshTaskActions146();}
    }
    private void CloseTaskActions146(){taskMenu146.IsOpen=false;taskEdit146.IsOpen=false;RestoreTaskFocus146();}
    private void RefreshTaskActions146()
    {
        var task=controller.Tasks.FirstOrDefault(t=>t.Id==menuTask146);if(task is null){taskMenu146.IsOpen=false;taskEdit146.IsOpen=false;return;}
        taskMenuTitle146.Text=task.Title;taskMenuTitle146.ToolTip=task.Title;((C.Button)taskMenuBody146.Children[1]).Content=AiWorkspaceText146.Text("RenameTask");taskPin146!.Content=AiWorkspaceText146.Text(task.IsPinned?"UnpinTask":"PinTask");taskDelete146!.Content=AiWorkspaceText146.Text("DeleteTask");taskDelete146.IsEnabled=!controller.IsTaskRunning(task.Id);taskDelete146.ToolTip=taskDelete146.IsEnabled?null:AiWorkspaceText146.Text("TaskRunningDelete");bool rename=editKind146=="rename";taskEditTitle146.Text=AiWorkspaceText146.Text(rename?"RenameTask":"DeleteTask");taskEditHelp146.Text=AiWorkspaceText146.Text(rename?"TaskName":"DeleteTaskHelp");taskName146.Visibility=rename?W.Visibility.Visible:W.Visibility.Collapsed;taskConfirm146!.Content=AiWorkspaceText146.Text(rename?"Save":"DeleteTask");taskConfirm146.IsEnabled=rename||!controller.IsTaskRunning(task.Id);((C.Button)((C.WrapPanel)taskEditBody146.Children[taskEditBody146.Children.Count-1]).Children[1]).Content=AiWorkspaceText146.Text("Cancel");
    }
}
