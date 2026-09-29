using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Security.Cryptography;
namespace ScreenshotTranslationUiTester;
public enum SupportLevel
{
	Supported,
	Candidate,
	Planned,
	Unknown
}
public sealed record GameInfo(string ExePath, string Name, string Engine, string Architecture, string Details, SupportLevel Support, string? DataDirectory, string? AdapterId = null);
public static class GameDetector
{
	public static GameInfo Detect(string exePath)
	{
		exePath = Path.GetFullPath(exePath);
		if (!File.Exists(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("请拖入有效的 Windows 游戏 .exe 文件。");
		}
		string directoryName = Path.GetDirectoryName(exePath)!;
		string stem = Path.GetFileNameWithoutExtension(exePath);
		string? text = EngineStructureDetection.UnityDataDirectory(directoryName, stem);
		string architecture = ReadArchitecture(exePath);
		if(ToolStructureDetection.IsKnownHelper(exePath))return ToolStructureDetection.Detect(exePath,architecture)!;
		if (text != null)
		{
			string path = Path.Combine(Path.Combine(text, "Managed"), "Assembly-CSharp.dll");
			string path2 = Path.Combine(directoryName, "GameAssembly.dll");
			string text2 = ReadUnityVersion(Path.Combine(text, "globalgamemanagers"));
			if(text2=="未知版本")text2=ReadUnityVersion(Path.Combine(text,"data.unity3d"));
			var managed=Path.Combine(text,"Managed");
			// This Cloud Meadow build stores Mono in Data/Mono/EmbedRuntime, not Data/Mono itself.
			var monoPresent=File.Exists(Path.Combine(text,"Mono","EmbedRuntime","mono.dll"))
				||File.Exists(Path.Combine(text,"Mono","mono.dll"))
				||File.Exists(Path.Combine(directoryName,"MonoBleedingEdge","EmbedRuntime","mono-2.0-bdwgc.dll"));
			var appInfo=Path.Combine(text,"app.info");
			var product=File.Exists(appInfo)?File.ReadAllText(appInfo):"";
			if(monoPresent&&File.Exists(Path.Combine(managed,"Game.dll"))&&File.Exists(Path.Combine(managed,"mscorlib.dll")))
			{
				bool cloud=product.Split('\n').Any(x=>x.Trim()=="Cloud Meadow")&&product.Contains("Team Nimbus",StringComparison.Ordinal);
				bool exact=cloud&&architecture=="64 位"&&text2=="2017.4.2f2"&&File.Exists(Path.Combine(directoryName,"UnityPlayer.dll"))&&Matches(exePath,"2DBEFEB394DD4013CF1F16E75E0D571E081D8239D441A8DF62ACC29DD707E033")&&Matches(Path.Combine(managed,"Game.dll"),CloudGameSha256)&&Matches(Path.Combine(managed,"Unity.TextMeshPro.dll"),CloudTmpSha256);
				// Keep the verified legacy integration for its exact build. Other
				// builds must still reach the same structural Mono adapter as any
				// other game; a product name is not a compatibility exclusion.
				if(exact)return new GameInfo(exePath,stem,"Unity Mono",architecture,"Unity "+text2+" · Cloud Meadow 原有适配",SupportLevel.Candidate,text,"cloud-meadow");
			}
			if (File.Exists(path))
			{
				var infoPath=Path.Combine(text,"app.info");
				var isMgi=File.Exists(infoPath)&&File.ReadAllText(infoPath).Contains("Monster Girl Island",StringComparison.OrdinalIgnoreCase);
				if(isMgi)return new GameInfo(exePath, stem, "Unity Mono", architecture, "Unity " + text2 + " · MGI 原有适配",SupportLevel.Supported,text,"mgi");
			}
			if(UnityEmbeddedAdapter.Detect(exePath) is {} unity)
			{
				bool available=unity.Backend=="Mono"||unity.Architecture=="x64";
				return new(exePath,stem,"Unity "+unity.Backend,architecture,"Unity "+unity.UnityVersion+(available?" · 通用文本提取与内嵌翻译测试适配":" · 当前架构尚无翻译组件"),available?SupportLevel.Candidate:SupportLevel.Planned,text,available?"unity-"+unity.Backend.ToLowerInvariant():null);
			}
			if (File.Exists(path2))
			{
				return new GameInfo(exePath, stem, "Unity IL2CPP", architecture, "Unity " + text2 + " · IL2CPP 适配器尚未加入", SupportLevel.Planned, text);
			}
			return new GameInfo(exePath, stem, "Unity（未确定后端）", architecture, "Unity " + text2, SupportLevel.Planned, text);
		}
		if (Directory.Exists(Path.Combine(directoryName,"renpy")) || Directory.EnumerateFiles(directoryName, "*.rpa", SearchOption.TopDirectoryOnly).Any())
		{
			{ var engine=RenpyGameAdapter.Detect(exePath);
            return new GameInfo(exePath,stem,"Ren’Py",architecture,engine is null?"未找到桌面运行环境":"Ren’Py "+engine.Version+" · 剧情预译与内嵌显示",engine is null?SupportLevel.Planned:SupportLevel.Candidate,engine?.GameDirectory,engine is null?null:"renpy"); }
		}
		if (RpgMakerDataAdapter.Detect(exePath) is { } rpg)
		{
			return new GameInfo(exePath, stem, "RPG Maker "+rpg.Engine, architecture, "RPG 文字翻译与数据修改适配候选", SupportLevel.Candidate, rpg.DataRoot,"rpg-maker-"+rpg.Engine.ToLowerInvariant());
		}
		if(GodotGameAdapter.Detect(exePath) is {} godot)return new(exePath,stem,"Godot",architecture,"Godot "+godot.Version+" · 文本提取与内嵌翻译测试适配",SupportLevel.Candidate,directoryName,godot.AdapterId);
		if(ToolStructureDetection.Detect(exePath,architecture) is {} tool)return tool;
        if (EngineStructureDetection.Detect(exePath, stem, architecture) is { } packed)
        {
            if(packed.Engine=="Unreal Engine"&&UnrealGameAdapter.Detect(exePath) is {} unreal)
            {
                bool candidate=architecture=="64 位"&&UnrealGameAdapter.Candidate(unreal);
                return packed with {Details="UE "+unreal.Version+(candidate?" · 通用文本提取与内嵌翻译测试适配；需通过启动检查":" · 当前运行时尚未适配"),Support=candidate?SupportLevel.Candidate:SupportLevel.Planned,AdapterId=candidate?"unreal":null};
            }
            return packed;
        }
		return new GameInfo(exePath, stem, "未知引擎", architecture, "暂时无法内嵌；后续可使用 OCR 兼容模式", SupportLevel.Unknown, null);
	}

	private static string ReadArchitecture(string exePath)
	{
		using FileStream fileStream = File.OpenRead(exePath);
		using BinaryReader binaryReader = new BinaryReader(fileStream);
		if (fileStream.Length < 64 || binaryReader.ReadUInt16() != 23117)
		{
			return "未知架构";
		}
		fileStream.Position = 60L;
		int num = binaryReader.ReadInt32();
		if (num < 64 || num > fileStream.Length - 6) return "未知架构";
		fileStream.Position = num;
		if (binaryReader.ReadUInt32() != 0x00004550) return "未知架构";
		return binaryReader.ReadUInt16() switch
		{
			34404 => "64 位", 
			332 => "32 位", 
			43620 => "ARM64", 
			_ => "未知架构", 
		};
	}

	private static string ReadUnityVersion(string path)
	{
		if (!File.Exists(path))
		{
			return "未知版本";
		}
		using var input=File.OpenRead(path);byte[] array=new byte[Math.Min(input.Length,4096)];var length=input.Read(array,0,array.Length);
		Match match = Regex.Match(Encoding.ASCII.GetString(array, 0, length), "(?:20\\d{2}|6000|5)\\.\\d+\\.\\d+[abfp]\\d+");
		if (!match.Success)
		{
			return "未知版本";
		}
		return match.Value;
	}
	internal const string CloudGameSha256="312EF5A43CDBE8462316A6542C80014FE653090A37835B4C48F95CB682A5DD89";
	internal const string CloudTmpSha256="E137863D8A2F5EC4D5239763E9D87F6BE0512256E2CDBAF53DADE13C1939FC7D";
	private static bool Matches(string path,string hash){if(!File.Exists(path))return false;using var stream=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream))==hash;}
}
