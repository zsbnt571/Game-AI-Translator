# RC2 WDA Full截图生命周期报告

## 已验证前提

真实4K AffinityCapture确认WDA_EXCLUDEFROMCAPTURE与当前CopyFromScreen路径兼容：MainForm和PreviewForm被排除，窗口位置显示真实后方内容，无黑块、透明洞；其他进程窗口保持正常。

## 正式Full进入流程

1. MainForm及所有现有PreviewForm在Handle创建/重建时应用WDA_EXCLUDEFROMCAPTURE。
2. 每次截图前重新校验所有本程序顶层窗口的WDA状态。
3. WDA失败时取消本次截图并记录错误，不使用Hide作为回退。
4. MainForm和PreviewForm保持可见，直接CopyFromScreen生成FrozenBitmap。
5. 显示已经预热、复用Handle的CaptureOverlay。

正式Full路径已移除MainForm.Hide、MainForm.Show、MainForm.Activate和用于等待Hide完成的DwmFlush。HideCapture/AffinityCapture内部诊断模式仍保留各自所需调用，不属于正式Full路径。

## 确认流程

1. Overlay从现有FrozenBitmap完成最终裁剪。
2. Overlay仍为TopMost且可见时，创建PreviewForm、完成Handle和WDA、调用Show；Preview位于Overlay后方。
3. Preview准备完成后，Overlay才Hide并结束ShowDialog。
4. 不再显示或激活MainForm。

右键和Esc只隐藏Overlay，不Show或Activate MainForm。

## 自动测试

- 91项通过，0项失败，真实API 0次。
- MainForm自动WDA检查通过。
- 两个PreviewForm分别自动WDA检查通过。
- 合成4K Create约53 FPS、Move约57 FPS、Resize约47 FPS。
- MouseMove CopyFromScreen 0次，完整Bitmap复制0次。
- 连续取消50次GDI变化0。
- 复用Overlay 25轮保持同一Handle；内存变化约23KB、存活期GDI变化+6。
- TranslationService和OcrService保持RC1哈希一致。

仍需真实4K人工确认正式Full路径的首次进入、确认、取消及多PreviewForm视觉体验。
