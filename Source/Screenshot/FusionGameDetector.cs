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
            string path2=Path.Combine(directoryName,"GameAssembly.dll");
            string text2=ReadUnityVersion(Path.Combine(text,"globalgamemanagers"));
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
		if(YukarGameAdapter.Detect(exePath) is {} yukar)return new(exePath,stem,"RPG Developer Bakin","64 位","文字绘制与变量修改测试接入；需要实机验证",SupportLevel.Candidate,yukar.Directory,"yukar-bakin");
		if(WebGameAdapter.Game(exePath,architecture) is {} web)return web;
        if(DefoldTextCatalog.Detect(exePath) is not null){bool candidate=DefoldGameAdapter.Candidate(exePath);return new(exePath,stem,"Defold",architecture,candidate?"游戏内文字翻译与中文字库接入（测试）":"已识别引擎，当前资源包或脚本环境尚未接入",candidate?SupportLevel.Candidate:SupportLevel.Planned,directoryName,candidate?"defold":null);}
        if(ExperimentalRpgCatalog.Detect(exePath) is {} experimental&&experimental=="WOLF RPG Editor"&&WolfGameAdapter.Candidate(exePath))return new(exePath,stem,experimental,architecture,"游戏内文字翻译接入（测试）",SupportLevel.Candidate,Path.Combine(directoryName,"Data"),"wolf");
        if(RgssGameAdapter.Detect(exePath) is {} rgss)return new(exePath,stem,"RPG Maker "+rgss.Name,architecture,(rgss.Mkxp?"mkxp-z":"RGSS "+rgss.Version)+" · 通用文字内嵌翻译测试接入",SupportLevel.Candidate,Path.Combine(directoryName,"Data"),"rgss");
        if(ExperimentalRpgCatalog.Detect(exePath) is {} readOnlyEngine)return new(exePath,stem,readOnlyEngine,architecture,"实验性只读文本提取；内嵌翻译与写回尚未接入",SupportLevel.Planned,Path.Combine(directoryName,"Data"));
		if(GameMakerGameAdapter.Detect(exePath) is {} gm)return new(exePath,stem,"GameMaker",architecture,"GameMaker "+gm.Version+(gm.Candidate?" · 通用脚本文字内嵌翻译测试接入":" · 已识别引擎，当前编译格式尚未接入"),gm.Candidate?SupportLevel.Candidate:SupportLevel.Planned,directoryName,gm.Candidate?"gamemaker":null);
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
}

