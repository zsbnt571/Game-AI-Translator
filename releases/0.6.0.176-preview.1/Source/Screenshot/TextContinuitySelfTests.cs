using System.Text;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class TextContinuitySelfTests
{
    internal static int Run(string output,string? cacheFile=null,string? legacyList=null)
    {
        var checks=new List<object>();int failed=0;
        void Check(string name,bool ok){checks.Add(new{name,passed=ok});if(!ok)failed++;}
        void Reject(string name,Action action){try{action();Check(name,false);}catch(InvalidDataException){Check(name,true);}}
        foreach(string source in new[]{"50% chance", "Increase % defense", "Lose % of current health", "25% speed", "100% complete", "50%chance", "A % discount", "A % increase"})
            Check("literal percent prose: "+source,EmbeddedTranslationText.Protect(source).Codes.Length==0);
        foreach(string code in new[]{"%s","%d","%.2f","%+08.2f","% d","%2$s","%(name)s","%%","%*.*f"})
            Check("real format retained: "+code,EmbeddedTranslationText.Protect("Value "+code+".").Codes.SequenceEqual(new[]{code}));
        string structure="<color=red>{player}</color>\r\n\r\nCost: %02d\\n";
        var protectedText=EmbeddedTranslationText.Protect(structure,true);
        Check("request round trip preserves CRLF, blank paragraph and markup",EmbeddedTextContinuity.RestoreRequest(structure,protectedText.Text,protectedText.Codes)==structure);
        Check("cached validation still accepts reordered named variables",RpgTranslationText.ValidCachedPair("{hero} helps {friend}","{friend}得到了{hero}的帮助",EmbeddedTextSyntax.Generic));
        Check("misnested markup stays invalid",!RpgTranslationText.ValidCachedPair("<b><i>Hello</i></b>","<b><i>你好</b></i>",EmbeddedTextSyntax.Generic));
        var breaks=EmbeddedTranslationText.Protect("First\nSecond\nThird",true);
        Check("provider extra breaks do not create UI rows",EmbeddedTextContinuity.RestoreRequest("Hello world","你\n好，世\n界。",[])=="你好，世界。");
        Reject("missing authored break rejected",()=>EmbeddedTextContinuity.RestoreRequest("First\nSecond\nThird","一⟦RPG0⟧二三",breaks.Codes));
        Reject("reversed break tokens rejected",()=>EmbeddedTextContinuity.RestoreRequest("First\nSecond\nThird","一⟦RPG1⟧二⟦RPG0⟧三",breaks.Codes));
        Reject("duplicate token rejected",()=>EmbeddedTextContinuity.RestoreRequest("First\nSecond\nThird","一⟦RPG0⟧⟦RPG0⟧二⟦RPG1⟧三",breaks.Codes));
        foreach(var item in new[]{("50% chance of success","成功的几率为50% c","成功的几率为50%"),
            ("Lose % of health","损失% of生命","损失%生命"),("Increase % defense","增加% d防御","增加%防御"),
            ("Increase % speed","提高% s速度","提高%速度"),("50%chance","有50%c几率","有50%几率")})
            Check("cached partial-word detected without guessing suffix: "+item.Item1,EmbeddedTextContinuity.HasFormatArtifact(item.Item1,item.Item2)&&EmbeddedTextContinuity.NormalizeAutomatic(item.Item1,item.Item2)==item.Item2);
        Check("literal units survive repair",EmbeddedTextContinuity.NormalizeAutomatic("Resistance is 50% C","抗性为50% C")=="抗性为50% C");
        Check("Latin translations never lose letters",EmbeddedTextContinuity.NormalizeAutomatic("50% chance","50% chance")=="50% chance");
        Check("real format and percent prose coexist",EmbeddedTranslationText.Protect("Bonus %d at 50% chance.").Codes.SequenceEqual(new[]{"%d"}));
        Check("existing real format stays protected",EmbeddedTextContinuity.NormalizeAutomatic("Value % d.","数值% d。")=="数值% d。");
        Check("real format next to Chinese stays protected",RpgTranslationText.ValidCachedPair("Deals %d damage.","造成%d点伤害。",EmbeddedTextSyntax.Generic));
        Check("printf arguments adjacent to units stay protected",EmbeddedTranslationText.Protect("%dpts %sName %.2fkg").Codes.SequenceEqual(new[]{"%d","%s","%.2f"}));
        string prose="This long description continues across the original\nEnglish layout lines so that the translated text may\nflow normally inside its own available text area without losing any information.";
        string wrapped="这是一段\n连续的说明，\n应该在自己的\n文字区域正常\n显示。";
        Check("cached authored line breaks remain unchanged",EmbeddedTextContinuity.NormalizeAutomatic(prose,wrapped)==wrapped);
        Check("paragraph separator preserved",EmbeddedTextContinuity.NormalizeAutomatic(prose+"\n\n"+prose,wrapped+"\n\n"+wrapped).Split("\n\n").Length==2);
        Check("lists retain their rows",EmbeddedTextContinuity.NormalizeAutomatic("- First selectable option in the menu\n- Second selectable option in the menu\n- Third selectable option in the menu", "- 第一项\n- 第二项\n- 第三项")=="- 第一项\n- 第二项\n- 第三项");
        Check("short authored rows preserved",EmbeddedTextContinuity.NormalizeAutomatic("North\nSouth\nEast\nWest","北\n南\n东\n西")=="北\n南\n东\n西");
        string list="These changes apply to all of the following statistics:\n+{0} chance for a positive change in this statistic\n+{1} chance for another positive change in this statistic\n{2} chance for a negative change in this statistic";
        string listText="以下属性生效：\n+{0}正面变化\n+{1}其他变化\n{2}负面变化";
        Check("numeric variable lists retain independent rows",EmbeddedTextContinuity.NormalizeAutomatic(list,listText)==listText);
        var raw=new RpgTranslationCache.Loaded("fixture.json",new(){[prose]=wrapped},false);
        var repaired=RpgTranslationCache.Normalize(raw,EmbeddedTextSyntax.Generic);
        Check("loading cache does not rewrite authored layout",!repaired.NeedsSave&&ReferenceEquals(raw,repaired)&&repaired.Entries[prose]==wrapped);
        Check("repair is idempotent",!RpgTranslationCache.Normalize(repaired with {NeedsSave=false},EmbeddedTextSyntax.Generic).NeedsSave);
        Check("other syntaxes retain their cache",ReferenceEquals(raw,RpgTranslationCache.Normalize(raw,EmbeddedTextSyntax.Renpy))&&ReferenceEquals(raw,RpgTranslationCache.Normalize(raw,EmbeddedTextSyntax.Rpg)));
        var manual=new TranslationWorkspaceStore.Document{Manual=new(){["50% chance"]="我的译文：50% c几率"}};
        Check("human correction stays authoritative",TranslationWorkspaceStore.Manual(manual,"50% chance",EmbeddedTextSyntax.Generic)=="我的译文：50% c几率");
        var settings=new ApiSettings{TargetLanguage="zh-CN"};
        Check("damaged suffix requests a fresh translation",!AutomaticTranslationQuality.Valid("Gain 10% for 2 turns","增加10% f或2回合",settings));
        Check("clean percentage cache remains reusable",AutomaticTranslationQuality.Valid("Gain 10% for 2 turns","增加10%，持续2回合",settings));
        Check("other parsers do not acquire the generic cache migration",AutomaticTranslationQuality.Valid("50% chance","50% c几率",settings,syntax:EmbeddedTextSyntax.Renpy)&&AutomaticTranslationQuality.Valid("50% chance","50% c几率",settings,syntax:EmbeddedTextSyntax.Rpg));
        var translated=RpgTranslationBatch.Translate(["A 50% chance."],settings,settings,async (text,_,_)=>{await Task.Yield();Check("real queue sends the complete percent word",text=="A 50% chance.");return "有50%的几率。";},CancellationToken.None,syntax:EmbeddedTextSyntax.Generic).GetAwaiter().GetResult();
        Check("actual queue returns clean percent text",translated.GetValueOrDefault("A 50% chance.")=="有50%的几率。");
        var multi=RpgTranslationBatch.Translate(["Hello\nWorld","Other\nText"],settings,settings,(text,_,_)=>Task.FromResult("{\"t0\":\"你好⟦RPG0⟧世界\",\"t1\":\"其他文字\"}"),CancellationToken.None,syntax:EmbeddedTextSyntax.Generic).GetAwaiter().GetResult();
        Check("batch retains valid rows and retries only damaged structure",multi.Count==1&&multi["Hello\nWorld"]=="你好\n世界");
        object? migration=null;
        if(cacheFile is not null)
        {
            // Explicit offline input only. The source files are never modified.
            var original=RpgTranslationCache.Read(cacheFile)??throw new InvalidDataException("缓存输入无效。");
            var normalized=new Dictionary<string,string>(RpgTranslationCache.Normalize(new(cacheFile,original,false),EmbeddedTextSyntax.Generic).Entries,StringComparer.Ordinal);
            var changed=original.Where(p=>normalized[p.Key]!=p.Value).Select(p=>new{source=p.Key,before=p.Value,after=normalized[p.Key]}).ToArray();
            var damaged=original.Where(p=>EmbeddedTextContinuity.HasFormatArtifact(p.Key,p.Value)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
            foreach(string source in damaged.Keys)normalized.Remove(source);
            int accepted=0,rejected=0,existing=0;
            if(legacyList is not null)foreach(string file in JsonSerializer.Deserialize<string[]>(File.ReadAllText(legacyList))!)
            {
                if(new FileInfo(file).Length>64*1024*1024)throw new InvalidDataException("旧缓存输入过大。");
                var utf8=new UTF8Encoding(false,true);
                foreach(string line in File.ReadLines(file))
                {
                    if(string.IsNullOrWhiteSpace(line))continue;
                    string[] columns=line.Split('\t');if(columns.Length!=2){rejected++;continue;}
                    try
                    {
                        string source=utf8.GetString(Convert.FromBase64String(columns[0])),value=utf8.GetString(Convert.FromBase64String(columns[1]));
                        if(normalized.ContainsKey(source)){existing++;continue;}
                        value=EmbeddedTextContinuity.NormalizeAutomatic(source,value);
                        if(!TranslationWorkspaceStore.Valid(source,value,EmbeddedTextSyntax.Generic)||!AutomaticTranslationQuality.Valid(source,value,settings)){rejected++;continue;}
                        normalized.Add(source,value);accepted++;
                    }
                    catch(FormatException){rejected++;}catch(DecoderFallbackException){rejected++;}
                }
            }
            string normalizedPath=output+".normalized-cache.json";
            File.WriteAllText(normalizedPath,JsonSerializer.Serialize(normalized,new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(output+".cache-changes.json",JsonSerializer.Serialize(changed,new JsonSerializerOptions{WriteIndented=true}));
            var pending=damaged.Keys.Where(source=>!normalized.ContainsKey(source)).ToArray();
            File.WriteAllText(output+".damaged-cache.json",JsonSerializer.Serialize(damaged,new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(output+".pending-retranslation.json",JsonSerializer.Serialize(pending,new JsonSerializerOptions{WriteIndented=true}));
            migration=new{originalEntries=original.Count,changedEntries=changed.Length,damagedEntries=damaged.Count,recoveredDamaged=damaged.Count-pending.Length,pendingRetranslation=pending.Length,legacyAccepted=accepted,legacyRejected=rejected,legacyExisting=existing,finalEntries=normalized.Count,normalizedPath};
            Check("every undamaged cache key retained",original.Keys.Except(damaged.Keys).All(normalized.ContainsKey));
            Check("normalized real cache is idempotent",!RpgTranslationCache.Normalize(new("",normalized,false),EmbeddedTextSyntax.Generic).NeedsSave);
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{failed,checks,migration,providerRequests=0},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:1;
    }
}
