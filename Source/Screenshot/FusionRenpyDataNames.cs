namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private string RenpyDataLabel(string name)=>_renpyDataPresentation.Label(name,SelectedScriptTranslationSession()?.Labels?.Lookup(RenpyDataPresentation.Readable(name))??"");

    private void EnsureRenpyDataNames(string path,GameDataSession session,ApiSettings settings)
    {
        string identity=FusionConfiguration.TranslationIdentity(settings);
        if(session.Labels is not null&&session.LabelIdentity!=identity){session.Labels.Dispose();session.Labels=null;}
        if(session.Labels is not null)return;
        session.LabelIdentity=identity;
        // Loading a cached Chinese name does not enqueue or pay for a translation.
        var labels=new RpgLabelTranslations(path,settings,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text,allLanguages:true);
        session.Labels=labels;
        labels.Changed+=()=>
        {
            if(!IsHandleCreated||IsDisposed||Disposing)return;
            try{BeginInvoke((Action)(()=>{if(!IsDisposed&&SameGame(_selectedGame?.ExePath,path)&&ReferenceEquals(SelectedScriptTranslationSession()?.Labels,labels)){RefreshRenpyDataNames();QueueRenpyDataNames();}}));}catch(InvalidOperationException){}
        };
    }

    private void RefreshRenpyDataNames()
    {
        // A translation arriving in the background must never reset an edit or selection.
        foreach(DataGridViewRow cell in _renpyDataGrid.Rows)
            if(cell.Tag is System.Text.Json.JsonElement row)
            {
                string name=row.GetProperty("name").GetString()!;
                cell.Cells[0].Value=RenpyDataLabel(name);
                cell.Cells[0].ToolTipText=_renpyDataPresentation.Notes.ContainsKey(name)?"你的备注":SelectedScriptTranslationSession()?.Labels?.Lookup(RenpyDataPresentation.Readable(name)).Length>0?"机器译名，请结合原文和当前值核对":"通用词义提示";
            }
        if(SelectedScriptTranslationSession()?.Labels is {} labels)_renpyDataNamesStatus.Text=labels.Status.Length>0?labels.Status:"中文名已更新，原文和游戏变量保持不变。";
    }

    private void RequestRenpyDataNames()=>RequestRenpyNamesCore(false);
    private void RequestRenpyNamesCore(bool automatic)
    {
        if(_selectedGame is not {} game||SelectedScriptTranslationSession() is not {} session)return;
        try
        {
            var settings=_fusion!.GameSettings(_appliedDesktopSettings,game.ExePath);
            if(string.IsNullOrWhiteSpace(settings.ApiUrl)||string.IsNullOrWhiteSpace(settings.Model)||(!settings.AllowEmptyApiKey&&string.IsNullOrWhiteSpace(settings.ApiKey)))
            {if(!automatic)_renpyDataNamesStatus.Text="设置可用翻译方案后会自动补齐中文名。原文和已有备注仍可使用。";return;}
            EnsureRenpyDataNames(game.ExePath,session,settings);var labels=session.Labels!;
            if(!automatic)labels.RetryMissing();
            var names=_renpyDataGrid.Rows.Cast<DataGridViewRow>().Select(row=>((System.Text.Json.JsonElement)row.Tag!).GetProperty("name").GetString()!).Where(name=>!_renpyDataPresentation.Notes.ContainsKey(name)).Select(RenpyDataPresentation.Readable).Distinct(StringComparer.Ordinal).Where(name=>labels.NeedsRequest(name)).Take(200).ToArray();
            foreach(string name in names)labels.Request(name);
            RefreshRenpyDataNames();
            _renpyDataNamesStatus.Text=names.Length==0?"当前列表已有中文名缓存或备注。":$"正在补齐当前列表的中文名（本次最多 {names.Length} 项），可继续修改。";
        }
        catch(Exception ex){_renpyDataNamesStatus.Text="中文名未能更新："+SafeDiagnosticOutput.ExceptionSummary(ex);}
    }

    private void SaveRenpyDataView(bool favorite)
    {
        if(_selectedGame is not {} game||SelectedRenpyRow() is not {} row)return;
        string name=row.GetProperty("name").GetString()!;
        // Persist a fresh copy first; a failed save must not appear successful in the UI.
        var next=new RenpyDataPresentation {Notes=new(_renpyDataPresentation.Notes,StringComparer.Ordinal),Favorites=new(_renpyDataPresentation.Favorites,StringComparer.Ordinal)};
        if(favorite){if(!next.Favorites.Add(name))next.Favorites.Remove(name);}
        else if(_renpyDataNote.Text.Trim() is {Length:>0} note)next.Notes[name]=note;
        else next.Notes.Remove(name);
        try
        {
            next.Save(game.ExePath);_renpyDataPresentation=next;
            _renpyDataFavorite!.Text=next.Favorites.Contains(name)?"移出常用":"加入常用";
            RefreshRenpyDataNames();
            if(favorite&&_renpyDataScope.SelectedIndex==4)FilterRenpyData();
            _renpyDataNamesStatus.Text=favorite?(next.Favorites.Contains(name)?"已加入“我的常用”。":"已移出常用。"):"中文备注已保存，游戏变量名未改变。清空备注再保存可恢复自动名称。";
        }
        catch(Exception ex){_renpyDataNamesStatus.Text="没有保存成功："+SafeDiagnosticOutput.ExceptionSummary(ex);}
    }
}
