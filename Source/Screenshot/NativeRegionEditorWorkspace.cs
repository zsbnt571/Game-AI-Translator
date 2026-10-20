using W=System.Windows;
using WC=System.Windows.Controls;
namespace ScreenshotTranslationUiTester;
public sealed partial class RecognitionRegionEditorForm
{
    private WC.Grid? _nativeRegionRoot;
    private void BuildUi()
    {
        var root=new WC.Grid();_nativeRegionRoot=root;root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});
        var bar=new WC.WrapPanel{Margin=new(8)};root.Children.Add(bar);
        void Add(string caption,Action action)=>bar.Children.Add(NativeDialogControls.Button(caption,action));
        Add(UiStrings.SelectMoveResize,()=>_tool=Tool.Select);Add(UiStrings.CreateRegion,()=>_tool=Tool.Create);Add(UiStrings.EditPolygon,()=>_tool=Tool.Polygon);
        Add(UiStrings.DeleteRegion,DeleteSelected);Add(UiStrings.MergeRegions,MergeSelected);Add(UiStrings.SplitHorizontal,()=>SplitSelected(false));Add(UiStrings.SplitVertical,()=>SplitSelected(true));
        Add(UiStrings.Undo,()=>{_session.Undo();RefreshAll();});Add(UiStrings.Redo,()=>{_session.Redo();RefreshAll();});Add(UiStrings.RerunRegionOcr,async()=>await RunRegionOcr());Add(UiStrings.RerunVisual,async()=>await RunVisual());
        Add(UiStrings.ResetSelected,ResetSelected);Add(UiStrings.ResetAll,()=>{_session.ResetAllAuto(_automatic);RefreshAll();});Add(UiStrings.SourceLines,()=>{_showSourceLines=!_showSourceLines;_canvas.Invalidate();});
        Add(UiStrings.ZoomIn,()=>SetZoom(_zoom*1.2f));Add(UiStrings.ZoomOut,()=>SetZoom(_zoom/1.2f));Add(UiStrings.ActualSize,()=>SetZoom(1));
        var body=new WC.Grid();body.ColumnDefinitions.Add(new());body.ColumnDefinitions.Add(new(){Width=new(6)});body.ColumnDefinitions.Add(new(){Width=new(320),MinWidth=220});WC.Grid.SetRow(body,1);root.Children.Add(body);
        _canvas.Image=_image;_scroll.SetPicture(_canvas);body.Children.Add(_scroll.View);
        var splitter=new WC.GridSplitter{HorizontalAlignment=W.HorizontalAlignment.Stretch};splitter.SetResourceReference(WC.Control.BackgroundProperty,"Fusion.Border");WC.Grid.SetColumn(splitter,1);body.Children.Add(splitter);
        var right=new WC.Grid{Margin=new(8)};right.RowDefinitions.Add(new());right.RowDefinitions.Add(new(){Height=W.GridLength.Auto});right.RowDefinitions.Add(new(){Height=new(160)});WC.Grid.SetColumn(right,2);body.Children.Add(right);right.Children.Add(_list.View);
        var properties=NativeDialogControls.Column(NativeDialogControls.Text(UiStrings.RegionType),_role.View,NativeDialogControls.Text(UiStrings.ReadingOrder),_order.View,_ignore.View,_preserve.View);WC.Grid.SetRow(properties,1);right.Children.Add(properties);
        var detail=new WC.ScrollViewer{Content=_diagnostics.View,VerticalScrollBarVisibility=WC.ScrollBarVisibility.Auto};WC.Grid.SetRow(detail,2);right.Children.Add(detail);
        var actions=NativeDialogControls.Row(NativeDialogControls.Button(UiStrings.Cancel,()=>{DialogResult=DialogResult.Cancel;Close();}),NativeDialogControls.Button(UiStrings.Apply,()=>{DialogResult=DialogResult.OK;Close();},true));actions.Margin=new(8);actions.HorizontalAlignment=W.HorizontalAlignment.Right;WC.Grid.SetRow(actions,2);root.Children.Add(actions);
        root.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){DialogResult=DialogResult.Cancel;Close();e.Handled=true;}};
        _canvas.View.PreviewMouseLeftButtonDown+=(_,_)=>_canvas.View.CaptureMouse();_canvas.View.PreviewMouseLeftButtonUp+=(_,_)=>_canvas.View.ReleaseMouseCapture();
    }
}
