# OCR Phase 1 — Real local results

Status: **execution complete; human quality rating UNRATED**.

The fixed eight-image set from OcrComparisonLab was used without substitution. Windows OCR's existing 8×5 baseline was retained because its implementation hash did not change. RapidOCR and PaddleOCR each ran 8 images × 5 iterations through persistent workers (80 new local OCR executions total).

| Engine | Samples | Runs | Average first use | Average warm OCR | Peak working set |
|---|---:|---:|---:|---:|---:|
| Windows OCR | 8 | existing 40 | retained baseline | retained baseline | retained baseline |
| RapidOCR 3.9.2 / ONNX Runtime 1.28 CPU | 8 | 40 | 514.2 ms | 356.8 ms | 145.3 MiB |
| PaddleOCR 3.7 / PP-OCRv6_medium CPU | 8 | 40 | 10,183.4 ms | 10,072.7 ms | 640.9 MiB |

Paddle used `enable_mkldnn=False` / `FLAGS_use_mkldnn=0`. No GPU provider was enabled. Raw JSON, raw text, blocks, polygons, confidence and per-run timings are under `reports/ocr-phase1-real/`. The machine-generated benchmark does not select a winner and does not assign quality scores.

Notable performance result: Rapid is materially faster than Paddle medium on this CPU and fixed set. This is only a performance observation; the default remains Windows OCR until the user rates actual recognition quality.
