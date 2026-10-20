namespace ScreenshotTranslationUiTester;

public sealed partial class MainForm
{
    private bool _dataUpdatingNames;
    private readonly Dictionary<ComboBox,Action> _dataNameChoices=[];
    private void BindDataNames(ComboBox choice,Func<string[]> names,Func<int,string,string> format)
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
        if(_selectedGame is not {} game||SelectedDataSession() is not {} session)return "";
        ApiSettings settings;
        try{settings=_fusion!.GameSettings(_appliedDesktopSettings,game.ExePath);}catch(InvalidOperationException){return "";}
        if(!string.IsNullOrWhiteSpace(fallback)&&InterfaceDisplayLanguage.Matches(settings.TargetLanguage))return fallback;
        // Reuse game translations only when their target matches the UI.
        if(session.Translation?.Enabled==true&&InterfaceDisplayLanguage.Matches(settings.TargetLanguage))
        {
            session.Labels?.Dispose();session.Labels=null;
            return session.Translation.Lookup(source);
        }
        string identity=UiLanguage.Current+"|"+FusionConfiguration.TranslationIdentity(settings);
        if(session.Labels is not null&&session.LabelIdentity!=identity){session.Labels.Dispose();session.Labels=null;}
        if(session.Labels is null)
        {
            try
            {
                session.LabelIdentity=identity;
                session.Labels=new(game.ExePath,settings,async(text,profile,token)=>(await _translationService.TranslatePlainAsync(text,profile,token)).Text);
                session.Labels.Changed+=()=>
                {
                    if(!IsHandleCreated||IsDisposed||Disposing)return;
                    try{BeginInvoke((Action)(()=>{if(!IsDisposed&&SameGame(_selectedGame?.ExePath,game.ExePath))RefreshDataLabels();}));}catch(InvalidOperationException){}
                };
            }
            catch(InvalidOperationException){return "";}
        }
        string value=session.Labels.Lookup(source);if(value.Length==0)session.Labels.Request(source);return value;
    }
    private void RefreshDataLabels()
    {
        // Patch text cells in place. Keep selection, scroll, editor inputs and event pages.
        foreach(DataGridViewRow row in _dataGrid.Rows)
            if(row.Tag is System.Text.Json.JsonElement item)
            {string name=DataChinese(item);if(!Equals(row.Cells[1].Value,name))row.Cells[1].Value=name;}
        if(_dataSelected is {} selected)
        {
            string translated=DataChinese(selected),original=selected.GetProperty("name").GetString()??"";
            _dataSelectedName.Text=translated.Length>0?translated:original;_dataOriginalName.Text=original;
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
}
