# OCR Phase 1 Real Results

相同 8 张真实原图，每个引擎 5 次。Windows 代码哈希未变化，复用已有 40 次真实基线；Rapid/Paddle 各新增 40 次真实本地运行。没有后处理混合，也没有自动选赢家。

| Engine | Runs | Avg first use | Avg warm | Global min | Global max | Peak working set |
|---|---:|---:|---:|---:|---:|---:|
| Windows OCR | 40 | 2861.5 ms | 2852.6 ms | 1284 ms | 4302 ms | baseline process |
| RapidOCR | 40 | 514.2 ms | 356.8 ms | 187 ms | 1092 ms | 145.3 MiB |
| PaddleOCR | 40 | 10183.4 ms | 10072.7 ms | 1486 ms | 20299 ms | 640.9 MiB |

- Paddle: CPU，`enable_mkldnn=False`。
- Rapid: ONNX Runtime CPU。
- 人工质量评分：**UNRATED**。默认引擎继续为 Windows OCR。
- 原始 JSON、文本和 CSV 位于 `reports/ocr-phase1-real`。
