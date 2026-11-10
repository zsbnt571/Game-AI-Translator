using W=System.Windows;
using C=System.Windows.Controls;
using A=System.Windows.Automation;
using M=System.Windows.Media;

namespace ScreenshotTranslationUiTester;

// Preview is local to the dialog. Only Apply changes the persisted appearance.
internal sealed class NativeIconAppearanceDialog:NativeDialogWindow
{
    internal static readonly string[] Engines=["Construct 3 / NW.js","Defold","Electron","GameMaker","Godot","Ren’Py","RPG Maker MV","RPG Maker MZ / Electron","RPG Maker VX Ace","RPG Maker XP","Unity Mono / IL2CPP","Unreal Engine","WOLF RPG Editor"];
    private readonly C.ComboBox colors;
    private readonly C.Slider size;
    private readonly C.TextBlock sizeLabel=NativeDialogControls.Text("");
    private readonly List<(string Name,C.Image Image)> previews=[];
    internal UiIconColorMode PreviewMode=>(UiIconColorMode)Math.Max(0,colors.SelectedIndex);
    internal int PreviewSize=>(int)Math.Round(size.Value);
    internal NativeIconAppearanceDialog(UiIconColorMode currentMode,int currentSize,Action<UiIconColorMode,int> apply):base("图标预览与大小",760,780)
    {
        currentSize=Math.Clamp(currentSize,16,28);
        colors=NativeDialogControls.Choice(["黑白","原色"],(int)currentMode);colors.MinWidth=180;
        A.AutomationProperties.SetAutomationId(colors,"IconPreviewMode");A.AutomationProperties.SetName(colors,UiLanguage.Text("图标风格"));
        size=new C.Slider{Minimum=16,Maximum=28,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=2,Value=currentSize,MinHeight=32,VerticalAlignment=W.VerticalAlignment.Center};
        A.AutomationProperties.SetAutomationId(size,"IconPreviewSize");A.AutomationProperties.SetName(size,UiLanguage.Text("图标大小"));
        var controls=new C.Grid{Margin=new(0,0,0,12)};controls.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});controls.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});
        var left=NativeDialogControls.Column(NativeDialogControls.Text("图标风格"),colors);left.Margin=new(0,0,20,0);
        var right=NativeDialogControls.Column(sizeLabel,size);C.Grid.SetColumn(right,1);controls.Children.Add(left);controls.Children.Add(right);
        var table=new C.Grid();table.ColumnDefinitions.Add(new(){Width=new(1,W.GridUnitType.Star)});table.ColumnDefinitions.Add(new(){Width=new(90)});table.ColumnDefinitions.Add(new(){Width=new(90)});
        table.RowDefinitions.Add(new(){Height=new(34)});
        AddText("引擎",0,0,true);AddText("当前",0,1,true);AddText("预览",0,2,true);
        for(int i=0;i<Engines.Length;i++)
        {
            int row=i+1;table.RowDefinitions.Add(new(){Height=new(40)});AddText(Engines[i],row,0,false);
            var old=new C.Image{Source=LibraryEngineIcons.Image(Engines[i],UiTheme.Current.SecondaryText,currentMode),Width=currentSize,Height=currentSize,VerticalAlignment=W.VerticalAlignment.Center,HorizontalAlignment=W.HorizontalAlignment.Center};
            var preview=new C.Image{VerticalAlignment=W.VerticalAlignment.Center,HorizontalAlignment=W.HorizontalAlignment.Center};
            A.AutomationProperties.SetName(old,Engines[i]+" · "+UiLanguage.Text("当前"));A.AutomationProperties.SetName(preview,Engines[i]+" · "+UiLanguage.Text("预览"));
            C.Grid.SetRow(old,row);C.Grid.SetColumn(old,1);C.Grid.SetRow(preview,row);C.Grid.SetColumn(preview,2);table.Children.Add(old);table.Children.Add(preview);previews.Add((Engines[i],preview));
            M.RenderOptions.SetBitmapScalingMode(old,M.BitmapScalingMode.HighQuality);M.RenderOptions.SetBitmapScalingMode(preview,M.BitmapScalingMode.HighQuality);
        }
        void AddText(string value,int row,int column,bool bold)
        {
            var text=NativeDialogControls.Text(value);text.Margin=new(0,0,8,0);text.VerticalAlignment=W.VerticalAlignment.Center;
            if(column>0)text.HorizontalAlignment=W.HorizontalAlignment.Center;if(bold)text.FontWeight=W.FontWeights.SemiBold;
            C.Grid.SetRow(text,row);C.Grid.SetColumn(text,column);table.Children.Add(text);
        }
        var reset=NativeDialogControls.Button("恢复默认图标",()=>{colors.SelectedIndex=0;size.Value=17;RefreshPreview();});
        var save=NativeDialogControls.Button("应用",()=>{apply(PreviewMode,PreviewSize);Finish(System.Windows.Forms.DialogResult.OK);},true);
        var cancel=ResultButton("取消",System.Windows.Forms.DialogResult.Cancel);
        A.AutomationProperties.SetAutomationId(reset,"IconPreviewReset");A.AutomationProperties.SetAutomationId(save,"IconPreviewApply");A.AutomationProperties.SetAutomationId(cancel,"IconPreviewCancel");
        var body=new C.Grid();body.RowDefinitions.Add(new(){Height=W.GridLength.Auto});body.RowDefinitions.Add(new(){Height=new(1,W.GridUnitType.Star)});body.Children.Add(controls);
        var scroll=new C.ScrollViewer{Content=table,VerticalScrollBarVisibility=C.ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=C.ScrollBarVisibility.Disabled};C.Grid.SetRow(scroll,1);body.Children.Add(scroll);
        Content=NativeDialogControls.Layout(body,NativeDialogControls.Row(reset,save,cancel),scroll:false);
        colors.SelectionChanged+=(_,_)=>RefreshPreview();size.ValueChanged+=(_,_)=>RefreshPreview();RefreshPreview();
    }
    private void RefreshPreview()
    {
        sizeLabel.Text=UiLanguage.Text("图标大小")+" · "+PreviewSize+" px";
        foreach(var item in previews){item.Image.Source=LibraryEngineIcons.Image(item.Name,UiTheme.Current.SecondaryText,PreviewMode);item.Image.Width=item.Image.Height=PreviewSize;}
    }
}
