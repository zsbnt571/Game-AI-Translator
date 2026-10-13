using System.Text.RegularExpressions;
namespace FusionYukar;
// Same control grammar is compiled into the desktop queue and game bridge.
// Repeated arguments (for example party[0][2]) are a single instruction.
internal static class TextCodes
{
    internal const string Pattern=@"\\(?:\$\[[^\]\r\n]{0,256}\]|[A-Za-z][A-Za-z0-9_]*(?:\[[^\]\r\n]{0,256}\])*|[.!|><^{}$\\])";
    internal static readonly Regex At=new Regex(@"\G(?:"+Pattern+@")",RegexOptions.Compiled);
    internal static readonly Regex All=new Regex(Pattern,RegexOptions.Compiled);
}
