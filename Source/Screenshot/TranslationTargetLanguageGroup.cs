namespace ScreenshotTranslationUiTester;

// Keep the optional enhancement next to the target-language selection in the
// legacy settings page too. Measure at the assigned width, including translated
// labels; do not hide the explanation in a tooltip or grow the page sideways.
internal sealed class TranslationTargetLanguageGroup : Panel
{
    private readonly ComboBox target;
    private readonly CheckBox enhancement;
    private readonly Font hintFont = FontManager.Create(null, 9);
    private readonly Label hint = new()
    {
        Name = "CharacterGenderEnhancementHint",
        Text = "根据可靠角色信息调整译文。关闭后使用普通翻译，已有译文和角色设置会保留。",
        AutoSize = false,
        UseMnemonic = false,
        TabStop = false
    };
    private bool arranging;

    internal TranslationTargetLanguageGroup(ComboBox target, CheckBox enhancement)
    {
        this.target = target; this.enhancement = enhancement;
        hint.Font = hintFont; Disposed += (_, _) => hintFont.Dispose();
        Name = "TargetLanguageGroup"; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        target.TabIndex = 0; target.AccessibleName = "目标语言"; target.MinimumSize = new(160, 28); target.MaximumSize = new(340, 0);
        enhancement.TabIndex = 1; enhancement.AutoSize = false;
        Controls.AddRange([target, enhancement, hint]);
        foreach (var control in new Control[] { target, enhancement, hint })
        { control.TextChanged += (_, _) => PerformLayout(); control.FontChanged += (_, _) => PerformLayout(); }
    }

    private Size Arrange(int width, bool apply)
    {
        int S(int value) => value * DeviceDpi / 96;
        width = Math.Max(1, width);
        int targetWidth = Math.Min(width, target.MaximumSize.Width > 0 ? target.MaximumSize.Width : width);
        int y = target.Height + S(10);
        int checkHeight = Math.Max(S(30), TextRenderer.MeasureText(enhancement.Text, enhancement.Font,
            new Size(Math.Max(1, width - S(26)), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + S(8));
        if (apply) { target.SetBounds(0, 0, targetWidth, target.Height); enhancement.SetBounds(0, y, width, checkHeight); }
        y += checkHeight + S(4);
        int hintHeight = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        if (apply) hint.SetBounds(0, y, width, hintHeight);
        return new(width, y + hintHeight);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (target is null) return base.GetPreferredSize(proposedSize);
        int width = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue ? proposedSize.Width : Math.Max(160, Width);
        return Arrange(width, false);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (arranging || target is null) return;
        arranging = true;
        try { Arrange(ClientSize.Width, true); base.OnLayout(e); }
        finally { arranging = false; }
    }
}
