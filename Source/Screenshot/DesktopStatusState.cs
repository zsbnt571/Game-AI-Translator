namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private string _desktopStatus = "就绪";
    internal string DesktopStatus
    {
        get => WpfWorkspace.Enabled ? _desktopStatus : _statusLabel.Text;
        set
        {
            if (!WpfWorkspace.Enabled) { _statusLabel.Text = value; return; }
            if (_desktopStatus == value) return;
            _desktopStatus = value; ScreenshotWorkspace?.SetStatus(value); WpfStatusChanged?.Invoke();
        }
    }
}
