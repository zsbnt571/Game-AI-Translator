using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Fusion.UnityEmbedded
{
    // Typewriters keep a complete line in a fully transparent trailing color.
    // RGB is irrelevant at alpha zero; preserve every other formatting envelope.
    // No prefix guesses, arbitrary game getters or substring cache matches.
    internal static class CompleteRevealText
    {
        static readonly Regex Hidden=new Regex(@"<color\s*=\s*[""']?#[0-9a-f]{6}00[""']?\s*>",RegexOptions.IgnoreCase);
        static readonly Regex Tail=new Regex(@"<color\s*=\s*[""']?#[0-9a-f]{6}00[""']?\s*>([^<>]*)</color\s*>\s*$",RegexOptions.IgnoreCase);
        internal static bool TryGet(string frame,out string complete)
        {
            complete=frame;if(string.IsNullOrEmpty(frame)||frame.Length>6000)return false;
            var match=Tail.Match(frame);if(!match.Success)return false;
            string prefix=frame.Substring(0,match.Index);
            // A second hidden envelope is not the single-suffix writer format.
            if(Hidden.IsMatch(prefix))return false;
            complete=(prefix+match.Groups[1].Value).Replace("\r\n","\n").Trim();
            return complete.Length>=2;
        }
    }
    // Exact template matching only; ambiguous templates and damaged placeholders
    // remain live-translation candidates. No game names, paths or scripts involved.
    internal sealed class TextLookup
    {
        sealed class Template { internal string[] Parts,Tokens;internal string Text; }
        sealed class Entry {internal string Key,Text,Prefix;internal Template Template;}
        readonly Dictionary<string,Dictionary<string,Template>> templates=new Dictionary<string,Dictionary<string,Template>>(StringComparer.Ordinal);
        readonly Dictionary<string,Entry> indexed=new Dictionary<string,Entry>(StringComparer.Ordinal);
        readonly Dictionary<string,Dictionary<string,int>> variants=new Dictionary<string,Dictionary<string,int>>(StringComparer.Ordinal);
        readonly Dictionary<string,string> memo=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly Dictionary<string,string> whitespace=new Dictionary<string,string>(StringComparer.Ordinal);
        internal long UpdateCount{get;private set;}
        static readonly Regex Space=new Regex(@"\s+");
        static string PlainKey(string source)
        {
            // Layout wrapping may change between serialized and visible text.
            // Formatting, variables and control syntax cannot be normalized.
            if(source.Length<24||source.Length>6000||source.IndexOfAny(new[]{'<','>','{','}','[',']','\\','\0'})>=0)return null;
            return Space.Replace(source.Trim()," ");
        }
        static readonly Regex Token=new Regex(@"\{[A-Za-z_0-9][A-Za-z_0-9]*\}|:[A-Za-z_][A-Za-z_0-9]*:|<(?!COLOR>|STYLE>|SPRITE>|SPACE>|FONT>|SIZE>|ALPHA>|LINK>)[A-Z][A-Z0-9_]{2,}>");
        static readonly Regex Wrapper=new Regex(@"^(\s*)<([A-Za-z]+)(?:=[^>\r\n]*)?>(.*)</\2>(\s*)$",RegexOptions.Singleline);
        internal void Rebuild(Dictionary<string,string> cache)
        {
            Clear();foreach(var pair in cache)Update(pair.Key,pair.Value);
        }
        internal void Clear(){templates.Clear();memo.Clear();whitespace.Clear();variants.Clear();indexed.Clear();}
        internal bool Update(string source,string text)
        {
            Entry previous;
            if(indexed.TryGetValue(source,out previous))
            {
                if(previous.Text==text)return false;
                if(previous.Key!=null)ChangeVariant(previous.Key,previous.Text,-1);
                if(previous.Template!=null){var bucket=templates[previous.Prefix];bucket.Remove(source);if(bucket.Count==0)templates.Remove(previous.Prefix);}
            }
            // A live batch changes at most 128 entries. Keep its cost proportional
            // to those changes, rather than rebuilding every cached alias on the
            // game thread. Corrections revoke memoized and ambiguous aliases too.
            memo.Clear();UpdateCount++;
            var entry=new Entry{Key=PlainKey(source),Text=text};indexed[source]=entry;
            if(entry.Key!=null)ChangeVariant(entry.Key,text,1);
            var tokens=Token.Matches(source);
            if(tokens.Count>0&&tokens.Count<=8&&Token.Replace(source,"").Length>=12)
            {
                var parts=new List<string>();var names=new List<string>();int cursor=0;bool valid=true;
                foreach(Match token in tokens)
                {
                    if(Count(text,token.Value)!=Count(source,token.Value)){valid=false;break;}
                    parts.Add(source.Substring(cursor,token.Index-cursor));names.Add(token.Value);cursor=token.Index+token.Length;
                }
                if(valid)
                {
                    parts.Add(source.Substring(cursor));entry.Prefix=source.Substring(0,Math.Min(4,tokens[0].Index));
                    Dictionary<string,Template> bucket;
                    if(!templates.TryGetValue(entry.Prefix,out bucket))templates[entry.Prefix]=bucket=new Dictionary<string,Template>(StringComparer.Ordinal);
                    bucket[source]=entry.Template=new Template{Parts=parts.ToArray(),Tokens=names.ToArray(),Text=text};
                }
            }
            return true;
        }
        void ChangeVariant(string key,string text,int change)
        {
            Dictionary<string,int> values;
            if(!variants.TryGetValue(key,out values))variants[key]=values=new Dictionary<string,int>(StringComparer.Ordinal);
            int count;values.TryGetValue(text,out count);count+=change;
            if(count<=0)values.Remove(text);else values[text]=count;
            if(values.Count==0){variants.Remove(key);whitespace.Remove(key);}
            else if(values.Count==1){foreach(var value in values){whitespace[key]=value.Key;break;}}
            else whitespace[key]=null;
        }
        static int Count(string value,string token){int count=0,at=0;while((at=value.IndexOf(token,at,StringComparison.Ordinal))>=0){count++;at+=token.Length;}return count;}
        static Dictionary<string,string> MatchTemplate(Template candidate,string source)
        {
            if(!source.StartsWith(candidate.Parts[0],StringComparison.Ordinal))return null;
            int position=candidate.Parts[0].Length;var values=new Dictionary<string,string>(StringComparer.Ordinal);
            for(int i=0;i<candidate.Tokens.Length;i++)
            {
                string delimiter=candidate.Parts[i+1];bool last=i==candidate.Tokens.Length-1;
                if(delimiter.Length==0&&!last)return null;
                int end=last?(source.EndsWith(delimiter,StringComparison.Ordinal)?source.Length-delimiter.Length:-1):source.IndexOf(delimiter,position,StringComparison.Ordinal);
                if(end<=position||end-position>120)return null;
                if(!last){int second=source.IndexOf(delimiter,end+1,StringComparison.Ordinal);if(second>=0&&second-position<=120)return null;}
                string part=source.Substring(position,end-position),prior,token=candidate.Tokens[i];
                if(part.IndexOfAny(new[]{'\r','\n','<','>','[',']','{','}'})>=0||values.TryGetValue(token,out prior)&&prior!=part)return null;
                values[token]=part;position=end+delimiter.Length;
            }
            return position==source.Length?values:null;
        }
        internal bool TryGet(Dictionary<string,string> cache,string source,out string text){text=Lookup(cache,source,0);return text!=null;}
        string Lookup(Dictionary<string,string> cache,string source,int depth)
        {
            string text;if(cache.TryGetValue(source,out text)||memo.TryGetValue(source,out text))return text;
            if(depth>8)return null;
            var wrapper=Wrapper.Match(source);
            if(wrapper.Success&&wrapper.Groups[3].Value.IndexOf("</"+wrapper.Groups[2].Value+">",StringComparison.Ordinal)<0)
            {
                string body=Lookup(cache,wrapper.Groups[3].Value,depth+1);
                if(body!=null)text=source.Substring(0,wrapper.Groups[3].Index)+body+source.Substring(wrapper.Groups[3].Index+wrapper.Groups[3].Length);
            }
            string trimmed=source.Trim();
            if(text==null&&trimmed!=source&&trimmed.Length>0)
            {string body=Lookup(cache,trimmed,depth+1);if(body!=null){int at=source.IndexOf(trimmed,StringComparison.Ordinal);text=source.Substring(0,at)+body+source.Substring(at+trimmed.Length);}}
            if(text==null)
            {
                string key=PlainKey(source);if(key!=null)whitespace.TryGetValue(key,out text);
            }
            if(text==null)
            {
                var candidates=new List<Template>();bool bounded=true;
                for(int n=0;n<=Math.Min(4,source.Length);n++)
                {
                    Dictionary<string,Template> bucket;if(!templates.TryGetValue(source.Substring(0,n),out bucket))continue;
                    if(candidates.Count+bucket.Count>256){bounded=false;break;}candidates.AddRange(bucket.Values);
                }
                if(bounded)foreach(var candidate in candidates)
                {
                    var values=MatchTemplate(candidate,source);if(values==null)continue;
                    string result=candidate.Text;foreach(var value in values)result=result.Replace(value.Key,value.Value);
                    if(text!=null&&text!=result){text=null;break;}text=result;
                }
            }
            if(memo.Count>=4096)memo.Clear();memo[source]=text;return text;
        }
    }
}
