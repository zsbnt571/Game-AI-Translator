using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal enum RenpyNumericComparison { Equal, Increased, Decreased, Unchanged, Changed }

// Read-only candidate narrowing over the engine's exposed data. No memory scan,
// writes, translated-name keys or game-specific variable rules belong here.
internal sealed class RenpyNumericFinder
{
    internal readonly record struct Number(string Kind,long Integer,double Floating)
    {
        internal bool EqualsValue(Number other)
        {
            if(Kind==other.Kind)return Kind=="int"?Integer==other.Integer:Floating==other.Floating;
            long integer=Kind=="int"?Integer:other.Integer;
            double floating=Kind=="float"?Floating:other.Floating;
            // Avoid rounding a large integer to double before comparing it.
            return floating>=long.MinValue&&floating<9223372036854775808d&&Math.Truncate(floating)==floating&&(long)floating==integer;
        }
        internal int CompareSameKind(Number other)=>Kind=="int"?Integer.CompareTo(other.Integer):Floating.CompareTo(other.Floating);
    }

    private readonly Dictionary<string,Number> latest=new(StringComparer.Ordinal);
    private Dictionary<string,Number> candidates=new(StringComparer.Ordinal);
    private object? connection;
    private int? generation;
    internal bool Active {get;private set;}
    internal bool MainMenu {get;private set;}
    internal bool Truncated {get;private set;}
    internal int InitialCount {get;private set;}
    internal int Count=>candidates.Count;
    internal int Round {get;private set;}

    internal static bool TryNumber(JsonElement row,out Number value)
    {
        value=default;
        if(!row.TryGetProperty("kind",out var kind)||!row.TryGetProperty("value",out var raw)||raw.ValueKind!=JsonValueKind.Number)return false;
        if(kind.GetString()=="int"&&raw.TryGetInt64(out long integer)){value=new("int",integer,0);return true;}
        if(kind.GetString()=="float"&&raw.TryGetDouble(out double floating)&&double.IsFinite(floating)){value=new("float",0,floating);return true;}
        return false;
    }
    internal static bool TryQuery(string text,out Number value)
    {
        value=default;text=text.Trim();if(text.Length>128)return false;
        if(long.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out long integer)){value=new("int",integer,0);return true;}
        // Classify the decimal spelling before conversion to binary floating
        // point. In particular 9007199254740993.0 must never become ...992.
        var match=Regex.Match(text,@"^([+-]?)(\d+(?:\.\d*)?|\.\d+)(?:[eE]([+-]?\d+))?$");
        if(!match.Success)return false;
        int exponent=0;if(match.Groups[3].Success&&!int.TryParse(match.Groups[3].Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out exponent))return false;
        string mantissa=match.Groups[2].Value;int dot=mantissa.IndexOf('.');
        long scale=(long)exponent-(dot<0?0:mantissa.Length-dot-1);
        string digits=mantissa.Replace(".","").TrimStart('0');
        if(digits.Length==0){value=new("int",0,0);return true;}
        int last=digits.Length;while(last>0&&digits[last-1]=='0'){last--;scale++;}digits=digits[..last];
        if(scale>=0)
        {
            if(digits.Length+scale>19)return false;
            string exact=match.Groups[1].Value+digits+new string('0',(int)scale);
            if(!long.TryParse(exact,NumberStyles.Integer,CultureInfo.InvariantCulture,out integer))return false;
            value=new("int",integer,0);return true;
        }
        if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out double floating)||!double.IsFinite(floating)||Math.Truncate(floating)==floating)return false;
        value=new("float",0,floating);return true;
    }
    internal string Observe(object currentConnection,int currentGeneration,bool mainMenu,bool truncated,IEnumerable<JsonElement> rows)
    {
        string reason="";
        if(connection is not null&&(!ReferenceEquals(connection,currentConnection)||generation!=currentGeneration))
        {if(Active)reason="连接或游戏进度已变化，请重新开始数值查找。";Reset();}
        if(mainMenu&&Active){reason="已回到主菜单，请进入游戏后重新查找。";Reset();}
        connection=currentConnection;generation=currentGeneration;MainMenu=mainMenu;Truncated=truncated;
        latest.Clear();var duplicates=new HashSet<string>(StringComparer.Ordinal);
        if(!mainMenu)foreach(var row in rows)
        {
            if(!TryNumber(row,out var value)||!row.TryGetProperty("name",out var name)||name.GetString() is not {Length:>0} path||duplicates.Contains(path))continue;
            if(!latest.TryAdd(path,value)){latest.Remove(path);duplicates.Add(path);}
        }
        return reason;
    }
    internal void Reset(){Active=false;candidates.Clear();InitialCount=0;Round=0;}
    internal void ClearSession(){Reset();latest.Clear();connection=null;generation=null;MainMenu=false;Truncated=false;}
    internal void Begin(Number? value)
    {
        if(connection is null||MainMenu)throw new InvalidOperationException("请先进入游戏并刷新数据。");
        candidates=latest.Where(p=>value is null||p.Value.EqualsValue(value.Value)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.Ordinal);
        InitialCount=candidates.Count;Round=0;Active=true;
    }
    internal void Narrow(RenpyNumericComparison comparison,Number? value)
    {
        if(!Active)throw new InvalidOperationException("请先开始数值查找。");
        if(comparison==RenpyNumericComparison.Equal&&value is null)throw new InvalidOperationException("请填写要查找的当前数值。");
        var next=new Dictionary<string,Number>(StringComparer.Ordinal);
        foreach(var old in candidates)
        {
            if(!latest.TryGetValue(old.Key,out var current)||current.Kind!=old.Value.Kind)continue;
            int delta=current.CompareSameKind(old.Value);
            bool include=comparison switch
            {
                RenpyNumericComparison.Equal=>current.EqualsValue(value!.Value),
                RenpyNumericComparison.Increased=>delta>0,
                RenpyNumericComparison.Decreased=>delta<0,
                RenpyNumericComparison.Unchanged=>delta==0,
                RenpyNumericComparison.Changed=>delta!=0,
                _=>throw new ArgumentOutOfRangeException(nameof(comparison))
            };
            if(include)next.Add(old.Key,current);
        }
        candidates=next;Round++;
    }
    internal bool Includes(JsonElement row)=>!Active||(row.TryGetProperty("name",out var name)&&name.GetString() is {} path&&
        candidates.TryGetValue(path,out var candidate)&&TryNumber(row,out var current)&&candidate.Kind==current.Kind);
}
