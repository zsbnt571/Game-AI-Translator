using System.Collections.ObjectModel;

namespace ScreenshotTranslationUiTester;

internal class BufferedPage : Panel
{
    internal BufferedPage()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw,true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Pages own an opaque background even while native children are being
        // restored. Never rely on the desktop or a sibling's retained pixels.
        using var background=new SolidBrush(BackColor.A==255?BackColor:UiTheme.Current.Main);
        e.Graphics.FillRectangle(background,e.ClipRectangle);
    }
}

internal sealed class SettingsPage : BufferedPage { internal SettingsPage(string title) { Text=title; } }

// Composite the active workspace and its native descendants together. Keep the
// custom top-level window outside this boundary so restore/show owns its frame.
internal sealed class WorkspacePageHost : BufferedPage
{
    protected override CreateParams CreateParams
    {get{var cp=base.CreateParams;cp.ExStyle|=0x02000000;return cp;}}
}

internal static class WorkspacePages
{
    internal static void Show(Control host,Control next)
    {
        host.SuspendLayout();
        try
        {
            // Publish the destination before hiding the old page; there is
            // never a moment where the host has no visible content.
            next.Visible=true;next.BringToFront();
            foreach(Control page in host.Controls)if(page!=next)page.Visible=false;
        }
        finally { host.ResumeLayout(true); }
    }
}

// Settings already has a separate navigation list. A native TabControl with
// one-pixel multiline headers adds a second selection/handle lifecycle to it.
// Keep persistent, opaque pages and switch their visibility in one place.
internal sealed class SettingsPageHost : BufferedPage
{
    private int selected=-1;
    internal SettingsPageHost() { TabPages=new Pages(this);Padding=new Padding(1); }
    internal Pages TabPages {get;}
    internal int TabCount=>TabPages.Count;
    internal SettingsPage? SelectedTab=>selected>=0&&selected<TabCount?TabPages[selected]:null;
    internal event EventHandler? SelectedIndexChanged;
    internal int SelectedIndex
    {
        get=>selected;
        set
        {
            if(value==selected)return;
            if(value<0||value>=TabCount)throw new ArgumentOutOfRangeException(nameof(value));
            selected=value;WorkspacePages.Show(this,TabPages[value]);
            SelectedIndexChanged?.Invoke(this,EventArgs.Empty);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);using var pen=new Pen(UiTheme.Current.Border);
        e.Graphics.DrawRectangle(pen,0,0,Math.Max(0,Width-1),Math.Max(0,Height-1));
    }
    internal sealed class Pages(SettingsPageHost owner) : Collection<SettingsPage>
    {
        protected override void InsertItem(int index,SettingsPage item)
        {
            base.InsertItem(index,item);item.Dock=DockStyle.Fill;item.Visible=false;owner.Controls.Add(item);
            if(owner.selected<0)owner.SelectedIndex=0;
        }
    }
}

// Suspend every layout container, not just the form. Leaf font changes must
// settle before their autosizing parents; scroll positions survive the batch.
internal sealed class UiLayoutBatch : IDisposable
{
    private readonly Control[] controls;
    private readonly (ScrollableControl control,Point scroll)[] scrolls;
    internal UiLayoutBatch(Control root)
    {
        controls=Walk(root).ToArray();
        scrolls=controls.OfType<ScrollableControl>().Where(c=>c.AutoScroll).Select(c=>(c,c.AutoScrollPosition)).ToArray();
        foreach(var c in controls)c.SuspendLayout();
    }
    internal static IEnumerable<Control> Walk(Control root)
    {
        yield return root;foreach(Control c in root.Controls)foreach(var child in Walk(c))yield return child;
    }
    public void Dispose()
    {
        for(var i=controls.Length-1;i>=0;i--)if(!controls[i].IsDisposed)controls[i].ResumeLayout(true);
        foreach(var (c,p) in scrolls)if(!c.IsDisposed&&c.AutoScrollPosition!=p)c.AutoScrollPosition=new Point(-p.X,-p.Y);
    }
}
