using System.Collections.ObjectModel;

namespace ScreenshotTranslationUiTester;

internal static class LibraryNavigationIcon
{
    internal static string EngineName(string? value)=>string.IsNullOrWhiteSpace(value)?"待识别":value;
    internal static string Count(LibraryNavigationTarget target,string text)
    {
        int at=text.LastIndexOf("  ",StringComparison.Ordinal);
        return target.Kind is "engine" or "collection"&&at>=0&&int.TryParse(text[(at+2)..],out _)?text[(at+2)..]:"";
    }
    internal static string Label(LibraryNavigationTarget target,string text)=>Count(target,text).Length>0?text[..text.LastIndexOf("  ",StringComparison.Ordinal)]:text;
    internal static string Key(LibraryNavigationTarget target, bool expanded) => target.Kind switch
    {
        "engine" or "heading" => expanded ? "down" : "next",
        "collection" or "folder" => "folder",
        "tag" => "layers",
        "tag-browser" => "tags",
        "group-tag" => "tag",
        "tag-settings" => "settings",
        "new-category" or "new-folder" or "add-root" => "plus",
        "game" => "game",
        _ => target.Value switch { "recent" => "history", "running" => "play", "favorite" => "star", _ => "grid" }
    };
}

internal sealed class LibraryNavigationNode(string text)
{
    internal string Name { get; set; } = "";
    internal string Text { get; set; } = text;
    internal object? Tag { get; set; }
    internal string ToolTipText { get; set; } = "";
    internal LibraryNavigationNode? Parent { get; set; }
    internal LibraryNavigationNodes? Owner { get; set; }
    internal int Level => Parent is null ? 0 : Parent.Level+1;
    internal LibraryNavigationNodes Nodes { get; } = new();
    internal void Remove() => Owner?.Remove(this);
}

internal sealed class LibraryNavigationNodes : Collection<LibraryNavigationNode>
{
    internal LibraryNavigationNode? Parent { get; set; }
    internal LibraryNavigationNode? this[string name] => this.FirstOrDefault(n=>n.Name==name);
    protected override void InsertItem(int index,LibraryNavigationNode node)
    {
        node.Remove();node.Parent=Parent;node.Owner=this;node.Nodes.Parent=node;base.InsertItem(index,node);
    }
    protected override void RemoveItem(int index) { var node=this[index];base.RemoveItem(index);node.Parent=null;node.Owner=null; }
    protected override void ClearItems() { foreach(var node in this){node.Parent=null;node.Owner=null;}base.ClearItems(); }
}

internal interface ILibraryNavigationSession : IDisposable
{
    System.Windows.FrameworkElement NativeView { get; }
    event Action<LibraryNavigationTarget>? Navigate;
    event Action<LibraryNavigationTarget,Point>? ContextRequested;
    void UpdateEntries(RecentGame[] games,LibraryMetadata metadata,Dictionary<string,string> engines,IEnumerable<string> activePaths,string? selectedPath,string scope,LibraryPreferences preferences,Dictionary<string,Bitmap>? newIcons=null,LibraryTagDocument? tagDocument=null);
    void UpdateRunning(IEnumerable<string> paths);
}
