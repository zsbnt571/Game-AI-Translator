# OCR 实验运行环境准备

## 用途

`SETUP-OCR-RUNTIME.ps1` 只为当前 `OcrComparisonLab` 准备隔离环境。它不会修改系统 PATH，不会安装系统级 OCR，不会修改 RC1、RC2 或正式配置。

安装内容全部位于：

```text
runtime/
  python/
  venv/
  models/
  cache/
  logs/
```

## 使用方法

1. 保持电脑联网。
2. 在本文件所在目录打开普通 Windows PowerShell。
3. 运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\SETUP-OCR-RUNTIME.ps1
```

脚本会从 Python 官方网站获取 Python 3.11 x64，并在项目内部创建虚拟环境。随后安装 CPU 版本的 PaddlePaddle、PaddleOCR 3.7、RapidOCR 3.9+和 ONNX Runtime CPU。

Paddle 使用 `PP-OCRv6_medium_det` 与 `PP-OCRv6_medium_rec`；RapidOCR使用其当前官方 PP-OCRv6 ONNX 模型。不会安装 CUDA、TensorRT、GPU运行时、EasyOCR或Tesseract。

## 验证

安装最后会用 `tests/ocr-samples` 中的一张现有图片分别真实运行 PaddleOCR 和 RapidOCR。只有两个管线都返回可解析结果，脚本才返回成功。

结果文件：

- `runtime/requirements-lock.txt`：实际安装版本。
- `runtime/verification-result.json`：引擎、耗时、模型路径与大小。
- `runtime/logs/setup-ocr-runtime.log`：完整安装日志。

首次运行需要下载官方模型；模型固定到项目的 `runtime/models`、`runtime/cache`或隔离虚拟环境中。后续运行应复用本地文件。

## 清理

运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\REMOVE-OCR-RUNTIME.ps1
```

清理脚本只删除当前实验的 `runtime` 环境及其模型链接，不删除源码、测试图片、评测结果、RC1、RC2或正式用户配置。

## 故障

失败时窗口不会自动关闭，会显示失败阶段和日志位置，并返回非零退出码。请保留 `runtime/logs/setup-ocr-runtime.log` 供诊断，日志不包含翻译 API Key。
