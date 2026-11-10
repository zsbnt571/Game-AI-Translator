using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal sealed record GameMediaPreferences(InputBinding Screenshot,InputBinding Recording)
{
    internal static GameMediaPreferences Default=>new(new(InputBindingKind.Keyboard,Keys.F9,Ctrl:true,Alt:true),new(InputBindingKind.Keyboard,Keys.F10,Ctrl:true,Alt:true));
    internal static bool ValidBinding(InputBinding? binding)=>binding is not null&&(binding.IsEmpty||binding.Kind==InputBindingKind.Keyboard&&!binding.Win&&
        (binding.Key is >= Keys.F1 and <= Keys.F24||
         (binding.Ctrl||binding.Alt)&&binding.Key is >= Keys.A and <= Keys.Z||
         (binding.Ctrl||binding.Alt)&&binding.Key is >= Keys.D0 and <= Keys.D9));
    internal void Validate()
    {
        if(!ValidBinding(Screenshot)||!ValidBinding(Recording))throw new ArgumentException("请选择功能键，或 Ctrl／Alt 与字母、数字的组合。");
        if(!Screenshot.IsEmpty&&Screenshot==Recording)throw new ArgumentException("截图和录像不能使用相同快捷键。");
    }
    internal static GameMediaPreferences Load(string? dataRoot=null)
    {
        var path=Path.Combine(dataRoot??AppDataPaths.Root,"game-media-settings.json");GameMediaStore.CheckPath(path);
        if(!File.Exists(path))return Default;
        if(new FileInfo(path).Length>16384)throw new IOException("截图录像设置文件过大。");
        var result=JsonSerializer.Deserialize<GameMediaPreferences>(File.ReadAllText(path))??throw new IOException("截图录像设置无法读取。");
        result.Validate();return result;
    }
    internal void Save(string? dataRoot=null)
    {
        Validate();var path=Path.Combine(dataRoot??AppDataPaths.Root,"game-media-settings.json");GameMediaStore.CheckPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,this);stream.Flush(true);}
            if(File.Exists(path)){GameMediaStore.CheckPath(path+".bak");File.Replace(temporary,path,path+".bak",true);}else File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
