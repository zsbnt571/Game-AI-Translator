using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class RgssGameAdapter
{
    internal const string Folder=".fusion-rgss";
    internal sealed record Engine(string Name,int Version,string Scripts,string? Archive,bool Mkxp);
    private sealed record Ledger(int Version,string ExeHash,string OriginalIniHash,string InstalledIniHash,string OriginalScriptsHash,string ScriptPath,string? Archive,bool Mkxp,Dictionary<string,string> Files,string FontHash,bool FontsCreated,string? RuntimePath,string? RuntimeHash);
    private const string FontPath="Fonts/Fusion-R1-RGSS.otf";
    private static string At(string exe,string path)=>GameMakerGameAdapter.InGame(exe,path);
    private static string Hash(string path)=>GameMakerGameAdapter.Hash(path);
    private static string Digest(byte[] bytes)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    internal static string Bridge {get {using var input=typeof(RgssGameAdapter).Assembly.GetManifestResourceStream("Fusion.RgssTranslationBridge.rb")!;using var reader=new StreamReader(input);return reader.ReadToEnd();}}
    private static (string Text,Encoding Encoding,int Preamble) IniText(byte[] bytes)
    {
        if(bytes.Length>65536)throw new IOException("Game.ini 超过限制。");
        if(bytes.AsSpan().StartsWith(new byte[]{255,254}))return(Encoding.Unicode.GetString(bytes,2,bytes.Length-2),Encoding.Unicode,2);
        if(bytes.AsSpan().StartsWith(new byte[]{239,187,191}))return(Encoding.UTF8.GetString(bytes,3,bytes.Length-3),new UTF8Encoding(true),3);
        return(Encoding.Latin1.GetString(bytes),Encoding.Latin1,0);
    }
    private static Match Setting(string text,string key)
    {
        var sections=Regex.Matches(text,@"(?im)^\s*\[Game\]\s*\r?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(sections.Count!=1)throw new IOException("Game.ini 的游戏配置区不唯一。");
        int start=sections[0].Index+sections[0].Length;var next=Regex.Match(text[start..],@"(?m)^\s*\[",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        string section=text.Substring(start,next.Success?next.Index:text.Length-start);
        var values=Regex.Matches(section,@"(?im)^(?<head>[ \t]*"+Regex.Escape(key)+@"[ \t]*=[ \t]*)(?<value>[^\r\n]*?)(?<tail>[ \t]*)(?=\r?$)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        if(values.Count!=1)throw new IOException("Game.ini 的 "+key+" 配置缺失或重复。");
        // Return a match against the original text so replacement preserves all other bytes.
        return new Regex(@"\G(?<head>[ \t]*"+Regex.Escape(key)+@"[ \t]*=[ \t]*)(?<value>[^\r\n]*?)(?<tail>[ \t]*)(?=\r?$)",RegexOptions.IgnoreCase|RegexOptions.Multiline,TimeSpan.FromMilliseconds(100)).Match(text,start+values[0].Index);
    }
    internal static byte[] SetScripts(byte[] original,string scripts)=>SetSetting(original,"Scripts",scripts);
    private static string IniValue(byte[] bytes,string key)=>Setting(IniText(bytes).Text,key).Groups["value"].Value;
    private static bool SameIniPath(string a,string b)=>string.Equals(a.Replace('\\','/'),b.Replace('\\','/'),StringComparison.OrdinalIgnoreCase);
    internal static byte[] RestoreOwnedIni(byte[] original,byte[] current,bool mkxp)
    {
        string key=mkxp?"Scripts":"Library",before=IniValue(original,key);
        string installed=Folder+"/"+(mkxp?"Scripts"+Path.GetExtension(before):"FusionRgss.dll");
        string now=IniValue(current,key);
        // Games may add fullscreen, volume or window settings after launch.
        // Restore our one owned value in the CURRENT document, never the whole backup.
        if(SameIniPath(now,before))return current;
        if(!SameIniPath(now,installed))throw new IOException("Game.ini 的翻译接入项已有其他修改，未覆盖。原配置保留在翻译目录中。");
        return SetSetting(current,key,before);
    }
    private static bool OwnedIniInstalled(string exe,Ledger r)
    {
        byte[] original=File.ReadAllBytes(At(exe,Folder+"/Original.ini")),current=File.ReadAllBytes(At(exe,"Game.ini"));
        if(Digest(original)!=r.OriginalIniHash)return false;
        string key=r.Mkxp?"Scripts":"Library",other=r.Mkxp?"Library":"Scripts";
        string installed=Folder+"/"+(r.Mkxp?"Scripts"+Path.GetExtension(r.ScriptPath):"FusionRgss.dll");
        return Digest(SetSetting(original,key,installed))==r.InstalledIniHash
            &&SameIniPath(IniValue(current,key),installed)&&SameIniPath(IniValue(current,other),IniValue(original,other));
    }
    private static byte[] SetSetting(byte[] original,string key,string replacement)
    {
        var (text,encoding,preamble)=IniText(original);var match=Setting(text,key);var value=match.Groups["value"];
        byte[] body=encoding.GetBytes(text[..value.Index]+replacement+text[(value.Index+value.Length)..]);
        return preamble==0?body:original.AsSpan(0,preamble).ToArray().Concat(body).ToArray();
    }
    internal static Engine? Detect(string exe)
    {
        try
        {
            if(!File.Exists(At(exe,"Game.ini")))return null;
            byte[] ini=HasRecord(exe)?File.ReadAllBytes(At(exe,Folder+"/Original.ini")):File.ReadAllBytes(At(exe,"Game.ini"));
            string text=IniText(ini).Text,scripts=Setting(text,"Scripts").Groups["value"].Value.Replace('\\','/');
            string library=Setting(text,"Library").Groups["value"].Value;int version=Path.GetExtension(scripts).ToLowerInvariant() switch{".rxdata"=>1,".rvdata"=>2,".rvdata2"=>3,_=>0};
            if(version==0||scripts.Any(c=>c>127)||!scripts.StartsWith("Data/",StringComparison.OrdinalIgnoreCase))return null;
            _=At(exe,scripts);bool mkxp=File.Exists(At(exe,"mkxp.json"));
            if(mkxp)
            {
                string config=File.ReadAllText(At(exe,"mkxp.json"));if(config.Length>262144)return null;
                using var json=JsonDocument.Parse(config,new(){CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});var obj=json.RootElement;
                if(obj.ValueKind!=JsonValueKind.Object||obj.EnumerateObject().GroupBy(p=>p.Name).Any(g=>g.Count()>1))return null;
                if(obj.TryGetProperty("customScript",out var custom)&&!string.IsNullOrEmpty(custom.GetString()))return null;
                if(obj.TryGetProperty("execName",out var execName)&&execName.GetString() is {} en&&!en.Equals("Game",StringComparison.OrdinalIgnoreCase))return null;
                if(obj.TryGetProperty("gameFolder",out var root)&&root.GetString() is {} dir&&dir is not ("" or "."))return null;
                if(obj.TryGetProperty("rgssVersion",out var v)&&v.GetInt32()!=0&&v.GetInt32()!=version)return null;
                if(!Directory.EnumerateFiles(Path.GetDirectoryName(exe)!,"*ruby*.dll").Any())return null;
            }
            else
            {
                if(!Regex.IsMatch(Path.GetFileName(library),@"^RGSS[123][0-9]{2}[A-Za-z]?\.dll$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))return null;
                using var pe=new System.Reflection.PortableExecutable.PEReader(File.OpenRead(At(exe,library)));
                if(pe.PEHeaders.CoffHeader.Machine!=System.Reflection.PortableExecutable.Machine.I386)return null;
                using var mainPe=new System.Reflection.PortableExecutable.PEReader(File.OpenRead(exe));
                if(mainPe.PEHeaders.CoffHeader.Machine!=System.Reflection.PortableExecutable.Machine.I386)return null;
            }
            string extension=version==3?".rgss3a":version==2?".rgss2a":".rgssad";
            string[] archives=Directory.EnumerateFiles(Path.GetDirectoryName(exe)!,"*"+extension).ToArray();
            if(archives.Length>1)return null;string? archive=archives.SingleOrDefault();
            if(archive!=null){using var pack=new RgssArchive(archive);if(!pack.Entries.ContainsKey(scripts))return null;}
            else if(!File.Exists(At(exe,scripts)))return null;
            return new(version==1?"XP":version==2?"VX":"VX Ace",version,scripts,archive==null?null:Path.GetFileName(archive),mkxp);
        }
        catch(Exception ex)when(ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException or BadImageFormatException){return null;}
    }
    internal static byte[] ScriptBytes(string exe,Engine engine)
    {
        // RGSS archives take precedence when present; never mix a loose stale script with a packed game.
        if(engine.Archive!=null){using var archive=new RgssArchive(At(exe,engine.Archive));return archive.Read(engine.Scripts);}
        string path=At(exe,engine.Scripts);if(new FileInfo(path).Length>32*1024*1024)throw new IOException("RGSS 脚本包超过限制。");return File.ReadAllBytes(path);
    }
    private static void Stopped(string exe){if(FusionAdapterInstaller.ProcessState(new(exe,"RGSS","RPG Maker","","",SupportLevel.Candidate,null,"rgss"))!=FusionGameProcessState.Stopped)throw new IOException("请先退出游戏，再准备或恢复翻译。");}
    internal static bool HasRecord(string exe)=>File.Exists(At(exe,Folder+"/install.json"));
    private static Ledger ReadLedger(string exe)
    {
        string path=At(exe,Folder+"/install.json");if(new FileInfo(path).Length>16384)throw new IOException("RGSS 组件记录过大。");
        var r=JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path))??throw new IOException("RGSS 组件记录无效。");
        bool Sha(string? s)=>s is {Length:64}&&s.All(Uri.IsHexDigit);
        if(r.ScriptPath is not {Length:>0 and <4096})throw new IOException("RGSS 原脚本路径无效。");
        string patched=r.Mkxp?"Scripts"+Path.GetExtension(r.ScriptPath):"FusionRgss.dll";
        if(r.Version!=157||!Sha(r.ExeHash)||!Sha(r.OriginalIniHash)||!Sha(r.InstalledIniHash)||!Sha(r.OriginalScriptsHash)||!Sha(r.FontHash)||r.Files is null||r.Files.Count!=3||
           !r.Files.ContainsKey("Original.ini")||!r.Files.ContainsKey(patched)||!r.Files.ContainsKey("bridge.rb")||r.Files.Values.Any(v=>!Sha(v))||Path.GetExtension(r.ScriptPath).ToLowerInvariant() is not (".rxdata" or ".rvdata" or ".rvdata2"))throw new IOException("RGSS 组件记录的文件范围无效。");
        if(!r.Mkxp){if(r.RuntimePath is not {Length:>0 and <4096}||!Sha(r.RuntimeHash))throw new IOException("RGSS 原运行时记录无效。");_=At(exe,r.RuntimePath);}
        _=At(exe,r.ScriptPath);if(r.Archive!=null)_=At(exe,r.Archive);return r;
    }
    internal static string InspectionStamp(string exe)=>exe+"|"+string.Join("|",new[]{"Game.ini",Folder+"/install.json",Folder+"/Scripts.rxdata",Folder+"/Scripts.rvdata",Folder+"/Scripts.rvdata2",Folder+"/FusionRgss.dll",Folder+"/bridge.rb",FontPath}.Select(n=>{var f=new FileInfo(At(exe,n));return f.Exists?f.Length+":"+f.LastWriteTimeUtc.Ticks:"missing";}));
    internal static bool IsInstalled(string exe)
    {
        try{var r=ReadLedger(exe);var engine=Detect(exe);return engine!=null&&(r.Mkxp||Hash(At(exe,r.RuntimePath!))==r.RuntimeHash&&Hash(At(exe,Folder+"/FusionRgss.dll"))==Hash(Path.Combine(AppContext.BaseDirectory,"adapters/rgss/FusionRgss.dll")))&&Hash(exe)==r.ExeHash&&OwnedIniInstalled(exe,r)&&Hash(At(exe,FontPath))==r.FontHash&&Digest(ScriptBytes(exe,engine))==r.OriginalScriptsHash&&r.Files.All(p=>Hash(At(exe,Folder+"/"+p.Key))==p.Value)&&File.ReadAllText(At(exe,Folder+"/bridge.rb"))==Bridge;}
        catch(Exception e)when(e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or JsonException){return false;}
    }
    internal static void Install(string exe)
    {
        Stopped(exe);if(IsInstalled(exe))return;if(HasRecord(exe))Restore(exe);
        var engine=Detect(exe)??throw new EmbeddedCompatibilityException("此 RGSS 运行时或脚本包尚未支持，未修改游戏。");
        string folder=At(exe,Folder);if(Directory.Exists(folder)||File.Exists(folder))throw new IOException("游戏中已有同名组件目录，未覆盖。");
        byte[] original=File.ReadAllBytes(At(exe,"Game.ini")),scripts=ScriptBytes(exe,engine);
        string patchedName=engine.Mkxp?"Scripts"+Path.GetExtension(engine.Scripts):"FusionRgss.dll";
        byte[] patched=engine.Mkxp?RgssScriptBundle.Prepare(scripts,Bridge):File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"adapters/rgss/FusionRgss.dll"));
        byte[] ini=engine.Mkxp?SetScripts(original,Folder+"/"+patchedName):SetSetting(original,"Library",Folder+"/"+patchedName);
        string? runtimePath=engine.Mkxp?null:Setting(IniText(original).Text,"Library").Groups["value"].Value;
        string? runtimeHash=runtimePath is null?null:Hash(At(exe,runtimePath));
        string font=At(exe,FontPath),fontSource=Path.Combine(AppContext.BaseDirectory,"adapters/gamemaker/bridge-font.otf");
        if(File.Exists(font))throw new IOException("游戏中已有同名翻译字体，未覆盖。");
        bool fontsCreated=!Directory.Exists(At(exe,"Fonts"));string fontHash=Hash(fontSource);
        string stage=At(exe,Folder+"-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllBytes(Path.Combine(stage,"Original.ini"),original);File.WriteAllBytes(Path.Combine(stage,patchedName),patched);File.WriteAllText(Path.Combine(stage,"bridge.rb"),Bridge,new UTF8Encoding(false));
            var files=Directory.EnumerateFiles(stage).ToDictionary(p=>Path.GetFileName(p)!,p=>Hash(p));
            File.WriteAllText(Path.Combine(stage,"install.json"),JsonSerializer.Serialize(new Ledger(157,Hash(exe),Digest(original),Digest(ini),Digest(scripts),engine.Scripts,engine.Archive,engine.Mkxp,files!,fontHash,fontsCreated,runtimePath,runtimeHash)),new UTF8Encoding(false));
            Stopped(exe);if(Hash(At(exe,"Game.ini"))!=Digest(original)||Digest(ScriptBytes(exe,engine))!=Digest(scripts))throw new IOException("准备期间游戏文件发生变化，未覆盖。");
            Directory.Move(stage,folder);
            Directory.CreateDirectory(At(exe,"Fonts"));File.Copy(fontSource,font);
            // The ownership record is durable before the single replace. Restore handles either side of this transaction.
            AtomicReplace(At(exe,"Game.ini"),ini);
        }
        finally{if(Directory.Exists(stage)){foreach(var p in Directory.EnumerateFiles(stage))File.Delete(p);Directory.Delete(stage);}}
    }
    private static void AtomicReplace(string target,byte[] bytes)
    {
        string temp=target+".fusion-"+Guid.NewGuid().ToString("N");try{File.WriteAllBytes(temp,bytes);File.Move(temp,target,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal static void Restore(string exe)
    {
        Stopped(exe);if(!HasRecord(exe))return;var r=ReadLedger(exe);byte[] current=File.ReadAllBytes(At(exe,"Game.ini"));
        if(r.Files.Any(p=>Hash(At(exe,Folder+"/"+p.Key))!=p.Value)||Hash(At(exe,Folder+"/Original.ini"))!=r.OriginalIniHash)throw new IOException("RGSS 组件或备份已变化，未删除。");
        if(File.Exists(At(exe,FontPath))&&Hash(At(exe,FontPath))!=r.FontHash)throw new IOException("翻译字体已被改变，未删除。");
        if(Directory.EnumerateFileSystemEntries(At(exe,Folder)).Any(p=>Path.GetFileName(p)!="install.json"&&!r.Files.ContainsKey(Path.GetFileName(p))))throw new IOException("RGSS 组件目录含额外文件，未删除。");
        byte[] original=File.ReadAllBytes(At(exe,Folder+"/Original.ini"));
        string key=r.Mkxp?"Scripts":"Library",installed=Folder+"/"+(r.Mkxp?"Scripts"+Path.GetExtension(r.ScriptPath):"FusionRgss.dll");
        if(Digest(SetSetting(original,key,installed))!=r.InstalledIniHash)throw new IOException("RGSS 配置接入记录不一致，未覆盖。");
        byte[] restored=RestoreOwnedIni(original,current,r.Mkxp);
        Stopped(exe);if(Hash(At(exe,"Game.ini"))!=Digest(current))throw new IOException("恢复期间游戏配置发生变化，未覆盖。");
        if(!current.AsSpan().SequenceEqual(restored))AtomicReplace(At(exe,"Game.ini"),restored);
        if(File.Exists(At(exe,FontPath)))File.Delete(At(exe,FontPath));
        if(r.FontsCreated&&Directory.Exists(At(exe,"Fonts"))&&!Directory.EnumerateFileSystemEntries(At(exe,"Fonts")).Any())Directory.Delete(At(exe,"Fonts"));
        foreach(string n in r.Files.Keys)File.Delete(At(exe,Folder+"/"+n));File.Delete(At(exe,Folder+"/install.json"));Directory.Delete(At(exe,Folder));
    }
}
