# 本地 OCR 依赖 / Local OCR dependencies

[English](OCR-LOCAL-DEPENDENCIES.en.md)

Alpha 源码与公开运行包不附带 Python、RapidOCR、ONNX Runtime 或 OCR 模型。
这些依赖由用户自行合法取得；程序不会下载、安装、更新或搜索其他软件的运行环境。
缺少它们只影响相应的 OCR 功能，不妨碍打开主程序、设置和游戏库。

The Alpha source and application distribution do not bundle Python, RapidOCR,
ONNX Runtime, or OCR models. Users must lawfully provide them. The application
does not download, install, update, or search other installations for these files.
Missing OCR dependencies do not prevent the application, settings, or library
from opening.

## 固定目录 / Fixed directory

```text
<application-directory>/
  GameTranslator.exe
  workers/                         # Project-owned worker source files
    ocr_dependency_probe.py
    rapid_worker.py
    rapid_health.py
    ...
  runtime/                         # User-provided files; excluded from Release
    python/
      python.exe                   # Python 3.11.9 x64
      ...                          # Its matching local standard library/runtime
    venv/Lib/site-packages/
      rapidocr/                    # RapidOCR 3.9.2
        models/
          PP-OCRv6_det_small.onnx
          ch_ppocr_mobile_v2.0_cls_mobile.onnx
          PP-OCRv6_rec_small.onnx
      rapidocr-3.9.2.dist-info/
      onnxruntime/                 # ONNX Runtime 1.28.0
      onnxruntime-1.28.0.dist-info/
      ...                          # Matching lawful dependencies and metadata
```

这里的 `venv/Lib/site-packages` 是固定包目录名，不要求通过另一个环境的
`venv/Scripts/python.exe` 启动。`runtime/python/python.exe` 必须使用同一
`runtime` 目录内的标准库与运行环境，不允许指回开发机的基础 Python 安装。
实际配置还需要 RapidOCR 所需的其他 Python 依赖。只放三个版本元数据文件不代表
OCR 可以运行；“模型与运行环境管理”的实际识别检查仍需通过。

`venv/Lib/site-packages` is a fixed package directory, not a request to run a
different virtual environment's interpreter. The fixed interpreter must use its
standard library and runtime within this `runtime` directory. RapidOCR's other
Python dependencies are also required. Correct metadata alone does not establish
working OCR; the runtime manager separately runs an actual recognition check.

公开配置默认使用程序旁的 `runtime`。显式的 `dependencies.json` 只接受程序目录
内的相对路径。绝对路径、越界相对路径或损坏的配置不会触发其他位置的查找。

The default is `runtime` next to the application. An explicit `dependencies.json`
may select an application-contained relative directory. Absolute paths, escaping
relative paths, or invalid configuration never cause a search elsewhere.

## 固定版本与模型 / Pinned versions and models

| Component | Required version / architecture |
|---|---|
| Python | 3.11.9, x64 |
| RapidOCR | 3.9.2 |
| ONNX Runtime | 1.28.0 |

模型身份沿用现有 OCR 路径中的固定 SHA-256；本轮未替换模型或修改识别算法。
The existing model identities and recognition algorithm are unchanged.

| Model | SHA-256 |
|---|---|
| PP-OCRv6_det_small.onnx | `090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f` |
| ch_ppocr_mobile_v2.0_cls_mobile.onnx | `e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c` |
| PP-OCRv6_rec_small.onnx | `6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884` |

哈希匹配只是文件身份检查，不表示项目已经取得该文件的公开分发权限。
A matching hash proves file identity, not redistribution permission.

## 检查与状态 / Checks and states

- `Missing`：缺少 Python、包目录、工作脚本或模型 / A required local component is absent.
- `InvalidVersion`：Python、RapidOCR 或 ONNX Runtime 版本不符 / A pinned version differs.
- `WrongArchitecture`：Python 不是 x64 / The interpreter is not x64.
- `HashMismatch`：模型 SHA-256 不符 / A model hash differs.
- `NotChecked`：文件存在但还未验证，不能视为 Ready / Files exist but have not been validated.
- `CheckFailed`：路径越界、链接、无法解析的元数据、超时或取消 / Unsafe path, link, invalid metadata, timeout, or cancellation.
- `Ready`：本地依赖检查通过 / Local dependency validation passed.

版本探针以 `-I -S -B` 启动固定 Python，只读取明确包目录中的 distribution
metadata，不导入 RapidOCR 或 ONNX Runtime，也不执行 site hooks。探针最多运行
8 秒。检查同时核对解释器前缀是否仍在本地运行目录内，且拒绝链接路径。
后续 OCR 工作进程只加入项目工作脚本目录和固定包目录，不继承开发机的 Python、
虚拟环境、测量日志位置或搜索路径。已有 SDK 网络阻断用于防止隐式模型下载。

The metadata probe runs the explicit interpreter with `-I -S -B`, reads only the
selected local distribution metadata, and has an eight-second timeout. It does
not import OCR engines or execute site hooks. Interpreter prefixes must remain
inside the runtime directory; links are rejected. OCR workers use only the
application's worker and package directories, with inherited Python, virtual
environment, timing-output, and search-path overrides removed. The SDK network
guard prevents implicit model downloads.

`Ready` 的依赖结果与实际识别成功应分别看待。实际识别检查失败时仍显示未就绪。
本说明针对 RapidOCR 路径；Windows OCR 取决于操作系统组件与已安装语言包，
其他实验 OCR 路径不属于上述版本组合的已验证结论。

Dependency readiness and successful recognition are separate checks. A failed
recognition check remains unavailable. This document covers the RapidOCR route.
Windows OCR depends on OS components and installed languages. Other experimental
OCR routes are not covered by this pinned-environment verification.
