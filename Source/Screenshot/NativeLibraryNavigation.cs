namespace ScreenshotTranslationUiTester;

internal sealed partial class NativeLibraryNavigation:ILibraryNavigationSession
{
    private readonly LibraryNavigationNodes nodes=new();
    private readonly Dictionary<string,Bitmap> icons=new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> running=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expanded=new(StringComparer.Ordinal){"engines","categories"};
    private List<LibraryNavigationNode> rows=[];
    private string? selectionKey,hoverKey;
    private int offset,grab;
    private bool dragging,overScroll;
    internal LibraryNavigationNodes Nodes=>nodes;
    internal LibraryNavigationNode? SelectedNode {get;private set;}
    internal int ScrollOffset=>offset;
    internal int ItemHeight=>Math.Max(S(28),Font.Height+S(12));
    internal int ScrollMaximum=>Math.Max(0,rows.Count*ItemHeight-NavigationViewport.Height);
    public event Action<LibraryNavigationTarget>? Navigate;
    public event Action<LibraryNavigationTarget,Point>? ContextRequested;
    private sealed record Entry(string Key,string Text,LibraryNavigationTarget Target,string ToolTip="")
    {internal List<Entry> Children {get;}=[];}
    internal NativeLibraryNavigation(){}
    private int S(int n)=>n*DeviceDpi/96;
    private Rectangle Track=>ScrollMaximum>0?new(Math.Max(0,NavigationViewport.Width-S(10)),S(3),S(10),Math.Max(0,NavigationViewport.Height-S(6))):Rectangle.Empty;
    private Rectangle Thumb
    {get{var r=Track;if(r.Height<=0)return Rectangle.Empty;int h=Math.Clamp((int)((long)r.Height*NavigationViewport.Height/Math.Max(1,rows.Count*ItemHeight)),Math.Min(S(30),r.Height),r.Height);return new(r.X,r.Y+(int)((long)(r.Height-h)*offset/Math.Max(1,ScrollMaximum)),r.Width,h);}}
    public void UpdateEntries(RecentGame[] games,LibraryMetadata metadata,Dictionary<string,string> engines,IEnumerable<string> activePaths,string? selectedPath,string scope,LibraryPreferences preferences,Dictionary<string,Bitmap>? newIcons=null)
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
    private static void Reconcile(LibraryNavigationNodes nodes,List<Entry> entries)
    {
        var wanted=entries.Select(x=>x.Key).ToHashSet(StringComparer.Ordinal);
        for(int i=nodes.Count-1;i>=0;i--)if(!wanted.Contains(nodes[i].Name))nodes.RemoveAt(i);
        for(int i=0;i<entries.Count;i++)
        {
            var entry=entries[i];var node=nodes.Cast<LibraryNavigationNode>().FirstOrDefault(x=>x.Name==entry.Key);
            if(node is null){node=new LibraryNavigationNode(entry.Text){Name=entry.Key,Tag=entry.Target,ToolTipText=entry.ToolTip};nodes.Insert(i,node);}
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
        var next=new List<LibraryNavigationNode>();void Add(LibraryNavigationNodes nodes){foreach(LibraryNavigationNode n in nodes){next.Add(n);if(expanded.Contains(n.Name))Add(n.Nodes);}}Add(Nodes);rows=next;offset=Math.Clamp(offset,0,ScrollMaximum);
    }
    private static IEnumerable<LibraryNavigationNode> AllNodes(LibraryNavigationNodes nodes){foreach(LibraryNavigationNode n in nodes){yield return n;foreach(var child in AllNodes(n.Nodes))yield return child;}}
    private void InvalidateRow(string? key){int i=rows.FindIndex(n=>n.Name==key);if(i>=0)Invalidate(new Rectangle(0,i*ItemHeight-offset,NavigationViewport.Width,ItemHeight));}
    public void UpdateRunning(IEnumerable<string> paths)
    {var next=paths.ToHashSet(StringComparer.OrdinalIgnoreCase);if(running.SetEquals(next))return;var before=running;running=next;if(Nodes["running"] is {} n){n.Text="正在运行  "+next.Count;InvalidateRow(n.Name);}foreach(var node in rows)if(node.Tag is LibraryNavigationTarget {Kind:"game"} t&&before.Contains(t.Value)!=next.Contains(t.Value))InvalidateRow(node.Name);}
    private void ScrollTo(int value){value=Math.Clamp(value,0,ScrollMaximum);if(offset==value)return;offset=value;SetTip(null);Invalidate();}
    private LibraryNavigationNode? Hit(Point p)=>p.X<0||p.X>=NavigationViewport.Width||p.Y<0||p.Y>=NavigationViewport.Height?null:rows.ElementAtOrDefault((p.Y+offset)/ItemHeight);
    private void OnMouseDown(MouseEventArgs e)
    {
        Focus();if(e.Button==MouseButtons.Left&&Track.Contains(e.Location)){var thumb=Thumb;grab=thumb.Contains(e.Location)?e.Y-thumb.Top:thumb.Height/2;dragging=true;Capture=true;MoveThumb(e.Y);return;}
        var n=Hit(e.Location);if(n?.Tag is not LibraryNavigationTarget target)return;
        if(e.Button==MouseButtons.Right){ContextRequested?.Invoke(target,e.Location);return;}
        if(e.Button!=MouseButtons.Left)return;
        if(n.Nodes.Count>0&&(target.Kind=="heading"||e.X<S(38)+n.Level*S(12))){if(!expanded.Add(n.Name))expanded.Remove(n.Name);Flatten();Invalidate();return;}
        SelectNode(n);Navigate?.Invoke(target);
    }
    private void SelectNode(LibraryNavigationNode n)
    {
        if(ReferenceEquals(SelectedNode,n)&&selectionKey==n.Name)return;
        var old=SelectedNode?.Name;SelectedNode=n;selectionKey=n.Name;InvalidateRow(old);InvalidateRow(n.Name);
    }
    internal void SelectGame(string path)
    {
        var node=AllNodes(Nodes).FirstOrDefault(n=>n.Tag is LibraryNavigationTarget {Kind:"game"} target&&StringComparer.OrdinalIgnoreCase.Equals(target.Value,path));
        if(node is null)return;
        if(node.Parent is {} parent&&expanded.Add(parent.Name)){Flatten();Invalidate();}
        SelectNode(node);
    }
    private void MoveThumb(int y){var track=Track;var thumb=Thumb;ScrollTo((int)((long)(y-track.Y-grab)*ScrollMaximum/Math.Max(1,track.Height-thumb.Height)));}
    private void OnMouseMove(MouseEventArgs e)
    {if(dragging){MoveThumb(e.Y);return;}bool hot=Track.Contains(e.Location);var node=hot?null:Hit(e.Location);if(hot!=overScroll){overScroll=hot;Invalidate(Track);}if(hoverKey!=node?.Name){var old=hoverKey;hoverKey=node?.Name;InvalidateRow(old);InvalidateRow(hoverKey);SetTip(node is null?null:node.Text+(node.ToolTipText.Length>0?"\n"+node.ToolTipText:""));}}
    private void OnMouseLeave(EventArgs e){var old=hoverKey;hoverKey=null;overScroll=false;InvalidateRow(old);Invalidate(Track);}
    private void OnMouseUp(MouseEventArgs e){if(e.Button==MouseButtons.Left&&dragging){dragging=false;Capture=false;Invalidate(Track);}}
    private void OnMouseCaptureChanged(EventArgs e){if(!Capture)dragging=false;}
    private void OnMouseWheel(MouseEventArgs e){ScrollTo(offset-e.Delta*ItemHeight*Math.Max(1,SystemInformation.MouseWheelScrollLines)/120);if(e is HandledMouseEventArgs h)h.Handled=true;}
    private void OnKeyDown(KeyEventArgs e)
    {
        int at=rows.IndexOf(SelectedNode!);int height=NavigationViewport.Height;int next=e.KeyCode switch{Keys.Up=>at-1,Keys.Down=>at+1,Keys.Home=>0,Keys.End=>rows.Count-1,Keys.PageDown=>at+Math.Max(1,height/ItemHeight),Keys.PageUp=>at-Math.Max(1,height/ItemHeight),_=>at};
        if(e.KeyCode is Keys.Left or Keys.Right&&SelectedNode is {} branch){if(e.KeyCode==Keys.Right)expanded.Add(branch.Name);else expanded.Remove(branch.Name);Flatten();Invalidate();e.Handled=true;return;}
        if(e.KeyCode is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown){if(rows.Count>0){next=Math.Clamp(next,0,rows.Count-1);SelectNode(rows[next]);if(next*ItemHeight<offset)ScrollTo(next*ItemHeight);else if((next+1)*ItemHeight>offset+height)ScrollTo((next+1)*ItemHeight-height);}e.Handled=true;}
        if(SelectedNode?.Tag is LibraryNavigationTarget t){if(e.KeyCode==Keys.Enter){Navigate?.Invoke(t);e.Handled=true;}if(e.KeyCode==Keys.Apps||e.Shift&&e.KeyCode==Keys.F10){ContextRequested?.Invoke(t,new(S(25),Math.Clamp(at*ItemHeight-offset,0,Math.Max(0,height-1))));e.Handled=true;}}
    }
    public void Dispose(){if(!disposed){disposed=true;foreach(var icon in icons.Values)icon.Dispose();icons.Clear();}}
}
