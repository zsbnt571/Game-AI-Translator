"""Validation and explicit repair of the bundled PR2 model set. No OCR tuning."""
import contextlib
import hashlib
import os
from pathlib import Path
import shutil
import socket
import tempfile
import time
import urllib.request
import uuid

# Pinned to the files and upstream checksums shipped in PR2, not a latest-model lookup.
MODELS = (
    ("PP-OCRv6_det_small.onnx", "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f", "PP-OCRv6/det"),
    ("ch_ppocr_mobile_v2.0_cls_mobile.onnx", "e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c", "PP-OCRv4/cls"),
    ("PP-OCRv6_rec_small.onnx", "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884", "PP-OCRv6/rec"),
)

def model_root():
    return Path(os.environ.get("ST_FUSION_RUNTIME_ROOT", str(Path(__file__).resolve().parent.parent / "runtime"))) / "venv/Lib/site-packages/rapidocr/models"

def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def inspect_models():
    rows = []
    for name, expected, _ in MODELS:
        path = model_root() / name
        state = "MISSING" if not path.is_file() else "VALID" if digest(path) == expected else "INVALID"
        rows.append(dict(name=name, state=state))
    return rows

def require_valid_models():
    bad = [row for row in inspect_models() if row["state"] != "VALID"]
    if bad:
        raise RuntimeError("OCR 模型缺失或损坏，请打开运行环境管理检查并修复：" + ", ".join(row["name"] for row in bad))

@contextlib.contextmanager
def no_network():
    # Process-local guard: a health check must not let the SDK repair files implicitly.
    names = ("create_connection", "getaddrinfo")
    originals = {name: getattr(socket, name) for name in names}
    connect, connect_ex = socket.socket.connect, socket.socket.connect_ex
    def blocked(*args, **kwargs):
        raise RuntimeError("检查期间禁止联网；需要下载时请使用明确的联网修复入口。")
    try:
        for name in names: setattr(socket, name, blocked)
        socket.socket.connect = blocked
        socket.socket.connect_ex = blocked
        yield
    finally:
        for name, original in originals.items(): setattr(socket, name, original)
        socket.socket.connect, socket.socket.connect_ex = connect, connect_ex

def check(recognize):
    result = dict(engineStatus="ERROR", code="COMPONENT_LOAD_FAILED", componentsReady=False,
                  modelsValid=False, recognitionPassed=False, networkAllowed=False,
                  downloadsAttempted=0, errorMessage="", model="RapidOCR PP-OCRv6 / CPU")
    try:
        with no_network():
            import cv2
            import numpy as np
            import onnxruntime
            import rapidocr
            result["componentsReady"] = True
            rows = inspect_models()
            result["models"] = rows
            bad = [row for row in rows if row["state"] != "VALID"]
            if bad:
                result["code"] = "MODEL_MISSING" if any(row["state"] == "MISSING" for row in bad) else "MODEL_INVALID"
                result["errorMessage"] = "模型缺失或校验失败：" + ", ".join(row["name"] for row in bad) + "。检查未下载或修改模型。"
                return result
            result["modelsValid"] = True
            result["code"] = "RECOGNITION_FAILED"
            image = np.full((100, 480, 3), 255, np.uint8)
            cv2.putText(image, "OCR TEST 123", (18, 66), cv2.FONT_HERSHEY_SIMPLEX, 1.5, (0, 0, 0), 3, cv2.LINE_AA)
            with tempfile.TemporaryDirectory(prefix="st-ocr-check-") as temp:
                path = str(Path(temp) / "probe.png")
                if not cv2.imwrite(path, image): raise RuntimeError("无法写入检查图片")
                observed = recognize(path)
            text = "".join(str(row.get("text", "")) for row in observed.get("blocks", []))
            normalized = "".join(char for char in text.upper() if char.isalnum())
            if normalized != "OCRTEST123":
                raise RuntimeError("小图实际识别结果未通过校验")
            result.update(engineStatus="ENVIRONMENT_READY", code="READY", recognitionPassed=True,
                          probeText=text, detail="组件可加载；3 个模型校验通过；包内引擎实际识别通过。检查未联网。")
    except Exception as exc:
        result["errorMessage"] = f"{result['code']}：{type(exc).__name__}: {exc}"
    return result

def repair(allow_network):
    before = inspect_models()
    result = dict(engineStatus="ERROR", code="REPAIR_FAILED", before=before,
                  downloadsAttempted=0, downloaded=[], restored=[], errorMessage="")
    if allow_network:
        result.update(code="LOCAL_DEPENDENCY_REQUIRED", errorMessage="本 Alpha 不下载 OCR 模型；请自行合法提供固定版本的本地依赖。")
        return result
    deadline = time.monotonic() + 65
    try:
        root = model_root()
        for name, expected, folder in MODELS:
            target = root / name
            if target.is_file() and digest(target) == expected: continue
            saved = Path(str(target) + ".quarantine")
            local = saved.is_file() and digest(saved) == expected
            if not local and not allow_network:
                result.update(code="NETWORK_REQUIRED", errorMessage="没有有效的本地模型恢复材料；需要明确授权联网下载模型，或重新解压完整程序包。")
                return result
            root.mkdir(parents=True, exist_ok=True)
            temporary = root / (name + ".repair-" + uuid.uuid4().hex)
            try:
                if local:
                    shutil.copyfile(saved, temporary)
                else:
                    result["downloadsAttempted"] += 1
                    url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/" + folder + "/" + name
                    with urllib.request.urlopen(url, timeout=15) as response, open(temporary, "wb") as stream:
                        total = 0
                        while True:
                            if time.monotonic() > deadline: raise TimeoutError("联网修复总时限已到")
                            chunk = response.read(65536)
                            if not chunk: break
                            total += len(chunk)
                            if total > 64 * 1024 * 1024: raise RuntimeError("下载大小超出本包模型上限")
                            stream.write(chunk)
                if digest(temporary) != expected: raise RuntimeError("恢复文件校验失败，未覆盖现有模型")
                os.replace(temporary, target)
                result["restored" if local else "downloaded"].append(name)
            finally:
                temporary.unlink(missing_ok=True)
        result.update(engineStatus="REPAIR_COMPLETE", code="REPAIRED",
                      detail=f"本地恢复 {len(result['restored'])} 个，联网下载 {len(result['downloaded'])} 个；待重新检查实际识别。")
    except Exception as exc:
        result["errorMessage"] = "修复未完成：离线、连接失败、超时或恢复材料无效（" + type(exc).__name__ + "）。未无限重试；请联网后重试或重新解压完整程序包。"
    return result
