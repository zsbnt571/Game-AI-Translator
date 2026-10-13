using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class NativeGameSessionFiles
{
    internal static string Create(string exe,string engine,bool compact=false)
    {
        string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant())));
        string path=compact?Path.Combine(AppDataPaths.Root,"native-sessions",engine,Guid.NewGuid().ToString("N")):Path.Combine(AppDataPaths.Root,"native-sessions",engine,identity,Guid.NewGuid().ToString("N"));
        for(string? p=path;p is not null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("游戏通信目录包含链接，未启动。");
        Directory.CreateDirectory(path);return path;
    }

    // Steam's manifest provides the app identity for direct launches. Never
    // manufacture an ID or replace an explicit environment supplied by Steam.
    internal static void ApplySteamIdentity(ProcessStartInfo info)
    {
        if(info.Environment.TryGetValue("SteamAppId",out var existing)&&!string.IsNullOrWhiteSpace(existing))return;
        try
        {
            var root=new DirectoryInfo(Path.GetDirectoryName(info.FileName)!);
            if(root.Parent?.Name.Equals("common",StringComparison.OrdinalIgnoreCase)!=true||root.Parent.Parent?.Name.Equals("steamapps",StringComparison.OrdinalIgnoreCase)!=true)return;
            string? id=null;
            foreach(string path in Directory.EnumerateFiles(root.Parent.Parent.FullName,"appmanifest_*.acf").Take(2048))
            {
                if(new FileInfo(path).Length>65536)continue;
                string value=File.ReadAllText(path);
                var install=Regex.Match(value,"\"installdir\"\\s+\"([^\"]+)\"",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                if(!install.Success||!install.Groups[1].Value.Equals(root.Name,StringComparison.OrdinalIgnoreCase))continue;
                var app=Regex.Match(value,"\"appid\"\\s+\"([0-9]{1,10})\"",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
                if(!app.Success||id is not null)return;id=app.Groups[1].Value;
            }
            if(id is not null){info.Environment["SteamAppId"]=id;info.Environment["SteamGameId"]=id;}
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or RegexMatchTimeoutException){}
    }
}
