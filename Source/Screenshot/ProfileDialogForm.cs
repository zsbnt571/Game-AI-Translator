namespace ScreenshotTranslationUiTester;

internal sealed class ProfileDialogForm : Form
{
    private readonly Label heading,description;
    private readonly GameActionButton[] actions;
    internal GamePlanBox? TemplateBox {get;}
    private bool arranging;
    internal ProfileDialogForm(string title,string headingText,string message,ApiSettings settings,bool template)
    {
        SuspendLayout();Text=title;Name=template?"NewProfileDialog":"UnsavedProfileDialog";
        AutoScaleMode=AutoScaleMode.Dpi;AutoScaleDimensions=new(96,96);
        ClientSize=new(500,280);FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterParent;MinimizeBox=MaximizeBox=ShowInTaskbar=false;
        Font=new Font("Microsoft YaHei UI",10);AppBranding.Apply(this);
        heading=new Label{Text=headingText,AutoSize=false,Font=new Font(Font.FontFamily,16,FontStyle.Bold),Name="DialogHeading",TabIndex=0};
        description=new Label{Text=message,AutoSize=false,Name="SettingsHelp",TabIndex=2};
        Controls.AddRange([heading,description]);
        GameActionButton Action(string text,DialogResult result,bool primary=false)=>new(text){DialogResult=result,Primary=primary,PrimaryFill=primary?SettingsColors.PrimaryFill:null,MinimumLogicalWidth=92,TabIndex=3};
        if(template)
        {
            TemplateBox=new GamePlanBox{AccessibleName="接口模板",Name="ProfileTemplate",TabIndex=1};
            TemplateBox.Items.AddRange(["DeepSeek","OpenAI","自定义兼容接口（需要密钥）","本地／无鉴权兼容接口"]);
            TemplateBox.SelectedIndex=0;Controls.Add(TemplateBox);
            actions=[Action("取消",DialogResult.Cancel),Action("继续",DialogResult.OK,true)];
        }
        else actions=[Action("取消",DialogResult.Cancel),Action("放弃修改",DialogResult.No),Action("保存",DialogResult.Yes,true)];
        for(int i=0;i<actions.Length;i++){actions[i].TabIndex=i+3;Controls.Add(actions[i]);}
        AcceptButton=actions[^1];CancelButton=actions[0];
        MainForm.ConfigureFusionInputs(this);UiTheme.Apply(this,settings);FontManager.ApplyUi(this,settings);
        description.ForeColor=SettingsColors.Muted;ResumeLayout(true);
        Shown+=(_,_)=>{PerformLayout();if(TemplateBox is not null)TemplateBox.Focus();else actions[0].Focus();};
    }
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);SettingsNativeTheme.Caption(this);}
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);if(arranging||actions is null)return;arranging=true;
        try
        {
            int S(int n)=>n*DeviceDpi/96;int pad=S(24),y=pad;
            foreach(var action in actions)action.RefreshMetrics();
            int actionsWidth=actions.Sum(b=>b.Width)+(actions.Length-1)*S(8);
            int dialogWidth=Math.Max(S(500),actionsWidth+pad*2);
            if(ClientSize.Width!=dialogWidth)ClientSize=new(dialogWidth,ClientSize.Height);
            int width=ClientSize.Width-pad*2;
            int h=heading.GetPreferredSize(new(width,0)).Height;heading.SetBounds(pad,y,width,h);y+=h+S(20);
            if(TemplateBox is not null){TemplateBox.RefreshMetrics();TemplateBox.SetBounds(pad,y,width,TemplateBox.Height);y+=TemplateBox.Height+S(12);}
            h=description.GetPreferredSize(new(width,0)).Height;description.SetBounds(pad,y,width,h);y+=h+S(28);
            int x=Math.Max(pad,ClientSize.Width-pad-actionsWidth);
            foreach(var action in actions){action.Location=new(x,y);x+=action.Width+S(8);}
            int height=y+actions.Max(b=>b.Height)+pad;
            if(ClientSize.Height!=height)ClientSize=new(ClientSize.Width,height);
        }
        finally{arranging=false;}
    }
}
