using System.Text.Json.Serialization;

internal sealed record CatalogText(string ns,string key,string source,string culture,Dictionary<string,string> localized,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? provenance=null)
{
 internal const string BlueprintLocalizedCandidate="blueprintLocalizedCandidate";
}

internal static class CatalogMerge
{
 internal static void Add(Dictionary<string,CatalogText> rows,HashSet<string> conflicts,CatalogText row) {
  string id=row.ns+"\0"+row.key;
  if(rows.TryGetValue(id,out var old)) {
   if(old.source!=row.source){conflicts.Add(id);return;}
   // An unverified byte-pattern candidate can neither downgrade an existing
   // source nor contribute localized strings. A stronger source upgrades it.
   if(row.provenance==CatalogText.BlueprintLocalizedCandidate)return;
   if(old.provenance==CatalogText.BlueprintLocalizedCandidate){rows[id]=row;return;}
   foreach(var pair in row.localized)old.localized[pair.Key]=pair.Value;
   if(row.localized.Count>0)rows[id]=row with { localized=old.localized };
  } else if(rows.Count<100000)rows[id]=row;
 }
}
