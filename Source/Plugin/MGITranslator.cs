using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: AssemblyVersion("0.6.0.1")]
namespace MGITranslator;

[BepInPlugin("local.codex.mgi.translator", "Game Translator Fusion", "0.6.0.1")]
public sealed class TranslatorPlugin : BaseUnityPlugin
{
	private ConfigEntry<bool> enabledConfig;
    private ConfigEntry<KeyCode> toggleKey;
    private string effectivePrompt;

	private ConfigEntry<string> endpointConfig;

	private ConfigEntry<string> apiKeyConfig;

	private ConfigEntry<string> modelConfig;

	private ConfigEntry<string> targetLanguageConfig;

	private ConfigEntry<string> systemPromptConfig;

	private ConfigEntry<float> scanIntervalConfig;

	private ConfigEntry<int> concurrentRequestsConfig;

	private ConfigEntry<int> batchSizeConfig;

	private ConfigEntry<float> requestTimeoutConfig;

	private readonly object sync = new object();

	private readonly LinkedList<string> pending = new LinkedList<string>();

	private readonly HashSet<string> queued = new HashSet<string>();

	private readonly Dictionary<string, string> translations = new Dictionary<string, string>();

	private int activeRequests;

	private float nextApiAttempt;

	private float nextScan;

	private float lastRequestSeconds;

	private string lastStatus = "Ready";

	private Font chineseFont;

	private string cachePath;

	private int apiRequestCount;

	private int cacheWriteCount;

	private int fungusFramesObserved;

	private int fungusUniqueRequests;

	private int fungusCacheHits;

	private int fungusInFlightReuses;

	private static readonly Regex LatinLetter = new Regex("[A-Za-z]", RegexOptions.Compiled);

	private static readonly Regex OnlyNoise = new Regex("^[\\d\\s\\p{P}\\p{S}]+$", RegexOptions.Compiled);

	private void Awake()
	{
		enabledConfig = ((BaseUnityPlugin)this).Config.Bind<bool>("General", "Enabled", true, "Enable in-game translation. Press F8 to toggle.");
		endpointConfig = ((BaseUnityPlugin)this).Config.Bind<string>("API", "Endpoint", "https://api.openai.com/v1/chat/completions", "OpenAI-compatible chat completions endpoint.");
		apiKeyConfig = ((BaseUnityPlugin)this).Config.Bind<string>("API", "ApiKey", "", "API key. Keep this file private.");
		modelConfig = ((BaseUnityPlugin)this).Config.Bind<string>("API", "Model", "gpt-4.1-mini", "Model name supported by your API provider.");
		targetLanguageConfig = ((BaseUnityPlugin)this).Config.Bind<string>("Translation", "TargetLanguage", "Simplified Chinese", "Translation target language.");
		systemPromptConfig = ((BaseUnityPlugin)this).Config.Bind<string>("Translation", "SystemPrompt", "Translate game UI and dialogue naturally. Preserve names, placeholders, rich-text tags, and line breaks. Return only the translation.", "Instruction sent to the translation model.");
		scanIntervalConfig = ((BaseUnityPlugin)this).Config.Bind<float>("General", "ScanIntervalSeconds", 0.6f, "How often active UI text is inspected.");
		concurrentRequestsConfig = ((BaseUnityPlugin)this).Config.Bind<int>("General", "ConcurrentRequests", 3, "Maximum simultaneous translation requests. Recommended range: 2-4.");
		batchSizeConfig = ((BaseUnityPlugin)this).Config.Bind<int>("General", "BatchSize", 8, "Maximum text items combined into one API request.");
		requestTimeoutConfig = ((BaseUnityPlugin)this).Config.Bind<float>("General", "RequestTimeoutSeconds", 15f, "Cancel a translation request after this many seconds.");
		toggleKey = Config.Bind<KeyCode>("General", "ToggleKey", KeyCode.F8, "Toggle new text scanning; pending requests may finish.");
        var encoded = Config.Bind<string>("Translation", "PromptBase64", "", "Fusion effective translation instructions.");
        effectivePrompt = string.IsNullOrEmpty(encoded.Value) ? systemPromptConfig.Value : Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Value));
        string identity = "fusion-cache-v1|" + ResolveEndpoint(endpointConfig.Value) + "|" + modelConfig.Value + "|" + targetLanguageConfig.Value + "|" + effectivePrompt;
        using (var sha = SHA256.Create()) {
            string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").ToLowerInvariant();
            cachePath = Path.Combine(Paths.ConfigPath, "MGITranslator.fusion." + hash + ".tsv");
        }
        Logger.LogInfo("Fusion 0.6.0.1; cache=" + Path.GetFileName(cachePath) + "; target=" + targetLanguageConfig.Value);
		LoadCache();
		TryLoadChineseFont();
		Logger.LogInfo((object)"Fusion loaded. Toggle key stops new scanning; queued work may finish.");
	}

	private void Update()
	{
		if (Input.GetKeyDown(toggleKey.Value))
		{
			enabledConfig.Value = !enabledConfig.Value;
			Logger.LogInfo((object)("Translation " + (enabledConfig.Value ? "enabled" : "disabled")));
		}
		int num = Math.Max(1, Math.Min(6, concurrentRequestsConfig.Value));
		while (activeRequests < num && pending.Count > 0 && Time.unscaledTime >= nextApiAttempt)
		{
			((MonoBehaviour)this).StartCoroutine(TranslateNext());
		}
		if (enabledConfig.Value && !(Time.unscaledTime < nextScan))
		{
			nextScan = Time.unscaledTime + Math.Max(0.2f, scanIntervalConfig.Value);
			ScanActiveText();
		}
	}

	private void ScanActiveText()
	{
		Text[] array = Resources.FindObjectsOfTypeAll<Text>();
		foreach (Text val in array)
		{
			if ((UnityEngine.Object)(object)val == (UnityEngine.Object)null || !((Component)val).gameObject.activeInHierarchy)
			{
				continue;
			}
			bool isFungusFrame;
			string text = GetTranslationKey(val.text, out isFungusFrame);
			if (!ShouldTranslate(text))
			{
				continue;
			}
			lock (sync)
			{
				if (isFungusFrame)
				{
					fungusFramesObserved++;
				}
				if (translations.TryGetValue(text, out var value))
				{
					if (isFungusFrame)
					{
						fungusCacheHits++;
					}
					ApplyToLabel(val, value);
				}
				else if (!queued.Contains(text))
				{
					queued.Add(text);
					if (isFungusFrame)
					{
						fungusUniqueRequests++;
					}
					if (text.Length >= 8 || text.IndexOf(' ') >= 0)
					{
						pending.AddFirst(text);
					}
					else
					{
						pending.AddLast(text);
					}
				}
				else if (isFungusFrame)
				{
					fungusInFlightReuses++;
				}
			}
		}
	}

	private void ApplyToLabel(Text label, string translated)
	{
		if (!(label.text == translated))
		{
			label.text = translated;
			if ((UnityEngine.Object)(object)chineseFont != (UnityEngine.Object)null)
			{
				label.font = chineseFont;
			}
		}
	}

	private IEnumerator TranslateNext()
	{
		List<string> sources = new List<string>();
		lock (sync)
		{
			if (pending.Count == 0)
			{
				yield break;
			}
			int num = Math.Max(1, Math.Min(20, batchSizeConfig.Value));
			while (pending.Count > 0 && sources.Count < num)
			{
				sources.Add(pending.First.Value);
				pending.RemoveFirst();
			}
		}
		activeRequests++;
		lastStatus = "Requesting";
		if (string.IsNullOrEmpty(apiKeyConfig.Value))
		{
			Logger.LogWarning((object)"Translation paused: API key is empty.");
			for (int i = 0; i < sources.Count; i++)
			{
				queued.Remove(sources[i]);
			}
			activeRequests--;
			nextApiAttempt = Time.unscaledTime + 10f;
			lastStatus = "API key missing";
			yield break;
		}
		string value = effectivePrompt + "\nTarget language: " + targetLanguageConfig.Value + "\nTranslate every numbered item. Return exactly one line per item using this format: <T id=\"0\">translation</T>. Do not omit items.";
		StringBuilder stringBuilder = new StringBuilder();
		for (int j = 0; j < sources.Count; j++)
		{
			stringBuilder.Append("[[").Append(j).Append("]] ")
				.Append(sources[j])
				.Append('\n');
		}
		string text = ((endpointConfig.Value.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0) ? ",\"thinking\":{\"type\":\"disabled\"},\"max_tokens\":1200" : ",\"max_tokens\":1200");
		string s = "{\"model\":\"" + EscapeJson(modelConfig.Value) + "\",\"messages\":[{\"role\":\"system\",\"content\":\"" + EscapeJson(value) + "\"},{\"role\":\"user\",\"content\":\"" + EscapeJson(stringBuilder.ToString()) + "\"}],\"temperature\":0.2" + text + "}";
		byte[] bytes = Encoding.UTF8.GetBytes(s);
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		dictionary["Content-Type"] = "application/json";
		dictionary["Accept"] = "application/json";
		dictionary["Authorization"] = "Bearer " + apiKeyConfig.Value;
		string endpoint = ResolveEndpoint(endpointConfig.Value);
		float started = Time.realtimeSinceStartup;
		float timeout = Math.Max(5f, requestTimeoutConfig.Value);
		apiRequestCount++;
		WWW request = new WWW(endpoint, bytes, dictionary);
		try
		{
			while (!request.isDone && Time.realtimeSinceStartup - started < timeout)
			{
				yield return null;
			}
			lastRequestSeconds = Time.realtimeSinceStartup - started;
			if (!request.isDone)
			{
				Logger.LogWarning((object)("Translation timed out after " + lastRequestSeconds.ToString("0.0") + "s"));
				RemoveQueued(sources);
				nextApiAttempt = Time.unscaledTime + 2f;
				lastStatus = "Timeout " + lastRequestSeconds.ToString("0.0") + "s";
			}
			else if (!string.IsNullOrEmpty(request.error))
			{
				Logger.LogWarning((object)("Translation request failed. See service connectivity and configuration."));
				RemoveQueued(sources);
				nextApiAttempt = Time.unscaledTime + 5f;
				lastStatus = "API error";
			}
			else
			{
				float responseReceivedAt = Time.realtimeSinceStartup;
				string text2 = ExtractContent(request.text);
				Dictionary<int, string> dictionary2 = ParseBatch(text2);
				if (dictionary2.Count == 0 && sources.Count == 1 && !string.IsNullOrEmpty(text2))
				{
					dictionary2[0] = text2;
				}
				int num2 = 0;
				Dictionary<string, string> completed = new Dictionary<string, string>();
				for (int k = 0; k < sources.Count; k++)
				{
					if (dictionary2.TryGetValue(k, out var value2) && !string.IsNullOrEmpty(value2))
					{
						value2 = Normalize(value2.Trim());
						translations[sources[k]] = value2;
						AppendCache(sources[k], value2);
						cacheWriteCount++;
						completed[sources[k]] = value2;
						num2++;
					}
					queued.Remove(sources[k]);
				}
				if (completed.Count > 0)
				{
					TryImmediateApply(completed, responseReceivedAt);
				}
				if (num2 == 0)
				{
					Logger.LogWarning((object)"Translation failed: batch response format was not recognized.");
					nextApiAttempt = Time.unscaledTime + 2f;
					lastStatus = "Bad API response";
				}
				else
				{
					lastStatus = "Done " + num2 + " in " + lastRequestSeconds.ToString("0.0") + "s";
					Logger.LogInfo((object)(
						"Translated batch: " + num2 + "/" + sources.Count +
						" in " + lastRequestSeconds.ToString("0.0") + "s" +
						" | API calls " + apiRequestCount +
						" | cache writes " + cacheWriteCount +
						" | Fungus frames " + fungusFramesObserved +
						", unique requests " + fungusUniqueRequests +
						", in-flight reuses " + fungusInFlightReuses +
						", cache hits " + fungusCacheHits));
				}
			}
		}
		finally
		{
			((IDisposable)request)?.Dispose();
		}
		activeRequests--;
	}

	private void TryImmediateApply(Dictionary<string, string> completed, float responseReceivedAt)
	{
		try
		{
			Text[] array = Resources.FindObjectsOfTypeAll<Text>();
			int matched = 0;
			int applied = 0;
			foreach (Text label in array)
			{
				if ((UnityEngine.Object)(object)label == (UnityEngine.Object)null ||
					!((Component)label).gameObject.activeInHierarchy)
				{
					continue;
				}
				bool ignored;
				string current = GetTranslationKey(label.text, out ignored);
				if (!completed.TryGetValue(current, out var translated))
				{
					continue;
				}
				matched++;
				if (!(label.text == translated))
				{
					ApplyToLabel(label, translated);
					applied++;
				}
			}
			float delayMs = (Time.realtimeSinceStartup - responseReceivedAt) * 1000f;
			Logger.LogInfo((object)(
				"Immediate apply: sources " + completed.Count +
				", matched " + matched +
				", applied " + applied +
				", response-to-display " + delayMs.ToString("0.0") + " ms"));
		}
		catch (Exception ex)
		{
			Logger.LogWarning((object)(
				"Immediate apply failed; periodic scan will retry: " + ex.Message));
		}
	}

	private void RemoveQueued(List<string> sources)
	{
		for (int i = 0; i < sources.Count; i++)
		{
			queued.Remove(sources[i]);
		}
	}

	private static Dictionary<int, string> ParseBatch(string content)
	{
		Dictionary<int, string> dictionary = new Dictionary<int, string>();
		if (string.IsNullOrEmpty(content))
		{
			return dictionary;
		}
		MatchCollection matchCollection = Regex.Matches(content, "<T\\s+id=[\"']?(\\d+)[\"']?\\s*>([\\s\\S]*?)</T>", RegexOptions.IgnoreCase);
		for (int i = 0; i < matchCollection.Count; i++)
		{
			if (int.TryParse(matchCollection[i].Groups[1].Value, out var result))
			{
				dictionary[result] = matchCollection[i].Groups[2].Value.Trim();
			}
		}
		return dictionary;
	}

	private void OnGUI()
	{
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		if (enabledConfig.Value)
		{
			string text = "AI Translate  |  " + lastStatus + "  |  active " + activeRequests + "  |  queued " + pending.Count;
			float num = Math.Min(620f, Math.Max(330f, (float)text.Length * 7f));
			GUI.Box(new Rect((float)Screen.width - num - 12f, 12f, num, 30f), text);
		}
	}

	private static string ResolveEndpoint(string value)
	{
		string text = (value ?? "").Trim().TrimEnd('/');
		if (text.EndsWith("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
		{
			return text + "/chat/completions";
		}
		if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
		{
			return text + "/chat/completions";
		}
		return text;
	}

	private static string ExtractContent(string json)
	{
		int num = json.IndexOf("\"message\"", StringComparison.Ordinal);
		int num2 = json.IndexOf("\"content\"", (num >= 0) ? num : 0, StringComparison.Ordinal);
		if (num2 < 0)
		{
			return null;
		}
		int num3 = json.IndexOf(':', num2);
		int num4 = json.IndexOf('"', num3 + 1);
		if (num4 < 0)
		{
			return null;
		}
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		for (int i = num4 + 1; i < json.Length; i++)
		{
			char c = json[i];
			if (flag)
			{
				switch (c)
				{
				case 'n':
					stringBuilder.Append('\n');
					break;
				case 'r':
					stringBuilder.Append('\r');
					break;
				case 't':
					stringBuilder.Append('\t');
					break;
				case 'b':
					stringBuilder.Append('\b');
					break;
				case 'f':
					stringBuilder.Append('\f');
					break;
				case 'u':
				{
					if (i + 4 < json.Length && int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result))
					{
						stringBuilder.Append((char)result);
						i += 4;
					}
					break;
				}
				default:
					stringBuilder.Append(c);
					break;
				}
				flag = false;
			}
			else
			{
				switch (c)
				{
				case '\\':
					flag = true;
					break;
				case '"':
					return stringBuilder.ToString();
				default:
					stringBuilder.Append(c);
					break;
				}
			}
		}
		return null;
	}

	private static string EscapeJson(string value)
	{
		if (value == null)
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(value.Length + 16);
		foreach (char c in value)
		{
			switch (c)
			{
			case '\\':
				stringBuilder.Append("\\\\");
				continue;
			case '"':
				stringBuilder.Append("\\\"");
				continue;
			case '\n':
				stringBuilder.Append("\\n");
				continue;
			case '\r':
				stringBuilder.Append("\\r");
				continue;
			case '\t':
				stringBuilder.Append("\\t");
				continue;
			}
			if (c < ' ')
			{
				int num = c;
				stringBuilder.Append("\\u" + num.ToString("x4"));
			}
			else
			{
				stringBuilder.Append(c);
			}
		}
		return stringBuilder.ToString();
	}

	private static bool ShouldTranslate(string value)
	{
		if (string.IsNullOrEmpty(value) || value.Length < 2 || value.Length > 2000)
		{
			return false;
		}
		if (!LatinLetter.IsMatch(value) || OnlyNoise.IsMatch(value))
		{
			return false;
		}
		if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		return true;
	}

	private static string Normalize(string value)
	{
		if (value != null)
		{
			return value.Replace("\r\n", "\n").Trim();
		}
		return "";
	}

	private static string GetTranslationKey(string value, out bool isFungusFrame)
	{
		string completeText;
		isFungusFrame = FungusDialogueNormalizer.TryExtractCompleteText(value, out completeText);
		return isFungusFrame ? completeText : Normalize(value);
	}

	private void TryLoadChineseFont()
	{
		try
		{
			string[] array = new string[4] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial Unicode MS" };
			chineseFont = Font.CreateDynamicFontFromOSFont(array, 24);
		}
		catch (Exception ex)
		{
			Logger.LogWarning((object)("Could not load a Chinese system font: " + ex.Message));
		}
	}

	private void LoadCache()
	{
		if (!File.Exists(cachePath))
		{
			return;
		}
		try
		{
			string[] array = File.ReadAllLines(cachePath, Encoding.UTF8);
			foreach (string text in array)
			{
				int num = text.IndexOf('\t');
				if (num > 0)
				{
					string key = DecodeCache(text.Substring(0, num));
					string value = DecodeCache(text.Substring(num + 1));
					if (!translations.ContainsKey(key))
					{
						translations.Add(key, value);
					}
				}
			}
			Logger.LogInfo((object)("Loaded " + translations.Count + " cached translations."));
		}
		catch (Exception ex)
		{
			Logger.LogWarning((object)("Could not load translation cache: " + ex.Message));
		}
	}

	private void AppendCache(string source, string translated)
	{
		try
		{
			File.AppendAllText(cachePath, EncodeCache(source) + "\t" + EncodeCache(translated) + Environment.NewLine, Encoding.UTF8);
		}
		catch (Exception ex)
		{
			Logger.LogWarning((object)("Could not save translation cache: " + ex.Message));
		}
	}

	private static string EncodeCache(string value)
	{
		return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
	}

	private static string DecodeCache(string value)
	{
		return Encoding.UTF8.GetString(Convert.FromBase64String(value));
	}
}
