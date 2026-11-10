namespace ScreenshotTranslationUiTester;

// This joins labels for presentation only. A terminology name never becomes a
// speaker binding, and two native IDs remain two independently editable rows.
internal static class GameCharacterWorkspaceRows
{
    internal sealed record Row(GameTerminology.View Term,GameCharacterProfileView? Character)
    {
        internal string Key=>Character is null?"term:"+Term.Source:"character:"+Character.CharacterId+"\0"+Term.Source;
    }
    internal static Row[] Merge(IEnumerable<GameTerminology.View> terms,IEnumerable<GameCharacterProfileView> characters)
    {
        var names=terms.ToArray();var profiles=characters.ToArray();
        var translated=names.ToDictionary(n=>n.Source,StringComparer.Ordinal);
        var attached=new HashSet<string>(StringComparer.Ordinal);var result=new List<Row>();
        foreach(var profile in profiles)
        {
            string primary=string.IsNullOrWhiteSpace(profile.DisplayName)?profile.CharacterId:profile.DisplayName;
            var labels=(profile.DisplayNames??[]).Where(n=>translated.TryGetValue(n,out var term)&&term.Kind is "人物" or "自定义").Append(primary).Distinct(StringComparer.Ordinal);
            foreach(string name in labels)
            {
            translated.TryGetValue(name,out var term);
            // A display-name collision with a location/item is not evidence
            // that the existing category belongs to this character.
            term=term is {Kind:"人物" or "自定义"}?term with{Kind="人物"}:new(name,term?.Translation??"","人物",[],term?.Conflicts??[],term?.KeepOriginal??false,false);
            if(translated.TryGetValue(name,out var existing)&&existing.Kind is "人物" or "自定义")attached.Add(name);
            result.Add(new(term,profile));
            }
        }
        result.AddRange(names.Where(n=>!attached.Contains(n.Source)).Select(n=>new Row(n,null)));
        return result.OrderBy(r=>r.Term.Source,StringComparer.CurrentCultureIgnoreCase).ThenBy(r=>r.Key,StringComparer.Ordinal).ToArray();
    }
}
