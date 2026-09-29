using System.Drawing.Drawing2D;
namespace ScreenshotTranslationUiTester;
internal sealed record LibraryNavigationTarget(string Kind,string Value="");
// The TreeView below is a data model only: no native window or native scrollbars.
// All visible rows are composed in one double-buffered paint pass.
internal sealed class LibraryNavigationTree:Control
{
    private readonly TreeView model=new();
    private readonly ToolTip tips=new();
    private readonly Dictionary<string,Bitmap> icons=new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> running=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expanded=new(StringComparer.Ordinal){"engines","categories"};
    private List<TreeNode> rows=[];
    private string? selectionKey,hoverKey;
    private int offset,grab;
    private bool dragging,overScroll;
    internal TreeNodeCollection Nodes=>model.Nodes;
    internal TreeNode? SelectedNode {get;private set;}
    internal int ScrollOffset=>offset;
    internal int ItemHeight=>Math.Max(S(28),Font.Height+S(12));
    internal int ScrollMaximum=>Math.Max(0,rows.Count*ItemHeight-ClientSize.Height);
    internal event Action<LibraryNavigationTarget>? Navigate;
    internal event Action<LibraryNavigationTarget,Point>? ContextRequested;
    private sealed record Entry(string Key,string Text,LibraryNavigationTarget Target,string ToolTip="")
    {internal List<Entry> Children {get;}=[];}
    internal LibraryNavigationTree()
    {
        TabStop=true;AccessibleName="游戏导航";AccessibleRole=AccessibleRole.Outline;
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
    }
    private int S(int n)=>n*DeviceDpi/96;
    private Rectangle Track=>ScrollMaximum>0?new(Math.Max(0,Width-S(10)),S(3),S(10),Math.Max(0,Height-S(6))):Rectangle.Empty;
    private Rectangle Thumb
    {get{var r=Track;if(r.Height<=0)return Rectangle.Empty;int h=Math.Clamp((int)((long)r.Height*Height/Math.Max(1,rows.Count*ItemHeight)),Math.Min(S(30),r.Height),r.Height);return new(r.X,r.Y+(int)((long)(r.Height-h)*offset/Math.Max(1,ScrollMaximum)),r.Width,h);}}
    internal void UpdateEntries(RecentGame[] games,LibraryMetadata metadata,Dictionary<string,string> engines,IEnumerable<string> activePaths,string? selectedPath,string scope,LibraryPreferences preferences,Dictionary<string,Bitmap>? newIcons=null)
    {
        var oldRows=rows.Select(n=>(n.Name,n.Text,n.Level)).ToArray();var oldRunning=running;var oldSelection=SelectedNode?.Name;
        bool imagesChanged=false;
        if(newIcons is not null)
        {
            // Keep owned images when pixels are unchanged; ordinary selection and
            // polling must not repaint the entire navigation list.
            foreach(var key in icons.Keys.Except(newIcons.Keys,StringComparer.OrdinalIgnoreCase).ToArray()){icons[key].Dispose();icons.Remove(key);imagesChanged=true;}
            foreach(var pair in newIcons){if(icons.TryGetValue(pair.Key,out var prior)&&SameImage(prior,pair.Value)){pair.Value.Dispose();continue;}prior?.Dispose();icons[pair.Key]=pair.Value;imagesChanged=true;}
        }
        running=activePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries=new List<Entry>();
        Entry Add(List<Entry> into,string key,string text,string kind,string value="",string tooltip="")
        {var entry=new Entry(key,text,new(kind,value),tooltip);into.Add(entry);return entry;}
        Add(entries,"home","库首页","scope","home");Add(entries,"recent","最近使用","scope","recent");Add(entries,"running","正在运行  "+running.Count,"scope","running");Add(entries,"favorite","收藏","scope","favorite");
        var engineRoot=Add(entries,"engines","按引擎","heading");
        foreach(var group in games.GroupBy(x=>engines.GetValueOrDefault(x.ExePath)??metadata.Get(x.Id).Engine).OrderBy(x=>x.Key))
        {
            var name=string.IsNullOrWhiteSpace(group.Key)?"待识别":group.Key;
            var engine=Add(engineRoot.Children,"engine:"+name,name+"  "+group.Count(),"engine",name);
            foreach(var game in group.OrderBy(x=>x.Name))Add(engine.Children,"game:"+game.Id,game.Name,"game",game.ExePath);
        }
        var folderRoot=Add(entries,"folders","按文件夹","heading");
        foreach(var folder in LibraryFolders.All(preferences))
            Add(folderRoot.Children,"collection:"+folder.Id,folder.Name+"  "+games.Count(g=>LibraryFolders.Contains(folder,g)),"collection",folder.Id,folder.Root??"库内收纳文件夹");
        Add(folderRoot.Children,"add-root","添加游戏总目录…","add-root");
        Add(folderRoot.Children,"new-folder","新建文件夹…","new-folder");
        var categories=Add(entries,"categories","我的分类","heading","categories");
        var tags=(preferences.Categories??[]).Concat(games.SelectMany(x=>metadata.Get(x.Id).Tags.Split([',','，',';','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))).Distinct().OrderBy(x=>x);
        foreach(var tag in tags)Add(categories.Children,"tag:"+tag,tag,"tag",tag);
        Add(entries,"add-category","新建分类","new-category");
        Add(entries,"add-library-folder","新建文件夹","new-folder");


        Reconcile(Nodes,entries);
        var requested=AllNodes(Nodes).FirstOrDefault(n=>n.Tag is LibraryNavigationTarget {Kind:"game"} t&&selectedPath is not null&&string.Equals(t.Value,selectedPath,StringComparison.OrdinalIgnoreCase))??AllNodes(Nodes).FirstOrDefault(n=>n.Name==scope);
        string requestedKey=requested?.Name??scope;
        if(selectionKey!=requestedKey||SelectedNode is null||!AllNodes(Nodes).Contains(SelectedNode))
        {SelectedNode=requested;if(selectedPath is not null&&requested?.Parent is {} parent)expanded.Add(parent.Name);}
        selectionKey=requestedKey;Flatten();
        if(imagesChanged||!oldRows.SequenceEqual(rows.Select(n=>(n.Name,n.Text,n.Level)))||!oldRunning.SetEquals(running))Invalidate();
        else if(oldSelection!=SelectedNode?.Name){InvalidateRow(oldSelection);InvalidateRow(SelectedNode?.Name);}
    }
    private static void Reconcile(TreeNodeCollection nodes,List<Entry> entries)
    {
        var wanted=entries.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);
        for(int i=nodes.Count-1;i>=0;i--)if(!wanted.Contains(nodes[i].Name))nodes.RemoveAt(i);
        for(int i=0;i<entries.Count;i++)
        {
            var entry=entries[i];var node=nodes.Cast<TreeNode>().FirstOrDefault(x=>x.Name==entry.Key);
            if(node is null){node=new TreeNode(entry.Text){Name=entry.Key,Tag=entry.Target,ToolTipText=entry.ToolTip};nodes.Insert(i,node);}
            else
            {
                if(nodes.IndexOf(node)!=i){node.Remove();nodes.Insert(i,node);}
                if(node.Text!=entry.Text)node.Text=entry.Text;
                if(!Equals(node.Tag,entry.Target))node.Tag=entry.Target;
                if(node.ToolTipText!=entry.ToolTip)node.ToolTipText=entry.ToolTip;
            }
            Reconcile(node.Nodes,entry.Children);
        }
    }
    private static bool SameImage(Bitmap a,Bitmap b)
    {
        if(a.Size!=b.Size)return false;
        var area=new Rectangle(Point.Empty,a.Size);var format=System.Drawing.Imaging.PixelFormat.Format32bppArgb;
        var x=a.LockBits(area,System.Drawing.Imaging.ImageLockMode.ReadOnly,format);
        try{var y=b.LockBits(area,System.Drawing.Imaging.ImageLockMode.ReadOnly,format);try{var bx=new byte[Math.Abs(x.Stride)*a.Height];var by=new byte[Math.Abs(y.Stride)*b.Height];System.Runtime.InteropServices.Marshal.Copy(x.Scan0,bx,0,bx.Length);System.Runtime.InteropServices.Marshal.Copy(y.Scan0,by,0,by.Length);return bx.AsSpan().SequenceEqual(by);}finally{b.UnlockBits(y);}}finally{a.UnlockBits(x);}
    }
    private void Flatten()
    {
        var next=new List<TreeNode>();void Add(TreeNodeCollection nodes){foreach(TreeNode n in nodes){next.Add(n);if(expanded.Contains(n.Name))Add(n.Nodes);}}Add(Nodes);rows=next;offset=Math.Clamp(offset,0,ScrollMaximum);
    }
    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes){foreach(TreeNode n in nodes){yield return n;foreach(var child in AllNodes(n.Nodes))yield return child;}}
    private void InvalidateRow(string? key){int i=rows.FindIndex(n=>n.Name==key);if(i>=0)Invalidate(new Rectangle(0,i*ItemHeight-offset,Width,ItemHeight));}
    internal void UpdateRunning(IEnumerable<string> paths)
    {var next=paths.ToHashSet(StringComparer.OrdinalIgnoreCase);if(running.SetEquals(next))return;var before=running;running=next;if(Nodes["running"] is {} n){n.Text="正在运行  "+next.Count;InvalidateRow(n.Name);}foreach(var node in rows)if(node.Tag is LibraryNavigationTarget {Kind:"game"} t&&before.Contains(t.Value)!=next.Contains(t.Value))InvalidateRow(node.Name);}
    private void ScrollTo(int value){value=Math.Clamp(value,0,ScrollMaximum);if(offset==value)return;offset=value;tips.SetToolTip(this,null);Invalidate();}
    private TreeNode? Hit(Point p)=>p.X<0||p.X>=Width||p.Y<0||p.Y>=Height?null:rows.ElementAtOrDefault((p.Y+offset)/ItemHeight);
    protected override void OnPaintBackground(PaintEventArgs e){}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;var p=UiTheme.Current;g.Clear(UiTheme.Sidebar);g.SmoothingMode=SmoothingMode.AntiAlias;
        for(int i=Math.Max(0,offset/ItemHeight);i<rows.Count&&i*ItemHeight-offset<Height;i++)
        {
            var node=rows[i];if(node.Tag is not LibraryNavigationTarget target)continue;
            var row=new Rectangle(S(6),i*ItemHeight-offset,Math.Max(0,Width-S(12)-(ScrollMaximum>0?S(10):0)),ItemHeight);bool selected=node==SelectedNode;
            if(selected||node.Name==hoverKey){using var shape=WorkspaceDrawing.Round(row,S(5));using var brush=new SolidBrush(selected?WorkspaceSkin.SoftAccent:p.Control);g.FillPath(brush,shape);}
            var iconBox=new Rectangle(S(14)+node.Level*S(12),row.Y+(row.Height-S(17))/2,S(17),S(17));
            var key=target.Kind switch{"engine"=>"game","folder" or "collection"=>"folder","tag"=>"tag","new-category" or "new-folder" or "add-root"=>"plus","heading"=>expanded.Contains(node.Name)?"down":"next","game"=>"game",_=>target.Value switch{"recent"=>"calendar","running"=>"play","favorite"=>"tag",_=>"grid"}};
            if(target.Kind=="engine")key=expanded.Contains(node.Name)?"down":"next";
            if(target.Kind=="game"&&icons.TryGetValue(target.Value,out var image))g.DrawImage(image,WorkspaceDrawing.Fit(image.Size,iconBox));else WorkspaceSkin.Icon(g,key,iconBox,selected?p.Accent:p.SecondaryText);
            bool active=target.Kind=="game"&&running.Contains(target.Value);var box=new Rectangle(iconBox.Right+S(8),row.Y,Math.Max(0,row.Right-iconBox.Right-S(active?28:12)),ItemHeight);
            WorkspaceDrawing.Text(g,node.Text,Font,box,selected?p.Accent:p.Text);
            if(active){using var dot=new SolidBrush(Color.FromArgb(55,197,104));g.FillEllipse(dot,row.Right-S(15),row.Y+(row.Height-S(7))/2,S(7),S(7));}
        }
        if(ScrollMaximum>0){var thumb=Thumb;int w=S(dragging||overScroll?6:4);thumb=new(Width-S(2)-w,thumb.Y,w,thumb.Height);using var path=WorkspaceDrawing.Round(thumb,Math.Max(1,w/2));using var brush=new SolidBrush(Color.FromArgb(dragging?220:overScroll?180:100,p.SecondaryText));g.FillPath(brush,path);}
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);Focus();if(e.Button==MouseButtons.Left&&Track.Contains(e.Location)){var thumb=Thumb;grab=thumb.Contains(e.Location)?e.Y-thumb.Top:thumb.Height/2;dragging=true;Capture=true;MoveThumb(e.Y);return;}
        var n=Hit(e.Location);if(n?.Tag is not LibraryNavigationTarget target)return;
        if(e.Button==MouseButtons.Right){ContextRequested?.Invoke(target,e.Location);return;}
        if(e.Button!=MouseButtons.Left)return;
        if(n.Nodes.Count>0&&(target.Kind=="heading"||e.X<S(38)+n.Level*S(12))){if(!expanded.Add(n.Name))expanded.Remove(n.Name);Flatten();Invalidate();return;}
        SelectNode(n);Navigate?.Invoke(target);
    }
    private void SelectNode(TreeNode n){var old=SelectedNode?.Name;SelectedNode=n;selectionKey=n.Name;InvalidateRow(old);InvalidateRow(n.Name);}
    private void MoveThumb(int y){var track=Track;var thumb=Thumb;ScrollTo((int)((long)(y-track.Y-grab)*ScrollMaximum/Math.Max(1,track.Height-thumb.Height)));}
    protected override void OnMouseMove(MouseEventArgs e)
    {base.OnMouseMove(e);if(dragging){MoveThumb(e.Y);return;}bool hot=Track.Contains(e.Location);var node=hot?null:Hit(e.Location);if(hot!=overScroll){overScroll=hot;Invalidate(Track);}if(hoverKey!=node?.Name){var old=hoverKey;hoverKey=node?.Name;InvalidateRow(old);InvalidateRow(hoverKey);tips.SetToolTip(this,node is null?null:node.Text+(node.ToolTipText.Length>0?"\n"+node.ToolTipText:""));}}
    protected override void OnMouseLeave(EventArgs e){base.OnMouseLeave(e);var old=hoverKey;hoverKey=null;overScroll=false;InvalidateRow(old);Invalidate(Track);}
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);if(e.Button==MouseButtons.Left&&dragging){dragging=false;Capture=false;Invalidate(Track);}}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)dragging=false;}
    protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);ScrollTo(offset-e.Delta*ItemHeight*Math.Max(1,SystemInformation.MouseWheelScrollLines)/120);if(e is HandledMouseEventArgs h)h.Handled=true;}
    protected override bool IsInputKey(Keys key)=> (key&Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageDown or Keys.PageUp||base.IsInputKey(key);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);int at=rows.IndexOf(SelectedNode!);int next=e.KeyCode switch{Keys.Up=>at-1,Keys.Down=>at+1,Keys.Home=>0,Keys.End=>rows.Count-1,Keys.PageDown=>at+Math.Max(1,Height/ItemHeight),Keys.PageUp=>at-Math.Max(1,Height/ItemHeight),_=>at};
        if(e.KeyCode is Keys.Left or Keys.Right&&SelectedNode is {} branch){if(e.KeyCode==Keys.Right)expanded.Add(branch.Name);else expanded.Remove(branch.Name);Flatten();Invalidate();e.Handled=true;return;}
        if(e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageDown or Keys.PageUp){if(rows.Count>0){next=Math.Clamp(next,0,rows.Count-1);SelectNode(rows[next]);if(next*ItemHeight<offset)ScrollTo(next*ItemHeight);else if((next+1)*ItemHeight>offset+Height)ScrollTo((next+1)*ItemHeight-Height);}e.Handled=true;}
        if(SelectedNode?.Tag is LibraryNavigationTarget t){if(e.KeyCode==Keys.Enter){Navigate?.Invoke(t);e.Handled=true;}if(e.KeyCode==Keys.Apps||e.Shift&&e.KeyCode==Keys.F10){ContextRequested?.Invoke(t,new(S(25),Math.Clamp(at*ItemHeight-offset,0,Math.Max(0,Height-1))));e.Handled=true;}}
    }
    protected override void OnResize(EventArgs e){base.OnResize(e);offset=Math.Clamp(offset,0,ScrollMaximum);}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);if(rows is not null)Flatten();Invalidate();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);Flatten();Invalidate();}
    protected override void Dispose(bool disposing){if(disposing){tips.Dispose();model.Dispose();foreach(var icon in icons.Values)icon.Dispose();icons.Clear();}base.Dispose(disposing);}
}

public sealed partial class MainForm
{
    private readonly LibraryNavigationTree _libraryNavigation=new(){Dock=DockStyle.Fill};
    private LibraryPreferences _libraryPreferences=new();
    private string _libraryScope="home",_libraryTag="";
    private Control? _libraryNavigationHost;
    private readonly GameLibraryGrid _recentShelf=new(){Dock=DockStyle.Fill,ShelfMode=true};
    private Control? _recentShelfHost;
    private bool _showRecentShelf=true;
    private bool _sizingRecentShelf;
    private Label? _libraryHeading;
    private void SizeRecentShelf()
    {
        if(_sizingRecentShelf||_recentShelfHost is not TableLayoutPanel shelf||shelf.Parent is not TableLayoutPanel parent)return;
        var title=_recentSectionHeading;
        int row=parent.GetRow(shelf);if(row<0||row>=parent.RowStyles.Count)return;
        int height=_showRecentShelf?_recentShelf.ShelfContentHeight+_recentShelf.Margin.Vertical+title.GetPreferredSize(Size.Empty).Height+title.Margin.Vertical+shelf.Margin.Vertical+shelf.Padding.Vertical:0;
        _sizingRecentShelf=true;
        try
        {
            if(Math.Abs(parent.RowStyles[row].Height-height)>1)parent.RowStyles[row].Height=height;
            // A visibility change can collapse the cached row while a child resize is
            // in progress. Finish that parent's layout even when the target is unchanged.
            parent.PerformLayout();
        }
        finally{_sizingRecentShelf=false;}
    }
    private void NavigateLibrary(LibraryNavigationTarget target)
    {
        if(target.Kind=="heading")return;
        if(target.Kind=="new-folder"){CreateLibraryFolder();return;}
        if(target.Kind=="add-root"){_=ChooseLibraryRootAsync();return;}
        if(target.Kind=="new-category"){AddLibraryCategory();return;}
        if(target.Kind=="game"){_=OpenLibraryGameAsync(target.Value);return;}
        ShowLibraryHome();_libraryFiltersLoading=true;
        try{_libraryCollection=target.Kind=="collection"?target.Value:"";_libraryScope=target.Kind=="scope"?target.Value:"home";_libraryTag=target.Kind=="tag"?target.Value:"";
            _libraryFilter.SelectedIndex=0;_libraryEngine.SelectedItem=target.Kind=="engine"?target.Value:"全部引擎";_libraryFolder.SelectedItem=target.Kind=="folder"?target.Value:"全部文件夹";}
        finally{_libraryFiltersLoading=false;}
        _libraryPage=0;ScheduleLibraryRefresh();
    }
    private void AddLibraryCategory()
    {
        using var dialog=new Form{Text="新建分类",StartPosition=FormStartPosition.CenterParent,ClientSize=new(350,150),Font=Font,FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false};
        var name=new TextBox{MaxLength=40,Width=280,PlaceholderText="分类名称"};var content=WorkspaceColumn();content.Padding=new(18);
        WorkspaceAdd(content,WorkspaceRow(name));var save=WorkspaceButton("创建",(_,_)=>{if(!string.IsNullOrWhiteSpace(name.Text))dialog.DialogResult=DialogResult.OK;});
        WorkspaceAdd(content,WorkspaceRow(save));dialog.AcceptButton=save;dialog.Controls.Add(content);UiTheme.Apply(dialog,_appliedDesktopSettings);
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        try{_libraryPreferences=_libraryPreferences with{Categories=(_libraryPreferences.Categories??[]).Append(name.Text.Trim()).Distinct().ToArray()};LibraryPreferencesStore.Save(_libraryPreferences);ScheduleLibraryRefresh();}
        catch(Exception ex){ShowFusionError(ex);}
    }
    private void UpdateWorkspaceNavigation()
    {
        // Status alignment is the only shared chrome difference between pages.
        // The page host and the library's own sidebar never change width here.
        var padding=new Padding((_currentMainPage=="内嵌翻译"?(_librarySplit?.LogicalSidebarWidth??206)+20:20)*DeviceDpi/96,0,0,0);
        if(_statusLabel.Padding!=padding)_statusLabel.Padding=padding;
    }
}
