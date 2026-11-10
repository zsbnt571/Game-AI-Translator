namespace ScreenshotTranslationUiTester;

internal sealed partial class RpgTranslationSession
{
    private GameCharacterRuntimeLane? characterRuntime;
    private bool characterRuntimeNegotiated;
    private void InitializeCharacterRuntime()
    {
        characterRuntime=null;characterRuntimeNegotiated=false;
        if(!GameDialogueRoles.Applies(cacheSettings))return;
        string? Manual(string source)=>TryManual(source,EffectiveWorkspace(),out var value)?value:null;
        string? Ordinary(string source)
        {
            var doc=EffectiveWorkspace();
            if(cache.TryGetValue(source,out var value)&&nativeGlossary.Matches(source,value,doc.Terms)
                &&AutomaticTranslationQuality.Displayable(source,value,cacheSettings,doc.Terms,options.Syntax))return value;
            return null;
        }
        characterRuntime=new(characters,characterCache,cacheSettings,singleSettings,batchSettings,options.Syntax,
            ()=>EffectiveWorkspace(),Manual,Ordinary,(prompt,settings,token)=>TranslationFlightPool.Shared.Request(translate,prompt,settings,token),
            ()=>characterDirty=true,cachePath+".character-runtime-issues-v1.json",lifetime.Token);
    }
}
