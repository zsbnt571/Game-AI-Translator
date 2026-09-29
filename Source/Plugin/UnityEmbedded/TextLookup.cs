using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Fusion.UnityEmbedded
{
    // Exact template matching only; ambiguous templates and damaged placeholders
    // remain live-translation candidates. No game names, paths or scripts involved.
    internal sealed class TextLookup
    {
        sealed class Template { internal string[] Parts,Tokens;internal string Text; }
        readonly Dictionary<string,List<Template>> templates=new Dictionary<string,List<Template>>(StringComparer.Ordinal);
        readonly Dictionary<string,string> memo=new Dictionary<string,string>(StringComparer.Ordinal);
        static readonly Regex Token=new Regex(@"\{[A-Za-z_0-9][A-Za-z_0-9]*\}|:[A-Za-z_][A-Za-z_0-9]*:");
        static readonly Regex Wrapper=new Regex(@"^(\s*)<([A-Za-z]+)(?:=[^>\r\n]*)?>(.*)</\2>(\s*)$",RegexOptions.Singleline);
        internal void Rebuild(Dictionary<string,string> cache)
        {
            templates.Clear();memo.Clear();
            foreach(var pair in cache)
            {
                var tokens=Token.Matches(pair.Key);if(tokens.Count==0||tokens.Count>8||Token.Replace(pair.Key,"").Length<12)continue;
                var parts=new List<string>();var names=new List<string>();int cursor=0;bool valid=true;
                foreach(Match token in tokens)
                {
                    if(Count(pair.Value,token.Value)!=Count(pair.Key,token.Value)){valid=false;break;}
                    parts.Add(pair.Key.Substring(cursor,token.Index-cursor));names.Add(token.Value);cursor=token.Index+token.Length;
                }
                if(!valid)continue;parts.Add(pair.Key.Substring(cursor));
                string prefix=pair.Key.Substring(0,Math.Min(4,tokens[0].Index));List<Template> bucket;
                if(!templates.TryGetValue(prefix,out bucket))templates[prefix]=bucket=new List<Template>();
                bucket.Add(new Template{Parts=parts.ToArray(),Tokens=names.ToArray(),Text=pair.Value});
            }
        }
        static int Count(string value,string token){return value.Split(new[]{token},StringSplitOptions.None).Length-1;}
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
                var candidates=new List<Template>();for(int n=0;n<=Math.Min(4,source.Length);n++){List<Template> bucket;if(templates.TryGetValue(source.Substring(0,n),out bucket))candidates.AddRange(bucket);}
                if(candidates.Count<=256)foreach(var candidate in candidates)
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
