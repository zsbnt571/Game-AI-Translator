using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal sealed record RgssLanguageSwitch(int Id,string OffLanguage,string OnLanguage);

internal static partial class RgssScriptCatalog
{
    private const string SettingsAssignment=@"\bCUSTOM_SWITCHES\s*(?:=(?!=)|\|\|=|&&=|\+=)";
    private const string SettingsMutation=@"(?m)(?:^|;)[ \t]*(?:[A-Za-z_]\w*::)*CUSTOM_SWITCHES\s*(?:\.[ \t]*(?:merge!|update|replace|clear|delete)(?![A-Za-z0-9_])|\[[^\r\n]*\]\s*=)";
    internal static bool HasLanguageSettingsWrite(string code)=>Regex.IsMatch(code,SettingsAssignment,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))||
        Regex.IsMatch(code,SettingsMutation,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    // YEA System's declarative switch contract is [ID, title, off, on, help].
    // Parse only static settings, never execute Ruby or infer a saved switch.
    internal static RgssLanguageSwitch[] LanguageSwitches(string code,Encoding encoding,Action check)
    {
        var candidates=Regex.Matches(code,@"(?m)^[ \t]*CUSTOM_SWITCHES[ \t]*=[ \t]*\{",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(candidates.Count!=1)return [];
        // A later replacement/mutation makes the earlier static table unsafe.
        // False positives in comments merely keep the ordinary translation path.
        if(Regex.Matches(code,SettingsAssignment,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)).Count!=1||
            Regex.IsMatch(code,SettingsMutation,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))return [];
        var candidate=candidates[0];
        var end=Regex.Match(code[(candidate.Index+candidate.Length)..],@"(?m)^[ \t]*\}[^\r\n]*",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(!end.Success||end.Index>65536)return [];
        int cutoff=candidate.Index+candidate.Length+end.Index+end.Length;
        try
        {
            // Later method bodies can contain unsupported Ruby expressions;
            // those cannot invalidate an already bounded settings declaration.
            var tokens=new Lexer(code[..cutoff],encoding,check).Read();var modules=new List<string>();
            for(int i=0;i<tokens.Count;i++)
            {
                check();var t=tokens[i];bool line=i==0||Is(tokens[i-1],"\n")||Is(tokens[i-1],";");
                if(!line||t.Kind!='i')continue;
                if(t.Value=="module")
                {
                    int p=i+1;string name="";
                    while(p<tokens.Count&&!Is(tokens[p],"\n")&&!Is(tokens[p],";"))
                    {if(tokens[p].Kind!='i'&&!Is(tokens[p],"::"))return [];name+=tokens[p++].Value;}
                    modules.Add(name);continue;
                }
                if(t.Value=="end"){if(modules.Count==0)return [];modules.RemoveAt(modules.Count-1);continue;}
                if(t.Value is "if" or "unless" or "case" or "while" or "until" or "for" or "begin" or "def" or "class")return [];
                if(t.Value!="CUSTOM_SWITCHES")continue;
                if(string.Join("::",modules)!="YEA::SYSTEM"||i+2>=tokens.Count||!Is(tokens[i+1],"=")||!Is(tokens[i+2],"{"))return [];
                int close=Matching(tokens,i+2);
                if(tokens.Skip(close+1).Any(x=>!Is(x,"\n")))return [];
                List<RgssLanguageSwitch> found=[];var keys=new HashSet<string>(StringComparer.Ordinal);
                foreach(var part in Arguments(tokens,i+3,close))
                {
                    var entry=tokens.Skip(part.Start).Take(part.End-part.Start).Where(x=>!Is(x,"\n")).ToList();
                    int arrow=entry.FindIndex(x=>Is(x,"=>"));
                    if(arrow<0||arrow+2>=entry.Count||!Is(entry[arrow+1],"[")||!Is(entry[^1],"]"))continue;
                    if(!(arrow==2&&Is(entry[0],":")&&entry[1].Kind=='i'||arrow==1&&entry[0].Kind=='s'))continue;
                    string key=(arrow==2?"symbol:":"string:")+entry[arrow-1].Value;
                    if(!keys.Add(key))return [];
                    var args=Arguments(entry,arrow+2,entry.Count-1);if(args.Count<4)continue;
                    var id=entry.Skip(args[0].Start).Take(args[0].End-args[0].Start).ToArray();
                    if(id.Length!=1||id[0].Kind!='n'||!int.TryParse(id[0].Value,out int number)||number is <1 or >999999)continue;
                    string? title=Literal(entry,args[1].Start,args[1].End);
                    if(title?.Trim().ToLowerInvariant() is not ("language" or "text language" or "dialogue language" or "言語" or "语言" or "語言" or "язык" or "idioma" or "langue" or "sprache" or "언어"))continue;
                    string off=CatalogLanguageBranches.RowLocale(Literal(entry,args[2].Start,args[2].End)??"");
                    string on=CatalogLanguageBranches.RowLocale(Literal(entry,args[3].Start,args[3].End)??"");
                    if(off.Length>0&&on.Length>0&&off!=on)found.Add(new(number,off,on));
                }
                return found.GroupBy(s=>s.Id).Where(g=>g.Select(s=>(s.OffLanguage,s.OnLanguage)).Distinct().Count()==1).Select(g=>g.First()).ToArray();
            }
        }
        catch(Exception e)when(e is InvalidDataException or NotSupportedException or DecoderFallbackException or EncoderFallbackException){}
        return [];
    }
}
