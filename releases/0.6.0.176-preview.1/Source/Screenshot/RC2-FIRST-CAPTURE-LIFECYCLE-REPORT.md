# RC2 首次截图窗口生命周期修复报告

## 代码路径差异

首次进入截图会执行：MainForm.Hide、DwmFlush、准备FrozenBitmap、首次创建CaptureOverlay Handle、首次创建全屏WinForms/DWM表面、首次OnShown和首次完整Paint。

工具栏“重新选择”只在当前Overlay内调用ResetSelection：清空选区、窗口高亮和工具栏后重绘。它不会new CaptureOverlay、不会CreateHandle、不会Hide/Show MainForm、不会重新CopyFromScreen，也不会再次OnShown。

这解释了人工观察到的“第一次闪、重新选择不闪”：差异不在Move、Resize、DirtyRegion、遮罩或截图算法，而在首次创建和切换全屏Overlay窗口。

## 新生命周期

程序主窗口首次Shown后：创建一个可复用CaptureOverlay，设置VirtualScreen Bounds、DPI/双缓冲样式、工具栏和GDI资源，并提前CreateHandle；窗口保持不可见，不截图、不抢焦点。

每次真正截图：MainForm.Hide → DwmFlush → 获取新的FrozenBitmap → 重置选区和交互状态 → 显示已有Handle的Overlay。

截图结束：Hide Overlay → 释放当次FrozenBitmap → 保留Handle、工具栏和轻量GDI资源。主程序退出时才Dispose Overlay。

MainForm仍然Hide。WDA_EXCLUDEFROMCAPTURE没有启用，因为当前自动桌面无法证明它在CopyFromScreen路径中可靠排除本程序所有窗口；贸然启用可能污染截图或产生黑块。

## 自动验证

- 89项通过，0项失败，真实API 0次。
- 复用25轮保持同一Handle。
- 25轮后托管内存变化约22KB，GDI对象变化+6（Overlay仍存活）；最终Dispose后由既有资源测试覆盖。
- 合成4K Create约61 FPS，Move约61 FPS，Resize本轮测量41～50 FPS。
- MouseMove CopyFromScreen 0次，完整Bitmap复制0次。
- 复用合成时间线：Handle预热约2.4ms；首轮准备约0.7ms、显示及Paint约26.1ms；第二轮准备约1.3ms、显示及Paint约26.7ms。

自动时间使用注入Bitmap，不代表真实4K CopyFromScreen或肉眼闪屏结果。必须完成真实4K人工验收后才能确认首次闪屏已经消除。
