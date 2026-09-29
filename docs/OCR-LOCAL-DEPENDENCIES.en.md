# Local OCR dependencies

[中文](OCR-LOCAL-DEPENDENCIES.md)

The Alpha source and application distribution do not bundle Python, RapidOCR,
ONNX Runtime, or OCR models. Users must lawfully provide them. The application
does not download, install, update, or search other installations for these files.
Missing OCR dependencies do not prevent the application, settings, or library
from opening.

## Fixed directory

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

`venv/Lib/site-packages` is a fixed package directory, not a request to run a
different virtual environment's `venv/Scripts/python.exe`. The fixed interpreter
must use its standard library and runtime within this `runtime` directory. It
must not point back to a development machine's base Python installation.
RapidOCR's other Python dependencies are also required. Correct metadata alone
does not establish working OCR; the runtime manager separately runs an actual
recognition check.

The default is `runtime` next to the application. An explicit `dependencies.json`
may select an application-contained relative directory. Absolute paths, escaping
relative paths, or invalid configuration never cause a search elsewhere.

## Pinned versions and models

| Component | Required version / architecture |
|---|---|
| Python | 3.11.9, x64 |
| RapidOCR | 3.9.2 |
| ONNX Runtime | 1.28.0 |

The existing model identities and recognition algorithm are unchanged.

| Model | SHA-256 |
|---|---|
| PP-OCRv6_det_small.onnx | `090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f` |
| ch_ppocr_mobile_v2.0_cls_mobile.onnx | `e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c` |
| PP-OCRv6_rec_small.onnx | `6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884` |

A matching hash proves file identity, not redistribution permission.

## Checks and states

- `Missing`: A required Python interpreter, package directory, worker, or model is absent.
- `InvalidVersion`: The Python, RapidOCR, or ONNX Runtime version differs from its pin.
- `WrongArchitecture`: The interpreter is not x64.
- `HashMismatch`: A model hash differs.
- `NotChecked`: Files exist but have not been validated and are not Ready.
- `CheckFailed`: Unsafe path, link, invalid metadata, timeout, or cancellation.
- `Ready`: Local dependency validation passed.

The metadata probe runs the explicit interpreter with `-I -S -B`, reads only the
selected local distribution metadata, and has an eight-second timeout. It does
not import RapidOCR or ONNX Runtime or execute site hooks. Interpreter prefixes
must remain inside the runtime directory; links are rejected. OCR workers use
only the application's worker and package directories, with inherited Python,
virtual environment, timing-output, and search-path overrides removed. The SDK
network guard prevents implicit model downloads.

Dependency readiness and successful recognition are separate checks. A failed
recognition check remains unavailable. This document covers the RapidOCR route.
Windows OCR depends on OS components and installed languages. Other experimental
OCR routes are not covered by this pinned-environment verification.
