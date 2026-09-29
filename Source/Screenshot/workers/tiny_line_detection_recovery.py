"""Recover a missing tiny dark text line from image evidence, without changing OCR thresholds.

This supplements detector misses only. Existing accepted OCR and the C3 low-score
orientation recovery are returned unchanged. Candidate pixels never alter the source.
"""
import difflib
import re
import time
import cv2
import numpy as np

CONTRACT = "UNCOVERED_TINY_LIGHT_SURFACE_CONSENSUS_V1"


def proposals(image, blocks):
    heights = sorted(float(b["height"]) for b in blocks if float(b["height"]) >= 8)
    if len(heights) < 4:
        return [], "INSUFFICIENT_EXISTING_TEXT_CONTEXT"
    median = heights[(len(heights) - 1) // 2]
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    closed = cv2.morphologyEx(gray, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (13, 7)))
    ink = ((gray < 155) & (closed > 210) & ((closed.astype(np.int16) - gray) > 55)).astype(np.uint8)
    # A discovered layout area is not a cleanup mask: this is sampling/proposal exclusion only.
    for block in blocks:
        x, y, w, h = [float(block[k]) for k in ("x", "y", "width", "height")]
        l, t = max(0, int(np.floor(x)) - 1), max(0, int(np.floor(y)) - 1)
        r, b = min(gray.shape[1], int(np.ceil(x+w)) + 1), min(gray.shape[0], int(np.ceil(y+h)) + 1)
        if r > l and b > t:
            ink[t:b, l:r] = 0
    count, labels, stats, _ = cv2.connectedComponentsWithStats(ink, connectivity=8)
    ids = [i for i in range(1, count) if 1 <= stats[i, 2] <= 28 and
           1 <= stats[i, 3] <= 13 and 2 <= stats[i, 4] <= 110]
    if not ids:
        return [], "NO_UNCOVERED_TINY_INK"
    eligible = np.isin(labels, ids).astype(np.uint8)
    grouped = cv2.morphologyEx(eligible, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (9, 3)))
    _, _, groups, _ = cv2.connectedComponentsWithStats(grouped, connectivity=8)
    retained = stats[ids]
    result = []
    for x, y, w, h, area in groups[1:]:
        x, y, w, h, area = map(int, (x, y, w, h, area))
        if not (60 <= w <= min(380, image.shape[1]*.28) and 5 <= h <= min(20, median*.5) and w/h >= 5):
            continue
        members = retained[(retained[:, 0] >= x) & (retained[:, 1] >= y) &
                           (retained[:, 0]+retained[:, 2] <= x+w) & (retained[:, 1]+retained[:, 3] <= y+h)]
        pad = 4
        surroundings = gray[max(0,y-pad):min(gray.shape[0],y+h+pad),max(0,x-pad):min(gray.shape[1],x+w+pad)]
        bright = float(np.mean(surroundings > 210))
        density = float(np.mean(ink[y:y+h, x:x+w] > 0))
        if len(members) < 10 or bright <= .65 or not .03 < density < .5:
            continue
        result.append(dict(x=x, y=y, width=w, height=h, componentCount=len(members),
                           brightFraction=bright, inkDensity=density, medianExistingHeight=median))
    result.sort(key=lambda p:(-p["brightFraction"], -p["componentCount"], p["y"], p["x"]))
    return result, "UNTOUCHED_SMALL_INK_PROPOSALS"


def recover(image, blocks, recognizer):
    started = time.perf_counter()
    diagnostic = dict(contract=CONTRACT, detectorSettingsChanged=False, globalScoreChanged=False,
                      candidateCount=0, localRecognitionCalls=0, recoveredCount=0, trace=[])
    added = []
    try:
        candidates, reason = proposals(image, blocks)
        diagnostic.update(candidateCount=len(candidates), reason=reason)
        # Precision before coverage: bounded independently of screenshot area or OCR line count.
        for proposal in candidates[:2]:
            row = dict(proposal, recovered=False, families=[])
            diagnostic["trace"].append(row)
            x, y, w, h = [proposal[k] for k in ("x", "y", "width", "height")]
            valid_families = []
            for rotation in (0, 180):
                variants = []
                for pad in (0, 1, 2):
                    crop = image[max(0,y-pad):min(image.shape[0],y+h+pad),
                                 max(0,x-pad):min(image.shape[1],x+w+pad)].copy()
                    if rotation:
                        crop = cv2.rotate(crop, cv2.ROTATE_180)
                    from rapidocr.ch_ppocr_rec.typings import TextRecInput
                    answer = recognizer(TextRecInput(img=[crop], return_word_box=False))
                    diagnostic["localRecognitionCalls"] += 1
                    text = str(answer.txts[0]).strip() if answer.txts else ""
                    confidence = float(answer.scores[0]) if answer.scores else 0.0
                    variants.append(dict(pad=pad,text=text,score=confidence))
                normalized = ["".join(c.lower() for c in v["text"] if c.isalnum()) for v in variants]
                agreement = [[difflib.SequenceMatcher(None,a,b).ratio() for b in normalized] for a in normalized]
                minimum = min(min(values) for values in agreement)
                valid = minimum >= .9 and all(v["score"] >= .85 and len(n) >= 12 and
                    len(re.findall(r"[^\W\d_]{2,}",v["text"],flags=re.UNICODE)) >= 3 for v,n in zip(variants,normalized))
                row["families"].append(dict(rotation=rotation, variants=variants, minimumAgreement=minimum, valid=valid))
                if valid:
                    valid_families.append((rotation,variants,agreement))
            if len(valid_families) != 1:
                row["reason"] = "NO_UNAMBIGUOUS_COMPLETE_LINE_CONSENSUS"
                continue
            rotation, variants, agreement = valid_families[0]
            medoid = max(range(3),key=lambda i:(sum(agreement[i]),i==1))
            from small_line_candidate_selection import select as select_candidate
            medoid, selection = select_candidate(variants, medoid)
            row['candidateSelection'] = selection
            chosen = variants[medoid]
            # Keep actual recognizer text, including uncertain spellings. No fixture-specific correction.
            pad = 2
            l,t=max(0,x-pad),max(0,y-pad)
            r,b=min(image.shape[1],x+w+pad),min(image.shape[0],y+h+pad)
            added.append(dict(text=chosen["text"],confidence=min(v["score"] for v in variants),
                x=l,y=t,width=r-l,height=b-t,polygon=[[l,t],[r,t],[r,b],[l,b]],
                tinyLineRecovery=CONTRACT))
            row.update(recovered=True,reason="UNCOVERED_TINY_LINE_ORIENTATION_CONSENSUS",
                       acceptedText=chosen["text"],acceptedScore=added[-1]["confidence"],localRotation=rotation)
        diagnostic["recoveredCount"] = len(added)
    except Exception as error:
        # Enhancement failures never drop already accepted full-frame OCR.
        diagnostic["reason"] = "ORIGINAL_PRESERVED_AFTER_" + type(error).__name__
        added = []
        diagnostic["recoveredCount"] = 0
    diagnostic["totalMs"] = (time.perf_counter()-started)*1000.0
    return added, diagnostic
