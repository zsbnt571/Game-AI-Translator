using System;
using System.Collections.Generic;
using MGITranslator;

internal static class Program
{
	private static int failures;

	private static void Main()
	{
		ExtractsKnownTransparentSuffix();
		AllTypewriterFramesShareOneKey();
		PreservesLineBreaksAndPunctuation();
		PreservesOrdinaryRichText();
		DoesNotAcceptOtherTransparentColors();
		SimulatesOneRequestAndOneCacheEntry();

		if (failures != 0)
		{
			throw new Exception(failures + " Fungus dialogue test(s) failed.");
		}
		Console.WriteLine("PASS: 6 Fungus dialogue tests");
	}

	private static void ExtractsKnownTransparentSuffix()
	{
		AssertExtract(
			"Please go<color=#FFFFFF00> find those crabs.</color>",
			"Please go find those crabs.");
	}

	private static void AllTypewriterFramesShareOneKey()
	{
		string[] frames =
		{
			"P<color=#FFFFFF00>lease go find those crabs.</color>",
			"Please go<color=#FFFFFF00> find those crabs.</color>",
			"Please go find those<color=#FFFFFF00> crabs.</color>"
		};
		foreach (string frame in frames)
		{
			AssertExtract(frame, "Please go find those crabs.");
		}
	}

	private static void PreservesLineBreaksAndPunctuation()
	{
		AssertExtract(
			"Please go.<color=#FFFFFF00>\r\nI'm counting on you!</color>",
			"Please go.\nI'm counting on you!");
	}

	private static void PreservesOrdinaryRichText()
	{
		AssertExtract(
			"<b>Please</b><color=#FFFFFF00> go now.</color>",
			"<b>Please</b> go now.");
	}

	private static void DoesNotAcceptOtherTransparentColors()
	{
		string output;
		bool extracted = FungusDialogueNormalizer.TryExtractCompleteText(
			"Please<color=#FF000000> wait.</color>", out output);
		Assert(!extracted, "A different color must not be treated as the confirmed Fungus pattern.");
	}

	private static void SimulatesOneRequestAndOneCacheEntry()
	{
		HashSet<string> queued = new HashSet<string>();
		Dictionary<string, string> cache = new Dictionary<string, string>();
		int apiRequests = 0;
		string[] frames =
		{
			"P<color=#FFFFFF00>lease go find those crabs.</color>",
			"Please<color=#FFFFFF00> go find those crabs.</color>",
			"Please go<color=#FFFFFF00> find those crabs.</color>"
		};

		foreach (string frame in frames)
		{
			string key;
			Assert(FungusDialogueNormalizer.TryExtractCompleteText(frame, out key), "Frame must be recognized.");
			if (!cache.ContainsKey(key) && queued.Add(key))
			{
				apiRequests++;
			}
		}
		string completedKey;
		FungusDialogueNormalizer.TryExtractCompleteText(frames[0], out completedKey);
		cache[completedKey] = "请去找那些螃蟹。";

		Assert(apiRequests == 1, "Typewriter frames must create exactly one API request.");
		Assert(cache.Count == 1, "Typewriter frames must create exactly one permanent cache entry.");
		Assert(!cache.ContainsKey(frames[0]), "Raw tagged frames must never become cache keys.");
	}

	private static void AssertExtract(string input, string expected)
	{
		string actual;
		bool extracted = FungusDialogueNormalizer.TryExtractCompleteText(input, out actual);
		Assert(extracted, "Expected confirmed Fungus frame.");
		Assert(actual == expected, "Expected [" + expected + "] but got [" + actual + "].");
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
		{
			failures++;
			Console.Error.WriteLine("FAIL: " + message);
		}
	}
}
