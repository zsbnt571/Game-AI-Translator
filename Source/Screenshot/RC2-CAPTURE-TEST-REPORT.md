# RC2 Capture UX 自动测试报告

- 自动测试：84 项通过，0 项失败
- 真实 DeepSeek API：0 次
- 合成 4K 新建选区：61.9 FPS
- 合成 4K Move：62.1 FPS
- 合成 4K Resize：49.4 FPS
- MouseMove 期间 CopyFromScreen：0 次
- MouseMove 期间完整 Bitmap 复制：0 次
- 旧矩形失效计数：272
- 新矩形失效计数：272
- 平均 DirtyRegion 面积：367,082 像素
- 连续取消 50 次后托管内存变化：+4,400 字节
- 连续取消 50 次后 GDI 对象变化：0

## 进入截图生命周期

旧流程：主窗口 Hide → 固定等待 160ms → CaptureOverlay 构造并 CopyFromScreen → ShowDialog → 首次 Paint。

新流程：主窗口 Hide → DwmFlush → CaptureOverlay 构造并立即完成 CopyFromScreen → ShowDialog → 首次 Paint 直接绘制冻结图和遮罩。

Overlay 每次进入只发生 1 次显示、0 次 Hide；Opacity 不切换；Bounds 在显示前设置 1 次。新流程消除了固定的 160ms 裸桌面重合成窗口。

## 启动首帧测量

自动环境无法可靠调用真实桌面 CopyFromScreen，因此启动生命周期测试使用注入的 2560×1440 冻结 Bitmap，连续运行 20 次：

- 触发到冻结 Bitmap 就绪：平均 6.6ms，最大 8.6ms
- 冻结 Bitmap 就绪到 Overlay 可见：平均 5.9ms
- 触发到第一次完整 Paint：平均 12.8ms
- Overlay Show：每次 1 次
- Overlay Hide：每次 0 次（窗口直接关闭）
- Opacity 切换：0 次
- Bounds/WindowState 可见后变化：0 次

合成 4K 帧率使用 3840×2160 不可变冻结 Bitmap；真实 4K 显示器上的进入闪屏仍需人工确认。用户已确认修复前 RC2 的 Move、Resize、无残影、工具栏和裁剪坐标表现正常，本次未修改这些路径。
