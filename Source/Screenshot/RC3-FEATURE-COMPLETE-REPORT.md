# RC3 Feature Complete Report

状态：**RC3 FEATURE COMPLETE CANDIDATE**，不是正式 0.3.0，等待人工验收。

## 核心冻结

- 唯一基线：`0.3.0-rc2-accepted/source`。
- `CaptureOverlay.cs` 当前/基线 SHA256：`391B6D8ED50F462DC56691576F45DE1136832721403C1FF9BD1C94CD17EB4B87`，未修改。
- `TranslationService.cs` 当前/基线 SHA256：`19C7339D8EE91771E31DE878AC2F2B1FDFEFF18E5FCBC75304918D1F90F36057`，未修改。
- Initial Hover Ready、独立 DirtyRegion、Pending Window Click、Create/Move/Resize、Temporary WDA、Settings Safe 与 thinking disabled 均保留。

## 自动结果

- 冻结基线回归：125 PASS / 0 FAIL。
- RC3 功能回归：20 PASS / 0 FAIL。
- 合计：145 PASS / 0 FAIL。
- Release build：PASS，0 error；2 个已知未赋值诊断字段 warning。
- 真实翻译 API：0 次。
- Capture：Create 63.3 / Move 62.2 / Resize 62.9 FPS。
- 50 次取消：内存 +3,784 bytes，GDI +0。
- Worker 无设计性残留：应用退出等待 shutdown；真实长期切换仍列入人工验收。

## 功能状态

- 三 OCR：完成；失败自动回退 Windows 且不改设置。
- OCR Cache：完成，LRU 32；命中返回副本。
- Translation Cache：完成，命中 API 次数 0，只缓存完整成功结果。
- OCR 编辑：完成；Raw 快照不覆盖。
- Block enable/order：完成。
- Preview：完成，多窗口隔离。
- 译图 V2：实验路径完成；溢出保留原文，默认关闭。
- History：会话内完成，默认 20 项并受内存预算限制。
- Settings migration/safe：完成；原子保存和 dirty guard。
- Temporary WDA：冻结回归全部通过。
- API Key：UI 掩码；日志、测试、报告不输出内容。

## 设置安全前置

Codex执行环境无法直接读取真实AppData settings；Preflight SHA256和安全备份由用户在正常Windows环境完成，原配置与备份SHA256一致。自动测试只使用安全副本、临时 AppData 和 synthetic settings。

## 已知限制

- 三 OCR 的真实质量尚未由用户人工评分，默认保持 Windows。
- Paddle CPU 在部分样本明显慢于 Windows/Rapid，且内存更高。
- 译图仍属实验功能，复杂背景和长文本需人工视觉验收。
- DPI/4K视觉、真实 Runtime 切换和真实 API 只留给最终人工验收。

## 发布目录启动检查

- EXE 存在：PASS。
- 从独立 `program` 目录启动：PASS。
- 窗口标题显示 `游戏 AI 截图翻译器 0.3.0-rc3-feature-complete-test`：PASS。
- 隔离配置不存在时启动不会在未修改情况下写出空配置：PASS。
- `CloseMainWindow` 正常退出：PASS。
- 启动检查后无实验 Runtime `python.exe` 残留。
