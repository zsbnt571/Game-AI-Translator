using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Optional metadata from engine structures, never from screen/poll order or
// guessed character names. The ordinary text catalogue remains authoritative.
internal sealed class StaticDialogueContexts
{
    private readonly Dictionary<string,GameDialogueContext?> candidates=new(StringComparer.Ordinal);
    private readonly Dictionary<string,GameCharacterBinding?> characterCandidates=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> actorNames=new(StringComparer.Ordinal);
    private readonly List<(string Text,string Speaker)> previous=[];
    internal void Boundary()=>previous.Clear();
    // A graph/database entry establishes its own actor, not traversal order.
    // Keep these identities available for manual choices, without carrying a
    // preceding node's self-statement across branches or unrelated resources.
    internal void IsolatedCharacter(string text,string id,string displayName)
    {Boundary();Line(text,id,id,displayName,catalogOnly:true);Boundary();}
    internal void Line(string text,string speaker,string characterId="",string displayName="",bool catalogOnly=false)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>600||text.Any(char.IsControl)||speaker.Length==0)
        {Boundary();return;}
        GameDialogueContext? context=null;
        if(previous.Count>0&&!AutomaticGenderAgreement.BlocksInheritedEvidence(text))
        {
            var actors=previous.Select(p=>p.Speaker).Append(speaker).Distinct(StringComparer.Ordinal).ToArray();
            if(actors.Length<=4)
            {
                string Id(string actor)=>"r"+Array.IndexOf(actors,actor);
                context=GameDialogueContext.Create(previous.Select(p=>p.Text),new(Id(speaker),previous.Select(p=>Id(p.Speaker)).ToArray()));
            }
        }
        if(candidates.ContainsKey(text))candidates[text]=null;
        else if(candidates.Count<50000)candidates.Add(text,context);
        if(GameCharacterBinding.ValidId(characterId))
        {
            var facts=AutomaticGenderAgreement.BlocksInheritedEvidence(text)?Enumerable.Empty<string>():previous.Where(p=>p.Speaker==speaker).Select(p=>p.Text);
            var binding=new GameCharacterBinding(characterId,displayName.Length>0?displayName:characterId,"",GameCharacterBinding.DetectGender(facts.Append(text)),catalogOnly);
            if(characterCandidates.ContainsKey(text))characterCandidates[text]=null;
            else if(characterCandidates.Count<50000)characterCandidates.Add(text,binding);
        }
        previous.Add((text,speaker));if(previous.Count>3)previous.RemoveAt(0);
    }
    internal IReadOnlyDictionary<string,GameCharacterBinding> FinishCharacters(IReadOnlyList<CatalogTextOrigin> origins,bool truncated,IReadOnlySet<string> texts)
    {
        if(truncated)return new Dictionary<string,GameCharacterBinding>();
        string Pair(CatalogTextOrigin o)=>string.Join("\0",o.File,o.Container,o.Location.Split('/')[0],o.Context.Split("|role=",StringSplitOptions.None)[0],o.Text);
        var displays=origins.Where(o=>o.Context.StartsWith(UnityNaninovelText.ReaderIdentity+"|",StringComparison.Ordinal)
            &&o.Context.Contains("|role=display-line|",StringComparison.Ordinal)).Select(Pair).ToHashSet(StringComparer.Ordinal);
        var occurrences=origins.Where(o=>!(o.Context.StartsWith(UnityNaninovelText.ReaderIdentity+"|",StringComparison.Ordinal)
            &&o.Context.EndsWith("|role=mapped-part",StringComparison.Ordinal)&&displays.Contains(Pair(o))))
            .GroupBy(o=>o.Text,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.DistinctBy(CatalogOrigins.Identity).Take(2).ToArray(),StringComparer.Ordinal);
        var result=new Dictionary<string,GameCharacterBinding>(StringComparer.Ordinal);
        foreach(var pair in characterCandidates)
            if(pair.Value is {} binding&&texts.Contains(pair.Key)&&occurrences.TryGetValue(pair.Key,out var at)&&at.Length==1)
            {
                var row=binding with{Scope=GameCharacterBinding.Hash(CatalogOrigins.Identity(at[0])+"\0"+at[0].ContentHash+"\0"+binding.CharacterId+"\0"+binding.AutomaticGender)};
                if(row.Valid())result.Add(pair.Key,row);
            }
        return result;
    }
    internal IReadOnlyDictionary<string,GameDialogueContext> Finish(IReadOnlyList<CatalogTextOrigin> origins,bool truncated,IReadOnlySet<string> texts)
    {
        if(truncated)return new Dictionary<string,GameDialogueContext>();
        // Naninovel emits both a map-part and the assembled display command.
        // Suppress only that proved paired representation, not another command,
        // ID, asset, script, locale or ordinary text field with equal wording.
        string Pair(CatalogTextOrigin o)=>string.Join("\0",o.File,o.Container,o.Location.Split('/')[0],o.Context.Split("|role=",StringSplitOptions.None)[0],o.Text);
        var displays=origins.Where(o=>o.Context.StartsWith(UnityNaninovelText.ReaderIdentity+"|",StringComparison.Ordinal)
            &&o.Context.Contains("|role=display-line|",StringComparison.Ordinal)).Select(Pair).ToHashSet(StringComparer.Ordinal);
        var occurrences=origins.Where(o=>!(o.Context.StartsWith(UnityNaninovelText.ReaderIdentity+"|",StringComparison.Ordinal)
            &&o.Context.EndsWith("|role=mapped-part",StringComparison.Ordinal)&&displays.Contains(Pair(o))));
        var counts=occurrences.GroupBy(o=>o.Text,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.Select(CatalogOrigins.Identity).Distinct(StringComparer.Ordinal).Take(2).Count(),StringComparer.Ordinal);
        return candidates.Where(p=>p.Value?.Roles is not null&&texts.Contains(p.Key)&&counts.GetValueOrDefault(p.Key)==1)
            .ToDictionary(p=>p.Key,p=>p.Value!,StringComparer.Ordinal);
    }
    internal void Naninovel(IEnumerable<UnityNaninovelText.Source> sources)
    {
        Boundary();int previousCommand=-2;
        foreach(var source in sources.Where(s=>s.CommandIndex>=0))
        {
            if(source.CommandIndex!=previousCommand+1||source.Role!="display-line")Boundary();
            Line(source.Text,source.Role=="display-line"?source.SpeakerId:"",source.Role=="display-line"?"naninovel:"+source.SpeakerId:"",source.SpeakerId);previousCommand=source.CommandIndex;
        }
        Boundary();
    }
    internal void NaninovelSource(string body,Action check)
    {
        // The author prefix is the Naninovel actor ID, not its display name.
        // Do not carry metadata across commands, labels or inferred ordering.
        foreach(string raw in body.Replace("\r\n","\n").Split('\n'))
        {
            check();var line=Regex.Match(raw.Trim(),@"^(?<actor>[A-Za-z_][A-Za-z_0-9]{0,63}):\s*(?<text>.+)$",RegexOptions.CultureInvariant);
            if(!line.Success)continue;string text=line.Groups["text"].Value;
            if(text.IndexOfAny(['[',']','{','}'])>=0)continue;
            string actor=line.Groups["actor"].Value;
            IsolatedCharacter(text,"naninovel:"+actor,actor);
        }
    }
    internal void VisualNovelMaker(JsonElement root,Action check)
    {
        // A native ShowMessage command binds a literal character UUID to its
        // own message. Narrator (0), expressions and partial/parallel messages
        // have no reliable actor binding. Command traversal is not play order.
        void Walk(JsonElement node,int depth)
        {
            check();if(depth>48)return;
            if(node.ValueKind==JsonValueKind.Array){foreach(var item in node.EnumerateArray())Walk(item,depth+1);return;}
            if(node.ValueKind!=JsonValueKind.Object)return;
            if(node.TryGetProperty("id",out var command)&&command.ValueKind==JsonValueKind.String&&command.GetString()=="gs.ShowMessage")
            {
                if(node.TryGetProperty("params",out var args)&&args.ValueKind==JsonValueKind.Object
                    &&node.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==node.EnumerateObject().Count()
                    &&args.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==args.EnumerateObject().Count()
                    &&args.TryGetProperty("characterId",out var character)&&character.ValueKind==JsonValueKind.String
                    &&Regex.IsMatch(character.GetString()!,@"^[A-Za-z0-9]{32,64}$",RegexOptions.CultureInvariant)
                    &&args.TryGetProperty("message",out var message)&&message.ValueKind==JsonValueKind.Object
                    &&message.TryGetProperty("defaultText",out var value)&&value.ValueKind==JsonValueKind.String
                    &&message.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==message.EnumerateObject().Count()
                    &&(!args.TryGetProperty("expressions",out var expressions)||expressions.ValueKind==JsonValueKind.Array&&expressions.GetArrayLength()==0)
                    &&(!args.TryGetProperty("partial",out var partial)||partial.ValueKind==JsonValueKind.Number&&partial.TryGetInt32(out int part)&&part==0))
                {
                    string text=value.GetString()!;
                    if(text.IndexOfAny(['{','}','[',']','\\'])<0)
                        IsolatedCharacter(text,"vnmaker:"+character.GetString(),character.GetString()!);
                }
                return;
            }
            foreach(var property in node.EnumerateObject())
                if(property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)Walk(property.Value,depth+1);
        }
        Walk(root,0);Boundary();
    }
    // MZ's name-box escape identifies an actor by its engine ID. A literal name,
    // portrait index, MV plugin convention or variable expression does not.
    internal void RpgMakerFile(JsonElement root,string file,Action check)
    {
        if(file.Equals("Actors.json",StringComparison.OrdinalIgnoreCase)&&root.ValueKind==JsonValueKind.Array)
            foreach(var actor in root.EnumerateArray())
            {
                check();if(actor.ValueKind==JsonValueKind.Object&&actor.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.Number&&id.TryGetInt32(out int number)&&number>0
                    &&actor.TryGetProperty("name",out var name)&&name.ValueKind==JsonValueKind.String&&name.GetString() is {Length:>0 and <=200} label&&!label.Any(char.IsControl))actorNames["actor:"+number]=label;
            }
        void List(JsonElement owner)
        {check();if(owner.ValueKind==JsonValueKind.Object&&owner.TryGetProperty("list",out var list)&&list.ValueKind==JsonValueKind.Array)RpgMakerMessages(list,check);}
        void Pages(JsonElement owner)
        {check();if(owner.ValueKind==JsonValueKind.Object&&owner.TryGetProperty("pages",out var pages)&&pages.ValueKind==JsonValueKind.Array)foreach(var page in pages.EnumerateArray())List(page);}
        if(Regex.IsMatch(file,@"^Map[0-9]+\.json$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase)
            &&root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("events",out var events)&&events.ValueKind==JsonValueKind.Array)
            foreach(var item in events.EnumerateArray())Pages(item);
        else if(file.Equals("CommonEvents.json",StringComparison.OrdinalIgnoreCase)&&root.ValueKind==JsonValueKind.Array)
            foreach(var item in root.EnumerateArray())List(item);
        else if(file.Equals("Troops.json",StringComparison.OrdinalIgnoreCase)&&root.ValueKind==JsonValueKind.Array)
            foreach(var item in root.EnumerateArray())Pages(item);
        Boundary();
    }
    internal void RpgMakerMessages(JsonElement list,Action check)
    {
        Boundary();string speaker="";var lines=new List<string>();
        void Flush(){if(lines.Count==1)Line(lines[0],speaker,speaker.Length>0?"mz:"+speaker:"",actorNames.GetValueOrDefault(speaker,speaker));else if(lines.Count>0)Boundary();lines.Clear();}
        foreach(var command in list.EnumerateArray())
        {
            check();
            if(command.ValueKind!=JsonValueKind.Object||!command.TryGetProperty("code",out var code)||code.ValueKind!=JsonValueKind.Number||!code.TryGetInt32(out int id)
                ||!command.TryGetProperty("parameters",out var args)||args.ValueKind!=JsonValueKind.Array)
            {Flush();Boundary();speaker="";continue;}
            if(id==401&&args.GetArrayLength()==1&&args[0].ValueKind==JsonValueKind.String)
            {if(lines.Count<2)lines.Add(args[0].GetString()!);continue;}
            Flush();
            if(id==101&&args.GetArrayLength()==5&&args[4].ValueKind==JsonValueKind.String)
            {
                string name=args[4].GetString()!;
                var actor=Regex.Match(name,@"^\\N\[([1-9][0-9]{0,5})\]$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
                speaker=actor.Success?"actor:"+actor.Groups[1].Value:"";
                if(speaker.Length==0)Boundary();
            }
            else{Boundary();speaker="";}
        }
        Flush();Boundary();
    }
    // Tyrano #id[:face] compiles to chara_ptext(name=id). Admit only a locally
    // declared literal character ID; labels, macros, conditions, script blocks,
    // inline tags and unsupported commands break the straight-line evidence.
    internal void Tyrano(string content,Action check)
    {
        Boundary();string speaker="",block="";bool comment=false;
        var declared=new HashSet<string>(StringComparer.Ordinal);var duplicate=new HashSet<string>(StringComparer.Ordinal);var names=new Dictionary<string,string>(StringComparer.Ordinal);
        string[] lines=content.Replace("\r\n","\n").Split('\n');
        const string identifier=@"[A-Za-z_][A-Za-z_0-9]{0,63}";
        foreach(string raw in lines)
        {
            check();string line=raw.Trim();
            if(block.Length>0){if(line=="["+block+"]"||line=="@"+block)block="";continue;}
            if(comment){if(line=="*/")comment=false;continue;}
            if(line=="/*"){Boundary();speaker="";comment=true;continue;}
            if(line.Length==0||line[0]==';')continue;
            var isolated=Regex.Match(line,@"^(?:\[|@)(iscript|html|macro)(?:\s|\]|$)",RegexOptions.CultureInvariant);
            if(isolated.Success)
            {Boundary();speaker="";block=isolated.Groups[1].Value switch{"iscript"=>"endscript","html"=>"endhtml",_=>"endmacro"};continue;}
            string? declaration=TyranoDeclaration(line,out string displayName);
            if(declaration is not null)
            {
                string id=declaration;
                if(!declared.Add(id))duplicate.Add(id);
                names[id]=displayName.Length>0?displayName:id;
                Boundary();speaker="";continue;
            }
            if(line[0]=='#')
            {
                var actor=Regex.Match(line,@"^#(?<id>"+identifier+@")(?:[:](?:"+identifier+@"))?$",RegexOptions.CultureInvariant);
                speaker=actor.Success&&declared.Contains(actor.Groups["id"].Value)&&!duplicate.Contains(actor.Groups["id"].Value)?actor.Groups["id"].Value:"";
                if(speaker.Length==0)Boundary();continue;
            }
            if(line is "[l]" or "[p]" or "[r]" or "[cm]" or "@l" or "@p" or "@r" or "@cm")continue;
            if(line[0] is '*' or '@'||line.Contains('[')||line.Contains('\\')){Boundary();speaker="";continue;}
            if(line[0]=='_')line=line[1..];
            Line(line,speaker,speaker.Length>0?"tyrano:"+speaker:"",names.GetValueOrDefault(speaker,speaker));
        }
        Boundary();
    }
    private static string? TyranoDeclaration(string line,out string displayName)
    {
        displayName="";
        string body=line.StartsWith("[chara_new ",StringComparison.Ordinal)&&line.EndsWith(']')?line[10..^1]:
            line.StartsWith("@chara_new ",StringComparison.Ordinal)?line[10..]:"";
        if(body.Length==0)return null;
        var attributes=new Dictionary<string,string>(StringComparer.Ordinal);
        while(body.Length>0)
        {
            var field=Regex.Match(body,@"\G\s*(?<key>[A-Za-z_][A-Za-z_0-9]*)=(?:""(?<value>[^""\\\r\n]*)""|'(?<value>[^'\\\r\n]*)'|(?<value>[^\s\[\]""'\\]+))(?:\s+|$)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
            // Regex.Match starts at zero; consume each complete literal field.
            if(!field.Success||field.Index!=0)return null;
            string value=field.Groups["value"].Value;
            if(value.StartsWith('&')||value.StartsWith('%')||!attributes.TryAdd(field.Groups["key"].Value,value))return null;
            body=body[field.Length..];
        }
        if(attributes.TryGetValue("jname",out var label)&&label.Length<=200&&!label.Any(char.IsControl))displayName=label;
        return !attributes.ContainsKey("cond")&&attributes.TryGetValue("name",out var name)&&Regex.IsMatch(name,@"^[A-Za-z_][A-Za-z_0-9]{0,63}$",RegexOptions.CultureInvariant)?name:null;
    }
}
