using System.Text.Json;
namespace ScreenshotTranslationUiTester;

// Selection identity must not change when translated names or INPC subscribers
// change. A mutable record's generated equality/hash is unsuitable for DataGrid.
internal sealed class NovelVariableRow(JsonElement source,bool builtInNames=false) : System.ComponentModel.INotifyPropertyChanged
{
    public JsonElement Source { get; } = source;
    public bool BuiltInNames { get; } = builtInNames;
    public string RawName=>Source.GetProperty("name").GetString()??"";
    private string translation="";
    private string BuiltInName => !BuiltInNames?RawName:RawName=="队伍.金钱"?UiLanguage.Text("队伍.金钱"):
        RawName.StartsWith("变量.",StringComparison.Ordinal)?UiLanguage.Text("变量.")+RawName[3..]:RawName;
    public string DisplayName=>translation.Length>0?translation:BuiltInName;
    public string Name=>DisplayName;
    public string Original=>translation.Length>0&&translation!=RawName?RawName:"";
    internal void SetTranslation(string text){if(translation==text)return;translation=text;RefreshLanguage();}
    public string Value => Source.GetProperty("value").ValueKind==JsonValueKind.String?Source.GetProperty("value").GetString()??"":Source.GetProperty("value").GetRawText();
    public string Locked => Source.GetProperty("locked").GetBoolean()?UiLanguage.Text("已锁定"):"";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLanguage()=>PropertyChanged?.Invoke(this,new(null));
}
