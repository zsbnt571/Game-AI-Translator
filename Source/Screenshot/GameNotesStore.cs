using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record GameNoteSelection(string Id,string LegacyNote);
internal sealed record GameNoteRun(string Text,bool Bold=false,bool Italic=false,bool Underline=false,bool Highlight=false);
internal sealed record GameNoteParagraph(GameNoteRun[] Runs,string ListKind="");
internal sealed record GameNoteDocument(int Schema,string Revision,string Text,DateTimeOffset UpdatedUtc,GameNoteParagraph[]? Paragraphs=null);

// Independent of the card's short note. Older versions can continue to edit
// card metadata without truncating a walkthrough written by this version.
internal sealed class GameNotesStore
{
    internal const int MaxLength=100_000;
    private const int MaxFileBytes=8*1024*1024;
    private readonly string root;
    internal GameNotesStore(string? dataRoot=null)=>root=Path.Combine(dataRoot??AppDataPaths.Root,"game-notes-v1");
    internal string PathFor(string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("Invalid game ID.");
        var path=Path.Combine(root,id+".json");AppDataPaths.RejectReparsePoints(path);return path;
    }
    internal GameNoteDocument Read(string id,string legacy="")
    {
        string path=PathFor(id);
        if(!File.Exists(path))return new(1,"",legacy,DateTimeOffset.MinValue);
        if(new FileInfo(path).Length>MaxFileBytes)throw new InvalidDataException("Note file exceeds its limit.");
        var document=JsonSerializer.Deserialize<GameNoteDocument>(File.ReadAllText(path));
        if(document is null||document.Schema is not (1 or 2)||document.Text is null||document.Text.Length>MaxLength||!Guid.TryParseExact(document.Revision,"N",out _))
            throw new InvalidDataException("Note file is invalid; the original is preserved.");
        ValidateFormatting(document.Text,document.Paragraphs);
        return document;
    }
    internal static string FormattedText(GameNoteParagraph[] paragraphs)=>string.Join("\n",paragraphs.Select(p=>string.Concat(p.Runs.Select(r=>r.Text))));
    private static void ValidateFormatting(string text,GameNoteParagraph[]? paragraphs)
    {
        if(paragraphs is null)return;
        if(paragraphs.Length>10000||paragraphs.Any(p=>p is null||p.Runs is null||p.ListKind is not ("" or "bullet" or "numbered")||p.Runs.Any(r=>r is null||r.Text is null))
            ||paragraphs.Sum(p=>(long)p.Runs.Length)>50000||FormattedText(paragraphs)!=text)
            throw new InvalidDataException("Formatted note is invalid; the original is preserved.");
    }
    internal GameNoteDocument Save(string id,string text,string expectedRevision,GameNoteParagraph[]? paragraphs=null)
    {
        if(text is null||text.Length>MaxLength)throw new ArgumentException("Note exceeds its limit.");
        ValidateFormatting(text,paragraphs);
        string path=PathFor(id);Directory.CreateDirectory(root);
        using var ownership=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var current=Read(id);
        if(current.Revision!=expectedRevision)throw new IOException("Note changed in another window; your draft has been retained.");
        if(current.Revision.Length>0&&current.Text==text&&JsonSerializer.Serialize(current.Paragraphs)==JsonSerializer.Serialize(paragraphs))return current;
        var next=new GameNoteDocument(paragraphs is null?1:2,Guid.NewGuid().ToString("N"),text,DateTimeOffset.UtcNow,paragraphs);
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(next);if(bytes.Length>MaxFileBytes)throw new InvalidDataException("Formatted note exceeds its limit.");
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {stream.Write(bytes);stream.Flush(true);}
            if(File.Exists(path))File.Replace(temporary,path,path+".previous");else File.Move(temporary,path);
            return next;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal HashSet<string> Search(IEnumerable<string> ids,string query)
    {
        var matches=new HashSet<string>(StringComparer.Ordinal);
        foreach(string id in ids)
            try{if(Read(id).Text.Contains(query,StringComparison.OrdinalIgnoreCase))matches.Add(id);}
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException){ }
        return matches;
    }
}
