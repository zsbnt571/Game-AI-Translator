namespace ScreenshotTranslationUiTester;

internal enum AppDialogKind { Information, Warning, Error, Confirmation }

internal static class AppDialog
{
    internal static DialogResult Show(IWin32Window? owner, string title, string message, AppDialogKind kind = AppDialogKind.Information, string? details = null)
    {
        using var form = new Form { Text=title, StartPosition=owner is null?FormStartPosition.CenterScreen:FormStartPosition.CenterParent,
            ClientSize=new Size(520,210), MinimumSize=new Size(440,210), MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,
            BackColor=UiTheme.Current.Main,ForeColor=UiTheme.Current.Text };
        form.SuspendLayout();form.AutoScaleMode=AutoScaleMode.Dpi;form.AutoScaleDimensions=new SizeF(96,96);
        var body=new Label{Dock=DockStyle.Fill,Padding=new Padding(24),Text=message,ForeColor=UiTheme.Current.Text};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=58,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(10),BackColor=UiTheme.Current.Secondary};
        Button Add(string text,DialogResult result){var b=new Button{Text=text,DialogResult=result,AutoSize=true,MinimumSize=new Size(96,36),BackColor=result is DialogResult.OK or DialogResult.Yes?UiTheme.Current.Accent:UiTheme.Current.Control,ForeColor=UiTheme.Current.Text,FlatStyle=FlatStyle.Flat,Margin=new Padding(6,0,0,0)};b.FlatAppearance.BorderColor=UiTheme.Current.Border;buttons.Controls.Add(b);return b;}
        if(kind==AppDialogKind.Confirmation){form.AcceptButton=Add("确定",DialogResult.Yes);form.CancelButton=Add("取消",DialogResult.No);}else{form.AcceptButton=Add("确定",DialogResult.OK);form.CancelButton=form.AcceptButton;}
        if(!string.IsNullOrWhiteSpace(details))
        {
            var detailBox=new TextBox{Dock=DockStyle.Bottom,Height=0,Visible=false,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text=details,BackColor=UiTheme.Current.Control,ForeColor=UiTheme.Current.Text};
            var detailButton=Add("查看详情",DialogResult.None);
            detailButton.Click+=(_,_)=>
            {
                detailBox.Visible=!detailBox.Visible;
                var scale=form.DeviceDpi/96f;
                detailBox.Height=detailBox.Visible?(int)Math.Round(110*scale):0;
                form.ClientSize=new Size(form.ClientSize.Width,(int)Math.Round((detailBox.Visible?330:210)*scale));
                detailButton.Text=detailBox.Visible?"隐藏详情":"查看详情";
            };
            form.Controls.Add(detailBox);
        }
        form.Controls.Add(body);form.Controls.Add(buttons);FontManager.ApplyUi(form,new ApiSettings());
        form.ResumeLayout(true);
        return ModalSafety.Show(owner, form);
    }
    internal static string UserFacingError(Exception ex)=>ex switch
    {
        VisionRuntimeMissingException missing=>missing.UserMessage,
        UnauthorizedAccessException=>"没有访问所需文件或目录的权限。请检查安装位置和安全软件设置。",
        FileNotFoundException or DirectoryNotFoundException=>"所需的本地组件或文件缺失。请打开模型与运行环境管理进行修复。",
        TimeoutException or TaskCanceledException=>"操作超时。请检查本地运行环境或网络连接后重试。",
        HttpRequestException=>"无法连接翻译服务。请检查网络、Base URL 和 Provider 设置。",
        _=>"操作失败。可查看技术详情或诊断日志。"
    };
}

internal static class ModalSafety
{
    internal static DialogResult Show(IWin32Window? owner, Form dialog)
    {
        var ownerForm=owner as Form;
        var wasTopMost=ownerForm?.TopMost==true;
        try
        {
            if(wasTopMost)ownerForm!.TopMost=false;
            dialog.TopMost=wasTopMost;
            return owner is null?dialog.ShowDialog():dialog.ShowDialog(owner);
        }
        finally
        {
            if(ownerForm is {IsDisposed:false,Disposing:false})ownerForm.TopMost=wasTopMost;
        }
    }

    internal static T Run<T>(Form owner, Func<T> show)
    {
        var wasTopMost=owner.TopMost;
        try{if(wasTopMost)owner.TopMost=false;return show();}
        finally{if(!owner.IsDisposed&&!owner.Disposing)owner.TopMost=wasTopMost;}
    }
}

internal sealed class VisionRuntimeMissingException(string userMessage,string technicalDetail,string missingPath):FileNotFoundException(technicalDetail,missingPath)
{ internal string UserMessage{get;}=userMessage; internal string TechnicalDetail{get;}=technicalDetail; }
