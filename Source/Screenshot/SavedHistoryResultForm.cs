namespace ScreenshotTranslationUiTester;
/// <summary>Read-only saved result when the original image is unavailable.</summary>
internal sealed class SavedHistoryResultForm : Form
{
    private readonly PictureBox _picture=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom};
    internal string SavedText { get; }
    internal bool HasSavedImage => _picture.Image is not null;
    internal bool RegenerationAvailable => false;
    internal SavedHistoryResultForm(SessionHistoryItem item)
    {
        if(item.OriginalPng.Length!=0)throw new ArgumentException("Use normal history preview when source exists",nameof(item));
        Text="历史保存结果 · 原图缺失";
        Size=new(1000,720);MinimumSize=new(480,320);StartPosition=FormStartPosition.CenterParent;
        SavedText=item.TranslationText??item.TranslationSummary??"";
        var notice=new Label{Dock=DockStyle.Top,Height=54,Padding=new(10),
            Text="原图已缺失。这里保留当时的译图和文字；重新识别或翻译需要重新打开原图。"};
        var text=new TextBox{Dock=DockStyle.Right,Width=280,Multiline=true,ReadOnly=true,
            ScrollBars=ScrollBars.Vertical,Text=SavedText};
        if(item.TranslatedImage is {Length:>0})
        {
            using var stream=new MemoryStream(item.TranslatedImage,false);
            using var decoded=Image.FromStream(stream,true,true);
            _picture.Image=new Bitmap(decoded);
        }
        Controls.Add(_picture);Controls.Add(text);Controls.Add(notice);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){var image=_picture.Image;_picture.Image=null;image?.Dispose();}
        base.Dispose(disposing);
    }
}
