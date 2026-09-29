using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ScreenshotTranslationUiTester;

// Retain WinForms menu keyboard/focus/auto-close behavior. Rendering and sizing
// are shared by library, launch and import menus; no global input is involved.
internal sealed class WorkspaceMenu : ContextMenuStrip
{
    internal WorkspaceMenu()
    {
        Renderer=new WorkspaceMenuRenderer();
        ShowImageMargin=false;ShowCheckMargin=false;
        DropShadowEnabled=true;AutoSize=false;
    }

    internal ToolStripMenuItem AddAction(string text,string icon,EventHandler action,bool enabled=true)
    {
        var item=new WorkspaceMenuItem(text,icon){Enabled=enabled};
        item.Click+=action;Items.Add(item);return item;
    }

    internal void ClearActions()
    {
        // The menu itself lives until its owning form closes. Never dispose it
        // in Closed: auto-close may run before a button/key callback completes.
        Close();
        while(Items.Count>0){var item=Items[0];Items.RemoveAt(0);item.Dispose();}
    }

    internal WorkspaceMenu AddGroup(string text,string icon)
    {
        var child=new WorkspaceMenu{Font=Font};
        Items.Add(new WorkspaceMenuItem(text,icon){DropDown=child});return child;
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        float scale=DeviceDpi/96f;int S(int value)=>(int)Math.Ceiling(value*scale);
        Padding=new(S(6),S(6),S(6),S(6));
        var area=Screen.FromControl(SourceControl??this).WorkingArea;
        int width=Math.Max(S(242),Items.OfType<ToolStripMenuItem>().Select(x=>TextRenderer.MeasureText(x.Text,Font).Width+S(76)).DefaultIfEmpty(0).Max());
        width=Math.Min(width,Math.Min(S(480),area.Width-S(24))-Padding.Horizontal);
        int height=Math.Max(S(38),Font.Height+S(16));
        foreach(ToolStripItem item in Items)
        {
            item.AutoSize=false;item.Margin=Padding.Empty;
            item.Size=new(width,item is ToolStripSeparator?S(13):height);
        }
        // ToolStripDropDownMenu's preferred size only accounts for native
        // text/image margins, not our icon column. Own the popup's full size.
        Size=new(width+Padding.Horizontal,Math.Min(Items.Cast<ToolStripItem>().Sum(x=>x.Height)+Padding.Vertical,area.Height-S(32)));
        base.OnOpening(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if(Width<2||Height<2)return;
        using var path=WorkspaceDrawing.Round(ClientRectangle,9*DeviceDpi/96);
        var previous=Region;Region=new Region(path);previous?.Dispose();
    }
}

internal sealed class WorkspaceMenuItem(string text,string icon) : ToolStripMenuItem(text)
{
    internal string IconKey {get;}=icon;
}

internal sealed class WorkspaceMenuRenderer : ToolStripRenderer
{
    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        if(e.Item is not { } item)return;
        int S(int value)=>value*(item.Owner?.DeviceDpi??96)/96;
        WorkspaceSkin.Icon(e.Graphics,"next",new(item.Width-S(27),(item.Height-S(14))/2,S(14),S(14)),item.Enabled?UiTheme.Current.Text:UiTheme.Current.SecondaryText);
    }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using var shape=WorkspaceDrawing.Round(new Rectangle(Point.Empty,e.ToolStrip.Size),9*e.ToolStrip.DeviceDpi/96);
        using var fill=new SolidBrush(UiTheme.Current==ThemePalette.Day?Color.White:UiTheme.Current.Main);
        g.FillPath(fill,shape);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var shape=WorkspaceDrawing.Round(new Rectangle(0,0,e.ToolStrip.Width-1,e.ToolStrip.Height-1),9*e.ToolStrip.DeviceDpi/96);
        using var border=new Pen(UiTheme.Current.Border);e.Graphics.DrawPath(border,shape);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if(!e.Item.Selected||!e.Item.Enabled)return;
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        int inset=5*(e.ToolStrip?.DeviceDpi??96)/96;
        using var shape=WorkspaceDrawing.Round(new Rectangle(inset,0,e.Item.Width-inset,e.Item.Height),inset);
        using var fill=new SolidBrush(WorkspaceSkin.SoftAccent);e.Graphics.FillPath(fill,shape);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        int S(int value)=>value*(e.ToolStrip?.DeviceDpi??96)/96;
        var palette=UiTheme.Current;
        var color=!e.Item.Enabled?palette.SecondaryText:e.Item.Selected?palette.Accent:palette.Text;
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        if(e.Item is WorkspaceMenuItem item)WorkspaceSkin.Icon(e.Graphics,item.IconKey,new(S(16),(item.Height-S(20))/2,S(20),S(20)),color);
        WorkspaceDrawing.Text(e.Graphics,e.Text??"",e.TextFont??e.Item.Font,new(S(49),0,Math.Max(0,e.Item.Width-S(e.Item is ToolStripMenuItem {HasDropDownItems:true}?82:62)),e.Item.Height),color);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int pad=15*(e.ToolStrip?.DeviceDpi??96)/96;
        using var line=new Pen(UiTheme.Current.Border);e.Graphics.DrawLine(line,pad,e.Item.Height/2,e.Item.Width-pad,e.Item.Height/2);
    }
}
