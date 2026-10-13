# RC3 Manual Acceptance（建议 15 项）

1. 1080p/1440p/4K，100%～200% DPI 下主界面无重叠或截断。
2. 快捷键截图、窗口选择、Create/Move/八方向 Resize、右键/Esc。
3. 打开三个 Preview，分别取消、关闭、重新翻译，互不影响。
4. API 未配置时完成一次 OCR Only。
5. Windows OCR 完成一次截图识别。
6. RapidOCR 完成一次截图识别并确认退出无残留 Worker。
7. PaddleOCR 完成一次截图识别并确认 oneDNN disabled。
8. 对同一图片打开 OCR 对比，人工查看文字、框、confidence 和 reading order。
9. 编辑 OCR 文本后重新翻译，Raw OCR 不变化。
10. 禁用一个 OCR Block、调整 Reading Order，再重新翻译。
11. 真实 DeepSeek 翻译一次，确认 thinking disabled、单请求和完整译文。
12. OCR/Translation 缓存命中时检查耗时与 API 次数。
13. 历史记录重开 Preview，关闭程序后历史自动释放。
14. 主动开启实验译图，检查正常与溢出区域，不允许半段裁剪。
15. 用真实用户设置启动、关闭且不修改，确认 settings 文件哈希不变。
