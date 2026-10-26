using W = System.Windows;
using WC = System.Windows.Controls;
using WI = System.Windows.Input;
using WM = System.Windows.Media;
using WA = System.Windows.Media.Animation;

namespace ScreenshotTranslationUiTester;

// One shared track and one moving selection surface. Only the render transform
// animates; changing content does not wait for, or rebuild, the visual transition.
internal sealed class WpfScreenshotModeSwitch : WC.Border
{
    private readonly WC.Grid track = new();
    private readonly WC.Border selection = new() { CornerRadius = new(6), HorizontalAlignment = W.HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly WM.TranslateTransform slide = new();
    private readonly WC.RadioButton[] options = new WC.RadioButton[4];
    private readonly WC.TextBlock[] labels = new WC.TextBlock[4];
    private int selected = -1, transition;
    private bool syncing;
    internal event Action<ScreenshotPaneView>? SelectionChanged;
    // Tests can exercise both motion preferences without changing desktop settings.
    internal Func<bool> AnimationPreference { get; set; } = static () => W.SystemParameters.ClientAreaAnimation;

    internal WpfScreenshotModeSwitch()
    {
        Height = 44; MaxWidth = 360; Padding = new(3); BorderThickness = new(1); CornerRadius = new(9);
        HorizontalAlignment = W.HorizontalAlignment.Left; VerticalAlignment = W.VerticalAlignment.Center;
        Child = track; selection.RenderTransform = slide; track.Children.Add(selection);
        var row = new WC.Primitives.UniformGrid { Rows = 1, Columns = 4 };
        track.Children.Add(row);
        var group = "ScreenshotMode-" + Guid.NewGuid().ToString("N");
        var template = (WC.ControlTemplate)W.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
              <Border x:Name="Outline" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="6" Padding="5,2">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource Fusion.Accent}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource Fusion.Text}"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        WpfFocusCues.GateFocusTriggers(template);
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            labels[i] = new() { TextTrimming = W.TextTrimming.CharacterEllipsis, TextWrapping = W.TextWrapping.NoWrap };
            var option = options[i] = new() { Content = labels[i], GroupName = group, Template = template,
                FocusVisualStyle = null, Cursor = WI.Cursors.Hand, HorizontalContentAlignment = W.HorizontalAlignment.Center };
            option.Checked += (_, _) => { if (!syncing) SelectionChanged?.Invoke((ScreenshotPaneView)index); };
            W.Automation.AutomationProperties.SetAutomationId(option, "ScreenshotMode-" + (ScreenshotPaneView)i);
            row.Children.Add(option);
        }
        WI.KeyboardNavigation.SetDirectionalNavigation(row, WI.KeyboardNavigationMode.Cycle);
        WI.KeyboardNavigation.SetTabNavigation(row, WI.KeyboardNavigationMode.Once);
        track.SizeChanged += (_, _) => Position(false);
        Loaded += (_, _) => Position(false);
        Unloaded += (_, _) => Position(false);
        W.Automation.AutomationProperties.SetAutomationId(this, "ScreenshotPaneMode");
    }

    protected override W.Size MeasureOverride(W.Size available)
    {
        double width = Math.Min(MaxWidth, available.Width);
        var measured = base.MeasureOverride(new(width, available.Height));
        return new(width, measured.Height);
    }

    internal void Refresh(ScreenshotPaneView value)
    {
        int next = Math.Clamp((int)value, 0, options.Length - 1);
        bool changed = selected != next, animate = selected >= 0 && changed;
        selected = next;
        var p = UiTheme.Current;
        Background = WpfDrawing.Brush(p.Control); BorderBrush = WpfDrawing.Brush(p.Border);
        selection.Background = WpfDrawing.Brush(p.Accent);
        string[] chinese = ["原图", "译图", "原文", "译文"];
        string[] english = ["Image", "Translated", "Source", "Text"];
        string[] russian = ["Изобр.", "Перевод", "Исходный", "Текст"];
        syncing = true;
        try
        {
            for (int i = 0; i < options.Length; i++)
            {
                options[i].IsChecked = i == selected;
                labels[i].Text = UiLanguage.Choose(chinese[i], english[i], russian[i]);
                labels[i].Foreground = i == selected ? WM.Brushes.White : WpfDrawing.Brush(p.Text);
                labels[i].FontWeight = i == selected ? W.FontWeights.SemiBold : W.FontWeights.Normal;
                options[i].ToolTip = UiLanguage.Text(chinese[i]);
                W.Automation.AutomationProperties.SetName(options[i], (string)options[i].ToolTip);
            }
        }
        finally { syncing = false; }
        W.Automation.AutomationProperties.SetName(this, UiLanguage.Choose("显示内容", "Display content", "Содержимое"));
        if (changed) Position(animate);
    }

    private void Position(bool animate)
    {
        if (selected < 0 || track.ActualWidth <= 0) return;
        double width = track.ActualWidth / options.Length, target = selected * width;
        if (Math.Abs(selection.Width - width) > .01 || double.IsNaN(selection.Width)) selection.Width = width;
        double from = slide.X;
        int current = ++transition;
        slide.BeginAnimation(WM.TranslateTransform.XProperty, null);
        if (!animate || !IsLoaded || !IsVisible || !AnimationPreference() || Math.Abs(from - target) < .1)
        {
            slide.X = target;
            return;
        }
        // WPF starts a new clock on its next tick. Keep the current position as
        // the base value until then, otherwise that first frame jumps to target.
        slide.X = from;
        var motion = new WA.DoubleAnimation(from, target, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new WA.CubicEase { EasingMode = WA.EasingMode.EaseOut } };
        motion.Completed += (_, _) =>
        {
            if (current != transition) return;
            slide.X = target;
            slide.BeginAnimation(WM.TranslateTransform.XProperty, null);
        };
        slide.BeginAnimation(WM.TranslateTransform.XProperty, motion, WA.HandoffBehavior.SnapshotAndReplace);
    }
}
