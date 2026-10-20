using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
namespace ScreenshotTranslationUiTester;
public sealed partial class MainForm
{
    private sealed partial class NativeRpgController
    {

    private readonly System.Windows.Threading.DispatcherTimer _dataMapTimer=new(){Interval=TimeSpan.FromMilliseconds(100)};
    private bool _dataMapSyncing,_dataMapFollowCurrent=true;
    private int _dataMapSyncRevision;
    private string _dataMapDetailSignature="";

    private bool MapSyncVisible=>!IsDisposed&&View.IsVisible&&owner.WpfNativeWindow?.WindowState!=System.Windows.WindowState.Minimized&&_dataScope.SelectedIndex==7;

    private string MapDetailSignature()=>_dataMap.MapId+"|"+_dataMap.SelectionText+"|"+_dataMap.TileDetails+"|"+string.Join(";",_dataMap.SelectedEvents.Select(e=>e.GetRawText()));

    private void UpdateMapChoices(int current)
    {
        if(_dataMapChoice.DroppedDown)return;
        _dataMapChoosing=true;
        try
        {
            for(int i=0;i<_dataMapChoice.Items.Count;i++)
                if(_dataMapChoice.Items[i] is MapChoice m&&m.Current!=(m.Id==current))_dataMapChoice.Items[i]=m with{Current=m.Id==current};
            _dataMapChoice.SelectedItem=_dataMapChoice.Items.Cast<MapChoice>().FirstOrDefault(m=>m.Id==_dataSelectedMapId);
        }
        finally{_dataMapChoosing=false;}
    }

    private async Task SyncGameMapAsync()
    {
        if(owner._workspaceResizing||owner._librarySplit?.Capture==true||!MapSyncVisible||_dataMapSyncing||_dataLoading||_dataWriting||_dataMap.Capture||_dataMapChoice.DroppedDown||owner._selectedGame is not { } game||SelectedDataSession() is not {Enabled:true,Ready:true} state||!state.Connection.Connected)return;
        int epoch=_dataEpoch,revision=_dataRevision,generation=state.Generation;
        _dataMapSyncing=true;
        try
        {
            var result=await state.Connection.RequestAsync(new(){["op"]="mapLive",["generation"]=generation,["mapId"]=_dataMapFollowCurrent?0:_dataSelectedMapId,["revision"]=_dataMapSyncRevision});
            if(owner._workspaceResizing||owner._librarySplit?.Capture==true||!IsDataView(game.ExePath,epoch)||revision!=_dataRevision||generation!=state.Generation||_dataLoading||_dataWriting||!MapSyncVisible||_dataMap.Capture||_dataMapChoice.DroppedDown||result.GetProperty("pending").GetBoolean())return;
            int before=_dataMap.MapId;
            if(result.TryGetProperty("map",out var map))
            {
                _dataMap.SetMap(map);_dataPage.Text=$"地图 {map.GetProperty("id").GetInt32():0000} · {map.GetProperty("width")} × {map.GetProperty("height")} 格";
            }
            _dataMap.UpdateLive(result.GetProperty("live"));
            _dataMapSyncRevision=result.GetProperty("revision").GetInt32();_dataMapGeneration=generation;_dataSelectedMapId=_dataMap.MapId;
            UpdateMapChoices(result.GetProperty("currentMapId").GetInt32());
            _dataMapSelection.Text=_dataMap.SelectionText;
            if(_dataMapDetailSignature!=MapDetailSignature())
            {
                // A moving marker must not recreate an event editor while typing.
                if(before!=_dataMap.MapId||_dataObjectEditor?.ContainsFocus!=true)await ShowSelectedMapTileAsync();
                else{_dataOriginalName.Text=UiLanguage.Text(_dataMap.SelectionText);_dataDescription.Text=UiLanguage.Text(_dataMap.TileDetails);}
            }
        }
        catch(Exception ex){AppLog.Write("rpg-map-sync",SafeDiagnosticOutput.ExceptionSummary(ex));}
        finally{_dataMapSyncing=false;}
    }
    }
}
