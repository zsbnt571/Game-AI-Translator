using W = System.Windows;
using I = System.Windows.Input;
using M = System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Keep focus and keyboard navigation intact. Only the extra focus decoration
// follows the input device, including focus restored after an async refresh.
internal static class WpfFocusCues
{
    internal static readonly W.DependencyProperty KeyboardInputProperty=W.DependencyProperty.RegisterAttached(
        "KeyboardInput",typeof(bool),typeof(WpfFocusCues),new W.FrameworkPropertyMetadata(false,W.FrameworkPropertyMetadataOptions.Inherits));
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<I.InputManager,object> Installed=new();
    internal static void Initialize()
    {
        var manager=I.InputManager.Current;if(Installed.TryGetValue(manager,out _))return;
        Installed.Add(manager,new());manager.PreProcessInput+=OnInput;
    }
    private static void OnInput(object sender,I.PreProcessInputEventArgs e)
    {
        var input=e.StagingItem.Input;bool keyboard;
        if(input is I.KeyEventArgs key&&key.RoutedEvent==I.Keyboard.PreviewKeyDownEvent)
        {
            if(key.Key is I.Key.LeftShift or I.Key.RightShift or I.Key.LeftCtrl or I.Key.RightCtrl or I.Key.LeftAlt or I.Key.RightAlt or I.Key.LWin or I.Key.RWin)return;
            keyboard=true;
        }
        else if(input is I.MouseButtonEventArgs mouse&&mouse.RoutedEvent==I.Mouse.PreviewMouseDownEvent||
                input is I.TouchEventArgs touch&&touch.RoutedEvent==W.UIElement.PreviewTouchDownEvent||
                input is I.StylusDownEventArgs stylus&&stylus.RoutedEvent==I.Stylus.PreviewStylusDownEvent)keyboard=false;
        else return;
        var source=(input.OriginalSource??input.Source??(keyboard?I.Keyboard.FocusedElement:I.Mouse.DirectlyOver)) as W.DependencyObject;
        while(source is W.ContentElement content)source=W.ContentOperations.GetParent(content)??(content as W.FrameworkContentElement)?.Parent;
        if(source is M.Visual visual&&W.PresentationSource.FromVisual(visual)?.RootVisual is {} root)
            root.SetCurrentValue(KeyboardInputProperty,keyboard);
    }
    internal static void Gate(W.Style style)
    {
        style.Setters.Add(new W.Setter(W.UIElement.VisibilityProperty,W.Visibility.Collapsed));
        var keyboard=new W.Trigger{Property=KeyboardInputProperty,Value=true};
        keyboard.Setters.Add(new W.Setter(W.UIElement.VisibilityProperty,W.Visibility.Visible));style.Triggers.Add(keyboard);
    }
    internal static void GateFocusTriggers(System.Windows.Controls.ControlTemplate template)
    {
        for(int i=0;i<template.Triggers.Count;i++)
        {
            if(template.Triggers[i] is not W.Trigger {Property:var property} focus||property!=W.UIElement.IsKeyboardFocusedProperty)continue;
            var keyboard=new W.MultiTrigger();keyboard.Conditions.Add(new(property,focus.Value));keyboard.Conditions.Add(new(KeyboardInputProperty,true));
            foreach(W.SetterBase setter in focus.Setters)keyboard.Setters.Add(setter);
            template.Triggers.RemoveAt(i);template.Triggers.Insert(i,keyboard);
        }
    }
}
