namespace ScreenshotTranslationUiTester;

internal static class ToastNotifier
{
    public static void Show(string message)
    {
        var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (owner is not null && owner.InvokeRequired)
        {
            owner.BeginInvoke(() => Show(message));
            return;
        }
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = Color.FromArgb(32, 38, 49),
            Size = new Size(240, 52),
            Opacity = .94
        };
        form.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, Text = message, TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White, Font = new Font("Microsoft YaHei UI", 10F)
        });
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        form.Location = new Point(area.Right - form.Width - 24, area.Bottom - form.Height - 24);
        var timer = new System.Windows.Forms.Timer { Interval = 1400 };
        timer.Tick += (_, _) => { timer.Stop(); timer.Dispose(); form.Close(); };
        form.FormClosed += (_, _) => timer.Dispose();
        form.Show();
        timer.Start();
    }
}
