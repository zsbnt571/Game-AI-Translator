using System.Diagnostics;
using System.Text.Json;
namespace ScreenshotTranslationUiTester;

internal static class RenpyReconnect
{
    internal static string PathFor(string exe)=>Path.Combine(RenpyGameAdapter.Storage(exe),"connection.json");
    internal static void Publish(string exe,string pipe,string secret,Process peer)
    {
        var file=PathFor(exe);
        for(string? p=file;p is not null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("重新连接记录路径包含链接。");
        PortableDataStorage.WriteJson(file,new{protocol=156,pipe,secret,pid=peer.Id,started=peer.StartTime.ToUniversalTime().Ticks});
    }
    internal static int? Peer(string exe)
    {
        try
        {
            var file=PathFor(exe);if(!File.Exists(file)||new FileInfo(file).Length>4096)return null;
            using var record=JsonDocument.Parse(File.ReadAllText(file));var r=record.RootElement;
            if(r.GetProperty("protocol").GetInt32()!=156)return null;
            using var peer=Process.GetProcessById(r.GetProperty("pid").GetInt32());
            if(peer.HasExited||peer.StartTime.ToUniversalTime().Ticks!=r.GetProperty("started").GetInt64())return null;
            string image=peer.MainModule?.FileName??"";
            return StringComparer.OrdinalIgnoreCase.Equals(image,Path.GetFullPath(exe))||RenpyGameAdapter.Detect(exe)?.RuntimeExecutables.Contains(image,StringComparer.OrdinalIgnoreCase)==true?peer.Id:null;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException or System.ComponentModel.Win32Exception or KeyNotFoundException){return null;}
    }
}
