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
    // Shared by resource preparation and runtime lookup. A bare named token is
    // data, not an instruction to evaluate game expressions. Preserve syntax,
    // exclude formatting/escaped brackets, and never infer a player's value.
    internal static class DisplayTemplateTokens
    {
        const string SquareControls="b|i|u|s|color|font|font_size|size|scale|scalex|scaley|tag|offsetx|offsety|spacing|opacity|url|img|center|right|left|fill|table|cell|wave|shake|tornado|rainbow|pulse|fade|outline_size|outline_color|bgcolor|fgcolor|dropcap|p|br|lb|rb|quote|code|list|ul|ol|li|tr|td|th|sub|sup|strike";
        const string Square=@"(?<![\[\\])\[(?!(?i:"+SquareControls+@")\])[A-Za-z_][A-Za-z_0-9]{0,63}\](?!\])";
        static readonly Regex Token=new Regex(@"\{[A-Za-z_0-9][A-Za-z_0-9]*\}|:[A-Za-z_][A-Za-z_0-9]*:|<(?!COLOR>|STYLE>|SPRITE>|SPACE>|FONT>|SIZE>|ALPHA>|LINK>)[A-Z][A-Z0-9_]{2,}>|"+Square);
        internal static Match[] Matches(string source)
        {
            if(string.IsNullOrEmpty(source)||source.Length>24000)return new Match[0];
            var matches=Token.Matches(source);var result=new Match[matches.Count];matches.CopyTo(result,0);return result;
        }
        internal static bool IsSquareVariable(string code)
        {
            if(string.IsNullOrEmpty(code)||code[0]!='[')return false;
            var matches=Matches(code);return matches.Length==1&&matches[0].Index==0&&matches[0].Length==code.Length;
        }
    }
    // Exact template matching only; ambiguous templates and damaged placeholders
    // remain live-translation candidates. No game names, paths or scripts involved.
    internal sealed class TextLookup
    {
        sealed class Template { internal string Source;internal string[] Parts,Tokens; }
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
            var tokens=DisplayTemplateTokens.Matches(source);int tokenLength=0;foreach(var token in tokens)tokenLength+=token.Length;
            if(tokens.Length>0&&tokens.Length<=8&&source.Length-tokenLength>=12)
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
                    bucket[source]=entry.Template=new Template{Source=source,Parts=parts.ToArray(),Tokens=names.ToArray()};
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
        static bool ValidTemplateText(Template candidate,string text)
        {
            if(string.IsNullOrEmpty(text)||text.Length>24000)return false;
            var expected=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var token in candidate.Tokens){int count;expected.TryGetValue(token,out count);expected[token]=count+1;}
            foreach(var token in DisplayTemplateTokens.Matches(text))
            {
                int count;if(!expected.TryGetValue(token.Value,out count)||count==0)return false;expected[token.Value]=count-1;
            }
            foreach(var count in expected.Values)if(count!=0)return false;
            return true;
        }
        static string Substitute(string text,Dictionary<string,string> values)
        {
            // Replace token spans once. A value containing another token's
            // spelling must remain a value, not become a second substitution.
            var result=new StringBuilder();int at=0;
            foreach(var token in DisplayTemplateTokens.Matches(text))
            {result.Append(text,at,token.Index-at);result.Append(values[token.Value]);at=token.Index+token.Length;}
            result.Append(text,at,text.Length-at);return result.ToString();
        }
        internal bool TryGetTemplate(string source,Func<string,string> read,out string text)
        {return TryGetTemplate(source,read,null,out text);}
        bool TryGetTemplate(string source,Func<string,string> read,TextLookup fallback,out string text)
        {
            text=null;if(string.IsNullOrEmpty(source)||source.Length>6000||read==null)return false;
            var candidates=new Dictionary<string,Template>(StringComparer.Ordinal);
            foreach(var index in fallback==null?new[]{this}:new[]{this,fallback})
            {
                for(int n=0;n<=Math.Min(4,source.Length);n++)
                {
                    Dictionary<string,Template> bucket;if(!index.templates.TryGetValue(source.Substring(0,n),out bucket))continue;
                    foreach(var candidate in bucket.Values)
                    {
                        if(candidates.ContainsKey(candidate.Source))continue;
                        if(candidates.Count>=256)return false;candidates.Add(candidate.Source,candidate);
                    }
                }
            }
            foreach(var candidate in candidates.Values)
            {
                var values=MatchTemplate(candidate,source);if(values==null)continue;
                string current=read(candidate.Source);if(!ValidTemplateText(candidate,current))continue;
                string result=Substitute(current,values);
                if(text!=null&&text!=result){text=null;return false;}text=result;
            }
            // Resolver results are deliberately not memoized: profiles, manual
            // corrections and terminology can change independently of this index.
            return text!=null;
        }
        internal bool TryGet(Dictionary<string,string> cache,string source,out string text)
        {text=Lookup(cache,source,0,null,null);return text!=null;}
        internal bool TryGet(Dictionary<string,string> cache,string source,out string text,TextLookup fallback,Dictionary<string,string> fallbackCache)
        {text=Lookup(cache,source,0,fallback,fallbackCache);return text!=null;}
        string Lookup(Dictionary<string,string> cache,string source,int depth,TextLookup fallback,Dictionary<string,string> fallbackCache)
        {
            string text;if(cache.TryGetValue(source,out text)||fallbackCache!=null&&fallbackCache.TryGetValue(source,out text))return text;
            // An overlay's authority can change in either dictionary. It must
            // not reuse a memo created for another fallback/profile context.
            if(fallback==null&&memo.TryGetValue(source,out text))return text;
            if(depth>8)return null;
            var wrapper=Wrapper.Match(source);
            if(wrapper.Success&&wrapper.Groups[3].Value.IndexOf("</"+wrapper.Groups[2].Value+">",StringComparison.Ordinal)<0)
            {
                string body=Lookup(cache,wrapper.Groups[3].Value,depth+1,fallback,fallbackCache);
                if(body!=null)text=source.Substring(0,wrapper.Groups[3].Index)+body+source.Substring(wrapper.Groups[3].Index+wrapper.Groups[3].Length);
            }
            string trimmed=source.Trim();
            if(text==null&&trimmed!=source&&trimmed.Length>0)
            {string body=Lookup(cache,trimmed,depth+1,fallback,fallbackCache);if(body!=null){int at=source.IndexOf(trimmed,StringComparison.Ordinal);text=source.Substring(0,at)+body+source.Substring(at+trimmed.Length);}}
            if(text==null)
            {
                string key=PlainKey(source);if(key!=null&&!whitespace.TryGetValue(key,out text)&&fallback!=null)fallback.whitespace.TryGetValue(key,out text);
            }
            if(text==null)
            {
                TryGetTemplate(source,key=>{string value;return cache.TryGetValue(key,out value)||fallbackCache!=null&&fallbackCache.TryGetValue(key,out value)?value:null;},fallback,out text);
            }
            if(fallback==null){if(memo.Count>=4096)memo.Clear();memo[source]=text;}return text;
        }
    }
}
