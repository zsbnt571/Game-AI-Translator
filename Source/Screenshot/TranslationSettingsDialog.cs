namespace ScreenshotTranslationUiTester;

public sealed record ProviderConnectionTestResult(bool Success, string Category, string Message, int? StatusCode = null);

public sealed class TranslationSettingsDialog : Form
{
    private readonly ComboBox _provider = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _displayName = new();
    private readonly TextBox _baseUrl = new();
    private readonly TextBox _apiKey = new() { UseSystemPasswordChar = true };
    private readonly TextBox _model = new();
    private readonly ComboBox _target = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _style = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _custom = new() { Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox _ids = new() { Text = "保留 ID / 代码", AutoSize = true };
    private readonly CheckBox _numbers = new() { Text = "保留数字", AutoSize = true };
    private readonly CheckBox _variables = new() { Text = "保留变量 / 占位符", AutoSize = true };
    private readonly Label _testStatus = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly Func<ApiSettings, CancellationToken, Task<ProviderConnectionTestResult>> _tester;
    private readonly ApiSettings _working;

    public ApiSettings ResultSettings => CaptureSettings();

    public TranslationSettingsDialog(ApiSettings settings,
        Func<ApiSettings, CancellationToken, Task<ProviderConnectionTestResult>>? tester = null)
    {
        SuspendLayout();AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new SizeF(96,96);
        _working = ApiSettingsSnapshot.Copy(settings);
        _tester = tester ?? TestWithServiceAsync;
        Text = "翻译设置";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(680, 520);
        BuildUi();
        LoadValues();
        FontManager.ApplyUi(this,_working);
        UiDarkTheme.Apply(this);
        DialogLayout.Attach(this);ResumeLayout(true);
    }

    private void BuildUi()
    {
        var tabs = new DarkTabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildProviderTab());
        tabs.TabPages.Add(BuildPromptTab());
        var buttons = new DialogActionBar(rightToLeft:true);
        var ok = new Button { Text = UiStrings.Ok, AutoSize=true, MinimumSize=new Size(100,36), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = UiStrings.Cancel, AutoSize=true, MinimumSize=new Size(100,36), DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([ok, cancel]);
        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(tabs);Controls.Add(buttons);
    }

    private TabPage BuildGeneralTab()
    {
        var tab = new TabPage("常规");
        var table = MakeTable();
        AddRow(table, "目标语言", _target);
        AddRow(table, "源语言", _source);
        AddRow(table, "翻译风格", _style);
        tab.Controls.Add(table); return tab;
    }

    private TabPage BuildProviderTab()
    {
        var tab = new TabPage("Provider");
        var table = MakeTable();
        AddRow(table, "Provider 类型", _provider);
        AddRow(table, "显示名称", _displayName);
        AddRow(table, "Base URL / API 地址", _baseUrl);
        AddRow(table, "API Key", _apiKey);
        AddRow(table, "模型", _model);
        var test = new Button { Text = "测试 Provider", AutoSize = true };
        test.Click += async (_, _) => await RunProviderTestAsync(test);
        AddRow(table, "连接测试", new FlowLayoutPanel { AutoSize = true, Controls = { test, _testStatus } });
        tab.Controls.Add(table); return tab;
    }

    private TabPage BuildPromptTab()
    {
        var tab = new TabPage("Prompt");
        var table = MakeTable();
        _custom.MinimumSize = new Size(430, 160);
        AddRow(table, "自定义提示词", _custom);
        AddRow(table, "保护规则", new FlowLayoutPanel { AutoSize = true, Controls = { _ids, _numbers, _variables } });
        var note = new Label { AutoSize = true, MaximumSize = new Size(610, 0),
            Text = "自定义提示词仅补充翻译风格；语义保真始终优先，系统禁止审查、弱化、解释、总结和增删内容，并要求保留稳定 ID 映射。" };
        AddRow(table, "安全规则", note);
        tab.Controls.Add(table); return tab;
    }

    private static TableLayoutPanel MakeTable()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(16) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Top|AnchorStyles.Right,
            Margin = new Padding(4, 10, 4, 6), TextAlign = ContentAlignment.MiddleRight }, 0, row);
        control.Dock = DockStyle.Top; control.Margin = new Padding(8, 6, 16, 6);
        if (control is TextBox or ComboBox) control.MinimumSize = new Size(410, 31);
        table.Controls.Add(control, 1, row);
    }

    private void LoadValues()
    {
        _provider.Items.AddRange(TranslationProviderRegistry.All.Select(x => x.DisplayName).ToArray());
        _provider.SelectedIndex = 0;
        _displayName.Text = _working.ProviderDisplayName;
        _baseUrl.Text = _working.ApiUrl;
        _apiKey.Text = _working.ApiKey;
        _model.Text = _working.Model;
        _target.Items.AddRange(UiTargetLanguageDisplay.DisplayNames.ToArray());
        _target.SelectedItem = UiTargetLanguageDisplay.DisplayFromCode(_working.TargetLanguage);
        _source.Items.AddRange(Enum.GetValues<SourceLanguageMode>().Select(UiStrings.SourceLanguageName).ToArray());
        _source.SelectedIndex = (int)_working.SourceLanguage;
        _style.Items.AddRange(Enum.GetValues<TranslationStyle>().Select(UiStrings.TranslationStyleName).ToArray());
        _style.SelectedIndex=(int)_working.TranslationStyle;
        _custom.Text = _working.CustomTranslationPrompt;
        _ids.Checked = _working.PreserveIdentifiers;
        _numbers.Checked = _working.PreserveNumbers;
        _variables.Checked = _working.PreserveVariables;
    }

    private ApiSettings CaptureSettings()
    {
        var result = ApiSettingsSnapshot.Copy(_working);
        result.TranslationProviderKind = TranslationProviderKind.OpenAiCompatible;
        result.TranslationProvider = "openai-compatible";
        result.ProviderDisplayName = _displayName.Text.Trim();
        result.ApiUrl = _baseUrl.Text.Trim(); result.ApiKey = _apiKey.Text; result.Model = _model.Text.Trim();
        result.TargetLanguage = UiTargetLanguageDisplay.CodeFromDisplay(_target.SelectedItem?.ToString());
        if (_source.SelectedIndex>=0) result.SourceLanguage = (SourceLanguageMode)_source.SelectedIndex;
        if(_style.SelectedIndex>=0)result.TranslationStyle=(TranslationStyle)_style.SelectedIndex;
        result.CustomTranslationPrompt = _custom.Text.Trim();
        result.PreserveIdentifiers = _ids.Checked; result.PreserveNumbers = _numbers.Checked; result.PreserveVariables = _variables.Checked;
        result.TranslationPromptVersion = TranslationPromptBuilder.Version;
        return result;
    }

    private async Task RunProviderTestAsync(Button button)
    {
        button.Enabled = false; _testStatus.Text = "正在测试…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _tester(CaptureSettings(), timeout.Token);
            _testStatus.Text = result.Success ? $"成功：{result.Message}" : $"{result.Category}：{result.Message}";
        }
        catch (Exception ex) { _testStatus.Text = ClassifyException(ex); }
        finally { button.Enabled = true; }
    }

    private static async Task<ProviderConnectionTestResult> TestWithServiceAsync(ApiSettings settings, CancellationToken token)
    {
        try
        {
            var response = await new TranslationService().TranslatePlainAsync("Hello.", settings, token);
            return new(true, "Success", $"HTTP {response.HttpStatusCode}，模型已返回简短结果。", response.HttpStatusCode);
        }
        catch (Exception ex) { return new(false, "Connection failed", ClassifyException(ex)); }
    }

    internal static string ClassifyException(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("401") || message.Contains("403")) return "Authentication failed：请检查 API Key。";
        if (message.Contains("404") || message.Contains("model", StringComparison.OrdinalIgnoreCase)) return "Model not found：请检查模型名称和 API 地址。";
        if (ex is TranslationTimeoutException or OperationCanceledException) return "Timeout：Provider 未在限制时间内完成。";
        return "Connection failed：" + message;
    }

    internal Task RunProviderTestForSmokeAsync() => RunProviderTestAsync(new Button());

    internal void SetRuntimeValuesForSmoke(string targetLanguage, TranslationStyle style,
        string model, string customPrompt)
    {
        _target.SelectedItem = UiTargetLanguageDisplay.DisplayFromCode(targetLanguage);
        _style.SelectedIndex=(int)style;
        _model.Text = model;
        _custom.Text = customPrompt;
    }

    private static string ToTargetCode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "simplified chinese" or "简体中文" => "zh-CN",
        "traditional chinese" or "繁體中文" or "繁体中文" => "zh-TW",
        "english" => "en", "japanese" => "ja", "korean" => "ko", _ => value
    };
}
