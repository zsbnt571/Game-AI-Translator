# OCR Runtime Manifest

## 已验证版本

- Python 3.11.9 x64
- PaddlePaddle 3.3.1 CPU
- PaddleOCR 3.7.0
- PaddleX 3.7.2
- RapidOCR 3.9.2
- ONNX Runtime 1.28.0 CPU
- NumPy 2.3.5
- OpenCV 5.0.0.93

## 模型

- Paddle `PP-OCRv6_medium_det`: 62,273,512 bytes
- Paddle `PP-OCRv6_medium_rec`: 76,837,481 bytes
- Rapid `PP-OCRv6_det_small.onnx`: 9,929,594 bytes
- Rapid `PP-OCRv6_rec_small.onnx`: 21,234,383 bytes
- Rapid angle classifier: 585,532 bytes

## 运行环境大小

- 项目 Python: 80,045,212 bytes
- 隔离 venv/packages: 977,164,116 bytes
- runtime cache: 26,216,840 bytes
- Paddle 模型合计: 139,110,993 bytes
- Rapid 模型合计: 31,749,509 bytes

发布目录使用 `runtime/location.txt` 指向本机已验证隔离 Runtime；未复制 pip 下载缓存。新机器可由用户主动运行安装/修复脚本。
