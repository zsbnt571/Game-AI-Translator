using W = System.Windows;
using WC = System.Windows.Controls;
using WA = System.Windows.Automation;

namespace ScreenshotTranslationUiTester;

internal sealed partial class NativeTranslationWorkspace
{
    private readonly NativeLocalizedTextBlock heading=new(){Localize=false,TextWrapping=W.TextWrapping.Wrap};
    private void RefreshHeading()
    {
        Title=UiLanguage.Text("译文管理")+" · "+game.Name;
        heading.Text=game.Name+" · "+UiLanguage.Text(UiTargetLanguageDisplay.DisplayFromCode(settings.TargetLanguage))+" · "+
            UiLanguage.Text("当前游戏译文");
    }
    private void BuildWorkspace()
    {
        foreach (var input in new[] { search, original, translated, termSource, termText })
        {
            input.Margin = new(0);
            input.MinHeight = input.AcceptsReturn ? 104 : 38;
            input.VerticalContentAlignment = input.AcceptsReturn ? W.VerticalAlignment.Top : W.VerticalAlignment.Center;
        }
        original.Height = translated.Height = 108;
        original.SetResourceReference(WC.Control.BackgroundProperty, "Fusion.Control");
        filter.Margin = new(0); filter.MinHeight = 38; filter.MinWidth = 164;
        search.MinWidth = 140;
        Identify(search, "TranslationSearch", "搜索");
        Identify(filter, "TranslationFilter", "全部译文");
        Identify(original, "TranslationOriginal", "完整原文 · 只读");
        Identify(translated, "TranslationEditor", "更正译文 · 保留变量和控制符");
        Identify(termSource, "TermSource", "原词"); Identify(termText, "TermText", "译名");
        Identify(previous, "TranslationPrevious", "上一页"); Identify(next, "TranslationNext", "下一页");
        foreach (var records in new[] { grid, terms, history })
        {
            records.View.SetResourceReference(WC.DataGrid.HorizontalGridLinesBrushProperty, "Fusion.Border");
            records.View.SetResourceReference(WC.DataGrid.VerticalGridLinesBrushProperty, "Fusion.Border");
            records.View.MinHeight = 104;
        }
        var fields = new WC.Grid();
        fields.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        fields.ColumnDefinitions.Add(new());
        fields.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        var searchLabel = Caption("搜索"); searchLabel.VerticalAlignment = W.VerticalAlignment.Center;
        searchLabel.Margin = new(0, 0, 10, 0);
        WA.AutomationProperties.SetLabeledBy(search, searchLabel);
        fields.Children.Add(searchLabel); WC.Grid.SetColumn(search, 1); fields.Children.Add(search);
        filter.Margin = new(12, 0, 0, 0); WC.Grid.SetColumn(filter, 2); fields.Children.Add(filter);
        var commands = ControlsRow(Action("刷新", RefreshAsync, "refresh"), Action("导入", ImportAsync, "folder"), Action("导出", ExportAsync, "export"));
        var toolbar = new TranslationCommandBar(fields, commands,
            () => searchLabel.DesiredSize.Width + search.MinWidth + filter.DesiredSize.Width) { Margin = new(0, 0, 0, 12) };
        Identify(toolbar, "TranslationToolbar", "");

        pageLabel.Margin = new(0); pageLabel.MinWidth = 150; pageLabel.TextAlignment = W.TextAlignment.Center;
        pageLabel.VerticalAlignment = W.VerticalAlignment.Center;
        pageLabel.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        var pagination = ControlsRow(previous, pageLabel, next);
        pagination.HorizontalAlignment = W.HorizontalAlignment.Right; pagination.Margin = new(0, 8, 0, 12);
        Identify(pagination, "TranslationPagination", "");

        var editor = Pair(Field("完整原文 · 只读", original), Field("更正译文 · 保留变量和控制符", translated));
        editor.Margin = new(0, 0, 0, 8);
        entryLabel.Margin = new(0, 0, 0, 10); entryLabel.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        var editorActions = ActionRows(save, restore, retry, Action("重译本页问题", RetryPageAsync), Action("撤销上次译文修改", UndoAsync));
        Identify(editorActions, "TranslationEditorActions", "");
        var texts = Table((toolbar, false), (grid.View, true), (pagination, false), (editor, false), (entryLabel, false), (editorActions, false));

        var termFields = Pair(Field("原词", termSource), Field("译名", termText));
        termFields.Margin = new(0, 0, 0, 10);
        var termActions = ActionRows(Action("保存统一译名", SaveTermAsync, "check", true), Action("移除统一规则", DeleteTermAsync, "trash"), Action("重译相关原句", RetryTermAsync, "refresh"), Action("重试待准备名称", RetryNamesAsync, "refresh"), Action("刷新", RefreshAsync, "refresh"));
        Identify(termActions, "TerminologyActions", "");
        keepTerm.Margin=new(0,0,0,12);Identify(keepTerm,"TermKeepOriginal","保留原文");
        termSearch.MinHeight=38;termSearch.Margin=new(0,0,0,10);Identify(termSearch,"TermSearch","搜索词语与名称");
        termKind.MinHeight=38;termKind.MinWidth=150;termKind.Margin=new(0,0,0,10);Identify(termKind,"TermCategory","名称分类");
        termCount.Margin=new(0,0,0,8);termCount.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");
        termEvidence.Height=112;termEvidence.Margin=new(0,10,0,0);Identify(termEvidence,"TermEvidence","出现原句与译名冲突");
        var termForm = new WC.Border { Padding = new(14), Margin = new(0, 0, 0, 12), CornerRadius = new(6), BorderThickness = new(1), Child = Table((termFields, false), (keepTerm,false), (termActions, false)) };
        termForm.SetResourceReference(WC.Border.BorderBrushProperty, "Fusion.Border");
        var termLayout = Table((Note("人物、地点、技能和物品等默认翻译；只有你选择后才保留原文。名称保持一致，句子按语境自然表达。任务和事件标题单独归类，不强制套进普通对白。人工更正保留。"), false), (Pair(Field("名称分类",termKind),Field("搜索词语与名称",termSearch)),false), (termCount,false), (termForm, false), (terms.View, true),(termEvidence,false));

        tabs.BorderThickness = new(0); tabs.Padding = new(0);
        AddTab("译文与问题", texts);
        AddTab("词语与名称", termLayout);
        AddTab("译文修改记录", Table((Note("保留最近 100 次译文修改。撤销针对译文更正和导入，不包含术语；自动翻译不覆盖人工记录。"), false), (history.View, true)));
        feedback.Margin = new(0, 12, 0, 0); feedback.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        RefreshHeading();heading.SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Text");
        heading.FontWeight = W.FontWeights.SemiBold; heading.Margin = new(0, 0, 0, 14);
        Content = NativeDialogControls.Layout(Table((heading, false), (tabs, true)), feedback, false);
    }

    private void AddTab(string title, WC.Grid body)
    {
        // TabItem's selected-header foreground must not color the page itself.
        body.SetResourceReference(W.Documents.TextElement.ForegroundProperty, "Fusion.Text");
        var tab=new WC.TabItem { Header = title, Content = new TranslationPage(body) { Margin = new(0, 14, 0, 0) } };
        WpfUiLanguage.Property(tab,WC.HeaderedContentControl.HeaderProperty);tabs.Items.Add(tab);
    }

    private static void Identify(W.FrameworkElement view, string id, string name)
    {
        WA.AutomationProperties.SetAutomationId(view, id);
        if (name.Length > 0){WA.AutomationProperties.SetName(view,name);WpfUiLanguage.Property(view,WA.AutomationProperties.NameProperty);}
    }

    private static WC.TextBlock Caption(string text)
    {
        var label = new NativeLocalizedTextBlock { Text = text, TextWrapping = W.TextWrapping.Wrap };
        label.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Text");
        return label;
    }

    private static WC.TextBlock Note(string text)
    {
        var label = Caption(text); label.Margin = new(0, 0, 0, 12);
        label.SetResourceReference(WC.TextBlock.ForegroundProperty, "Fusion.Muted");
        return label;
    }

    private static WC.Grid Field(string text, WC.Control control)
    {
        var label = Caption(text); label.Margin = new(0, 0, 0, 8);
        WA.AutomationProperties.SetLabeledBy(control, label);
        return Table((label, false), (control, true));
    }

    private static WC.Grid Pair(W.FrameworkElement left, W.FrameworkElement right)
    {
        var pair = new WC.Grid(); pair.ColumnDefinitions.Add(new()); pair.ColumnDefinitions.Add(new());
        WC.Grid.SetIsSharedSizeScope(pair, true);
        foreach (var field in new[] { left, right }.OfType<WC.Grid>()) field.RowDefinitions[0].SharedSizeGroup = "FieldCaption";
        left.Margin = new(0, 0, 8, 0); right.Margin = new(8, 0, 0, 0);
        pair.Children.Add(left); WC.Grid.SetColumn(right, 1); pair.Children.Add(right); return pair;
    }

    private static WC.Grid ControlsRow(params W.FrameworkElement[] controls)
    {
        var row = new WC.Grid();
        for (int i = 0; i < controls.Length; i++)
        {
            row.ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
            controls[i].Margin = new(i == 0 ? 0 : 8, 0, 0, 0);
            WC.Grid.SetColumn(controls[i], i); row.Children.Add(controls[i]);
        }
        return row;
    }

    private static WC.WrapPanel ActionRows(params WC.Button[] buttons)
    {
        var panel = new WC.WrapPanel { Margin = new(0, 0, -8, -8) };
        foreach (var button in buttons) { button.Margin = new(0, 0, 8, 8); panel.Children.Add(button); }
        return panel;
    }
}

// Keep the table virtualized and filling spare height, while making the whole
// editor reachable when translated labels or larger fonts need more room.
internal sealed class TranslationPage : WC.ScrollViewer
{
    private readonly WC.Grid body;
    internal TranslationPage(WC.Grid body)
    {
        this.body = body; Content = body;
        VerticalScrollBarVisibility = WC.ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = WC.ScrollBarVisibility.Disabled;
        HorizontalContentAlignment = W.HorizontalAlignment.Stretch;
        FocusVisualStyle = null; BorderThickness = new(0);
    }

    protected override W.Size MeasureOverride(W.Size constraint)
    {
        if (double.IsFinite(constraint.Height) && double.IsFinite(constraint.Width))
        {
            // Reserve scrollbar width during minimum-height measurement so a
            // newly visible scrollbar cannot cause an extra unmeasured wrap.
            double width = Math.Max(0, constraint.Width - W.SystemParameters.VerticalScrollBarWidth);
            var heights = new double[body.RowDefinitions.Count];
            foreach (W.FrameworkElement child in body.Children)
            {
                int row = WC.Grid.GetRow(child);
                if (body.RowDefinitions[row].Height.IsStar)
                    heights[row] = Math.Max(heights[row], child.MinHeight + child.Margin.Top + child.Margin.Bottom);
                else
                {
                    child.Measure(new(width, double.PositiveInfinity));
                    heights[row] = Math.Max(heights[row], child.DesiredSize.Height);
                }
            }
            body.Height = Math.Max(constraint.Height, heights.Sum());
        }
        return base.MeasureOverride(constraint);
    }
}

// Reflow whole search and command groups. Labels stay attached to their inputs.
internal sealed class TranslationCommandBar : WC.Grid
{
    private readonly W.FrameworkElement fields, commands;
    private readonly Func<double> minimumFieldsWidth;
    private bool stacked;
    internal TranslationCommandBar(W.FrameworkElement fields, W.FrameworkElement commands, Func<double> minimumFieldsWidth)
    {
        this.fields = fields; this.commands = commands;
        this.minimumFieldsWidth = minimumFieldsWidth;
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = W.GridLength.Auto });
        RowDefinitions.Add(new() { Height = W.GridLength.Auto }); RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        Children.Add(fields); Children.Add(commands); WC.Grid.SetColumn(commands, 1); commands.Margin = new(12, 0, 0, 0);
    }

    protected override W.Size MeasureOverride(W.Size constraint)
    {
        commands.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        fields.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        // A query can scroll inside its input; its text length must not move
        // unrelated command buttons to another row.
        bool next = constraint.Width < Math.Max(420, minimumFieldsWidth()) + commands.DesiredSize.Width;
        if (stacked != next)
        {
            stacked = next;
            WC.Grid.SetColumnSpan(fields, stacked ? 2 : 1);
            WC.Grid.SetColumn(commands, stacked ? 0 : 1); WC.Grid.SetRow(commands, stacked ? 1 : 0);
            WC.Grid.SetColumnSpan(commands, stacked ? 2 : 1);
            commands.Margin = stacked ? new(0, 10, 0, 0) : new(12, 0, 0, 0);
            commands.HorizontalAlignment = stacked ? W.HorizontalAlignment.Left : W.HorizontalAlignment.Stretch;
        }
        return base.MeasureOverride(constraint);
    }
}
