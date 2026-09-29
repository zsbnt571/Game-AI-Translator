using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class RpgTranslationText
{
    private static readonly Regex Codes=new(@"</?[A-Za-z][^>\r\n]{0,160}>|(?:\\|\x1b)(?:[A-Za-z]+(?:\[[^\]\r\n]*\])?|[.!|><^{}$\\])|\b(?:BGM|BGS|ME|SE|HP|MP|TP|ATK|DEF|MAT|MDF|EXP|LV|FPS)\b",RegexOptions.Compiled);
    internal static (string Text,string[] Codes) Protect(string value)
    {
        var codes=new List<string>();var text=Codes.Replace(value,m=>{var token="⟦RPG"+codes.Count+"⟧";codes.Add(m.Value);return token;});return(text,codes.ToArray());
    }
    internal static string Restore(string value,string[] codes)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>12000)throw new InvalidDataException("译文长度无效。");
        for(int i=0;i<codes.Length;i++){var token="⟦RPG"+i+"⟧";if(value.Split(token,StringSplitOptions.None).Length!=2)throw new InvalidDataException("译文没有保留游戏控制符，已保留原文。");value=value.Replace(token,codes[i]);}
        if(value.Contains("⟦RPG"))throw new InvalidDataException("译文包含未知控制符。");return value;
    }
}

