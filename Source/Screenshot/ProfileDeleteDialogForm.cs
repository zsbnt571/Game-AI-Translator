namespace ScreenshotTranslationUiTester;

internal sealed class ProfileDeleteDialogForm : Form
{
    private sealed record Choice(string Id,string Name) { public override string ToString()=>Name; }
    private readonly Label heading,description,label;
    private readonly GamePlanBox replacement;
    private readonly GameActionButton cancel,delete;
    private bool arranging;
    internal string? ReplacementId=>(replacement.SelectedItem as Choice)?.Id;
    private static string ReferenceText(string value)=>value.StartsWith("游戏：",StringComparison.Ordinal)
        ?UiLanguage.Choose("游戏：","Game: ","Игра: ")+value[3..]:UiLanguage.Text(value);

    internal ProfileDeleteDialogForm(string name,string[] references,(string Id,string Name)[] choices,ApiSettings settings)
    {
        SuspendLayout();Text="删除方案";Name="DeleteProfileDialog";
        AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);
        ClientSize=new(560,330);FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterParent;MinimizeBox=MaximizeBox=ShowInTaskbar=false;
        Font=new Font("Microsoft YaHei UI",10);AppBranding.Apply(this);
        heading=new(){Text=UiLanguage.Text("选择替代方案后删除"),Font=new Font(Font.FontFamily,16,FontStyle.Bold)};
        description=new(){Text=UiLanguage.Choose(
            $"删除“{name}”前，请为以下位置选择替代方案：\n{string.Join("；",references)}\n\n确认后一起保存。游戏文件、存档和已有译文不会删除。",
            $"Before deleting “{name}”, choose a replacement for:\n{string.Join("; ",references.Select(ReferenceText))}\n\nReferences and deletion are saved together. Game files, saves and translations are kept.",
            $"Перед удалением «{name}» выберите замену для:\n{string.Join("; ",references.Select(ReferenceText))}\n\nСсылки и удаление сохраняются вместе. Файлы игр, сохранения и переводы сохраняются."),Name="SettingsHelp"};
        label=new(){Text="替代方案"};
        replacement=new(){Name="DeleteProfileReplacement",AccessibleName="替代方案",DropDownStyle=ComboBoxStyle.DropDownList,TabIndex=0};
        replacement.Items.AddRange(choices.Select(c=>(object)new Choice(c.Id,c.Name)).ToArray());
        replacement.SelectedIndex=-1;
        cancel=new("取消"){DialogResult=DialogResult.Cancel,MinimumLogicalWidth=92,TabIndex=1};
        delete=new("替换并删除"){DialogResult=DialogResult.OK,Primary=true,PrimaryFill=SettingsColors.PrimaryFill,MinimumLogicalWidth=120,TabIndex=2,Enabled=false};
        replacement.SelectedIndexChanged+=(_,_)=>delete.Enabled=ReplacementId is not null;
        Controls.AddRange([heading,description,label,replacement,cancel,delete]);AcceptButton=delete;CancelButton=cancel;
        MainForm.ConfigureFusionInputs(this);UiTheme.Apply(this,settings);FontManager.ApplyUi(this,settings);UiLanguage.BindTree(this);
        ResumeLayout(true);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(arranging||delete is null)return;arranging=true;
        try
        {
            int S(int n)=>n*DeviceDpi/96;int pad=S(24),width=ClientSize.Width-pad*2,y=pad;
            foreach(var text in new[]{heading,description,label})
            {int height=text.GetPreferredSize(new(width,0)).Height;text.SetBounds(pad,y,width,height);y+=height+S(16);}
            replacement.RefreshMetrics();replacement.SetBounds(pad,y,width,replacement.Height);y+=replacement.Height+S(24);
            cancel.RefreshMetrics();delete.RefreshMetrics();delete.Location=new(ClientSize.Width-pad-delete.Width,y);cancel.Location=new(delete.Left-S(8)-cancel.Width,y);
            int heightNeeded=y+Math.Max(cancel.Height,delete.Height)+pad;
            if(ClientSize.Height!=heightNeeded)ClientSize=new(ClientSize.Width,heightNeeded);
        }
        finally{arranging=false;}
    }
}
