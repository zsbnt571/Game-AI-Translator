namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private void SetGameInfoText(string value) { if(WpfWorkspace.Enabled)GameDetail.Info.Text=value;else SetLabelText(_gameInfo,value); }
    private void SetGameFileStatusText(string value) { if(WpfWorkspace.Enabled)GameDetail.FileStatus.Text=value;else SetLabelText(_gameStatus,value); }
    private void SetGameMediaText(string value) { if(WpfWorkspace.Enabled)GameDetail.Media.Text=value;else SetLabelText(_mediaStatus,value); }
    private void SetGameCoverDetailsText(string value) { if(WpfWorkspace.Enabled)GameDetail.CoverDetails.Text=value;else SetLabelText(_coverDetails,value); }
    private void SetGameQueryText(string value) { if(WpfWorkspace.Enabled)GameDetail.Query.Text=value;else SetLabelText(_gameQueryDetails,value); }
    private void SetGameFeedbackText(string value) { if(WpfWorkspace.Enabled)GameDetail.Feedback.Text=value;else SetLabelText(_gameFeedback,value); }
    private void SetGameConfigurationText(string value) { if(WpfWorkspace.Enabled)GameDetail.Configuration.Text=value;else SetLabelText(_gameConfiguration,value); }
    private void SetGameHintText(string value) { if(WpfWorkspace.Enabled)GameDetail.Hint.Text=value;else SetLabelText(_gameActionHint,value); }
}
