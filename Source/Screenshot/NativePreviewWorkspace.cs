using W=System.Windows;
using WC=System.Windows.Controls;
namespace ScreenshotTranslationUiTester;

public sealed partial class PreviewForm
{
    private WC.Grid? _nativePreviewRoot;
    private WC.Border? _nativeImageNotice;
    private readonly List<Action> _nativeLanguageBindings=[];
    private void RefreshNativePreviewLanguage(object? sender,EventArgs args)
    {
        if(IsDisposed)return;
        foreach(var refresh in _nativeLanguageBindings)refresh();
        foreach(var choice in new[]{_imageMode,_textMode,_ocrEngineSelector,_zoom})choice.RefreshLanguage();
        _status.Text=_status.Text;
    }
    private void BuildUi()
    {
        var root=new WC.Grid();root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});root.RowDefinitions.Add(new());root.RowDefinitions.Add(new(){Height=W.GridLength.Auto});_nativePreviewRoot=root;
        var bar=new WC.WrapPanel{Margin=new(6)};root.Children.Add(bar);
        NativeRpgButton Add(string title,Action action,bool full=false,string? icon=null)
        {
            var button=new NativeRpgButton(title,icon??"",(_,_)=>action()){Margin=new(3)};_nativeLanguageBindings.Add(button.Invalidate);bar.Children.Add(button.View);if(full)_fullModeControls.Add(button);return button;
        }
        void Label(string text){var view=new WC.TextBlock{Text=UiLanguage.Text(text),Margin=new(8,0,4,0),VerticalAlignment=W.VerticalAlignment.Center};bar.Children.Add(view);_nativeLanguageBindings.Add(()=>view.Text=UiLanguage.Text(text));}
        var initial=ResolveInitialPreviewSelection();
        _preferredImageMode=initial.Image;_imageMode.Items.AddRange(["原图","译图"]);_suppressImageModeEvent=true;_imageMode.SelectedIndex=(int)_preferredImageMode;_suppressImageModeEvent=false;
        _imageMode.SelectedIndexChanged+=(_,_)=>{if(!_suppressImageModeEvent)SetPreferredImageMode((ImageViewMode)Math.Max(0,_imageMode.SelectedIndex),"UserSelection",true);};Label("图片");bar.Children.Add(_imageMode.View);
        RebuildTextModeItems(initial.Text);_textMode.SelectedIndexChanged+=(_,_)=>{_selectedTextMode=TextModeFromVisibleIndex();if(!_atomicCommitInProgress)UpdateIndependentViews();};_textMode.SelectionChangeCommitted+=(_,_)=>RememberCurrentPreviewSelection();Label("文本");bar.Children.Add(_textMode.View);
        _ocrEngineSelector.Items.AddRange(["RapidOCR（快速）","PaddleOCR（高质量）","Windows OCR（实验 / 回退）"]);_settings.OcrEngine=initial.Engine;_ocrEngineSelector.SelectedIndex=OcrEngineUiMapping.ToSelectedIndex(initial.Engine);_ocrEngineSelector.SelectionChangeCommitted+=(_,_)=>HandleOcrEngineSelectionCommitted();bar.Children.Add(_ocrEngineSelector.View);_fullModeControls.Add(_ocrEngineSelector);
        Add("重新识别 OCR",()=>StartOcr(false));_retryButton=Add("重新翻译",()=>StartTranslation());
        _textVisibilityButton=Add(_settings.PreviewTextPanelVisible?"隐藏文本":"显示文本",()=>SetTextPanelVisibility(!_settings.PreviewTextPanelVisible,true));
        NativeRpgButton pin=null!;pin=Add(TopMost?"已置顶":"置顶",()=>{TopMost=!TopMost;_settings.PreviewAlwaysOnTop=TopMost;pin.Text=TopMost?"已置顶":"置顶";AppearanceSettingsChanged?.Invoke(ApiSettingsSnapshot.Copy(_settings));},icon:"pin");
        var menu=new WC.ContextMenu();
        void Menu(string title,Action action){var item=new WC.MenuItem{Header=UiLanguage.Text(title)};_nativeLanguageBindings.Add(()=>item.Header=UiLanguage.Text(title));item.Click+=(_,_)=>action();menu.Items.Add(item);}
        Menu("按当前模式重绘",async()=>await RedrawUsingCurrentModeAsync());
        foreach(var pair in new (string Title,Action Action)[]{("编辑 OCR 区域",ShowOcrBlockEditor),("区域与调试图层",ShowV2LayerDialog),("取消翻译",CancelTranslation),("查看错误详情",ShowTranslationErrorDetails),("OCR 输入",ShowOcrInput),("译图与字体设置",ShowAppearanceQuickDialog),("翻译设置",ShowTranslationSettings),("复制图片",CopyImage),("复制当前文本",CopyText),("保存图片",SaveImage),("截取当前预览",RequestSelfCapture),("翻译输入追踪",ShowTranslationInputTrace),("重新选择",()=>{ReSelectRequested?.Invoke(this,EventArgs.Empty);Close();})})
        {
            Menu(pair.Title,pair.Action);var button=Add(pair.Title,pair.Action,true);if(pair.Title=="查看错误详情"){_errorDetailsButton=button;button.Enabled=false;}
        }
        Menu("切换完整 / 简洁模式",ToggleChromeMode);Add("切换到游戏简洁模式",ToggleChromeMode,true);
        _moreButton=Add("更多",()=>{menu.PlacementTarget=_moreButton!.View;menu.IsOpen=true;});
        _zoom.Items.AddRange(["适应窗口","100%"]);_zoom.SelectedIndex=0;_zoom.SelectedIndexChanged+=(_,_)=>HandleZoomSelectionChanged();bar.Children.Add(_zoom.View);
        _imagePanel.SetPicture(_picture);_split.Set(_imagePanel,_text);WC.Grid.SetRow(_split.View,1);root.Children.Add(_split.View);
        _nativeImageNotice=new WC.Border{HorizontalAlignment=W.HorizontalAlignment.Center,VerticalAlignment=W.VerticalAlignment.Top,Margin=new(12),Padding=new(10,5,10,5),CornerRadius=new(5),IsHitTestVisible=false,
            Child=new WC.TextBlock{Text=UiLanguage.Text("译图尚未生成，暂显示原图"),TextWrapping=W.TextWrapping.Wrap}};
        _nativeImageNotice.SetResourceReference(WC.Border.BackgroundProperty,"Fusion.Secondary");
        ((WC.TextBlock)_nativeImageNotice.Child).SetResourceReference(WC.TextBlock.ForegroundProperty,"Fusion.Muted");
        ((WC.Grid)_split.Panel1.View).Children.Add(_nativeImageNotice);
        _imagePanel.SizeChanged+=(_,_)=>UpdateImageLayout();_imagePanel.MouseWheel+=Picture_MouseWheel;_imagePanel.MouseDown+=Picture_MouseDown;_imagePanel.MouseMove+=PicturePan_MouseMove;_imagePanel.MouseUp+=Picture_MouseUp;
        _picture.View.LostMouseCapture+=(_,_)=>_panning=false;
        _picture.View.PreviewMouseLeftButtonDown+=(_,_)=>{if(!_fitView)_picture.View.CaptureMouse();};_picture.View.PreviewMouseLeftButtonUp+=(_,_)=>_picture.View.ReleaseMouseCapture();
        var imageMenu=new WC.ContextMenu();
        void ImageAction(string title,Action action){var item=new WC.MenuItem{Header=UiLanguage.Text(title)};_nativeLanguageBindings.Add(()=>item.Header=UiLanguage.Text(title));item.Click+=(_,_)=>action();imageMenu.Items.Add(item);}
        ImageAction("复制原图",()=>CopySpecificImage(_original,"已复制原图"));ImageAction("复制译图",()=>{if(_translatedDisplay is not null)CopySpecificImage(_translatedDisplay,"已复制译图");});ImageAction("保存图片",SaveImage);imageMenu.Opened+=(_,_)=>((WC.MenuItem)imageMenu.Items[1]).IsEnabled=_translatedDisplay is not null;_picture.View.ContextMenu=imageMenu;_imagePanel.View.ContextMenu=imageMenu;
        _status.Margin=new(12,6,12,6);WC.Grid.SetRow(_status.View,2);root.Children.Add(_status.View);
        ApplyChromeMode();UpdateIndependentViews();
        _nativeLanguageBindings.Add(()=>((WC.TextBlock)_nativeImageNotice.Child).Text=UiLanguage.Text("译图尚未生成，暂显示原图"));
        UiLanguage.LanguageChanged+=RefreshNativePreviewLanguage;
        Disposed+=(_,_)=>UiLanguage.LanguageChanged-=RefreshNativePreviewLanguage;
    }
}
