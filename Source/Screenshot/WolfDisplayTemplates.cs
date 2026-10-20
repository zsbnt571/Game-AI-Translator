using System.Text;

namespace ScreenshotTranslationUiTester;

// The game expands WOLF variables before its display hook. Reuse only the same
// conservative, literal-anchored grammar as WolfRenderIndex.h. Derived values
// are lookup results, never new automatic translations or variable assignments.
internal sealed class WolfDisplayTemplates
{
    internal readonly record struct Value(string Text,bool Manual=false);
    private sealed record Segment(string Text,string Command);
    private sealed record Parts(string[] Literals,string[] Variables);
    private sealed record Rule(Parts Source,Parts Target,string Key)
    {
        internal string? Match(string input)
        {
            var literals=Source.Literals;
            if(Source.Variables.Length==0)return input==literals[0]?Target.Literals[0]:null;
            if(!input.StartsWith(literals[0],StringComparison.Ordinal))return null;
            int at=literals[0].Length;var values=new Dictionary<string,string>(StringComparer.Ordinal);
            for(int n=0;n<Source.Variables.Length;n++)
            {
                string next=literals[n+1];int end;
                if(n+1==Source.Variables.Length)
                {
                    if(!input.EndsWith(next,StringComparison.Ordinal))return null;
                    end=input.Length-next.Length;
                }
                else
                {
                    if(next.Length==0)return null;
                    end=input.IndexOf(next,at,StringComparison.Ordinal);
                    if(end<0||input.IndexOf(next,end+1,StringComparison.Ordinal)>=0)return null;
                }
                if(end<at)return null;
                string value=input[at..end];
                if(Encoding.UTF8.GetByteCount(value)>1024)return null;
                if(values.TryGetValue(Source.Variables[n],out string? old)&&old!=value)return null;
                values[Source.Variables[n]]=value;at=end+next.Length;
            }
            if(at!=input.Length)return null;
            var output=new StringBuilder(Target.Literals[0]);
            for(int n=0;n<Target.Variables.Length;n++)
            {
                if(!values.TryGetValue(Target.Variables[n],out string? value))return null;
                output.Append(value).Append(Target.Literals[n+1]);
            }
            return output.Length is >0 and <=12000?output.ToString():null;
        }
    }
    private sealed class Owner(string source,string[] keys)
    {
        internal readonly string Source=source;
        internal readonly string[] Keys=keys;
        internal string Text="";
        internal Rule[] Rules=[];
        internal long TargetCost;
    }
    private readonly object gate=new();
    private readonly Dictionary<string,Owner> owners=new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<Owner>> buckets=new(StringComparer.Ordinal);
    private readonly Dictionary<string,HashSet<string>> clocks=new(StringComparer.Ordinal);
    private long bytes;
    private static string Trim(string s)=>s.Replace("\r\n","\n",StringComparison.Ordinal).Trim(' ','\r','\n','\t');
    private static string Anchor(string text,bool suffix)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(text);int count=Math.Min(4,bytes.Length);
        return (suffix?"$":"^")+Convert.ToHexString(bytes.AsSpan(suffix?bytes.Length-count:0,count));
    }
    private static List<Segment>? Segments(string raw)
    {
        var result=new List<Segment>();var run=new StringBuilder();
        for(int i=0;i<raw.Length;)
        {
            if(raw[i]!='\\'){run.Append(raw[i++]);continue;}
            if(i+1<raw.Length&&raw[i+1] is 'E' or 'N')
            {result.Add(new(Trim(run.ToString()),raw.Substring(i,2)));run.Clear();i+=2;continue;}
            int end=i+1;while(end<raw.Length&&raw[end] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')end++;
            if(end==i+1||end>=raw.Length||raw[end]!='[')return null;
            string name=raw[(i+1)..end];int number=++end;
            while(end<raw.Length&&raw[end] is >= '0' and <= '9')end++;
            if(end==number||end-number>9||end>=raw.Length||raw[end]!=']')return null;
            string code=raw[i..(end+1)];i=end+1;
            if(name is "c" or "f" or "i")
            {result.Add(new(Trim(run.ToString()),code));run.Clear();}
            else if(name is "s" or "v" or "self" or "cself")run.Append(code);
            else return null;
            if(result.Count>256)return null;
        }
        result.Add(new(Trim(run.ToString()),""));return result.Count<=257?result:null;
    }
    private static Parts? Parse(string text)
    {
        var literals=new List<string>();var variables=new List<string>();int start=0;
        for(int i=0;i<text.Length;i++)if(text[i]=='\\')
        {
            int end=text.IndexOf(']',i);if(end<0)return null;
            literals.Add(text[start..i]);variables.Add(text[i..(end+1)]);i=end;start=end+1;
        }
        literals.Add(text[start..]);return variables.Count<=4?new(literals.ToArray(),variables.ToArray()):null;
    }
    private static Rule[] Compile(string source,string text)
    {
        if(WolfLanguageBranches.IsTemplate(source))
        {
            var alternatives=new List<Rule>();
            foreach(var pair in WolfLanguageBranches.Pairs(source,text))
            {
                if(pair.Key.Contains('\\'))alternatives.AddRange(Compile(pair.Key,pair.Value));
                else alternatives.Add(new(new([pair.Key],[]),new([pair.Value],[]),"="+pair.Key));
            }
            return alternatives.ToArray();
        }
        if(!source.Contains('\\'))return [];
        var a=Segments(source);var b=Segments(text);
        if(a is null||b is null||a.Count!=b.Count||a.Count>32)return [];
        var rules=new List<Rule>();
        for(int n=0;n<a.Count;n++)
        {
            if(a[n].Command!=b[n].Command)return [];
            if(a[n].Text.Length==0&&b[n].Text.Length==0)continue;
            var x=Parse(a[n].Text);var y=Parse(b[n].Text);
            if(x is null||y is null||!x.Variables.Order(StringComparer.Ordinal).SequenceEqual(y.Variables.Order(StringComparer.Ordinal)))return [];
            string key;
            if(x.Variables.Length==0)
            {
                if(x.Literals[0].Length==0||y.Literals[0].Length==0)return [];
                key="="+x.Literals[0];
            }
            else
            {
                if(x.Literals.Sum(s=>Encoding.UTF8.GetByteCount(s))<8)continue;
                if(Encoding.UTF8.GetByteCount(x.Literals[0])>=4)key=Anchor(x.Literals[0],false);
                else if(Encoding.UTF8.GetByteCount(x.Literals[^1])>=4)key=Anchor(x.Literals[^1],true);
                else continue;
                if(x.Literals.Skip(1).SkipLast(1).Any(s=>s.Length==0))continue;
            }
            rules.Add(new(x,y,key));
        }
        return rules.ToArray();
    }
    internal void Register(string source)
    {
        if(ClockDisplayTemplate.TrySource(Trim(source),out var clock))
        {
            lock(gate)
            {
                if(!clocks.TryGetValue(clock.Key,out var owners))clocks[clock.Key]=owners=new(StringComparer.Ordinal);
                if(owners.Count<4096)owners.Add(source);
            }
            return;
        }
        if(source.Length is <1 or >6000||(!source.Contains('\\')&&!WolfLanguageBranches.IsTemplate(source))||source.Contains('\0'))return;
        lock(gate)
        {
            if(owners.ContainsKey(source))return;
            var rules=Compile(source,source);if(rules.Length==0)return;
            string[] keys=rules.Select(r=>r.Key).Distinct(StringComparer.Ordinal).ToArray();
            long cost=512+Encoding.UTF8.GetByteCount(source)*3L+rules.Length*256L;
            if(owners.Count>=100000||bytes+cost>32*1024*1024)return;
            bytes+=cost;var owner=new Owner(source,keys);owners[source]=owner;
            foreach(string key in keys)
            {if(!buckets.TryGetValue(key,out var bucket))buckets[key]=bucket=[];bucket.Add(owner);}
        }
    }
    internal string Find(string source,Func<string,Value?> resolve)
    {
        string display=Trim(source);
        if(display.Length is <1 or >6000||display.Contains('\\')||display.Contains('\0'))return "";
        if(ClockDisplayTemplate.TrySource(display,out var clock))
        {
            string[] clockOwners;lock(gate)clockOwners=clocks.TryGetValue(clock.Key,out var stored)?stored.ToArray():[];
            ClockDisplayTemplate.Target? automaticClock=null,manualClock=null;bool automaticClash=false,manualClash=false;
            foreach(string owner in clockOwners)
            {
                var value=resolve(owner);
                if(value is null||!ClockDisplayTemplate.TrySource(Trim(owner),out var original)||!ClockDisplayTemplate.TryTarget(original,Trim(value.Value.Text),out var target))continue;
                if(value.Value.Manual){manualClash|=manualClock is {} old&&old!=target;manualClock=target;}
                else {automaticClash|=automaticClock is {} old&&old!=target;automaticClock=target;}
            }
            if(manualClock is {} chosen)return manualClash?"":chosen.Render(clock.Time);
            return !automaticClash&&automaticClock is {} ready?ready.Render(clock.Time):"";
        }
        Owner[] candidates;
        lock(gate)
        {
            candidates=new[]{"="+display,Anchor(display,false),Anchor(display,true)}
                .Where(buckets.ContainsKey).SelectMany(k=>buckets[k]).Distinct().Take(65).ToArray();
        }
        if(candidates.Length>64)return "";
        string automatic="",manual="";bool automaticConflict=false,manualConflict=false;int examined=0;
        foreach(var owner in candidates)
        {
            var value=resolve(owner.Source);if(value is null)continue;
            Rule[] rules;
            lock(gate)
            {
                if(owner.Text!=value.Value.Text)
                {
                    var compiled=Compile(owner.Source,value.Value.Text);
                    long cost=value.Value.Text.Length*2L+compiled.Sum(r=>256L+
                        r.Source.Literals.Sum(s=>s.Length*2L+32)+r.Source.Variables.Sum(s=>s.Length*2L+32)+
                        r.Target.Literals.Sum(s=>s.Length*2L+32)+r.Target.Variables.Sum(s=>s.Length*2L+32));
                    bytes-=owner.TargetCost;owner.Text="";owner.Rules=[];owner.TargetCost=0;
                    if(bytes+cost<=32*1024*1024)
                    {bytes+=cost;owner.TargetCost=cost;owner.Text=value.Value.Text;owner.Rules=compiled;}
                }
                rules=owner.Rules;
            }
            foreach(var rule in rules)
            {
                if(++examined>64)return "";
                string? answer=rule.Match(display);if(string.IsNullOrEmpty(answer))continue;
                if(value.Value.Manual){if(manual.Length>0&&manual!=answer)manualConflict=true;manual=answer;}
                else{if(automatic.Length>0&&automatic!=answer)automaticConflict=true;automatic=answer;}
            }
        }
        if(manual.Length>0)return manualConflict?"":manual;
        return automaticConflict?"":automatic;
    }
}
