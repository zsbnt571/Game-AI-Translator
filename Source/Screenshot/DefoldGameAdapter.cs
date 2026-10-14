using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static ScreenshotTranslationUiTester.DefoldWire;

namespace ScreenshotTranslationUiTester;

internal static class DefoldGameAdapter
{
    internal const string Folder=".fusion-defold";
    private sealed record Ledger(int Version,string ExeHash,Dictionary<string,string> Inputs,Dictionary<string,string> Owned,string? BridgeHash=null);
    private static readonly Lazy<string> PayloadHash=new(()=>
    {
        using var stream=typeof(DefoldGameAdapter).Assembly.GetManifestResourceStream("Fusion.DefoldTranslationBridge.lua")??throw new IOException("内嵌组件缺失。");
        using var reader=new StreamReader(stream);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("DefoldFontBuilder/133|DefoldDisplay/136|"+reader.ReadToEnd()+"|"+Hash(Path.Combine(AppContext.BaseDirectory,"adapters","gamemaker","bridge-font.otf")))));
    });
    internal static string CurrentBridgeHash=>PayloadHash.Value;
    private static readonly string[] InputNames=["game.projectc","game.arci","game.arcd","game.dmanifest"];
    private static readonly string[] OwnedNames=[..InputNames,"bridge-version.txt"];
    private static bool Digest(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
    private static Ledger ReadLedger(string exe,string directory=Folder)
    {
        string file=InGame(exe,directory+"/install.json");
        if(new FileInfo(file).Length>16384)throw new IOException("翻译记录过大。");
        var l=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(file))??throw new IOException("翻译记录无效。");
        // Only exact owned file names grant removal permission. An untrusted
        // ledger cannot escape the adapter folder or nominate original files.
        if(l.Version!=133||!Digest(l.ExeHash)||l.BridgeHash is not null&&!Digest(l.BridgeHash)||l.Inputs is null||l.Owned is null||
           InputNames.Any(n=>!l.Inputs.ContainsKey(n))||OwnedNames.Any(n=>!l.Owned.ContainsKey(n))||
           l.Inputs.Any(p=>!InputNames.Contains(p.Key)&&p.Key!="game.public.der"||!Digest(p.Value))||
           l.Owned.Any(p=>!OwnedNames.Contains(p.Key)&&p.Key!="game.public.der"||!Digest(p.Value))||
           l.Inputs.ContainsKey("game.public.der")!=l.Owned.ContainsKey("game.public.der"))throw new IOException("翻译记录的文件范围无效。");
        return l;
    }
    private static void Stopped(string exe)
    {
        if(FusionAdapterInstaller.ProcessState(new(exe,"Defold","Defold","","",SupportLevel.Candidate,null,"defold"))!=FusionGameProcessState.Stopped)
            throw new IOException("请先退出游戏，再准备或恢复翻译。");
    }
    internal static string InGame(string exe,string relative)=>GameMakerGameAdapter.InGame(exe,relative);
    internal static bool Candidate(string exe)
    {
        try{using var s=File.OpenRead(InGame(exe,"game.arci"));Span<byte> b=stackalloc byte[4];s.ReadExactly(b);if(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b)!=4)return false;
            string section="";bool shared=false;foreach(string raw in File.ReadLines(InGame(exe,"game.projectc"))){string line=raw.Trim();if(line.StartsWith('[')){section=line.Trim('[',']');continue;}int at=line.IndexOf('=');if(at<0)continue;string key=line[..at].Trim(),value=line[(at+1)..].Trim();if(section=="script"&&key=="shared_state")shared=value=="1";if(section=="resource"&&key=="uri"&&value.Length>0)return false;}return shared;
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    private static string Hash(string p){using var s=File.OpenRead(p);return Convert.ToHexString(SHA256.HashData(s));}
    internal static bool HasRecord(string exe)=>File.Exists(InGame(exe,Folder+"/install.json"));
    internal static string InspectionStamp(string exe)=>exe+"|"+string.Join("|",new[]{"game.projectc","game.arci","game.arcd","game.dmanifest",Folder+"/install.json"}.Select(n=>{var f=new FileInfo(InGame(exe,n));return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    internal static bool IsInstalled(string exe)
    {
        try{var l=ReadLedger(exe);return l.BridgeHash==CurrentBridgeHash&&l.ExeHash==Hash(exe)&&l.Inputs.All(p=>Hash(InGame(exe,p.Key))==p.Value)&&l.Owned.All(p=>Hash(InGame(exe,Folder+"/"+p.Key))==p.Value);}catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return false;}
    }
    private static void ValidateOwned(string exe,string directory,Ledger ledger)
    {
        var folder=InGame(exe,directory);
        if(ledger.Owned.Any(p=>!File.Exists(InGame(exe,directory+"/"+p.Key))||Hash(InGame(exe,directory+"/"+p.Key))!=p.Value))throw new IOException("翻译文件已被更改，未替换。");
        foreach(string path in Directory.EnumerateFileSystemEntries(folder))
        {
            string name=Path.GetFileName(path);_ = InGame(exe,directory+"/"+name);
            if(Directory.Exists(path)||name!="install.json"&&!ledger.Owned.ContainsKey(name))throw new IOException("翻译目录含有额外文件，未替换。");
        }
    }
    // The fully prepared candidate replaces only a verified owned folder. Failed
    // preparation leaves the previous adapter usable; originals and saves are never moved.
    internal static void CommitStage(string exe,string stageDirectory,string? previousRecordHash)
    {
        Stopped(exe);
        if(!stageDirectory.StartsWith(Folder+"-stage-",StringComparison.Ordinal)||stageDirectory.IndexOfAny(['/', '\\'])>=0)throw new IOException("暂存目录无效。");
        var next=ReadLedger(exe,stageDirectory);ValidateOwned(exe,stageDirectory,next);
        if(next.BridgeHash!=CurrentBridgeHash||next.ExeHash!=Hash(exe)||next.Inputs.Any(p=>Hash(InGame(exe,p.Key))!=p.Value))throw new IOException("候选组件或游戏文件已变化，未替换。");
        string folder=InGame(exe,Folder),stage=InGame(exe,stageDirectory);
        if(previousRecordHash is null){if(Directory.Exists(folder))throw new IOException("翻译目录在准备期间出现，未覆盖。");Directory.Move(stage,folder);return;}
        if(Hash(InGame(exe,Folder+"/install.json"))!=previousRecordHash)throw new IOException("翻译记录在准备期间变化，未覆盖。");
        var prior=ReadLedger(exe);ValidateOwned(exe,Folder,prior);
        string backupDirectory=Folder+"-previous-"+Guid.NewGuid().ToString("N"),backup=InGame(exe,backupDirectory);
        Directory.Move(folder,backup);
        try{Directory.Move(stage,folder);}
        catch{if(!Directory.Exists(folder))Directory.Move(backup,folder);throw;}
        // Revalidate after the rename; do not delete any unexpected additions.
        try
        {
            ValidateOwned(exe,backupDirectory,prior);
            if(Hash(InGame(exe,backupDirectory+"/install.json"))!=previousRecordHash)return;
            foreach(string name in prior.Owned.Keys)File.Delete(InGame(exe,backupDirectory+"/"+name));
            File.Delete(InGame(exe,backupDirectory+"/install.json"));Directory.Delete(backup);
        }
        catch(IOException){/* A changed backup is retained for manual inspection. */}
        catch(UnauthorizedAccessException){/* The new adapter is complete; retain the old copy. */}
    }
    internal static string LuaBytes(byte[] b)=>"\""+string.Concat(b.Select(x=>"\\"+x.ToString("D3",System.Globalization.CultureInfo.InvariantCulture)))+"\"";
    internal static byte[] WrapScript(byte[] original,string bridge,IEnumerable<string> nodeIds,bool gui)
    {
        var module=Read(original);byte[] src=module.Single(x=>x.Number==1).Data;var source=Read(src);byte[]? code=source.SingleOrDefault(x=>x.Number==1)?.Data;
        var bytecode=source.SingleOrDefault(x=>x.Number==3)?.Data;var bytecode64=source.SingleOrDefault(x=>x.Number==4)?.Data;
        string load;
        if(bytecode is {Length:>0}){if(!bytecode.AsSpan().StartsWith(new byte[]{27,76,74}))throw new InvalidDataException("未知脚本字节码，未改写。");
            load="local bytes="+LuaBytes(bytecode)+"\n";if(bytecode64 is {Length:>0}&&!bytecode.AsSpan().SequenceEqual(bytecode64))load+="if jit and jit.arch=='x64' then bytes="+LuaBytes(bytecode64)+" end\n";
            load+="local original=assert(loadstring(bytes))\n";
        }else if(code is {Length:>0})load="local original=assert(loadstring("+LuaBytes(code)+"))\n";else throw new InvalidDataException("脚本内容为空。");
        string ids="{"+string.Join(",",nodeIds.Distinct().Select(x=>LuaBytes(Encoding.UTF8.GetBytes(x))))+"}";
        string wrapper="local bridge=(function()\n"+bridge+"\nend)()\n"+load+"setfenv(original,getfenv(1));original()\nbridge.wrap(getfenv(1),"+ids+","+(gui?"true":"false")+")\n";
        byte[] newSource=Write(source.Where(x=>x.Number is not (1 or 3 or 4)).Append(Bytes(1,Encoding.UTF8.GetBytes(wrapper))));return Replace(original,1,newSource);
    }
    internal static void Install(string exe)
    {
        Stopped(exe);if(IsInstalled(exe))return;if(DefoldTextCatalog.Detect(exe) is null||!Candidate(exe))throw new InvalidDataException("当前接入支持本地 v4 资源包及共享脚本环境。");
        string folder=InGame(exe,Folder);string? previousRecordHash=null;
        if(Directory.Exists(folder)){var prior=ReadLedger(exe);ValidateOwned(exe,Folder,prior);previousRecordHash=Hash(InGame(exe,Folder+"/install.json"));}
        var inputs=InputNames.Concat(File.Exists(InGame(exe,"game.public.der"))?["game.public.der"]:Array.Empty<string>()).ToDictionary(x=>x,x=>Hash(InGame(exe,x)),StringComparer.Ordinal);string exeHash=Hash(exe);
        string stageDirectory=Folder+"-stage-"+Guid.NewGuid().ToString("N"),stage=InGame(exe,stageDirectory);Directory.CreateDirectory(stage);
        try
        {
            using var archive=new DefoldArchive(exe);using var stream=typeof(DefoldGameAdapter).Assembly.GetManifestResourceStream("Fusion.DefoldTranslationBridge.lua")??throw new IOException("内嵌组件缺失。");using var reader=new StreamReader(stream);string bridge=reader.ReadToEnd();
            // Defold resolves application storage independently of APPDATA.
            // Only an explicitly marked test copy receives this save redirect.
            if(File.Exists(InGame(exe,GameTestCopyEnvironment.Marker)))
            {
                var test=new System.Diagnostics.ProcessStartInfo(exe);GameTestCopyEnvironment.Apply(test);
                string saves=InGame(exe,".fusion-test-data/saves");Directory.CreateDirectory(saves);
                bridge="if not rawget(_G,'__fusion_test_saves') then _G.__fusion_test_saves=true;local root="+LuaBytes(Encoding.UTF8.GetBytes(saves.Replace('\\','/')))+";local function safe(s) return tostring(s):gsub('[^%w]',function(c)return string.format('_%02x',c:byte()) end) end;sys.get_save_file=function(app,file) return root..'/'..safe(app)..'_'..safe(file) end end\n"+bridge;
            }
            var guis=new Dictionary<string,List<string>>(StringComparer.Ordinal);
            foreach(string n in archive.Names.Where(n=>n.EndsWith(".guic",StringComparison.Ordinal))){byte[] b=archive.Read(n);string script=Encoding.UTF8.GetString(One(b,1));if(!guis.TryGetValue(script,out var ids))guis[script]=ids=[];
                foreach(var node in Read(b).Where(x=>x.Number==6).Concat(Read(b).Where(x=>x.Number==9).SelectMany(x=>Read(x.Data).Where(y=>y.Number==2)))){var fields=Read(node.Data);if(fields.SingleOrDefault(x=>x.Number==6)?.Value==1&&fields.SingleOrDefault(x=>x.Number==11) is {} id)ids.Add(Encoding.UTF8.GetString(id.Data));}}
            var changes=new Dictionary<string,byte[]>(StringComparer.Ordinal);int scripts=0,fonts=0;
            foreach(string n in archive.Names)
            {
                if(n.EndsWith(".scriptc",StringComparison.Ordinal)||n.EndsWith(".gui_scriptc",StringComparison.Ordinal)){if(++scripts>128)throw new InvalidDataException("脚本数量超过本次接入上限。");changes[n]=WrapScript(archive.Read(n),bridge,guis.GetValueOrDefault(n)??[],n.EndsWith(".gui_scriptc",StringComparison.Ordinal));}
                else if(n.EndsWith(".fontc",StringComparison.Ordinal)){if(++fonts>16)throw new InvalidDataException("字库数量超过本次接入上限。");changes[n]=DefoldFontBuilder.Extend(archive.Read(n),Path.Combine(AppContext.BaseDirectory,"adapters","gamemaker","bridge-font.otf"));}
            }
            if(scripts==0||fonts==0)throw new InvalidDataException("未找到可验证的脚本和字库。");archive.Write(stage,changes);
            foreach(string n in new[]{"game.projectc","game.dmanifest","game.public.der"})if(File.Exists(InGame(exe,n)))File.Copy(InGame(exe,n),Path.Combine(stage,n));
            File.WriteAllText(Path.Combine(stage,"bridge-version.txt"),"FusionDefold/136 "+CurrentBridgeHash,new UTF8Encoding(false));
            var owned=Directory.EnumerateFiles(stage).ToDictionary(p=>Path.GetFileName(p),Hash,StringComparer.Ordinal);
            if(Hash(exe)!=exeHash||inputs.Any(p=>Hash(InGame(exe,p.Key))!=p.Value))throw new IOException("准备期间游戏文件发生变化，未安装。");
            File.WriteAllText(Path.Combine(stage,"install.json"),JsonSerializer.Serialize(new Ledger(133,exeHash,inputs,owned,CurrentBridgeHash)),new UTF8Encoding(false));CommitStage(exe,stageDirectory,previousRecordHash);
        }
        finally{if(Directory.Exists(stage)){foreach(string p in Directory.EnumerateFiles(stage))File.Delete(p);Directory.Delete(stage);}}
    }
    internal static void Restore(string exe)
    {
        Stopped(exe);string folder=InGame(exe,Folder);if(!Directory.Exists(folder))return;var l=ReadLedger(exe);
        if(l.Owned.Any(p=>!File.Exists(InGame(exe,Folder+"/"+p.Key))||Hash(InGame(exe,Folder+"/"+p.Key))!=p.Value))throw new IOException("翻译文件已变化，未删除。");
        foreach(var p in l.Owned)File.Delete(InGame(exe,Folder+"/"+p.Key));File.Delete(InGame(exe,Folder+"/install.json"));if(!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);
    }
    internal static RpgTextCatalogResult ReadCatalog(string exe,CancellationToken ct)
    {
        var r=DefoldTextCatalog.Read(exe,ct);
        var unlocalized=r.Entries.Where(e=>e.Location.StartsWith("gui-node:",StringComparison.Ordinal)&&UnityTextResourceLanguageSelection.ResourceLocale(e.File) is null).Select(e=>e.Text).ToHashSet(StringComparer.Ordinal);
        return new(r.Entries.Select(x=>x.Text).Distinct(StringComparer.Ordinal).ToArray(),r.Files.Count(x=>x.Status=="读取完成"),[]){UnlocalizedSourceTexts=unlocalized,ReadCompleted=false,Report=new(false,[new(CatalogIssueKind.Incomplete,"Defold","已读取资源内文本；动态拼接和编译后的脚本在游戏中补充。")])};
    }
}
