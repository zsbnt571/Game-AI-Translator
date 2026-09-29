using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace GameTranslatorManager;

public sealed class AdapterInstaller
{
	private sealed record InstallManifest(string GameExe, DateTimeOffset InstalledAt, List<InstalledFile> Files, bool BepInExExistedBefore = false);

	private sealed record InstalledFile(string RelativePath, bool ExistedBefore, string? BackupRelativePath);

	private const string ManifestName = ".game-translator-manager.json";

	private const string BackupFolder = ".game-translator-backup";

	private const string PluginRelativePath = "BepInEx/plugins/MGITranslator/MGITranslator.dll";

	private const long ExpectedPluginSize = 19456;

	private const string ExpectedPluginSha256 = "D5C1547CEC6374C0FA875563AAC43EA2C6FE2D043F54D46621567CC14C795EDC";

	public string LastVerificationReport { get; private set; } = "";

	public bool IsInstalled(GameInfo game)
	{
		return File.Exists(Path.Combine(Path.GetDirectoryName(game.ExePath), ".game-translator-manager.json"));
	}

	public bool HasManagedResidue(GameInfo game)
	{
		return Directory.Exists(Path.Combine(Path.GetDirectoryName(game.ExePath), ".game-translator-backup"));
	}

	public void Install(GameInfo game, TranslatorSettings settings)
	{
		if (game.Engine != "Unity Mono")
		{
			throw new InvalidOperationException("第一版目前只支持 Unity Mono 游戏。");
		}
		string directoryName = Path.GetDirectoryName(game.ExePath);
		string text = Path.Combine(directoryName, ".game-translator-backup");
		bool bepInExExistedBefore = Directory.Exists(Path.Combine(directoryName, "BepInEx"));
		Directory.CreateDirectory(text);
		List<InstalledFile> list = new List<InstalledFile>();
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameTranslatorManager.Payloads.UnityMono.zip") ?? throw new InvalidOperationException("程序内没有找到 Unity Mono 适配器。");
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		foreach (ZipArchiveEntry entry in zipArchive.Entries)
		{
			if (!string.IsNullOrEmpty(entry.Name))
			{
				string text2 = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
				string fullPath = Path.GetFullPath(Path.Combine(directoryName, text2));
				if (!fullPath.StartsWith(Path.GetFullPath(directoryName) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					throw new InvalidDataException("适配器包含不安全的文件路径。");
				}
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
				bool flag = File.Exists(fullPath);
				string text3 = null;
				if (flag)
				{
					text3 = Path.Combine(text, text2);
					Directory.CreateDirectory(Path.GetDirectoryName(text3));
					File.Copy(fullPath, text3, overwrite: true);
				}
				entry.ExtractToFile(fullPath, overwrite: true);
				list.Add(new InstalledFile(text2, flag, (text3 == null) ? null : Path.GetRelativePath(directoryName, text3)));
			}
		}
		VerifyInstalledPlugin(directoryName);
		WriteConfiguration(directoryName, settings);
		File.WriteAllText(Path.Combine(directoryName, ".game-translator-manager.json"), JsonSerializer.Serialize(new InstallManifest(game.ExePath, DateTimeOffset.Now, list, bepInExExistedBefore), new JsonSerializerOptions
		{
			WriteIndented = true
		}), Encoding.UTF8);
	}

	public void SaveSettings(GameInfo game, TranslatorSettings settings)
	{
		string? directoryName = Path.GetDirectoryName(game.ExePath);
		if (!IsInstalled(game))
		{
			throw new InvalidOperationException("请先安装翻译适配器。");
		}
		VerifyInstalledPlugin(directoryName);
		WriteConfiguration(directoryName, settings);
	}

	private void VerifyInstalledPlugin(string root)
	{
		string pluginPath = Path.Combine(root, PluginRelativePath.Replace('/', Path.DirectorySeparatorChar));
		if (!File.Exists(pluginPath))
		{
			LastVerificationReport =
				$"DLL 路径：{pluginPath}\n" +
				$"预期大小：{ExpectedPluginSize:N0} 字节\n" +
				$"实际大小：文件不存在\n" +
				$"预期 SHA-256：{ExpectedPluginSha256}\n" +
				$"实际 SHA-256：文件不存在\n" +
				"复制结果：失败\n验证结果：失败";
			throw new InvalidDataException("插件安装后验证失败。\n\n" + LastVerificationReport);
		}

		FileInfo file = new FileInfo(pluginPath);
		string actualHash;
		using (FileStream stream = File.OpenRead(pluginPath))
		using (SHA256 sha256 = SHA256.Create())
		{
			actualHash = Convert.ToHexString(sha256.ComputeHash(stream));
		}

		bool sizeMatches = file.Length == ExpectedPluginSize;
		bool hashMatches = string.Equals(actualHash, ExpectedPluginSha256, StringComparison.OrdinalIgnoreCase);
		bool verified = sizeMatches && hashMatches;
		LastVerificationReport =
			$"DLL 路径：{pluginPath}\n" +
			$"预期大小：{ExpectedPluginSize:N0} 字节\n" +
			$"实际大小：{file.Length:N0} 字节\n" +
			$"预期 SHA-256：{ExpectedPluginSha256}\n" +
			$"实际 SHA-256：{actualHash}\n" +
			"复制结果：成功\n" +
			$"验证结果：{(verified ? "通过" : "失败")}";

		if (!verified)
		{
			throw new InvalidDataException("插件安装后验证失败，管理器不会报告安装成功。\n\n" + LastVerificationReport);
		}
	}

	public void UpdateInstalled(GameInfo game, TranslatorSettings settings)
	{
		string directoryName = Path.GetDirectoryName(game.ExePath);
		if (!IsInstalled(game))
		{
			throw new InvalidOperationException("没有找到由本软件创建的安装记录。");
		}
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameTranslatorManager.Payloads.UnityMono.zip") ?? throw new InvalidOperationException("程序内没有找到 Unity Mono 适配器。");
		using ZipArchive zipArchive = new ZipArchive(stream, ZipArchiveMode.Read);
		foreach (ZipArchiveEntry entry in zipArchive.Entries)
		{
			if (!string.IsNullOrEmpty(entry.Name) && entry.FullName.StartsWith("BepInEx/plugins/MGITranslator/", StringComparison.OrdinalIgnoreCase))
			{
				string path = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
				string fullPath = Path.GetFullPath(Path.Combine(directoryName, path));
				Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
				entry.ExtractToFile(fullPath, overwrite: true);
			}
		}
		VerifyInstalledPlugin(directoryName);
		WriteConfiguration(directoryName, settings);
	}

	public void Uninstall(GameInfo game)
	{
		string directoryName = Path.GetDirectoryName(game.ExePath);
		string path = Path.Combine(directoryName, ".game-translator-manager.json");
		if (!File.Exists(path))
		{
			if (HasManagedResidue(game))
			{
				CleanupLegacyResidue(directoryName);
				return;
			}
			throw new InvalidOperationException("没有找到由本软件创建的安装记录或残留标记。");
		}
		InstallManifest installManifest = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path, Encoding.UTF8)) ?? throw new InvalidDataException("安装记录无法读取。");
		foreach (InstalledFile item in installManifest.Files.AsEnumerable().Reverse())
		{
			string fullPath = Path.GetFullPath(Path.Combine(directoryName, item.RelativePath));
			if (item.ExistedBefore && item.BackupRelativePath != null)
			{
				string fullPath2 = Path.GetFullPath(Path.Combine(directoryName, item.BackupRelativePath));
				if (File.Exists(fullPath2))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
					File.Copy(fullPath2, fullPath, overwrite: true);
				}
			}
			else if (File.Exists(fullPath))
			{
				File.Delete(fullPath);
			}
		}
		string text = Path.Combine(directoryName, "BepInEx");
		if (!installManifest.BepInExExistedBefore)
		{
			DeleteOwnedDirectory(text, directoryName);
		}
		else
		{
			DeleteOwnedDirectory(Path.Combine(text, "plugins", "MGITranslator"), directoryName);
			DeleteOwnedFile(Path.Combine(text, "config", "local.codex.mgi.translator.cfg"), directoryName);
			DeleteOwnedFile(Path.Combine(text, "config", "MGITranslator.cache.tsv"), directoryName);
			TryDeleteEmptyDirectories(Path.Combine(text, "plugins", "MGITranslator"), directoryName);
		}
		DeleteOwnedDirectory(Path.Combine(directoryName, ".game-translator-backup"), directoryName);
		File.Delete(path);
	}

	private static void CleanupLegacyResidue(string root)
	{
		string path = Path.Combine(root, ".game-translator-backup");
		if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Any())
		{
			throw new InvalidOperationException("旧备份目录中包含原有文件，软件不会自动删除。请保留该目录并手动检查。");
		}
		DeleteOwnedDirectory(Path.Combine(root, "BepInEx"), root);
		DeleteOwnedDirectory(path, root);
		string[] array = new string[4] { ".doorstop_version", "doorstop_config.ini", "winhttp.dll", "changelog.txt" };
		foreach (string path2 in array)
		{
			DeleteOwnedFile(Path.Combine(root, path2), root);
		}
	}

	private static void DeleteOwnedDirectory(string path, string root)
	{
		string value = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string fullPath = Path.GetFullPath(path);
		if (!fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("拒绝清理游戏目录之外的路径。");
		}
		if (Directory.Exists(fullPath))
		{
			Directory.Delete(fullPath, recursive: true);
		}
	}

	private static void DeleteOwnedFile(string path, string root)
	{
		string value = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string fullPath = Path.GetFullPath(path);
		if (!fullPath.StartsWith(value, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("拒绝清理游戏目录之外的路径。");
		}
		if (File.Exists(fullPath))
		{
			File.Delete(fullPath);
		}
	}

	private static void WriteConfiguration(string root, TranslatorSettings settings)
	{
		string path = Path.Combine(root, "BepInEx", "config", "local.codex.mgi.translator.cfg");
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		string contents = $"[General]\n\nEnabled = true\nScanIntervalSeconds = 0.6\nConcurrentRequests = 3\nBatchSize = 8\nRequestTimeoutSeconds = 15\n\n[API]\n\nEndpoint = {NormalizeEndpoint(settings.Endpoint)}\nApiKey = {settings.ApiKey.Trim()}\nModel = {settings.Model.Trim()}\n\n[Translation]\n\nTargetLanguage = {settings.TargetLanguage.Trim()}\nSystemPrompt = Translate game UI and dialogue naturally. Preserve names, placeholders, rich-text tags, and line breaks. Return only the translation.\n";
		File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private static string NormalizeEndpoint(string value)
	{
		string text = value.Trim().TrimEnd('/');
		if (text.Equals("https://api.deepseek.com", StringComparison.OrdinalIgnoreCase) || text.Equals("https://api.deepseek.com/v1", StringComparison.OrdinalIgnoreCase))
		{
			return "https://api.deepseek.com/chat/completions";
		}
		if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
		{
			return text + "/chat/completions";
		}
		return text;
	}

	private static void TryDeleteEmptyDirectories(string path, string stopAt)
	{
		string text = path;
		while (text != null && !text.Equals(stopAt, StringComparison.OrdinalIgnoreCase) && Directory.Exists(text) && !Directory.EnumerateFileSystemEntries(text).Any())
		{
			Directory.Delete(text);
			text = Path.GetDirectoryName(text);
		}
	}
}
public sealed record TranslatorSettings(string Endpoint, string ApiKey, string Model, string TargetLanguage);
public sealed class App : Application
{
	[STAThread]
	public static void Main()
	{
		new App().Run(new MainWindow());
	}
}
public enum SupportLevel
{
	Supported,
	Planned,
	Unknown
}
public sealed record GameInfo(string ExePath, string Name, string Engine, string Architecture, string Details, SupportLevel Support, string? DataDirectory);
public static class GameDetector
{
	public static GameInfo Detect(string exePath)
	{
		exePath = Path.GetFullPath(exePath);
		if (!File.Exists(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("请拖入有效的 Windows 游戏 .exe 文件。");
		}
		string directoryName = Path.GetDirectoryName(exePath);
		string stem = Path.GetFileNameWithoutExtension(exePath);
		string text = Directory.EnumerateDirectories(directoryName, "*_Data", SearchOption.TopDirectoryOnly).FirstOrDefault((string path3) => Path.GetFileName(path3).Equals(stem + "_Data", StringComparison.OrdinalIgnoreCase));
		if (text == null)
		{
			text = Directory.EnumerateDirectories(directoryName, "*_Data", SearchOption.TopDirectoryOnly).FirstOrDefault();
		}
		string architecture = ReadArchitecture(exePath);
		if (text != null)
		{
			string path = Path.Combine(Path.Combine(text, "Managed"), "Assembly-CSharp.dll");
			string path2 = Path.Combine(directoryName, "GameAssembly.dll");
			string text2 = ReadUnityVersion(Path.Combine(text, "globalgamemanagers"));
			if (File.Exists(path))
			{
				return new GameInfo(exePath, stem, "Unity Mono", architecture, "Unity " + text2 + " · 可使用游戏内插件", SupportLevel.Supported, text);
			}
			if (File.Exists(path2))
			{
				return new GameInfo(exePath, stem, "Unity IL2CPP", architecture, "Unity " + text2 + " · IL2CPP 适配器尚未加入", SupportLevel.Planned, text);
			}
			return new GameInfo(exePath, stem, "Unity（未确定后端）", architecture, "Unity " + text2, SupportLevel.Planned, text);
		}
		if (Directory.EnumerateFiles(directoryName, "*.rpa", SearchOption.AllDirectories).Any())
		{
			return new GameInfo(exePath, stem, "Ren'Py", architecture, "检测到 Ren'Py 资源 · 适配器尚未加入", SupportLevel.Planned, null);
		}
		if (File.Exists(Path.Combine(directoryName, "www", "js", "rpg_core.js")) || File.Exists(Path.Combine(directoryName, "www", "js", "rmmz_core.js")))
		{
			return new GameInfo(exePath, stem, "RPG Maker MV/MZ", architecture, "检测到 RPG Maker 脚本 · 适配器尚未加入", SupportLevel.Planned, null);
		}
		return new GameInfo(exePath, stem, "未知引擎", architecture, "暂时无法内嵌；后续可使用 OCR 兼容模式", SupportLevel.Unknown, null);
	}

	private static string ReadArchitecture(string exePath)
	{
		using FileStream fileStream = File.OpenRead(exePath);
		using BinaryReader binaryReader = new BinaryReader(fileStream);
		if (binaryReader.ReadUInt16() != 23117)
		{
			return "未知架构";
		}
		fileStream.Position = 60L;
		int num = binaryReader.ReadInt32();
		fileStream.Position = num + 4;
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
		byte[] array = File.ReadAllBytes(path);
		Match match = Regex.Match(Encoding.ASCII.GetString(array, 0, Math.Min(array.Length, 4096)), "(?:20\\d{2}|5)\\.\\d+\\.\\d+[abfp]\\d+");
		if (!match.Success)
		{
			return "未知版本";
		}
		return match.Value;
	}
}
public sealed class MainWindow : Window
{
	private readonly AdapterInstaller installer = new AdapterInstaller();

	private readonly SettingsStore settingsStore = new SettingsStore();

	private readonly Border dropPanel;

	private readonly TextBlock gameTitle;

	private readonly TextBlock detectionText;

	private readonly TextBlock statusText;

	private TextBox endpointBox;

	private PasswordBox apiKeyBox;

	private TextBox modelBox;

	private ComboBox languageBox;

	private ComboBox providerBox;

	private bool loadingProfile;

	private readonly Button installButton;

	private readonly Button launchButton;

	private readonly Button uninstallButton;

	private GameInfo? game;

	public MainWindow()
	{
		base.Title = "游戏 AI 翻译器 0.4.0-test";
		base.Width = 760.0;
		base.Height = 760.0;
		base.MinWidth = 680.0;
		base.MinHeight = 560.0;
		base.MaxHeight = SystemParameters.WorkArea.Height;
		base.WindowStartupLocation = WindowStartupLocation.CenterScreen;
		base.Background = new SolidColorBrush(Color.FromRgb(18, 22, 31));
		base.Foreground = Brushes.White;
		base.AllowDrop = true;
		base.Drop += OnDrop;
		base.DragOver += OnDragOver;
		Grid grid = new Grid
		{
			Margin = new Thickness(34.0)
		};
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = new GridLength(190.0)
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		grid.RowDefinitions.Add(new RowDefinition
		{
			Height = GridLength.Auto
		});
		base.Content = new ScrollViewer
		{
			Content = grid,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			PanningMode = PanningMode.VerticalOnly
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 20.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "游戏 AI 翻译器",
			FontSize = 28.0,
			FontWeight = FontWeights.SemiBold
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "把游戏应用程序拖进来，自动识别并安装内嵌翻译。",
			Foreground = new SolidColorBrush(Color.FromRgb(163, 172, 191)),
			FontSize = 14.0,
			Margin = new Thickness(0.0, 6.0, 0.0, 0.0)
		});
		Grid.SetRow(stackPanel, 0);
		grid.Children.Add(stackPanel);
		dropPanel = new Border
		{
			CornerRadius = new CornerRadius(16.0),
			BorderThickness = new Thickness(2.0),
			BorderBrush = new SolidColorBrush(Color.FromRgb(74, 91, 122)),
			Background = new SolidColorBrush(Color.FromRgb(26, 32, 44)),
			Padding = new Thickness(25.0),
			Cursor = Cursors.Hand
		};
		dropPanel.MouseLeftButtonUp += delegate
		{
			BrowseGame();
		};
		StackPanel stackPanel2 = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		UIElementCollection children = stackPanel2.Children;
		TextBlock obj = new TextBlock
		{
			Text = "拖入游戏 .exe",
			FontSize = 21.0,
			FontWeight = FontWeights.Medium,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		TextBlock element = obj;
		gameTitle = obj;
		children.Add(element);
		UIElementCollection children2 = stackPanel2.Children;
		TextBlock obj2 = new TextBlock
		{
			Text = "或点击这里选择文件",
			Foreground = new SolidColorBrush(Color.FromRgb(144, 157, 181)),
			FontSize = 14.0,
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
			TextAlignment = TextAlignment.Center
		};
		element = obj2;
		detectionText = obj2;
		children2.Add(element);
		dropPanel.Child = stackPanel2;
		Grid.SetRow(dropPanel, 1);
		grid.Children.Add(dropPanel);
		FrameworkElement element2 = CreateSettingsPanel();
		Grid.SetRow(element2, 2);
		grid.Children.Add(element2);
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 22.0, 0.0, 0.0)
		};
		installButton = CreateButton("安装翻译", Color.FromRgb(70, 112, byte.MaxValue));
		installButton.Click += delegate
		{
			Install();
		};
		launchButton = CreateButton("启动游戏", Color.FromRgb(45, 156, 112));
		launchButton.Click += delegate
		{
			Launch();
		};
		uninstallButton = CreateButton("恢复原版", Color.FromRgb(91, 99, 117));
		uninstallButton.Click += delegate
		{
			Uninstall();
		};
		stackPanel3.Children.Add(installButton);
		stackPanel3.Children.Add(launchButton);
		stackPanel3.Children.Add(uninstallButton);
		Grid.SetRow(stackPanel3, 3);
		grid.Children.Add(stackPanel3);
		statusText = new TextBlock
		{
			Text = "当前支持：Unity Mono（第一版）",
			Foreground = new SolidColorBrush(Color.FromRgb(138, 151, 176)),
			Margin = new Thickness(0.0, 18.0, 0.0, 0.0),
			TextWrapping = TextWrapping.Wrap
		};
		Grid.SetRow(statusText, 4);
		grid.Children.Add(statusText);
		UpdateButtons();
		LoadSelectedProfile();
		TryLoadLastGame();
	}

	private FrameworkElement CreateSettingsPanel()
	{
		Grid grid = new Grid
		{
			Margin = new Thickness(0.0, 24.0, 0.0, 0.0)
		};
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(125.0)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition());
		for (int i = 0; i < 5; i++)
		{
			grid.RowDefinitions.Add(new RowDefinition
			{
				Height = new GridLength(43.0)
			});
		}
		providerBox = new ComboBox
		{
			Background = new SolidColorBrush(Color.FromRgb(240, 243, 248)),
			Foreground = new SolidColorBrush(Color.FromRgb(22, 28, 38)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(64, 75, 96)),
			Padding = new Thickness(8.0, 5.0, 8.0, 5.0),
			ItemsSource = new string[2] { "OpenAI", "DeepSeek" }
		};
		providerBox.SelectionChanged += delegate
		{
			if (!loadingProfile)
			{
				LoadSelectedProfile();
			}
		};
		Grid grid2 = new Grid();
		grid2.ColumnDefinitions.Add(new ColumnDefinition());
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		Grid.SetColumn(providerBox, 0);
		grid2.Children.Add(providerBox);
		Button button = CreateButton("保存此方案", Color.FromRgb(91, 99, 117));
		button.Margin = new Thickness(10.0, 0.0, 0.0, 0.0);
		button.Padding = new Thickness(16.0, 8.0, 16.0, 8.0);
		button.Click += delegate
		{
			SaveCurrentProfile(showStatus: true);
		};
		Grid.SetColumn(button, 1);
		grid2.Children.Add(button);
		grid2.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		Button testApiButton = CreateButton("测试 API", Color.FromRgb(56, 124, 168));
		testApiButton.Margin = new Thickness(10.0, 0.0, 0.0, 0.0);
		testApiButton.Padding = new Thickness(16.0, 8.0, 16.0, 8.0);
		testApiButton.Click += async delegate
		{
			await TestApiAsync(testApiButton);
		};
		Grid.SetColumn(testApiButton, 2);
		grid2.Children.Add(testApiButton);
		AddSetting(grid, 0, "API 方案", grid2);
		endpointBox = AddTextSetting(grid, 1, "API 地址", "https://api.openai.com/v1/chat/completions");
		apiKeyBox = new PasswordBox
		{
			Background = new SolidColorBrush(Color.FromRgb(31, 38, 52)),
			Foreground = Brushes.White,
			BorderBrush = new SolidColorBrush(Color.FromRgb(64, 75, 96)),
			Padding = new Thickness(10.0, 7.0, 10.0, 7.0)
		};
		AddSetting(grid, 2, "API Key", apiKeyBox);
		modelBox = AddTextSetting(grid, 3, "模型", "gpt-4.1-mini");
		languageBox = new ComboBox
		{
			Background = new SolidColorBrush(Color.FromRgb(240, 243, 248)),
			Foreground = new SolidColorBrush(Color.FromRgb(22, 28, 38)),
			BorderBrush = new SolidColorBrush(Color.FromRgb(64, 75, 96)),
			Padding = new Thickness(8.0, 5.0, 8.0, 5.0),
			ItemsSource = new string[4] { "Simplified Chinese", "Traditional Chinese", "English", "Japanese" },
			SelectedIndex = 0
		};
		AddSetting(grid, 4, "目标语言", languageBox);
		return grid;
	}

	private static TextBox AddTextSetting(Grid panel, int row, string label, string value)
	{
		TextBox textBox = new TextBox
		{
			Text = value,
			Background = new SolidColorBrush(Color.FromRgb(31, 38, 52)),
			Foreground = Brushes.White,
			BorderBrush = new SolidColorBrush(Color.FromRgb(64, 75, 96)),
			Padding = new Thickness(10.0, 7.0, 10.0, 7.0)
		};
		AddSetting(panel, row, label, textBox);
		return textBox;
	}

	private static void AddSetting(Grid panel, int row, string label, UIElement control)
	{
		TextBlock element = new TextBlock
		{
			Text = label,
			VerticalAlignment = VerticalAlignment.Center,
			Foreground = new SolidColorBrush(Color.FromRgb(190, 199, 216))
		};
		Grid.SetRow(element, row);
		Grid.SetColumn(element, 0);
		panel.Children.Add(element);
		Grid.SetRow(control, row);
		Grid.SetColumn(control, 1);
		panel.Children.Add(control);
	}

	private static Button CreateButton(string text, Color color)
	{
		return new Button
		{
			Content = text,
			Background = new SolidColorBrush(color),
			Foreground = Brushes.White,
			BorderThickness = new Thickness(0.0),
			Padding = new Thickness(22.0, 11.0, 22.0, 11.0),
			Margin = new Thickness(0.0, 0.0, 12.0, 0.0),
			FontWeight = FontWeights.Medium,
			Cursor = Cursors.Hand
		};
	}

	private void OnDragOver(object sender, DragEventArgs e)
	{
		e.Effects = (e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None);
		e.Handled = true;
	}

	private void OnDrop(object sender, DragEventArgs e)
	{
		if (e.Data.GetData(DataFormats.FileDrop) is string[] array && array.Length != 0)
		{
			SelectGame(array[0]);
		}
	}

	private void BrowseGame()
	{
		OpenFileDialog openFileDialog = new OpenFileDialog
		{
			Filter = "Windows 游戏 (*.exe)|*.exe",
			Title = "选择游戏应用程序"
		};
		if (openFileDialog.ShowDialog(this) == true)
		{
			SelectGame(openFileDialog.FileName);
		}
	}

	private void SelectGame(string path)
	{
		try
		{
			game = GameDetector.Detect(path);
			gameTitle.Text = game.Name;
			detectionText.Text = $"{game.Engine} · {game.Architecture}\n{game.Details}";
			dropPanel.BorderBrush = new SolidColorBrush((game.Support == SupportLevel.Supported) ? Color.FromRgb(65, 190, 134) : Color.FromRgb(224, 166, 72));
			statusText.Text = ((game.Support != SupportLevel.Supported) ? "已识别游戏，但这个引擎的适配器还没有加入第一版。" : (installer.IsInstalled(game) ? "已安装翻译适配器，可以保存配置或启动游戏。" : "检测成功，可以安装翻译适配器。"));
			SaveLastGamePath(game.ExePath);
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.Message, "无法识别", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
		UpdateButtons();
	}

	private TranslatorSettings ReadSettings()
	{
		if (string.IsNullOrWhiteSpace(endpointBox.Text) || string.IsNullOrWhiteSpace(modelBox.Text))
		{
			throw new InvalidOperationException("API 地址和模型不能为空。");
		}
		return new TranslatorSettings(endpointBox.Text, apiKeyBox.Password, modelBox.Text, languageBox.SelectedItem?.ToString() ?? "Simplified Chinese");
	}

	private async Task TestApiAsync(Button button)
	{
		string original = button.Content?.ToString() ?? "测试 API";
		try
		{
			TranslatorSettings translatorSettings = ReadSettings();
			if (string.IsNullOrWhiteSpace(translatorSettings.ApiKey))
			{
				throw new InvalidOperationException("请先填写 API Key。");
			}
			button.IsHitTestVisible = false;
			button.Opacity = 0.55;
			button.Content = "测试中…";
			statusText.Text = "正在测试 API 连接和首字响应时间……";
			string text = NormalizeApiEndpoint(translatorSettings.Endpoint);
			string content = JsonSerializer.Serialize(text.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ? ((object)new
			{
				model = translatorSettings.Model,
				messages = new[]
				{
					new
					{
						role = "user",
						content = "Translate 'Hello' to Chinese. Return only the translation."
					}
				},
				thinking = new
				{
					type = "disabled"
				},
				temperature = 0.0,
				max_tokens = 16
			}) : ((object)new
			{
				model = translatorSettings.Model,
				messages = new[]
				{
					new
					{
						role = "user",
						content = "Translate 'Hello' to Chinese. Return only the translation."
					}
				},
				temperature = 0.0,
				max_tokens = 16
			}));
			using HttpClient client = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(20.0)
			};
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, text);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", translatorSettings.ApiKey);
			request.Content = new StringContent(content, Encoding.UTF8, "application/json");
			Stopwatch timer = Stopwatch.StartNew();
			using HttpResponseMessage response = await client.SendAsync(request);
			string text2 = await response.Content.ReadAsStringAsync();
			timer.Stop();
			if (!response.IsSuccessStatusCode)
			{
				string value = ((text2.Length > 240) ? text2.Substring(0, 240) : text2);
				throw new InvalidOperationException($"API 返回 {response.StatusCode}：{value}");
			}
			SaveCurrentProfile(showStatus: false);
			statusText.Text = $"API 测试成功，完整响应耗时 {timer.Elapsed.TotalSeconds:0.00} 秒。";
		}
		catch (TaskCanceledException)
		{
			statusText.Text = "API 测试超时（20 秒），请检查地址、网络或更换快速模型。";
		}
		catch (Exception ex2)
		{
			statusText.Text = "API 测试失败：" + ex2.Message;
		}
		finally
		{
			button.Content = original;
			button.IsHitTestVisible = true;
			button.Opacity = 1.0;
		}
	}

	private static string NormalizeApiEndpoint(string value)
	{
		string text = value.Trim().TrimEnd('/');
		if (text.Equals("https://api.deepseek.com", StringComparison.OrdinalIgnoreCase) || text.Equals("https://api.deepseek.com/v1", StringComparison.OrdinalIgnoreCase))
		{
			return "https://api.deepseek.com/chat/completions";
		}
		if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
		{
			return text + "/chat/completions";
		}
		return text;
	}

	private void Install()
	{
		if ((object)game == null)
		{
			return;
		}
		try
		{
			SaveCurrentProfile(showStatus: false);
			if (installer.IsInstalled(game))
			{
				installer.UpdateInstalled(game, ReadSettings());
				statusText.Text = "插件已更新并通过验证，配置已保存。\n" + installer.LastVerificationReport;
			}
			else
			{
				installer.Install(game, ReadSettings());
				statusText.Text = "安装完成并通过验证。游戏内按 F8 开关翻译。\n" + installer.LastVerificationReport;
			}
		}
		catch (UnauthorizedAccessException)
		{
			MessageBox.Show(this, "无法写入游戏目录。请把本软件关闭后，用“以管理员身份运行”重新打开。", "需要写入权限", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		}
		catch (Exception ex2)
		{
			MessageBox.Show(this, ex2.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		UpdateButtons();
	}

	private void Launch()
	{
		if ((object)game == null)
		{
			return;
		}
		try
		{
			if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.ExePath)).Any())
			{
				throw new InvalidOperationException("游戏仍在运行。请完全关闭游戏，再点击“启动游戏”，这样新版插件才能写入并加载。");
			}
			if (game.Support == SupportLevel.Supported && installer.IsInstalled(game))
			{
				SaveCurrentProfile(showStatus: false);
				installer.UpdateInstalled(game, ReadSettings());
				statusText.Text = "插件与配置已自动更新，正在启动游戏……";
			}
			Process.Start(new ProcessStartInfo
			{
				FileName = game.ExePath,
				WorkingDirectory = Path.GetDirectoryName(game.ExePath),
				UseShellExecute = true
			});
			statusText.Text = "游戏已启动。";
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void LoadSelectedProfile()
	{
		string text = providerBox.SelectedItem?.ToString() ?? settingsStore.State.SelectedProfile;
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		loadingProfile = true;
		try
		{
			providerBox.SelectedItem = text;
			ApiProfile profile = settingsStore.GetProfile(text);
			endpointBox.Text = profile.Endpoint;
			apiKeyBox.Password = settingsStore.ReadApiKey(profile);
			modelBox.Text = profile.Model;
			languageBox.SelectedItem = (languageBox.Items.Contains(profile.TargetLanguage) ? profile.TargetLanguage : "Simplified Chinese");
			settingsStore.State.SelectedProfile = text;
			settingsStore.Save();
		}
		finally
		{
			loadingProfile = false;
		}
	}

	private void SaveCurrentProfile(bool showStatus)
	{
		string text = providerBox.SelectedItem?.ToString() ?? "DeepSeek";
		settingsStore.SaveProfile(text, endpointBox.Text, apiKeyBox.Password, modelBox.Text, languageBox.SelectedItem?.ToString() ?? "Simplified Chinese");
		if (showStatus)
		{
			statusText.Text = "“" + text + "”方案已保存。以后切换到它会自动恢复地址、模型和密钥。";
		}
	}

	private void SaveLastGamePath(string exePath)
	{
		try
		{
			settingsStore.State.LastGameExe = exePath;
			settingsStore.Save();
		}
		catch
		{
		}
	}

	private void TryLoadLastGame()
	{
		try
		{
			string lastGameExe = settingsStore.State.LastGameExe;
			if (File.Exists(lastGameExe))
			{
				SelectGame(lastGameExe);
			}
		}
		catch
		{
		}
	}

	private void Uninstall()
	{
		if ((object)game == null)
		{
			return;
		}
		bool flag = !installer.IsInstalled(game) && installer.HasManagedResidue(game);
		string messageBoxText = (flag ? "清理旧版本遗留的 BepInEx、翻译缓存和备份目录？不会删除 Unity 崩溃报告。" : "恢复由本软件改动的文件？翻译缓存也将被删除。");
		if (MessageBox.Show(this, messageBoxText, "恢复原版", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
		{
			try
			{
				installer.Uninstall(game);
				statusText.Text = (flag ? "旧版本残留已清理。" : "已经完整恢复原版。");
			}
			catch (Exception ex)
			{
				MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Hand);
			}
			UpdateButtons();
		}
	}

	private void UpdateButtons()
	{
		bool flag = (object)game != null;
		bool flag2 = flag && game.Support == SupportLevel.Supported;
		bool flag3 = flag2 && installer.IsInstalled(game);
		bool flag4 = flag2 && installer.HasManagedResidue(game);
		SetButtonAvailable(installButton, flag2);
		installButton.Content = (flag3 ? "更新并保存" : "安装翻译");
		SetButtonAvailable(launchButton, flag);
		SetButtonAvailable(uninstallButton, flag3 || flag4);
		uninstallButton.Content = ((!flag3 && flag4) ? "清理残留" : "恢复原版");
	}

	private static void SetButtonAvailable(Button button, bool available)
	{
		button.IsHitTestVisible = available;
		button.Focusable = available;
		button.Opacity = (available ? 1.0 : 0.38);
	}
}
public sealed class SettingsStore
{
	private readonly string path;

	public AppSettings State { get; private set; }

	public SettingsStore()
	{
		string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameAITranslator");
		path = Path.Combine(text, "settings.json");
		State = Load();
		EnsureDefaults();
		ImportLegacyState(text);
	}

	public ApiProfile GetProfile(string name)
	{
		if (!State.Profiles.TryGetValue(name, out ApiProfile value))
		{
			value = CreateDefault(name);
			State.Profiles[name] = value;
		}
		return value;
	}

	public string ReadApiKey(ApiProfile profile)
	{
		if (string.IsNullOrWhiteSpace(profile.ProtectedApiKey))
		{
			return "";
		}
		try
		{
			return WindowsDataProtection.Unprotect(profile.ProtectedApiKey);
		}
		catch
		{
			return "";
		}
	}

	public void SaveProfile(string name, string endpoint, string apiKey, string model, string targetLanguage)
	{
		State.Profiles[name] = new ApiProfile
		{
			Endpoint = endpoint.Trim(),
			ProtectedApiKey = WindowsDataProtection.Protect(apiKey),
			Model = model.Trim(),
			TargetLanguage = targetLanguage.Trim()
		};
		State.SelectedProfile = name;
		Save();
	}

	public void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, JsonSerializer.Serialize(State, new JsonSerializerOptions
			{
				WriteIndented = true
			}), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch
		{
		}
	}

	private AppSettings Load()
	{
		try
		{
			if (File.Exists(path))
			{
				return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8)) ?? new AppSettings();
			}
		}
		catch
		{
		}
		return new AppSettings();
	}

	private void EnsureDefaults()
	{
		AppSettings state = State;
		if (state.Profiles == null)
		{
			Dictionary<string, ApiProfile> dictionary = (state.Profiles = new Dictionary<string, ApiProfile>());
		}
		if (!State.Profiles.ContainsKey("OpenAI"))
		{
			State.Profiles["OpenAI"] = CreateDefault("OpenAI");
		}
		if (!State.Profiles.ContainsKey("DeepSeek"))
		{
			State.Profiles["DeepSeek"] = CreateDefault("DeepSeek");
		}
		if (string.IsNullOrWhiteSpace(State.SelectedProfile) || !State.Profiles.ContainsKey(State.SelectedProfile))
		{
			State.SelectedProfile = "DeepSeek";
		}
	}

	private void ImportLegacyState(string folder)
	{
		try
		{
			bool flag = false;
			string text = Path.Combine(folder, "last-game.txt");
			if (string.IsNullOrWhiteSpace(State.LastGameExe) && File.Exists(text))
			{
				string lastGameExe = File.ReadAllText(text, Encoding.UTF8).Trim();
				if (File.Exists(lastGameExe))
				{
					State.LastGameExe = lastGameExe;
					flag = true;
				}
			}
			if (!string.IsNullOrWhiteSpace(State.LastGameExe))
			{
				string text2 = Path.Combine(Path.GetDirectoryName(State.LastGameExe), "BepInEx", "config", "local.codex.mgi.translator.cfg");
				if (File.Exists(text2))
				{
					Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
					string[] array = File.ReadAllLines(text2, Encoding.UTF8);
					for (int i = 0; i < array.Length; i++)
					{
						string text3 = array[i].Trim();
						int num = text3.IndexOf('=');
						if (num > 0 && !text3.StartsWith("#"))
						{
							dictionary[text3.Substring(0, num).Trim()] = text3.Substring(num + 1).Trim();
						}
					}
					dictionary.TryGetValue("Endpoint", out var value);
					string text4 = ((value != null && value.Contains("deepseek", StringComparison.OrdinalIgnoreCase)) ? "DeepSeek" : "OpenAI");
					ApiProfile profile = GetProfile(text4);
					dictionary.TryGetValue("ApiKey", out var value2);
					if (string.IsNullOrEmpty(profile.ProtectedApiKey) && !string.IsNullOrWhiteSpace(value2))
					{
						dictionary.TryGetValue("Model", out var value3);
						dictionary.TryGetValue("TargetLanguage", out var value4);
						State.Profiles[text4] = new ApiProfile
						{
							Endpoint = (value ?? profile.Endpoint),
							ProtectedApiKey = WindowsDataProtection.Protect(value2),
							Model = (value3 ?? profile.Model),
							TargetLanguage = (value4 ?? profile.TargetLanguage)
						};
						State.SelectedProfile = text4;
						flag = true;
					}
				}
			}
			if (flag)
			{
				Save();
			}
		}
		catch
		{
		}
	}

	private static ApiProfile CreateDefault(string name)
	{
		if (!(name == "DeepSeek"))
		{
			return new ApiProfile
			{
				Endpoint = "https://api.openai.com/v1/chat/completions",
				Model = "gpt-4.1-mini",
				TargetLanguage = "Simplified Chinese"
			};
		}
		return new ApiProfile
		{
			Endpoint = "https://api.deepseek.com",
			Model = "deepseek-v4-flash",
			TargetLanguage = "Simplified Chinese"
		};
	}
}
public sealed class AppSettings
{
	public string? LastGameExe { get; set; }

	public string SelectedProfile { get; set; } = "DeepSeek";

	public Dictionary<string, ApiProfile> Profiles { get; set; } = new Dictionary<string, ApiProfile>();
}
public sealed class ApiProfile
{
	public string Endpoint { get; set; } = "";

	public string ProtectedApiKey { get; set; } = "";

	public string Model { get; set; } = "";

	public string TargetLanguage { get; set; } = "Simplified Chinese";
}
internal static class WindowsDataProtection
{
	public static string Protect(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "";
		}
		return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
	}

	public static string Unprotect(string value)
	{
		byte[] encryptedData = Convert.FromBase64String(value);
		return Encoding.UTF8.GetString(ProtectedData.Unprotect(encryptedData, null, DataProtectionScope.CurrentUser));
	}
}
