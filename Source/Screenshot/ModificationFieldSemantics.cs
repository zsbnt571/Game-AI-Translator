using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Exact field vocabulary only. These names suggest a category, never prove
// that a database record belongs to the active party or that a value unlocks CG.
internal static class ModificationFieldSemantics
{
    // These are a fixed set of schema patterns. Retain their regex instances:
    // the global Regex cache holds only 15 by default and this classification
    // pass uses more, otherwise re-parsing each pattern for every database cell.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Pattern,RegexOptions Options),Regex> Patterns=new();
    private static Regex Pattern(string pattern,RegexOptions options=RegexOptions.None)=>Patterns.GetOrAdd((pattern,options),key=>new(key.Pattern,key.Options));
    private static bool Match(string value,string pattern,RegexOptions options=RegexOptions.None)=>Pattern(pattern,options).IsMatch(value);
    private static string Replace(string value,string pattern,string replacement)=>Pattern(pattern).Replace(value,replacement);
    internal sealed record Attribute(string Caption,string Group,string GroupTitle,int Order);
    private static readonly string[][] Combat=[
        ["攻击力","attack","atk","attack power","攻撃力","攻击力","攻擊力"],
        ["防御力","defense","defence","def","防御力","防御","防禦力"],
        ["魔法攻击","magic attack","magic attack power","mat","魔法攻撃力","魔法攻击力"],
        ["魔法防御","magic defense","magic defence","mdf","魔法防御力"],
        ["精神攻击","精神攻撃","精神攻击"],
        ["精神防御","精神防御"],
        ["敏捷","agility","agi","敏捷性","敏捷","素早さ"],
        ["命中","accuracy","hit rate","命中","命中率"],
        ["回避","evasion","evade rate","回避","回避率"],
        ["会心","critical","critical rate","クリティカル","クリティカル率","会心率"]
    ];

    internal static string Normalize(string value) =>
        Replace(value.Normalize(System.Text.NormalizationForm.FormKC),@"([a-z])([A-Z])","$1 $2")
            .Replace('_',' ').Replace('.',' ').ToLowerInvariant().Trim();

    internal static Attribute? Character(string field)
    {
        field=Normalize(field);
        bool percentage=Match(field,@"\s*(?:\[%\]|\(%\)|%)$");
        if(Match(field,@"^(?:レベル|等级|等級|level|lv)$"))return new("等级","basic","基础属性",0);
        if(Match(field,@"^(?:限界レベル|最大レベル|等级上限|等級上限|max(?:imum)? level|level (?:max(?:imum)?|limit))$"))return new("等级上限","basic","基础属性",1);
        if(Match(field,@"^(?:(?:player|current|hero|mc)\s+)?(?:hp|health|hit points)$|^(?:体力|生命值|生命)$"))return new("生命 / 体力","resources","生命与能量",10);
        if(Match(field,@"^(?:(?:max(?:imum)?|最大|上限)\s*(?:hp|health|hit points|体力|生命值|生命)|(?:hp|health|hit points|体力|生命值|生命)\s*(?:max(?:imum)?|上限|最大))$"))return new("生命 / 体力上限","resources","生命与能量",11);
        if(Match(field,@"^(?:(?:player|current|hero|mc)\s+)?(?:mp|mana|magic points)$|^(?:魔力|魔法值)$"))return new("魔力","resources","生命与能量",20);
        if(Match(field,@"^(?:(?:max(?:imum)?|最大|上限)\s*(?:mp|mana|magic points|魔力|魔法值)|(?:mp|mana|magic points|魔力|魔法值)\s*(?:max(?:imum)?|上限|最大))$"))return new("魔力上限","resources","生命与能量",21);
        // SP has different meanings between games. Keep the actual abbreviation
        // rather than assert that it is mana, stamina or skill points.
        if(Match(field,@"^(?:current\s+)?sp$"))return new("SP","resources","生命与能量",22);
        if(Match(field,@"^(?:(?:max(?:imum)?|最大|上限)\s*sp|sp\s*(?:max(?:imum)?|上限|最大))$"))return new("SP 上限","resources","生命与能量",23);
        if(Match(field,@"^(?:(?:player|current|hero|mc)\s+)?(?:stamina|energy)$|^(?:精力|気力|活力)$"))return new("精力","resources","生命与能量",30);
        if(Match(field,@"^(?:(?:max(?:imum)?|最大|上限)\s*(?:stamina|energy|精力|気力|活力)|(?:stamina|energy|精力|気力|活力)\s*(?:max(?:imum)?|上限|最大))$"))return new("精力上限","resources","生命与能量",31);
        // Percent annotations are supported for the combat-rate fields below.
        // Do not quietly turn HP[%] into an absolute hit-point editor.
        if(percentage)field=Replace(field,@"\s*(?:\[%\]|\(%\)|%)$","").TrimEnd();
        bool basic=Match(field,@"^(?:基本|基础|基礎|base\s+)");
        string term=Replace(field,@"^(?:基本|基础|基礎|base\s+)","");
        for(int i=0;i<Combat.Length;i++)
            if(Combat[i].Skip(1).Contains(term,StringComparer.Ordinal))
                return new((basic?"基础":"")+Combat[i][0]+(percentage?"（%）":""),"combat","战斗属性",40+i);
        return null;
    }

    internal static string Recollection(string field,string[] path)
    {
        field=Normalize(field);
        if(Match(field,@"(?:index|number|count|mode|file|path|resource|image|picture|list|current|trigger)|番号|枚数|モード|画像|ファイル|パス|リスト|切替|当前|索引|模式|路径|资源|数量",RegexOptions.IgnoreCase))return "";
        if(Match(field,@"^(?:回想登録|回想登记|回想登記|recollection registered|replay registered)$"))return "回想登记值";
        if(Match(field,@"^(?:(?:cg|回想|ギャラリー|图库|圖庫|replay|recollection|gallery)\s*(?:解放|開放|開放済み|解放済み|解锁|解鎖|unlocked|unlock)|(?:unlocked|unlock)\s+(?:cg|replay|recollection|gallery))$"))return "CG / 回想解锁值";
        if(Match(field,@"^(?:(?:cg|回想|ギャラリー|图库|圖庫|replay|recollection|gallery)\s*(?:閲覧済み|既読|既読済み|已看|已读|seen|viewed|read)|(?:seen|viewed|read)\s+(?:cg|replay|recollection|gallery))$"))return "CG / 回想查看值";
        bool collection=path.Length>=3&&Match(Normalize(path[0]),@"^(?:cg|cg gallery|gallery|replay|recollection|回想|回想一覧|回想集|cg回想|ギャラリー|图库|圖庫)$");
        if(collection&&Match(field,@"^(?:unlocked|unlock|解放|開放|解锁|解鎖)$"))return "CG / 回想解锁值";
        if(collection&&Match(field,@"^(?:seen|viewed|read|閲覧済み|既読|已看|已读)$"))return "CG / 回想查看值";
        return "";
    }
}
