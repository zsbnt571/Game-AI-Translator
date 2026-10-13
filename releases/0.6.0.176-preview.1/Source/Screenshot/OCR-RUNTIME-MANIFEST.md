# OCR Runtime Manifest

RC3 uses local CPU OCR workers and never downloads at startup.

## Engines

- Windows OCR: Windows.Media.Ocr, always available when Windows language packs are installed.
- RapidOCR: RapidOCR 3.9.2 + ONNX Runtime 1.28.0 CPU, PP-OCRv6 models.
- PaddleOCR: PaddlePaddle 3.3.1 + PaddleOCR 3.7.0 + PaddleX 3.7.2 CPU, PP-OCRv6_medium detection and recognition, oneDNN disabled.

## Runtime layout

The current verified experimental runtime is referenced through `runtime/location.txt`. A portable install may replace that reference with an embedded layout:

```
runtime/
  rapid/
  paddle/
  models/
  cache/
  logs/
```

Verified runtime size: 1,033.2 MiB (Python 76.3 MiB, isolated packages 931.9 MiB, cache 25.0 MiB). Paddle's official model cache is external to this number and is discovered by PaddleX. No pip download cache is copied into RC3.

Workers are lazy-loaded, CPU-only, limited to one active external engine, restarted once after a crash, and stopped on application exit.
