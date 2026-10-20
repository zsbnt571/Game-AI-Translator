using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Classification uses source metadata, never capitalization or a translated word.
internal static class GameTerminologyClassification
{
    internal static readonly string[] Categories =
        ["全部", "人物", "地点", "技能", "道具", "武器", "防具", "职业", "状态", "国家", "组织", "种族", "任务", "事件", "自定义"];

    private static string Field(string context, string key) =>
        Regex.Match(context, @"(?:^|;)\s*" + key + @"=([^;]*)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

    internal static string Kind(CatalogTextOrigin origin)
    {
        string kind=FieldKind(origin);
        return AcceptsContent(origin.Text,kind)?kind:"";
    }

    // A field can mention a name as the subject of a reaction without storing
    // that name. Neither "character lines" nor "point out location name" is an
    // entity-name schema.
    private static string FieldKind(CatalogTextOrigin origin)
    {
        if (origin.Kind is "speaker" or "display-name") return "人物";
        string at = origin.Location.Replace('\\', '/');
        if (origin.Kind == "display-field" &&
            Regex.IsMatch(at, @"/(?:name|nickname|displayName)$", RegexOptions.IgnoreCase))
            return Path.GetFileNameWithoutExtension(origin.File).ToLowerInvariant() switch
            {
                "actors" or "enemies" => "人物", "classes" => "职业", "items" => "道具",
                "weapons" => "武器", "armors" => "防具", "skills" => "技能",
                "states" => "状态", "mapinfos" => "地点", _ => ""
            };
        if (origin.Kind != "wolf-database-string") return "";
        string field = Field(origin.Context, "field"), type = Field(origin.Context, "type");
        string typeWords=Regex.Replace(type,@"[_-]"," ");
        if (!IsNameField(field)) return "";
        if (Regex.IsMatch(field, @"(?:クエスト|ミッション|quest|mission).*(?:名|name|title)", RegexOptions.IgnoreCase)) return "任务";
        // A character-appearance event's display label is not a person's name.
        if (Regex.IsMatch(field, @"イベント.*(?:名|name)|event.*name", RegexOptions.IgnoreCase)) return "事件";
        if (Regex.IsMatch(typeWords, @"台詞|セリフ|せりふ|対話|对白|对话|会話|\b(?:lines?|dialogue|dialog|speech|responses?|replies|messages?|quotes?)\b", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(LocaleStem(field), @"^(?:話者名|発言者名|会話者名|(?:speaker|actor|character)[ _-]*(?:name|displayname))$", RegexOptions.IgnoreCase)) return "";
        foreach (var pair in new (string Kind, string Pattern)[]
        {
            ("任务", @"クエスト|ミッション|依頼|任務|任务|\b(?:quests?|missions?)\b"),
            ("事件", @"イベント|\bevents?\b"),
            ("武器", @"武器|\bweapons?\b"), ("防具", @"防具|\b(?:armors?|armours?)\b"),
            ("地点", @"地名|場所|地域|マップ|ステージ|ダンジョン|地点|关卡|\b(?:maps?|locations?|stages?|dungeons?|areas?)\b"),
            ("国家", @"国家|国名|\b(?:countr(?:y|ies)|nations?)\b"),
            ("组织", @"組織|组织|陣営|阵营|ギルド|\b(?:organi[sz]ations?|factions?|guilds?)\b"),
            ("种族", @"種族|种族|\b(?:races?|species)\b"),
            ("人物", @"人物|キャラ|主人公|アクター|指名手配|\b(?:actors?|characters?|enemy|enemies|wanted)\b"),
            ("道具", @"アイテム|道具|物品|書類|証明書|证件|\b(?:items?|documents?)\b"),
            ("技能", @"技能|スキル|魔法|\b(?:skills?|spells?)\b"),
            ("职业", @"職業|职业|クラス|\b(?:class|classes|jobs?)\b"),
            ("状态", @"状態|状态|ステート|\b(?:status|statuses|states?)\b")
        })
            if (Regex.IsMatch(typeWords, pair.Pattern, RegexOptions.IgnoreCase)) return pair.Kind;
        return "";
    }

    internal static bool IsNameField(string field)
    {
        // Strip explicit locale suffixes; "name description" and image paths do
        // not become name fields just because they contain a name-related token.
        string stem = LocaleStem(field);
        return Regex.IsMatch(stem, @"(?:名前|名称|表示名|呼び名|名|name|nickname|title)$", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(stem, @"ファイル|画像|音声|台詞|セリフ|指摘|反応|応答|説明|对白|对话|提示|反应|filename|path|script|description|dialogue|\b(?:line|response|reply|message|prompt|reaction|pointing|comment|hint)\b", RegexOptions.IgnoreCase);
    }

    internal static bool AcceptsContent(string text,string kind)
    {
        if(kind.Length==0)return false;
        if(kind is "任务" or "事件" or "自定义")return true;
        // Rendering controls and multiple lines do not identify an entity.
        // Punctuation alone is not enough to reject a skill or item: "Fire!"
        // can be a real skill name in an explicit skill-name field.
        if(Regex.IsMatch(text,@"[\r\n]|^\s*[$@#]|(?:^|\s)[@#]\d",RegexOptions.CultureInvariant))return false;
        // Personal names need stronger content checks because a dialogue table
        // may use a seemingly valid speaker/name column for a whole reaction.
        // Apostrophes, initials, hyphens and multiword names remain valid.
        return kind!="人物"||!Regex.IsMatch(text,@"[!?！？…]|\.\.{1,}|^[A-Za-z]{2,}\.$",RegexOptions.CultureInvariant);
    }

    private static string LocaleStem(string field) =>
        Regex.Replace(field.Trim(), @"(?:[\s_（(\[]*(?:jp|ja|en|eng|english|japanese|日本語|英語|中文|zh|cn)[）)\]]*)$", "", RegexOptions.IgnoreCase).TrimEnd(' ', '_');

    internal static string Identity(CatalogTextOrigin origin)
    {
        if (origin.Kind != "wolf-database-string" || Kind(origin) is "" or "任务" or "事件") return "";
        string field = Field(origin.Context, "field"), stem = LocaleStem(field);
        // Only explicitly multilingual sibling columns prove aliases. Never
        // merge two people merely because their translated names look alike.
        if (field == stem) return "";
        string row = Regex.Match(origin.Location, @"^database/type:\d+/row:\d+").Value;
        return row.Length == 0 ? "" : origin.File.Replace('\\', '/') + "/" + row + "/" + stem.ToLowerInvariant();
    }

    internal static string Scope(string kind) => kind is "任务" or "事件" or "自定义" ? "label" : "entity";
}
