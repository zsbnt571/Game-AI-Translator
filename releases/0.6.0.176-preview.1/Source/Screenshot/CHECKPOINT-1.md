# CHECKPOINT-1 — 三 OCR 正式架构

- Build: PASS
- Windows / Rapid / Paddle 统一为 `IOcrEngine`、`OcrEngineResult`、`OcrEngineBlock`。
- Rapid/Paddle 使用按需常驻 Worker；同一时间最多一个外部 Worker；崩溃允许一次重启，再失败回退 Windows。
- Paddle CPU 且 oneDNN disabled；Rapid 使用 ONNX Runtime CPU。
- 8 张固定样本各 5 次真实运行完成，质量评分保持 UNRATED。
