# RC2 进入截图闪屏隔离测试

本入口只供内部诊断使用，不出现在普通界面。四种主模式互斥，每次只启动一个进程。日志写入程序目录的 `logs\capture-flash-*.log`。

## 启动命令

在诊断构建目录中运行：

```powershell
.\截图翻译界面测试器-0.3.0-rc2-capture-ux-test.exe --capture-flash-diagnostic=CaptureOnly
.\截图翻译界面测试器-0.3.0-rc2-capture-ux-test.exe --capture-flash-diagnostic=OverlayOnly
.\截图翻译界面测试器-0.3.0-rc2-capture-ux-test.exe --capture-flash-diagnostic=HideCapture
.\截图翻译界面测试器-0.3.0-rc2-capture-ux-test.exe --capture-flash-diagnostic=Full
.\截图翻译界面测试器-0.3.0-rc2-capture-ux-test.exe --capture-flash-diagnostic=AffinityCapture
```

Overlay AB测试：

```powershell
.\截图翻译界面测试器-0.3.0-rc2-capture-ux-test.exe --capture-flash-diagnostic=OverlayOnly --overlay-noactivate
.\截图翻译界面测试器-0.3.0-rc2-capture-ux-test.exe --capture-flash-diagnostic=OverlayOnly --overlay-noshade
```

- `CaptureOnly`：不隐藏MainForm、不显示Overlay，只执行一次CopyFromScreen。
- `OverlayOnly`：不隐藏MainForm、不执行CopyFromScreen，使用预先生成的静态Bitmap显示Overlay。
- `HideCapture`：隐藏MainForm、DwmFlush、CopyFromScreen，然后恢复MainForm，不显示Overlay。
- `Full`：现有RC2完整流程。
- `AffinityCapture`：MainForm保持可见，仅对本程序窗口临时设置WDA_EXCLUDEFROMCAPTURE，执行一次CopyFromScreen并在`logs`保存`wda-exclude-capture.png`，随后恢复WDA_NONE。用于判断截图中是正确排除、出现黑块，还是仍包含本程序窗口。
- `--overlay-noactivate`：使用WS_EX_NOACTIVATE和ShowWindow(SW_SHOWNOACTIVATE)显示测试Overlay。
- `--overlay-noshade`：保持相同Overlay与静态图，但遮罩画刷为透明，用于区分亮度突变和窗口闪屏。

每个模式观察3～5次后用右键退出Overlay；CaptureOnly和HideCapture完成后可直接关闭主窗口。

## 真实4K记录表

| 模式 | 是否闪屏 | 观察说明 |
| --- | --- | --- |
| Capture Only | 待人工测试 | |
| Overlay Only | 待人工测试 | |
| Hide + Capture | 待人工测试 | |
| Full | 待人工测试 | |
| Overlay不激活 | 待人工测试 | |
| Overlay无灰色遮罩 | 待人工测试 | |

自动环境只能验证分支、调用顺序和日志，不能替代真实4K肉眼闪屏判断。
