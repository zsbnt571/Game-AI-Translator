using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ScreenshotTranslationUiTester;

// Bounded virtual reads: no extraction paths, script execution or link following.
internal sealed class WebGameArchive : IDisposable
{
    private readonly FileStream? input;
    private readonly string? directory;
    private readonly ZipArchive? zip;
    private readonly JsonObject? header;
    private readonly long dataOffset;
    private readonly Dictionary<string,(long Offset,long Size,JsonObject? Node,ZipArchiveEntry? Zip)> entries=new(StringComparer.Ordinal);
    internal IEnumerable<string> Names=>entries.Keys;
    internal int UnreadableEntries {get;private set;}
    internal static string SafeName(string name)
    {
        if(name.Length is <1 or >512||name.Contains('\\')||name.Contains(':')||name.StartsWith('/')||name.Split('/').Any(s=>s.Length==0||s is "." or ".."||s.EndsWith('.')||s.EndsWith(' ')||s.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new InvalidDataException("资源包含有不安全路径。");
        return name;
    }
    internal WebGameArchive(string path)
    {
        if(Directory.Exists(path))
        {
            directory=Path.GetFullPath(path);var todo=new Stack<(string Path,int Depth)>();todo.Push((directory,0));
            int visited=0;var watch=System.Diagnostics.Stopwatch.StartNew();
            while(todo.Count>0)
            {
                var item=todo.Pop();
                if(item.Depth>40||(File.GetAttributes(item.Path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("网页目录含链接或层级过深。");
                foreach(string child in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    if(++visited>100000||watch.ElapsedMilliseconds>10000)throw new InvalidDataException("网页目录读取达到数量或时间上限。");
                    if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0){UnreadableEntries++;continue;}
                    if(Directory.Exists(child)){todo.Push((child,item.Depth+1));continue;}
                    string name=SafeName(Path.GetRelativePath(directory,child).Replace('\\','/'));
                    if(entries.Count>=100000)throw new InvalidDataException("网页目录条目过多。");
                    entries.Add(name,(0,new FileInfo(child).Length,null,null));
                }
            }
            return;
        }
        input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            if(input.Length>8L*1024*1024*1024)throw new InvalidDataException("资源包超过本次大小上限。");
            // ASAR is a case-sensitive virtual filesystem; LICENSE and license
            // may both exist. We never extract these names onto Windows paths.
            var unique=new HashSet<string>(path.EndsWith(".asar",StringComparison.OrdinalIgnoreCase)?StringComparer.Ordinal:StringComparer.OrdinalIgnoreCase);
            if(path.EndsWith(".asar",StringComparison.OrdinalIgnoreCase))
            {
                using var reader=new BinaryReader(input,Encoding.UTF8,true);
                if(input.Length<16||reader.ReadUInt32()!=4)throw new InvalidDataException("ASAR 文件头无效。");
                uint block=reader.ReadUInt32(),payload=reader.ReadUInt32(),size=reader.ReadUInt32();
                if(block is <8 or >16777216||payload!=block-4||size>block-8||block-8-size>3||8L+block>input.Length)throw new InvalidDataException("ASAR 索引长度无效。");
                header=JsonNode.Parse(reader.ReadBytes(checked((int)size)),documentOptions:new(){MaxDepth=96}) as JsonObject??throw new InvalidDataException("ASAR 索引无效。");dataOffset=8L+block;
                void Walk(JsonObject node,string prefix,int depth)
                {
                    if(depth>40||node["files"] is not JsonObject files)throw new InvalidDataException("ASAR 目录结构无效。");
                    foreach(var item in files)
                    {
                        string name=SafeName(prefix+item.Key);
                        if(item.Key.Contains('/')||item.Value is not JsonObject child||!unique.Add(name)||unique.Count>100000)throw new InvalidDataException("ASAR 条目重复或过多。");
                        if(child.ContainsKey("files")){Walk(child,name+"/",depth+1);continue;}
                        if(child.ContainsKey("link")||child["unpacked"]?.GetValue<bool>()==true)continue;
                        if(!long.TryParse(child["offset"]?.GetValue<string>(),out long offset)||offset<0||child["size"] is not JsonValue sv||!sv.TryGetValue<long>(out long length)||length<0||length>input.Length-dataOffset||offset>input.Length-dataOffset-length){UnreadableEntries++;continue;}
                        entries.Add(name,(offset,length,child,null));
                    }
                }
                Walk(header,"",0);
            }
            else
            {
                zip=new ZipArchive(input,ZipArchiveMode.Read,true);long expanded=0;
                foreach(var e in zip.Entries)
                {
                    string name=SafeName(e.FullName.TrimEnd('/'));
                    if(!unique.Add(name)||unique.Count>100000||((e.ExternalAttributes>>16)&0xf000)==0xa000)throw new InvalidDataException("资源包条目重复、链接或过多。");
                    if(e.FullName.EndsWith('/'))continue;
                    expanded=checked(expanded+e.Length);if(e.Length>512L*1024*1024||expanded>8L*1024*1024*1024)throw new InvalidDataException("资源包展开大小超过限制。");
                    entries.Add(name,(0,e.Length,null,e));
                }
            }
        }
        catch{Dispose();throw;}
    }
    internal bool Contains(string name)=>entries.ContainsKey(name);
    internal long Size(string name)=>entries[name].Size;
    internal byte[] Read(string name,int limit=32*1024*1024)
    {
        var e=entries[name];if(e.Size>limit)throw new InvalidDataException("资源条目超过本次读取上限。");
        var bytes=new byte[checked((int)e.Size)];
        if(e.Zip is not null){using var s=e.Zip.Open();s.ReadExactly(bytes);if(s.ReadByte()!=-1)throw new InvalidDataException("资源条目长度不符。");}
        else if(directory is not null){using var s=new FileStream(GameMakerGameAdapter.InGame(Path.Combine(directory,"__fusion_root.exe"),name),FileMode.Open,FileAccess.Read,FileShare.Read);s.ReadExactly(bytes);if(s.ReadByte()!=-1)throw new IOException("读取期间网页资源发生变化。");}
        else{input!.Position=dataOffset+e.Offset;input.ReadExactly(bytes);}return bytes;
    }
    internal string Text(string name,int limit=32*1024*1024)=>new UTF8Encoding(false,true).GetString(Read(name,limit));
    internal void Patch(string destination,IReadOnlyDictionary<string,byte[]> changes)
    {
        if(directory is not null)throw new InvalidOperationException("散装网页资源只通过启动配置安装，不整体替换目录。");
        foreach(var c in changes){SafeName(c.Key);if(c.Value.Length>16*1024*1024)throw new InvalidDataException("替换条目过大。");}
        using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        if(zip is not null)
        {
            using(var result=new ZipArchive(output,ZipArchiveMode.Create,true))
            {
                foreach(var e in zip.Entries)
                {
                    if(changes.ContainsKey(e.FullName))continue;
                    var next=result.CreateEntry(e.FullName,CompressionLevel.Fastest);next.LastWriteTime=e.LastWriteTime;next.ExternalAttributes=e.ExternalAttributes;
                    using var src=e.Open();using var dst=next.Open();src.CopyTo(dst);
                }
                foreach(var c in changes){using var dst=result.CreateEntry(c.Key,CompressionLevel.Fastest).Open();dst.Write(c.Value);}
            }
        }
        else
        {
            var next=(JsonObject)header!.DeepClone();long append=input!.Length-dataOffset;
            foreach(var c in changes)
            {
                var parts=c.Key.Split('/');var parent=next;
                foreach(string part in parts[..^1]){var files=(JsonObject)parent["files"]!;if(files[part] is null)files[part]=new JsonObject{["files"]=new JsonObject()};parent=(JsonObject)files[part]!;if(parent["files"] is not JsonObject)throw new InvalidDataException("资源文件与目录冲突。");}
                var all=(JsonObject)parent["files"]!;
                if(all[parts[^1]] is JsonObject old&&(old.ContainsKey("link")||old.ContainsKey("files")||old["unpacked"]?.GetValue<bool>()==true))throw new InvalidDataException("不能覆盖链接、目录或外置资源。");
                string hash=Convert.ToHexString(SHA256.HashData(c.Value)).ToLowerInvariant();
                all[parts[^1]]=new JsonObject{["size"]=c.Value.Length,["offset"]=append.ToString(System.Globalization.CultureInfo.InvariantCulture),["integrity"]=new JsonObject{["algorithm"]="SHA256",["hash"]=hash,["blockSize"]=16*1024*1024,["blocks"]=new JsonArray(JsonValue.Create(hash))}};
                append+=c.Value.Length;
            }
            byte[] json=Encoding.UTF8.GetBytes(next.ToJsonString());int padding=(4-json.Length%4)%4;
            using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(4);writer.Write(json.Length+8+padding);writer.Write(json.Length+4+padding);writer.Write(json.Length);writer.Write(json);writer.Write(new byte[padding]);
            input.Position=dataOffset;input.CopyTo(output);foreach(var c in changes)output.Write(c.Value);
        }
        output.Flush(true);
    }
    public void Dispose(){zip?.Dispose();input?.Dispose();}
}
