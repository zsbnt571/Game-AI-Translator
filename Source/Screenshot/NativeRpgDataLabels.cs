using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private sealed partial class NativeRpgController
    {

    private bool _dataUpdatingNames;
    private readonly Dictionary<NativeRpgChoice,Action> _dataNameChoices=[];
    private void BindDataNames(NativeRpgChoice choice,Func<string[]> names,Func<int,string,string> format)
    {
        void Refresh()
        {
            if(choice.IsDisposed||choice.DroppedDown)return;
            var sources=names();
            for(int i=0;i<Math.Min(choice.Items.Count,sources.Length);i++)
            {string chinese=DataLabel(sources[i]);string display=format(i,chinese.Length>0?chinese+" · "+sources[i]:sources[i]);if(!Equals(choice.Items[i],display))choice.Items[i]=display;}
        }
        if(!_dataNameChoices.ContainsKey(choice))choice.Disposed+=(_,_)=>_dataNameChoices.Remove(choice);
        _dataNameChoices[choice]=Refresh;
        bool previous=_dataUpdatingNames;_dataUpdatingNames=true;try{Refresh();}finally{_dataUpdatingNames=previous;}
    }
    private string DataLabel(string source,string fallback="")
    {
        if(owner._selectedGame is not {} game||SelectedDataSession() is not {} session)return "";
        ApiSettings settings;
        try{settings=owner._fusion!.GameSettings(owner._appliedDesktopSettings,game.ExePath);}catch(InvalidOperationException){return "";}
        // The label queue loads the matching target's game cache. A live game
        // translator may still belong to an earlier profile after settings change.
        string identity="game-target|"+FusionConfiguration.TranslationIdentity(settings);
        if(session.Labels is not null&&session.LabelIdentity!=identity){session.Labels.Dispose();session.Labels=null;}
        if(session.Labels is null)
        {
            try
            {
                session.LabelIdentity=identity;
                session.Labels=new(game.ExePath,settings,async(text,profile,token)=>(await owner._translationService.TranslatePlainAsync(text,profile,token)).Text,followGameTarget:true);
                session.Labels.Changed+=()=>
                {
                    // Label work runs on a pool thread. Even IsLoaded is a WPF
                    // property and must only be inspected after dispatching.
                    var dispatcher=View.Dispatcher;
                    if(dispatcher.HasShutdownStarted||dispatcher.HasShutdownFinished)return;
                    try{dispatcher.BeginInvoke((Action)(()=>{if(!IsDisposed&&!Disposing&&IsHandleCreated&&SameGame(owner._selectedGame?.ExePath,game.ExePath))RefreshDataLabels();}));}catch(InvalidOperationException){}
                };
            }
            catch(InvalidOperationException){return "";}
        }
        string value=session.Labels.Lookup(source);if(value.Length==0)session.Labels.Request(source);return value;
    }
    internal void RefreshInterfaceNames()
    {if(SelectedDataSession() is {} data){data.Labels?.Dispose();data.Labels=null;data.LabelIdentity="";}RefreshDataLabels();}
    private void RefreshDataLabels()
    {
        // Patch text cells in place. Keep selection, scroll, editor inputs and event pages.
        foreach(NativeRpgGridRow row in _dataGrid.Rows)
            if(row.Tag is System.Text.Json.JsonElement item)
            {string name=DataChinese(item);if(!Equals(row.Cells[1].Value,name))row.Cells[1].Value=name;}
        if(_dataSelected is {} selected)
        {
            string translated=DataChinese(selected),original=selected.GetProperty("name").GetString()??"";
            _dataSelectedName.Text=translated.Length>0?translated:original;_dataOriginalName.Text=translated.Length>0&&translated!=original?original:"";
            if(selected.GetProperty("type").GetString() is not ("actor" or "event"))_dataDescription.Text=DataDescription(selected);
        }
        if(!_dataMapChoice.DroppedDown)
        {
            _dataMapChoosing=true;
            try{for(int i=0;i<_dataMapChoice.Items.Count;i++)if(_dataMapChoice.Items[i] is MapChoice m){string name=DataLabel(m.Name);if(name!=m.Chinese)_dataMapChoice.Items[i]=m with{Chinese=name};}}
            finally{_dataMapChoosing=false;}
        }
        _dataUpdatingNames=true;
        try{foreach(var refresh in _dataNameChoices.Values.ToArray())refresh();}
        finally{_dataUpdatingNames=false;}
        if(!_dataWriting&&SelectedDataSession() is {} session)
            _dataFeedback.Text=session.Feedback+(session.Labels?.Status is {Length:>0} status?" · "+status:"");
    }
    private string DataDescription(JsonElement row)
    {
        string original=row.TryGetProperty("description",out var description)?description.GetString()??"":"";
        if(original.Length==0)return "";
        string translated=DataLabel(original,row.TryGetProperty("descriptionChinese",out var previous)?previous.GetString()??"":"");
        return translated.Length>0?translated:original;
    }
    }
}
