using System.Collections.ObjectModel;

namespace ScreenshotTranslationUiTester;

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
    void UpdateEntries(RecentGame[] games,LibraryMetadata metadata,Dictionary<string,string> engines,IEnumerable<string> activePaths,string? selectedPath,string scope,LibraryPreferences preferences,Dictionary<string,Bitmap>? newIcons=null);
    void UpdateRunning(IEnumerable<string> paths);
}
