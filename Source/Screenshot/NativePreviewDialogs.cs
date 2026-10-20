using W=System.Windows;
using WC=System.Windows.Controls;
using WD=System.Windows.Data;
namespace ScreenshotTranslationUiTester;

internal sealed class NativeOcrLinesDialog : NativeDialogWindow
{
    internal sealed class Row(OcrRegion source)
    {
        internal OcrRegion Source=>source;
        public bool Enabled{get;set;}=true;public int Order{get;set;}=source.ReadingOrder;public string Text{get;set;}=source.Text;
        public string Confidence=>source.Confidence.ToString("0.000");public string Bounds=>$"{source.Bounds.X:0},{source.Bounds.Y:0} {source.Bounds.Width:0}×{source.Bounds.Height:0}";
    }
    internal Row[] Rows{get;}
    internal WC.DataGrid Grid{get;}
    internal NativeOcrLinesDialog(IEnumerable<OcrRegion> regions):base("编辑 OCR 区域",920,560)
    {
        Rows=regions.OrderBy(x=>x.ReadingOrder).Select(x=>new Row(x)).ToArray();Grid=new(){ItemsSource=Rows,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,HeadersVisibility=WC.DataGridHeadersVisibility.Column,SelectionUnit=WC.DataGridSelectionUnit.FullRow};
        Grid.Columns.Add(new WC.DataGridCheckBoxColumn{Header=UiLanguage.Text("启用"),Binding=new WD.Binding(nameof(Row.Enabled)),Width=64});
        foreach(var column in new[]{("顺序",nameof(Row.Order),70d,false),("文字",nameof(Row.Text),1d,false),("置信度",nameof(Row.Confidence),85d,true),("坐标",nameof(Row.Bounds),190d,true)})Grid.Columns.Add(new WC.DataGridTextColumn{Header=UiLanguage.Text(column.Item1),Binding=new WD.Binding(column.Item2),Width=column.Item3==1?new(1,WC.DataGridLengthUnitType.Star):new(column.Item3),IsReadOnly=column.Item4});
        Content=NativeDialogControls.Layout(Grid,NativeDialogControls.Row(ResultButton("取消",System.Windows.Forms.DialogResult.Cancel),NativeDialogControls.Button("应用",()=>{if(Grid.CommitEdit(WC.DataGridEditingUnit.Cell,true)&&Grid.CommitEdit(WC.DataGridEditingUnit.Row,true))Finish(System.Windows.Forms.DialogResult.OK);},true)),false);
    }
    internal List<OcrRegion> Edited()=>Rows.Where(r=>r.Enabled&&!string.IsNullOrWhiteSpace(r.Text)).Select(r=>new OcrRegion{Id=r.Source.Id,Text=r.Text.Trim(),RawText=r.Text.Trim(),Bounds=r.Source.Bounds,Confidence=r.Source.Confidence,Language=r.Source.Language,ReadingOrder=r.Order,RawLines=[]}).ToList();
}

public sealed partial class PreviewForm
{
    private void ShowNativeLayers()
    {
        using var dialog=new NativeDialogWindow(UiStrings.RegionLayers,440,520);var body=new WC.StackPanel();var toggles=new Dictionary<RegionOverlayLayer,WC.CheckBox>();
        foreach(var layer in Enum.GetValues<RegionOverlayLayer>()){var toggle=new WC.CheckBox{Content=new RegionLayerOption(layer).ToString(),IsChecked=_visibleV2Layers.Contains(layer),MinHeight=36};toggles.Add(layer,toggle);body.Children.Add(toggle);}
        dialog.Content=NativeDialogControls.Layout(body,NativeDialogControls.Row(dialog.ResultButton("取消",DialogResult.Cancel),dialog.ResultButton(UiStrings.Apply,DialogResult.OK,true)));
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;_visibleV2Layers.Clear();foreach(var pair in toggles)if(pair.Value.IsChecked==true)_visibleV2Layers.Add(pair.Key);_picture.Invalidate();
    }
    private void ShowNativeOcrInput()
    {
        if(_document?.DebugImage is not{} bitmap){AppDialog.Show(this,"没有 OCR 调试图片","请先执行 OCR。",AppDialogKind.Warning);return;}
        var window=new NativeDialogWindow($"OCR实际输入：{_document.SelectedCandidate}｜{bitmap.Width}×{bitmap.Height}｜原始行 {_document.RawLines.Count}",1000,700);
        window.Content=new WC.Image{Source=WpfDrawing.Image(bitmap),Stretch=System.Windows.Media.Stretch.Uniform,Margin=new(12)};window.Show(this);
    }
    private static void AddNativeTraceTab(WC.TabControl tabs,string title,string value)
    {var text=NativeDialogControls.Input(value,true,true);text.Margin=new(12);tabs.Items.Add(new WC.TabItem{Header=title,Content=text});}
}
