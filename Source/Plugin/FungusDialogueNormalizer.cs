using System;
using System.Text.RegularExpressions;

namespace MGITranslator;

public static class FungusDialogueNormalizer
{
	private static readonly Regex TransparentHiddenText = new Regex(
		"<color\\s*=\\s*[\"']?#FFFFFF00[\"']?\\s*>([\\s\\S]*?)</color\\s*>",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);

	public static bool TryExtractCompleteText(string value, out string completeText)
	{
		completeText = NormalizeLineEndings(value);
		if (string.IsNullOrEmpty(value) || !TransparentHiddenText.IsMatch(value))
		{
			return false;
		}

		completeText = TransparentHiddenText.Replace(value, "$1");
		completeText = NormalizeLineEndings(completeText);
		return true;
	}

	private static string NormalizeLineEndings(string value)
	{
		if (value == null)
		{
			return "";
		}
		return value.Replace("\r\n", "\n").Trim();
	}
}
