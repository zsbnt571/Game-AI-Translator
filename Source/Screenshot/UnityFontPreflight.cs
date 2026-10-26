using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

// Read-only planning against the actual shipped fallback's resource table.
// This never loads an assembly/font, changes an asset or grants runtime write
// authority. A bundled page is not proof the game's TMP API can use that page.
internal static class UnityFontPreflight
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,FontTable> Bundled=new(StringComparer.Ordinal);
    internal sealed record Glyph(int Codepoint,string Character,int Uses);
    internal sealed record FontTable(string Name,string Evidence,string Sha256,
        IReadOnlyDictionary<int,int> Pages,bool Complete,bool Dynamic=false);
    internal sealed record Coverage(string Name,string Evidence,string Sha256,int Required,int Present,
        int Missing,int MissingUses,Glyph[] MissingSample,int RequiredPages,bool Complete,bool Dynamic);
    internal sealed record Report(int Version,string TargetLanguage,int Translations,int Characters,int Distinct,
        bool Truncated,int InvalidRows,bool RenderingVerified,Coverage[] Fonts,string[] Warnings);
    internal sealed record Limits(int Translations=100000,int Characters=4000000,int Distinct=65536);
    private static readonly Regex Markup=new(@"</?(?:b|i|u|s|color|size|font|font-weight|material|sprite|link|align|alpha|br|cspace|mspace|space|indent|line-indent|line-height|margin|margin-left|margin-right|mark|nobr|noparse|pos|rotate|voffset|width|style|sub|sup|uppercase|lowercase|smallcaps|allcaps|page|quad)(?:\s[^<>]{0,500}|=[^<>]{0,500})?/?>|<#[0-9a-f]{6}>",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));

    internal static Report WriteReport(string exe,ApiSettings settings,IReadOnlyDictionary<string,string> translations,
        string outputPath,CancellationToken token=default,string? fontPath=null)
    {
        token.ThrowIfCancellationRequested();var warnings=new List<string>();var fonts=new List<FontTable>();
        try
        {
            var game=UnityEmbeddedAdapter.Detect(exe)??throw new InvalidDataException("Unity profile unavailable");
            // These are immutable resources of this desktop assembly. Cache
            // validated tables only; cancelled/failed parses never enter it.
            string key=game.Backend+"|"+game.Architecture;
            if(!Bundled.TryGetValue(key,out var table))
            {
                using var stream=UnityEmbeddedPayload.Open(game.Backend,game.Architecture);
                using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
                var entry=zip.GetEntry("BepInEx/plugins/FusionUnityEmbedded/FusionUnityEmbedded.dll")??throw new InvalidDataException("Font runtime unavailable");
                if(entry.Length>64*1024*1024)throw new InvalidDataException("Font runtime exceeds limit");
                using var bytes=new MemoryStream();using(var input=entry.Open())input.CopyTo(bytes);
                table=ReadBundled(bytes.ToArray(),token);Bundled.TryAdd(key,table);
            }
            fonts.Add(table);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or BadImageFormatException or JsonException or ArgumentException or InvalidOperationException or OverflowException)
        {warnings.Add("Bundled fallback coverage unknown: "+ex.GetType().Name);fonts.Add(new("Fusion bundled fallback","unreadable-shipped-resource","",new Dictionary<int,int>(),false));}
        var report=CheckTables(translations,fonts,settings.TargetLanguage,token);
        warnings.AddRange(report.Warnings);
        if(fontPath is not null)
        {
            try
            {
                string required=string.Concat(Frequencies(translations,token,new Limits(),out _,out _,out _,out _).Keys.Select(char.ConvertFromUtf32));
                var checkedFont=GameFontCoverage.Check(fontPath,required,65536);
                var absent=checkedFont.Missing.ToHashSet();var codes=required.EnumerateRunes().Where(r=>!absent.Contains(r.Value)).ToDictionary(r=>r.Value,_=>0);
                var extra=CheckTables(translations,[new("Selected font","OpenType cmap, not shaping or game assignment",checkedFont.FontHash,codes,!checkedFont.Truncated)],settings.TargetLanguage,token);
                report=report with{Fonts=[..report.Fonts,..extra.Fonts]};
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or ArgumentException)
            {warnings.Add("Selected font coverage unknown: "+ex.GetType().Name);}
        }
        token.ThrowIfCancellationRequested();report=report with{Warnings=warnings.Distinct(StringComparer.Ordinal).ToArray()};
        TranslationStorage.WriteJson(outputPath,report);return report;
    }
    internal static Report CheckTables(IEnumerable<KeyValuePair<string,string>> translations,IEnumerable<FontTable> fonts,
        string target,CancellationToken token=default,Limits? limits=null)
    {
        var counts=Frequencies(translations,token,limits??new(),out int lines,out int characters,out bool truncated,out int invalid);
        var reports=new List<Coverage>();
        foreach(var font in fonts)
        {
            token.ThrowIfCancellationRequested();var missing=counts.Where(p=>!font.Pages.ContainsKey(p.Key)).OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).ToArray();
            reports.Add(new(font.Name,font.Evidence,font.Sha256,counts.Count,counts.Count-missing.Length,missing.Length,
                missing.Sum(p=>p.Value),missing.Take(128).Select(p=>new Glyph(p.Key,char.ConvertFromUtf32(p.Key),p.Value)).ToArray(),
                counts.Keys.Where(font.Pages.ContainsKey).Select(c=>font.Pages[c]).Where(p=>p>=0).Distinct().Count(),font.Complete&&!truncated&&invalid==0,font.Dynamic));
        }
        var warnings=new List<string>{"Coverage plans shipped fallback glyphs only; original game fonts, TMP compatibility, shaping, ligatures, wrapping and visible pixels are not verified.","A dynamic font may add glyphs not in its current table; missing means absent from this table, not a universal unsupported character."};
        if(truncated)warnings.Add("Input limit reached: remaining translations/characters are unknown, not covered.");
        if(invalid>0)warnings.Add("Invalid UTF-16 rows were excluded from complete-coverage claims.");
        return new(1,target,lines,characters,counts.Count,truncated,invalid,false,reports.ToArray(),warnings.ToArray());
    }
    private static Dictionary<int,int> Frequencies(IEnumerable<KeyValuePair<string,string>> translations,CancellationToken token,Limits limits,
        out int lines,out int characters,out bool truncated,out int invalid)
    {
        if(limits.Translations<1||limits.Characters<1||limits.Distinct<1||limits.Distinct>65536)throw new ArgumentOutOfRangeException(nameof(limits));
        lines=characters=invalid=0;truncated=false;var counts=new Dictionary<int,int>();
        foreach(var pair in translations)
        {
            token.ThrowIfCancellationRequested();if(lines>=limits.Translations){truncated=true;break;}lines++;
            string text=pair.Value??"";var row=new Dictionary<int,int>();int extra=0;bool bad=false;
            for(int i=0;i<text.Length;i++)if(char.IsHighSurrogate(text[i])){if(++i>=text.Length||!char.IsLowSurrogate(text[i])){bad=true;break;}}else if(char.IsLowSurrogate(text[i])){bad=true;break;}
            if(bad){invalid++;continue;}
            // Honor noparse spans: their bracketed text is visible, not markup.
            var segments=Regex.Split(text,@"(?i)(<noparse>|</noparse>)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));bool literal=false;
            foreach(string part in segments)
            {
                if(part.Equals("<noparse>",StringComparison.OrdinalIgnoreCase)){literal=true;continue;}
                if(part.Equals("</noparse>",StringComparison.OrdinalIgnoreCase)){literal=false;continue;}
                string visible=literal?part:Markup.Replace(part,"");
                foreach(var rune in visible.EnumerateRunes())
                {
                    if((++characters&1023)==0)token.ThrowIfCancellationRequested();
                    if(characters>limits.Characters){truncated=true;break;}
                    var category=Rune.GetUnicodeCategory(rune);int c=rune.Value;
                    if(Rune.IsWhiteSpace(rune)||category is UnicodeCategory.Control or UnicodeCategory.Format||c is >=0xfe00 and <=0xfe0f or >=0xe0100 and <=0xe01ef)continue;
                    if(!counts.ContainsKey(c)&&!row.ContainsKey(c)){if(counts.Count+extra>=limits.Distinct){truncated=true;break;}extra++;}
                    row[c]=row.GetValueOrDefault(c)+1;
                }
                if(truncated)break;
            }
            foreach(var p in row)counts[p.Key]=counts.GetValueOrDefault(p.Key)+p.Value;
            if(truncated)break;
        }
        return counts;
    }
    internal static FontTable ReadBundled(byte[] image,CancellationToken token=default)
    {
        byte[] index=Resource(image,"font-index.json",2*1024*1024);using var json=JsonDocument.Parse(index);var root=json.RootElement;
        if(root.GetProperty("schema").GetInt32()!=1||root.GetProperty("size").GetInt32()!=2048)throw new InvalidDataException("Unknown static font index");
        var pages=root.GetProperty("pages").EnumerateArray().ToArray();var rows=root.GetProperty("glyphs");
        if(pages.Length>32||rows.GetArrayLength()>50000)throw new InvalidDataException("Font table exceeds limits");
        for(int n=0;n<pages.Length;n++)
        {
            token.ThrowIfCancellationRequested();string expected="page-"+n.ToString("00",CultureInfo.InvariantCulture)+".sdf.gz";
            if(pages[n].GetProperty("file").GetString()!=expected)throw new InvalidDataException("Invalid page identity");
            byte[] bytes=Resource(image,expected,1024*1024);
            if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(pages[n].GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Page hash mismatch");
            using var gzip=new GZipStream(new MemoryStream(bytes),CompressionMode.Decompress);long length=0;byte[] block=new byte[8192];int read;
            while((read=gzip.Read(block))>0){length+=read;if(length>2048L*2048)throw new InvalidDataException("Invalid atlas length");}
            if(length!=2048L*2048)throw new InvalidDataException("Truncated atlas");
        }
        var codes=new Dictionary<int,int>();
        foreach(var row in rows.EnumerateArray())
        {
            if(row.GetArrayLength()!=9)throw new InvalidDataException("Invalid glyph row");
            int code=row[0].GetInt32(),page=row[1].GetInt32(),x=row[2].GetInt32(),y=row[3].GetInt32(),w=row[4].GetInt32(),h=row[5].GetInt32();
            if(!Rune.IsValid(code)||page<0||page>=pages.Length||x<0||y<0||w<0||h<0||x>2048-w||y>2048-h||!codes.TryAdd(code,page))throw new InvalidDataException("Invalid glyph bounds");
        }
        return new("Fusion bundled SDF fallback","validated embedded character table and page hashes; not original game fonts",Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant(),codes,true);
    }
    private static byte[] Resource(byte[] image,string shortName,int maximum)
    {
        using var pe=new PEReader(new MemoryStream(image));var metadata=pe.GetMetadataReader();string name="Fusion.UnityEmbedded.FontAssets."+shortName;
        foreach(var handle in metadata.ManifestResources)
        {
            var resource=metadata.GetManifestResource(handle);if(metadata.GetString(resource.Name)!=name)continue;
            if(!resource.Implementation.IsNil||resource.Offset>int.MaxValue)throw new InvalidDataException("External font resource");
            var directory=pe.PEHeaders.CorHeader?.ResourcesDirectory??throw new BadImageFormatException();int offset=checked((int)resource.Offset);
            if(offset<0||offset>directory.Size-4)throw new InvalidDataException("Font resource offset");var block=pe.GetSectionData(directory.RelativeVirtualAddress);
            var reader=block.GetReader(offset,4);int length=reader.ReadInt32();if(length<0||length>maximum||length>directory.Size-offset-4)throw new InvalidDataException("Font resource length");
            return block.GetContent(offset+4,length).ToArray();
        }
        throw new InvalidDataException("Missing font resource");
    }
}
