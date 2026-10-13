# Image Translation V2 Report

- 当前为明确的实验 Feature Flag，默认关闭。
- 使用一次紧凑 JSON 批量翻译的同一份结果，不额外调用 API。
- 每个稳定 Group 独立遮盖、测量、二分字号和绘制。
- 溢出时不绘制半段译文、不静默裁剪，完整保留原文；右侧完整译文始终可用。
- 复制/保存图片不包含高亮和调试边框。
- 未继承 0.2.11～0.2.13 的 VisualTextBlock/VisualContainer 段落合并实验。
- 视觉质量仍需人工验收，不作为核心文字翻译可用性的阻断项。
