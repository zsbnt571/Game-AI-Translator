# Repair4.1 Confidence Capability

## Verified data sources

- RapidOCR worker: block confidence comes from the worker `result.scores` output.
- PaddleOCR worker: block confidence comes from `rec_scores`.
- The established `mouth-preview-reading.png` smoke produced 28/28 confidence-bearing blocks for both engines.
- The established `april-real-ui.png` smoke produced 38/38 Rapid and 39/39 Paddle confidence-bearing blocks.

## Capability boundary

| Capability | Rapid | Paddle |
|---|---:|---:|
| Block/line confidence | Available | Available |
| Character confidence | Unavailable | Unavailable |
| Character Top-K | Unavailable | Unavailable |
| CTC logits | Unavailable | Unavailable |

The current worker JSON contracts expose recognition-level scores only. They do not return character posterior distributions, Top-K candidates, or raw CTC logits. Repair4.1 does not invent these values. Missing confidence is displayed as `Unavailable (N/A)` and is not counted as zero or low confidence.

## Retry verification

The April fixed sample produced a real Paddle block confidence of 0.7461. With the bounded 0.75 retry threshold, exactly one ROI retry ran (`PaddleRetryCount=1`). Retry uses 10% padding, 2x upscale and grayscale/contrast preprocessing. Original raw text is retained, and a candidate replaces it only when confidence improves by at least 0.12. Each image is limited to one retry block.
