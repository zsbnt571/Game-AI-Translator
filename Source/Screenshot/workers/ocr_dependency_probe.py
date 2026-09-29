"""Read local OCR package metadata only; never import OCR engines or fetch models.

The host runs this with -I -S -B so PYTHONPATH, user site, .pth files, and
sitecustomize cannot redirect this check to another installed environment.
"""
import importlib.metadata
import json
from pathlib import Path
import struct
import sys


def inspect(site):
    versions = {}
    locations = {}
    for distribution in importlib.metadata.distributions(path=[str(site)]):
        name = str(distribution.metadata.get("Name", "")).lower().replace("_", "-")
        if name in ("rapidocr", "onnxruntime"):
            if name in versions:
                raise RuntimeError("Duplicate package metadata is not allowed: " + name)
            versions[name] = distribution.version
            locations[name] = str(Path(distribution.locate_file("")).resolve())
    return {
        "schema": 1,
        "pythonVersion": ".".join(str(part) for part in sys.version_info[:3]),
        "bits": struct.calcsize("P") * 8,
        "executable": str(Path(sys.executable).resolve()),
        "prefix": str(Path(sys.prefix).resolve()),
        "basePrefix": str(Path(sys.base_prefix).resolve()),
        "rapidocrVersion": versions.get("rapidocr", ""),
        "onnxruntimeVersion": versions.get("onnxruntime", ""),
        "rapidocrLocation": locations.get("rapidocr", ""),
        "onnxruntimeLocation": locations.get("onnxruntime", ""),
    }


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Expected one explicit site-packages directory.")
    print(json.dumps(inspect(Path(sys.argv[1])), ensure_ascii=True, separators=(",", ":")))
