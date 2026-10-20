namespace ScreenshotTranslationUiTester;

// Presentation order only: API inputs and translation/source IDs stay intact.
internal static class PreviewTextFlow
{
    internal static IReadOnlyList<T> Order<T>(IEnumerable<T> source, Func<T, RectangleF> bounds, Func<T, int> order)
    {
        var items = source.OrderBy(order).ToArray();
        if (items.Length < 2 || items.Any(x => bounds(x).Width <= 0 || bounds(x).Height <= 0)) return items;
        return Columns(items, bounds, order, 0);
    }
    private static T[] Columns<T>(T[] items, Func<T, RectangleF> bounds, Func<T, int> order, int depth)
    {
        T[] LocalOrder() => depth == 0 ? items.OrderBy(order).ToArray() : items.OrderBy(x => bounds(x).Top).ThenBy(order).ToArray();
        if (items.Length < 4 || depth >= 12) return LocalOrder();
        var horizontal = items.OrderBy(x => bounds(x).Left).ToArray();
        float right = bounds(horizontal[0]).Right;
        float width = items.Max(x => bounds(x).Right) - items.Min(x => bounds(x).Left);
        for (int i = 1; i < horizontal.Length; i++)
        {
            var next = bounds(horizontal[i]);
            if (next.Left - right >= Math.Max(6, width * .006f) && i >= 2 && items.Length - i >= 2)
            {
                var a = horizontal[..i]; var b = horizontal[i..];
                float overlap = Math.Min(a.Max(x => bounds(x).Bottom), b.Max(x => bounds(x).Bottom)) - Math.Max(a.Min(x => bounds(x).Top), b.Min(x => bounds(x).Top));
                float typicalHeight = items.Select(x => bounds(x).Height).Order().ElementAt(items.Length / 2);
                if (overlap > typicalHeight * 1.5f)
                    return [..Columns(a, bounds, order, depth + 1), ..Columns(b, bounds, order, depth + 1)];
            }
            right = Math.Max(right, next.Right);
        }
        return LocalOrder();
    }
    internal static bool ContinuesBracket(string previous, string current, RectangleF a, RectangleF b)
    {
        if (a.Width <= 0 || b.Width <= 0 || b.Top < a.Top || b.Top - a.Bottom > Math.Max(a.Height, b.Height) * 1.6f) return false;
        float overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (overlap < Math.Min(a.Width, b.Width) * .45f) return false;
        foreach (var pair in new[] { ('[', ']'), ('（', '）'), ('(', ')'), ('【', '】') })
        {
            int balance = previous.Count(c => c == pair.Item1) - previous.Count(c => c == pair.Item2);
            if (balance > 0 && current.Contains(pair.Item2) && !current.Contains(pair.Item1)) return true;
        }
        return false;
    }
}
