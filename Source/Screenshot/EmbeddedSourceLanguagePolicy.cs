using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Locale-aware extractors are authoritative. This is a conservative fallback
// for old bridges and untagged catalogues, not a general language detector.
internal sealed class EmbeddedSourceLanguagePolicy
{
    private readonly SourceLanguageMode mode;
    internal string SelectedLanguage {get;private set;}
    internal bool Resolved {get;private set;}
    internal EmbeddedSourceLanguagePolicy(SourceLanguageMode mode)
    {
        this.mode=mode;SelectedLanguage=mode switch
        {SourceLanguageMode.English=>"en",SourceLanguageMode.Japanese=>"ja",SourceLanguageMode.SimplifiedChinese=>"zh",SourceLanguageMode.Korean=>"ko",SourceLanguageMode.Mixed=>"*",_=>""};
        Resolved=mode!=SourceLanguageMode.Auto;
    }
    private static readonly Regex Words=new(@"[\p{L}]+",RegexOptions.Compiled);
    private static readonly Regex Markup=new(@"</?[^>]+>|\\[A-Za-z]+\[[^\]]*\]",RegexOptions.Compiled);
    private static readonly Dictionary<string,HashSet<string>> Evidence=new()
    {
        ["en"]=new("the you your are is this that with have from don't what where who should new game load save settings options continue back exit yes no language volume controls quality window resolution".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["es"]=new("el los las una unos unas tienes eres está estás dónde quién qué para pero como guardar cargar juego ajustes salir idioma".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["fr"]=new("les une des vous votre avec dans pour mais êtes où quoi jouer sauvegarder charger quitter paramètres".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["de"]=new("der die das ein eine und ist sind nicht dein deine spiel laden speichern zurück einstellungen".Split(' '),StringComparer.OrdinalIgnoreCase),
        ["pt"]=new("você não uma são suas seu para jogo salvar carregar configurações sair".Split(' '),StringComparer.OrdinalIgnoreCase)
    };
    internal static string Identify(string source)
    {
        string text=Markup.Replace(source,"");int kana=0,han=0,korean=0,cyrillic=0,latin=0;
        var otherScripts=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(char c in text)
        {
            if(c is >= '\u3040' and <= '\u30ff' or >= '\uff66' and <= '\uff9d')kana++;
            else if(c is >= '\u3400' and <= '\u9fff')han++;
            else if(c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff')korean++;
            else if(c is >= '\u0400' and <= '\u052f')cyrillic++;
            else if(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')latin++;
            else if(char.IsLetter(c))
            {
                string script=c switch
                {>= '\u0600' and <= '\u06ff' or >= '\u0750' and <= '\u077f' or >= '\u08a0' and <= '\u08ff'=>"ar",
                 >= '\u0590' and <= '\u05ff'=>"he",>= '\u0e00' and <= '\u0e7f'=>"th",
                 >= '\u0370' and <= '\u03ff' or >= '\u1f00' and <= '\u1fff'=>"el",
                 >= '\u0900' and <= '\u097f'=>"hi",_=>"und"};
                otherScripts[script]=otherScripts.GetValueOrDefault(script)+1;
            }
        }
        if(kana>0)return "ja";
        if(korean>0)return "ko";
        if(han>0)return "han"; // Han-only labels cannot distinguish Japanese from Chinese.
        if(cyrillic>0)return "ru";
        var other=otherScripts.Where(p=>p.Key!="und").OrderByDescending(p=>p.Value).FirstOrDefault().Key;
        if(other is not null)return other;
        if(latin==0&&otherScripts.Count>0)return "und";
        if(latin==0)return "";
        var words=Words.Matches(text).Select(m=>m.Value).ToArray();
        var scores=Evidence.Select(p=>(p.Key,Score:words.Count(p.Value.Contains))).OrderByDescending(p=>p.Score).ToArray();
        if(scores[0].Score>0&&scores[0].Score>scores[1].Score)return scores[0].Key;
        return "latin"; // A proper name or unknown Latin-script label is not proof of English.
    }
    internal static string Choose(IEnumerable<string> texts)
    {
        var counts=new Dictionary<string,(int Rows,int Weight)>(StringComparer.Ordinal);
        foreach(var text in texts)
        {
            var language=Identify(text);var old=counts.GetValueOrDefault(language);
            counts[language]=(old.Rows+1,old.Weight+Math.Min(text.Length,512));
        }
        var en=counts.GetValueOrDefault("en");
        if(en.Rows>=2||en.Weight>=40)return "en";
        if(counts.ContainsKey("ja"))return "ja";
        if(en.Rows>0)return "en";
        var other=counts.Where(p=>p.Key is not ("" or "latin")).OrderByDescending(p=>p.Value.Weight).ThenBy(p=>p.Key,StringComparer.Ordinal).FirstOrDefault().Key;
        return other=="han"?"zh":other??(counts.ContainsKey("latin")?"en":"");
    }
    internal void SelectCatalog(IEnumerable<string> texts,string hint="")
    {
        if(Resolved)return;
        SelectedLanguage=string.IsNullOrWhiteSpace(hint)?Choose(texts):hint.ToLowerInvariant();
        Resolved=SelectedLanguage.Length>0;
    }
    internal bool Allows(string text,string provisional="")
    {
        var selected=Resolved?SelectedLanguage:provisional;
        if(selected=="*")return true;
        if(selected.Length==0)return false;
        var detected=Identify(text);
        return detected.Length>0&&(detected==selected||detected=="han"&&selected is "ja" or "zh"||
            detected=="latin"&&selected is "en" or "es" or "fr" or "de" or "pt");
    }
    internal string Label=>SelectedLanguage switch
    {"en"=>"英语","ja"=>"日语","zh"=>"中文","ko"=>"韩语","ru"=>"俄语","es"=>"西班牙语","fr"=>"法语","de"=>"德语","pt"=>"葡萄牙语","ar"=>"阿拉伯语","he"=>"希伯来语","th"=>"泰语","el"=>"希腊语","hi"=>"印地语","und"=>"其他原文","*"=>"混合语言",""=>"正在识别",_=>SelectedLanguage};
}
