using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Display metadata only. Keys are the exact engine paths, never executable expressions.
internal sealed class RenpyDataPresentation
{
    public Dictionary<string,string> Notes {get;set;}=new(StringComparer.Ordinal);
    public HashSet<string> Favorites {get;set;}=new(StringComparer.Ordinal);
    internal static string FilePath(string exe)=>Path.Combine(RenpyGameAdapter.Storage(exe),"modification-view.json");
    internal static RenpyDataPresentation Load(string exe)
    {
        foreach(string path in new[]{FilePath(exe),FilePath(exe)+".previous"})
            try {if(File.Exists(path))return JsonSerializer.Deserialize<RenpyDataPresentation>(File.ReadAllText(path))??new();}catch(JsonException){}catch(IOException){}
        return new();
    }
    internal void Save(string exe)=>PortableDataStorage.WriteJson(FilePath(exe),this);
    internal static string Readable(string path)
    {
        string text=Regex.Replace(path,@"^persistent\.","");
        text=Regex.Replace(text,@"([a-z0-9])([A-Z])","$1 $2");
        text=Regex.Replace(text,@"([A-Z])([A-Z][a-z])","$1 $2");
        return Regex.Replace(text,@"[_.\[\]\""':]+"," ").Trim();
    }
    private static readonly Dictionary<string,string> Words=new(StringComparer.OrdinalIgnoreCase)
    {
        ["money"]="金钱",["cash"]="现金",["gold"]="金币",["coin"]="硬币",["coins"]="硬币",["currency"]="货币",["wallet"]="钱包",["credit"]="点数",["credits"]="点数",["balance"]="余额",["ryo"]="两",
        ["inventory"]="背包",["item"]="物品",["items"]="物品",["bag"]="背包",["potion"]="药水",["food"]="食物",["gift"]="礼物",["ticket"]="票券",["key"]="钥匙",["keycard"]="门卡",["weapon"]="武器",["armor"]="护甲",
        ["quantity"]="数量",["count"]="数量",["amount"]="数量",["number"]="数量",["value"]="数值",["level"]="等级",["exp"]="经验",["experience"]="经验",["hp"]="生命值",["health"]="生命值",["mp"]="魔法值",["energy"]="精力",["stamina"]="体力",
        ["love"]="好感",["affection"]="好感",["trust"]="信任",["relationship"]="关系",["progress"]="进度",["quest"]="任务",["stage"]="阶段",["day"]="天数",["time"]="时间",["hour"]="小时",["points"]="点数",["score"]="分数",
        ["cg"]="CG",["gallery"]="画廊",["replay"]="回想",["recollection"]="回想",["scene"]="场景",["image"]="图片",["unlock"]="解锁",["unlocked"]="已解锁",["seen"]="已看过",["owned"]="已拥有",["enabled"]="已开启",["flag"]="标记",["total"]="总计",["max"]="上限",["current"]="当前",["player"]="玩家",
        ["お金"]="金钱",["所持金"]="金钱",["コイン"]="硬币",["アイテム"]="物品",["持ち物"]="背包",["ギャラリー"]="画廊",["回想"]="回想",["돈"]="金钱",["골드"]="金币",["아이템"]="物品",["деньги"]="金钱",["золото"]="金币",["предметы"]="物品",["dinero"]="金钱",["monedas"]="硬币",["inventario"]="背包",["argent"]="金钱",["inventaire"]="背包",["geld"]="金钱",["inventar"]="背包",["dinheiro"]="金钱"
    };
    internal static string LocalLabel(string raw)
    {
        string readable=Readable(raw);bool known=false;
        string label=Regex.Replace(readable,@"[\p{L}]+",m=>{if(Words.TryGetValue(m.Value,out string? word)){known=true;return word;}return m.Value;});
        return known||Regex.IsMatch(readable,@"[\u4e00-\u9fff]")?label:"";
    }
    internal string Label(string raw,string translated="")=>Notes.TryGetValue(raw,out var note)&&note.Length>0?note:translated.Length>0?translated:LocalLabel(raw) is {Length:>0} local?local:"待补充中文名";
}
