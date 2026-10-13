# 0.3.0-rc2-initial-hover-ready-test

## 修改范围

基线为 `0.3.0-rc2-hover-selection-stable-test`。本候选只提前准备 CaptureOverlay 的 Initial Hover 状态，并更新版本显示；未修改 OCR、翻译请求、Preview 业务逻辑、Temporary WDA、Settings Safe、Create、Move、Resize 或现有 DirtyRegion 策略。

## Initial Hover 生命周期

旧流程：

`FrozenBitmap ready → ResetReusableState(_hoveredWindow=Empty) → ShowDialog → 首帧无 Hover → 首次 MouseMove/HitTest → Hover 更新`

新流程：

`FrozenBitmap ready → ResetReusableState → Overlay 仍不可见 → Cursor.Position → 原有 FindWindowAt() → _hoveredWindow ready → ShowDialog`

Initial Hover 只更新内存状态，不调用 `Invalidate`、`Refresh` 或 `Update`。普通窗口、最大化窗口和无窗口区域均复用同一个 `FindWindowAt()` 及既有过滤规则，没有新增第二套窗口判断。

后续 MouseMove 通过统一的 `UpdateHoveredWindow()` 比较结果。首次检测仍为同一窗口时直接返回，不修改状态、不产生 DirtyRegion、不触发 Paint。

## 自动测试

- 总计：125 通过，0 失败。
- Initial Hover 普通窗口：通过。
- Initial Hover 最大化窗口：通过。
- Initial Hover 无窗口区域：通过。
- Show 前准备过程 Invalidate：0。
- 首次相同最大化窗口 MouseMove Invalidate：0。
- 不存在首帧可见的 `null → 最大化窗口` 状态转换：通过。
- 上一版窗口点击无全灰中间帧：通过。
- 远距离 WindowA/WindowB 独立 DirtyRegion：通过。
- Create、Move、Resize：全部通过。
- Settings Safe：全部通过。
- Temporary WDA：全部通过。
- MouseMove CopyFromScreen：0。
- MouseMove 完整 Bitmap 复制：0。
- 合成 4K：Create 62.9 FPS、Move 53.9 FPS、Resize 46.9 FPS。
- 50 次取消：内存变化 3,768 字节、GDI 变化 0。
- 真实 API 调用：0 次。

## 人工验收

本候选不宣称已解决剩余闪屏，只验证：

1. 鼠标位于普通窗口内，通过快捷键进入截图应保持正常。
2. 鼠标位于最大化窗口内，通过快捷键进入截图，观察剩余闪屏是否消失。
3. 点击最大化窗口，确认上一版已修复的点击闪屏没有回归。
