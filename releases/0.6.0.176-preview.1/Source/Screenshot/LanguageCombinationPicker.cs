namespace ScreenshotTranslationUiTester;

internal sealed class LanguageCombinationPicker : Panel
{
    private readonly Dictionary<string, CheckBox> choices = new(StringComparer.Ordinal);
    private bool loading, arranging;
    internal event EventHandler? SelectionChanged;
    internal LanguageCombinationPicker()
    {
        Name = "SourceLanguageCombination";
        AccessibleName = "需要翻译的原文语言";
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        SuspendLayout();
        foreach (var (code, name) in TranslationLanguagePolicy.Choices)
        {
            var box = new CheckBox { Name = "SourceLanguage_" + code, Text = name, AccessibleName = name,
                AutoSize = true, Margin = new(0, 6, 18, 8), Checked = code is "en" or "ja", TabIndex = choices.Count };
            choices.Add(code, box); Controls.Add(box);
            box.CheckedChanged += (_, _) => { if (!loading) SelectionChanged?.Invoke(this, EventArgs.Empty); };
        }
        ResumeLayout(false);
    }
    private (int columns, int rowHeight) Measure(int width)
    {
        int cell = choices.Values.Max(box => box.GetPreferredSize(Size.Empty).Width + box.Margin.Horizontal);
        int height = choices.Values.Max(box => box.GetPreferredSize(Size.Empty).Height + box.Margin.Vertical);
        return (Math.Clamp(width / Math.Max(1, cell), 1, 3), height);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (choices.Count == 0) return Size.Empty;
        int width = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Math.Max(100, Width);
        var (columns, height) = Measure(width);
        // Measurement must not reposition children or change the column count.
        // Autosizing parents ask at several widths before committing a layout.
        return new(width, ((choices.Count + columns - 1) / columns) * height);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (arranging || choices.Count == 0) return;
        arranging = true;
        try
        {
            var (columns, height) = Measure(ClientSize.Width);
            int index = 0;
            foreach (var box in choices.Values)
            {
                var size = box.GetPreferredSize(Size.Empty);
                box.SetBounds((index % columns) * ClientSize.Width / columns,
                    (index / columns) * height + box.Margin.Top, size.Width, size.Height);
                index++;
            }
            base.OnLayout(e);
        }
        finally { arranging = false; }
    }
    internal string[] SelectedLanguages => choices.Where(p => p.Value.Checked).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
    internal void SelectLanguages(IEnumerable<string>? languages)
    {
        var selected = TranslationLanguagePolicy.Normalize(languages).ToHashSet(StringComparer.Ordinal);
        loading = true;
        try { foreach (var (code, box) in choices) box.Checked = selected.Contains(code); }
        finally { loading = false; }
    }
    internal static Control WithMode(ComboBox mode, LanguageCombinationPicker picker) => new SourceLanguageEditor(mode, picker);
}

// A single source selector owns the optional combination. Keep measurement pure
// and publish visibility/height together after the native dropdown has closed.
internal sealed class SourceLanguageEditor : Panel
{
    private readonly ComboBox mode;
    private readonly LanguageCombinationPicker picker;
    private readonly Label hint;
    private bool expanded, arranging, refreshQueued;
    internal SourceLanguageEditor(ComboBox mode, LanguageCombinationPicker picker)
    {
        this.mode = mode; this.picker = picker;
        Name = "SourceLanguageEditor"; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MaximumSize = new(620, 0); Margin = Padding.Empty;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        mode.Name = "SourceLanguageMode"; mode.AccessibleName = "原文语言";
        mode.Dock = DockStyle.None; mode.MinimumSize = new(100, 0);
        hint = new Label { Name = "SourceLanguageHelp", AutoSize = false, UseMnemonic = false,
            Text = "选择“自选组合”可勾选多种原文语言，至少选择一种。备用语言包不一起翻译；语言选项名称和已是目标语言的文字默认保留。" };
        SuspendLayout(); Controls.AddRange([mode, picker, hint]);
        expanded = mode.SelectedIndex == (int)SourceLanguageMode.Mixed; picker.Visible = expanded;
        mode.SelectedIndexChanged += (_, _) => { if (!mode.DroppedDown && !refreshQueued) RefreshMode(); };
        mode.DropDownClosed += (_, _) =>
        {
            if (refreshQueued || !IsHandleCreated) return;
            refreshQueued = true;
            BeginInvoke((Action)(() => { refreshQueued = false; if (!IsDisposed) RefreshMode(); }));
        };
        ResumeLayout(false);
    }
    private void RefreshMode()
    {
        bool next = mode.SelectedIndex == (int)SourceLanguageMode.Mixed;
        if (next == expanded) return;
        Control root = Parent ?? this;
        for (Control? p = Parent; p is not null; p = p.Parent)
            if (p is ScrollableControl { AutoScroll: true }) { root = p; break; }
        using var batch = new UiLayoutBatch(root);
        expanded = next; picker.Visible = next;
        PerformLayout(); Parent?.PerformLayout();
    }
    private Size Arrange(int width, bool apply)
    {
        int S(int n) => n * DeviceDpi / 96;
        int modeWidth = Math.Min(width, mode.MaximumSize.Width > 0 ? mode.MaximumSize.Width : width);
        int y = mode.Height + S(8);
        if (apply) mode.SetBounds(0, 0, modeWidth, mode.Height);
        if (expanded)
        {
            var size = picker.GetPreferredSize(new(width, 0));
            if (apply) picker.SetBounds(0, y, width, size.Height);
            y += size.Height + S(8);
        }
        int hintHeight = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(Math.Max(1, width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        if (apply) hint.SetBounds(0, y, width, hintHeight);
        return new(width, y + hintHeight);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (hint is null) return base.GetPreferredSize(proposedSize);
        int width = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Math.Max(100, Width);
        return Arrange(width, false);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (arranging || hint is null) return;
        arranging = true;
        try { Arrange(ClientSize.Width, true); base.OnLayout(e); }
        finally { arranging = false; }
    }
}
