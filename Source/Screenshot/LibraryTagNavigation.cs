namespace ScreenshotTranslationUiTester;

internal sealed record LibraryTagNavigationItem(string Key, string Text, LibraryNavigationTarget Target);
internal sealed record LibraryTagNavigationBranch(string Key, string Text, LibraryTagNavigationItem[] Items);
internal static class LibraryTagNavigation
{
    internal static LibraryTagNavigationBranch[] Branches(LibraryTagDocument? data, RecentGame[] games)
    {
        if (data is null) return [];
        var result = new List<LibraryTagNavigationBranch>();
        foreach (var (slot, id, title) in new[] { ("primary", data.Sidebar.Primary, "主分类"), ("secondary", data.Sidebar.Secondary, "副分类") })
        {
            var group = data.Groups.FirstOrDefault(g => g.Id == id); if (group is null) continue;
            var options = group.Values.Append(new LibraryTagValue(LibraryTagStore.Unset, "未标注"));
            var items = options.Select(value =>
            {
                int count = games.Count(game => value.Id == LibraryTagStore.Unset ? LibraryTagStore.Assigned(data, game.Id, id).Length == 0 : LibraryTagStore.Assigned(data, game.Id, id).Contains(value.Id));
                var tag = new LibraryTagSelection(id, value.Id);
                return new LibraryTagNavigationItem("group-tag:" + tag.Key, LibraryTagStore.DisplayName(value) + "  " + count, new("group-tag", tag.Key));
            }).ToArray();
            result.Add(new("tag-" + slot, UiLanguage.Text(title) + " · " + LibraryTagStore.DisplayName(group), items));
        }
        return result.ToArray();
    }
}
