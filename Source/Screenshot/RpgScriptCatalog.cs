using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Bounded static string flow, NOT a JavaScript evaluator. Only literal strings,
// numbered RPG variables and a small set of pure string operations are read.
// Values enter the catalogue only when they reach a message display boundary.
internal sealed class RpgScriptCatalog(Action check,Action limit)
{
    private sealed record Token(string Value,char Kind='p');
    private sealed record Op(string Name,int A=0,int? B=null,string Text="",string Replacement="");
    private sealed record Expr(int Variable,string? Literal,Op[] Operations);
    private readonly Dictionary<int,List<Expr>> definitions=[];
    private readonly List<Expr> displays=[];
    private int items;
    private void Count(){check();if(++items>200000)limit();}
    private static bool Id(Token t)=>t.Kind=='i';
    private static bool Integer(List<Token> ts,ref int p,out int number)
    {
        number=0;bool negative=p<ts.Count&&ts[p].Value=="-";if(negative)p++;
        if(p>=ts.Count||ts[p].Kind!='n'||!int.TryParse(ts[p++].Value,NumberStyles.None,CultureInfo.InvariantCulture,out number))return false;
        if(negative)number=-number;return true;
    }
    private static bool Eat(List<Token> ts,ref int p,string value)
    {if(p<ts.Count&&ts[p].Value==value&&ts[p].Kind!='s'){p++;return true;}return false;}
    private static bool String(List<Token> ts,ref int p,out string value)
    {value="";if(p>=ts.Count||ts[p].Kind!='s')return false;value=ts[p++].Value;return true;}
    private static Expr? Expression(List<Token> ts,ref int p,Dictionary<string,Expr> locals,int depth=0)
    {
        if(depth>12||p>=ts.Count)return null;Expr? result=null;
        if(String(ts,ref p,out var text))result=new(0,text,[]);
        else if(Eat(ts,ref p,"(")){result=Expression(ts,ref p,locals,depth+1);if(!Eat(ts,ref p,")"))return null;}
        else if(Eat(ts,ref p,"$gameVariables"))
        {
            if(!Eat(ts,ref p,".")||!Eat(ts,ref p,"value")||!Eat(ts,ref p,"(")||!Integer(ts,ref p,out int id)||id<=0||!Eat(ts,ref p,")"))return null;
            result=new(id,null,[]);
        }
        else if(Id(ts[p])){locals.TryGetValue(ts[p++].Value,out result);}
        if(result is null)return null;
        while(p<ts.Count&&ts[p].Value==".")
        {
            p++;if(p>=ts.Count||!Id(ts[p]))return null;string name=ts[p++].Value;
            if(!Eat(ts,ref p,"("))return null;Op op;
            if(name=="trim")op=new(name);
            else if(name=="replace")
            {
                if(!String(ts,ref p,out var find)||!Eat(ts,ref p,",")||!String(ts,ref p,out var replacement)||replacement.Contains('$'))return null;
                op=new(name,Text:find,Replacement:replacement);
            }
            else if(name is "slice" or "substring" or "substr")
            {
                if(!Integer(ts,ref p,out int a))return null;int? b=null;
                if(Eat(ts,ref p,",")){if(!Integer(ts,ref p,out int second))return null;b=second;}
                op=new(name,a,b);
            }
            else return null;
            if(!Eat(ts,ref p,")")||result.Operations.Length>=12)return null;
            result=result with{Operations=[..result.Operations,op]};
        }
        if(Eat(ts,ref p,"+"))
        {
            var right=Expression(ts,ref p,locals,depth+1);if(right is null)return null;
            // Exactly one variable per expression; no speculative cross-products.
            if(right.Variable==0)result=result with{Operations=[..result.Operations,new("append",Text:Transform(right.Literal!,right.Operations))]};
            else if(result.Variable==0)result=right with{Operations=[..right.Operations,new("prepend",Text:Transform(result.Literal!,result.Operations))]};
            else return null;
        }
        return result;
    }
    private static string Transform(string value,Op[] ops)
    {
        foreach(var op in ops)
        {
            int n=value.Length;
            int Relative(int i)=>i<0?(int)Math.Clamp((long)n+i,0,n):Math.Min(n,i);
            int a,b;
            switch(op.Name)
            {
                case "slice":a=Relative(op.A);b=op.B.HasValue?Relative(op.B.Value):n;value=value.Substring(a,Math.Max(0,b-a));break;
                case "substring":a=Math.Clamp(op.A,0,n);b=op.B.HasValue?Math.Clamp(op.B.Value,0,n):n;value=value.Substring(Math.Min(a,b),Math.Abs(b-a));break;
                case "substr":a=Relative(op.A);b=op.B.HasValue?Math.Clamp(op.B.Value,0,n-a):n-a;value=value.Substring(a,b);break;
                case "trim":value=value.Trim();break;
                case "append":value+=op.Text;break;
                case "prepend":value=op.Text+value;break;
                case "replace":int at=value.IndexOf(op.Text,StringComparison.Ordinal);if(at>=0)value=value[..at]+op.Replacement+value[(at+op.Text.Length)..];break;
            }
            if(value.Length>6000)return "";
        }
        return value;
    }
    private void Define(int id,Expr value)
    {
        if(id<=0||id>1000000||value.Variable==id)return; // self-updates are runtime state, not recursive catalogue sources
        Count();if(!definitions.TryGetValue(id,out var rows))definitions[id]=rows=[];rows.Add(value);
    }
    internal void Variable(int id,string expression)
    {
        var ts=Lex(expression);int p=0;var value=Expression(ts,ref p,[]);
        if(value is not null&&p==ts.Count)Define(id,value);
    }
    internal void Display(string text)
    {
        text=Regex.Replace(text,"<wordwrap>","",RegexOptions.IgnoreCase);
        var matches=Regex.Matches(text,@"(?<!\\)\\[vV]\[(\d+)\]");
        if(matches.Count!=1)return;
        var match=matches[0];if(!int.TryParse(match.Groups[1].Value,out int id)||id<=0)return;
        Count();displays.Add(new(id,null,[new("prepend",Text:text[..match.Index]),new("append",Text:text[(match.Index+match.Length)..])]));
    }
    internal void Script(string script)
    {
        var ts=Lex(script);var locals=new Dictionary<string,Expr>(StringComparer.Ordinal);
        bool Boundary(int p)=>p==ts.Count||ts[p].Value is ";" or "}";
        for(int i=0;i<ts.Count;i++)
        {
            if((i&63)==0)check();int p=i;
            if(Eat(ts,ref p,"$gameVariables")&&Eat(ts,ref p,".")&&Eat(ts,ref p,"setValue")&&Eat(ts,ref p,"(")
                &&Integer(ts,ref p,out int id)&&Eat(ts,ref p,","))
            {
                var value=Expression(ts,ref p,locals);
                if(value is not null&&Eat(ts,ref p,")")&&Boundary(p)){Define(id,value);i=p-1;}
                continue;
            }
            p=i;
            if(Eat(ts,ref p,"$gameMessage")&&Eat(ts,ref p,".")&&Eat(ts,ref p,"add")&&Eat(ts,ref p,"("))
            {
                var value=Expression(ts,ref p,locals);
                if(value is not null&&Eat(ts,ref p,")")&&Boundary(p)){Count();displays.Add(value);i=p-1;}
                continue;
            }
            p=i;if(ts[p].Value is "var" or "let" or "const")p++;
            if(p<ts.Count&&Id(ts[p]))
            {
                string name=ts[p++].Value;
                if(Eat(ts,ref p,"=")&&(p==ts.Count||ts[p].Value!="="))
                {
                    locals.Remove(name);var value=Expression(ts,ref p,locals);
                    if(value is not null&&Boundary(p)){locals[name]=value;i=p-1;}
                }
            }
        }
    }
    internal void Emit(Action<string> add)
    {
        IEnumerable<string> Resolve(Expr expr,HashSet<int> path,int depth)
        {
            Count();if(depth>12)yield break;
            if(expr.Variable==0){yield return Transform(expr.Literal!,expr.Operations);yield break;}
            if(!path.Add(expr.Variable))yield break;
            if(definitions.TryGetValue(expr.Variable,out var values))
                foreach(var source in values)foreach(var text in Resolve(source,path,depth+1))yield return Transform(text,expr.Operations);
            path.Remove(expr.Variable);
        }
        foreach(var expr in displays.DistinctBy(e=>System.Text.Json.JsonSerializer.Serialize(e)))
            foreach(var text in Resolve(expr,[],0))add(text);
    }
    private List<Token> Lex(string script)
    {
        if(script.Length>262144){limit();return [];}
        var result=new List<Token>();int p=0;
        while(p<script.Length)
        {
            if((p&255)==0)check();char c=script[p++];
            if(char.IsWhiteSpace(c))continue;
            if(c=='/'&&p<script.Length&&script[p]=='/')
            {while(p<script.Length&&script[p]!='\n')p++;continue;}
            if(c=='/'&&p<script.Length&&script[p]=='*')
            {int end=script.IndexOf("*/",p+1,StringComparison.Ordinal);if(end<0)break;p=end+2;continue;}
            if(c=='/')return []; // division/regexp syntax is outside this literal reader
            if(c is '\'' or '"')
            {
                var value=new StringBuilder();bool valid=false;
                while(p<script.Length)
                {
                    char v=script[p++];if(v==c){valid=true;break;}
                    if(v is '\r' or '\n')break;
                    if(v=='\\')
                    {
                        if(p>=script.Length)break;v=script[p++];
                        if(v=='\r'){if(p<script.Length&&script[p]=='\n')p++;continue;}
                        if(v=='\n')continue;
                        if(v is 'x' or 'u')
                        {
                            int n=v=='x'?2:4;if(p+n>script.Length||!ushort.TryParse(script.AsSpan(p,n),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var code))break;
                            v=(char)code;p+=n;
                        }
                        else if(v is >= '0' and <= '9'){valid=false;break;} // legacy octal is not inferred
                        else v=v switch{'n'=>'\n','r'=>'\r','t'=>'\t','b'=>'\b','f'=>'\f','v'=>'\v',_=>v};
                    }
                    value.Append(v);
                }
                if(!valid)return [];result.Add(new(value.ToString(),'s'));continue;
            }
            if(c=='`')return []; // template interpolation requires runtime evaluation
            if(char.IsLetter(c)||c is '_' or '$')
            {int begin=p-1;while(p<script.Length&&(char.IsLetterOrDigit(script[p])||script[p] is '_' or '$'))p++;result.Add(new(script[begin..p],'i'));}
            else if(char.IsDigit(c))
            {int begin=p-1;while(p<script.Length&&char.IsDigit(script[p]))p++;result.Add(new(script[begin..p],'n'));}
            else result.Add(new(c.ToString()));
            if(result.Count>65536){limit();return [];}
        }
        return result;
    }
}
