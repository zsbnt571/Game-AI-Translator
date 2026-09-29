namespace ScreenshotTranslationUiTester;

// Scoped to settings dialogs. Image rendering and the application's font policy are unchanged.
internal sealed class DialogActionBar : FlowLayoutPanel
{
    private bool _arranging;
    internal DialogActionBar(bool rightToLeft = false)
    {
        Dock = DockStyle.Bottom; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        WrapContents = true;
        FlowDirection = rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Padding = new Padding(8);
        DpiChangedAfterParent += (_, _) => PerformLayout();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (!_arranging)
        {
            _arranging = true;
            try
            {
                var scale = DeviceDpi / 96f;
                foreach (var button in Controls.OfType<Button>())
                {
                    button.AutoSize = false; button.MinimumSize = Size.Empty;
                    var text = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty,
                        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    button.Size = new Size(Math.Max((int)(100 * scale), text.Width + (int)(28 * scale)),
                        Math.Max((int)(36 * scale), text.Height + (int)(16 * scale)));
                }
            }
            finally { _arranging = false; }
        }
        base.OnLayout(e);
    }
}

internal static class DialogLayout
{
    internal static Form NetworkRepairPrompt(Form owner)
    {
        var form=new Form{AutoScaleMode=AutoScaleMode.Dpi,AutoScaleDimensions=new SizeF(96,96),
            Text="联网修复 OCR 模型",ClientSize=new Size(600,280),MinimumSize=new Size(480,240),
            StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false};
        form.SuspendLayout();
        var panel=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(16)};
        var body=new Label{AutoSize=true,Text="检查已发现模型缺失或损坏。是否从 RapidOCR 官方模型地址下载本包所需模型？\n\n仅修复模型，不安装其他组件；离线或下载失败会停止并说明原因。"};
        panel.Controls.Add(body);panel.SizeChanged+=(_,_)=>body.MaximumSize=new Size(Math.Max(1,panel.ClientSize.Width-panel.Padding.Horizontal),0);
        var actions=new DialogActionBar(true);
        var yes=new Button{Text="联网修复",DialogResult=DialogResult.Yes};var no=new Button{Text="取消",DialogResult=DialogResult.No};
        actions.Controls.AddRange([yes,no]);form.Controls.Add(panel);form.Controls.Add(actions);form.AcceptButton=yes;form.CancelButton=no;
        FontManager.ApplyUi(form,new ApiSettings{UiFontSize=owner.Font.SizeInPoints});UiTheme.Apply(form,new ApiSettings());Attach(form);form.ResumeLayout(true);
        return form;
    }

    internal static void Attach(Form form)
    {
        var designMinimum = form.MinimumSize;
        void Fit()
        {
            if (form.IsDisposed) return;
            FitToWorkingArea(form, Screen.FromControl(form.Owner ?? form).WorkingArea, designMinimum);
        }
        form.Load += (_, _) => Fit();
        form.Shown += (_, _) => Fit();
        form.DpiChanged += (_, _) => { if(form.IsHandleCreated) form.BeginInvoke((Action)Fit); };
    }

    internal static void FitToWorkingArea(Form form, Rectangle area, Size designMinimum)
    {
        var scale = form.DeviceDpi / 96f;
        form.MinimumSize = new Size(Math.Min(area.Width, (int)Math.Ceiling(designMinimum.Width * scale)),
            Math.Min(area.Height, (int)Math.Ceiling(designMinimum.Height * scale)));
        form.MaximumSize = area.Size;
        var size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
        var location = new Point(Math.Clamp(form.Left, area.Left, area.Right - size.Width),
            Math.Clamp(form.Top, area.Top, area.Bottom - size.Height));
        form.Bounds = new Rectangle(location, size);
        form.PerformLayout();
    }
}
