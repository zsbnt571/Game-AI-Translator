# Screenshot Translator 0.4.2.6 人工验收候选

状态：独立人工验收候选（Candidate），不是 Accepted。

## 修复结论

- 已撤回 0.4.2.5 的重型全树 OwnerDraw 与递归 `Invalidate(true)`。
- ComboBox、NumericUpDown、TextBox 使用稳定原生绘制；无 ItemHeight、Font 或尺寸改写。
- Theme 不再修改 Button / Toolbar 的 Width、Height、PreferredSize、MinimumSize、Padding、Margin 或 Font。
- Settings Tab 仅绘制标题；鼠标状态未变化时不刷新，变化时仅刷新旧、新标题矩形，不绘制 TabPage。
- Renderer 外观刷新与 UI Theme 已分离；设置保存、Preview 外观同步不再重套整窗主题。
- 保留 0.4.2.5 Page Host invariant、Settings invariant、Diagnostics 与 Safe Recovery。
- OCR、Translation Core、Renderer、Preview State、Preview Size State 均未改动。

## 验证

- Release：0 warning / 0 error。
- V2 / UI / Runtime State 专项：123 PASS / 0 FAIL。
- 0.4.2.5 冻结回归基线：351 PASS / 0 FAIL（冻结程序集结果）。
- Settings 1.4 冻结基线：65 PASS / 0 FAIL（冻结程序集结果）。
- 合计基线：539 PASS / 0 FAIL。
- 真实 API Calls：0。
- ActiveOperations：0。
- Worker residue：NONE。
- Preview Toolbar 100% / 125% / 150% 截图已生成；`RapidOCR（快速）`、`适应窗口`完整显示。
- Settings 截图页、翻译页、外观页以及全部八个 Tab 截图已生成；“历史与缓存”“高级 / 诊断”完整显示。

说明：DPI 125% / 150% 证据由真实 WinForms 窗口在测试进程内执行对应缩放后捕获；当前只有一个物理显示 DPI，仍需人工在真实 125% / 150% 桌面最终确认。Hover 肉眼稳定性也保留为人工验收项。

## 要求逐项回答

1. 基于 0.4.2.5：YES
2. 覆盖历史版本：NO
3. Settings 1.4 未修改：YES
4. OCR 未修改：YES
5. Translation Core 未修改：YES
6. Renderer 未修改：YES
7. Preview State 未修改：YES
8. Preview Size State 未修改：YES
9. Page Host Diagnostics 保留：YES
10. Safe Recovery 保留：YES
11. 撤回重型 OwnerDraw：YES
12. Mouse Hover 仍明显闪烁：NO（自动结构检查；待人工复核）
13. UI 掉帧：未在自动窗口压力检查中发现；待人工复核
14. Paint Storm：NO（不存在全窗 hover invalidate 路径）
15. Layout Storm：NO（主题不改布局属性）
16. Theme 刷新整个 Form：NO
17. Theme 修改 Toolbar Control Size：NO
18. Theme 修改 PreferredSize：NO
19. `Rapid...`：NO
20. `适应...`：NO
21. 100% DPI Toolbar：PASS
22. 125% DPI Toolbar：PASS（缩放窗口证据）
23. 150% DPI Toolbar：PASS（缩放窗口证据）
24. Settings Tab 仍闪烁：NO（局部标题刷新；待人工复核）
25. ComboBox Hover / DropDown：恢复原生绘制；待人工连续 20 次复核
26. 中文按钮完整：YES
27. 白底白字：NO
28. 强制所有系统控件全深色：NO
29. Preview 默认译图保持：YES
30. FollowPrevious 保持：YES
31. Highlight 保持：YES
32. April Renderer 改变：NO（Renderer 源码未改）
33. Renderer PNG：本轮未重新生成；Renderer 路径冻结
34. Page Host 偶发 Bug 宣称根治：NO
35. Page Host Diagnostics 存在：YES
36. Release：0 warning / 0 error
37. 全部测试：539 PASS / 0 FAIL
38. API Calls：0
39. ActiveOperations：0
40. Worker residue：NONE

## 打包说明

当前环境无法连接 NuGet 下载缺失的 Windows x64 self-contained runtime pack，因此本候选以 Release 应用目录交付，而非伪称 self-contained 单文件。目录内 EXE 可在安装 .NET 8 Desktop Runtime 的 Windows 上运行。此限制不影响源码、测试或 UI 修复结论。
