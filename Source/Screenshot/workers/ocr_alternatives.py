"""Bounded, non-mutating candidate evidence for very small accepted sentence lines."""
import contextlib
import sys
import hashlib
import math
import time
import cv2
import numpy as np
from pathlib import Path

CONTRACT = "TINY_OCR_ALTERNATIVES_SOURCE_CROP_V1"
CROP_HASH_CONTRACT = "SHA256_ASCII_BGR8_LF_WIDTH_LF_HEIGHT_LF_THEN_ROW_MAJOR_BGR_BYTES"

def crop_sha(image):
    h, w = image.shape[:2]
    digest = hashlib.sha256(("BGR8\n"+str(w)+"\n"+str(h)+"\n").encode("ascii"))
    digest.update(np.ascontiguousarray(image, dtype=np.uint8).tobytes())
    return digest.hexdigest().upper()

def collect(image, blocks, recognizer, image_path):
    started = time.perf_counter()
    diagnostic = dict(contract=CONTRACT, cropHashContract=CROP_HASH_CONTRACT,
        eligible=0, inspected=0, recognitionCalls=0, retainedAlternatives=0,
        mainTextChanged=False, trace=[])
    try:
        heights = sorted(float(b["height"]) for b in blocks if float(b["height"]) >= 8)
        if len(heights) < 4:
            diagnostic["reason"] = "INSUFFICIENT_NORMAL_TEXT_CONTEXT"
            return diagnostic
        median = heights[(len(heights)-1)//2]
        selected = []
        for block in blocks:
            text = str(block.get("text",""))
            score = float(block.get("confidence") or 0)
            x,y,w,h = [float(block[k]) for k in ("x","y","width","height")]
            alpha_words = sum(sum(c.isalpha() for c in word) >= 2 for word in text.split())
            if not (math.isfinite(score) and .85 <= score <= 1 and
                    all(math.isfinite(v) for v in (x,y,w,h)) and
                    6 <= h <= min(18,median*.65) and 70 <= w <= min(380,image.shape[1]*.3) and
                    w/h >= 5 and 12 <= sum(c.isalnum() for c in text) <= 90 and alpha_words >= 3):
                continue
            selected.append(block)
        diagnostic["eligible"] = len(selected)
        if not selected:
            diagnostic["reason"] = "NO_ACCEPTED_TINY_SENTENCE"
            return diagnostic
        # Source order is retained; at most two local lines and eight direct calls per line.
        source_hash = hashlib.sha256(Path(image_path).read_bytes()).hexdigest().upper()
        from small_line_recovery import SmallLineRecoveryRecognizer
        from rapidocr.ch_ppocr_rec.typings import TextRecInput
        for block in selected[:2]:
            row = dict(sourceBlockId=block["id"], mainText=str(block["text"]), status="INSPECTED")
            diagnostic["trace"].append(row)
            l=max(0,int(math.floor(block["x"])));t=max(0,int(math.floor(block["y"])))
            r=min(image.shape[1],int(math.ceil(block["x"]+block["width"])))
            b=min(image.shape[0],int(math.ceil(block["y"]+block["height"])))
            source_crop = image[t:b,l:r].copy()
            bounds, retention, component_count = SmallLineRecoveryRecognizer.content_bounds(source_crop)
            row.update(sourceBlockRect=[l,t,r-l,b-t],contentBounds=bounds,inkRetention=retention,
                       components=component_count)
            if bounds is None:
                row["status"] = "CONTENT_CROP_NOT_PROVEN"
                continue
            diagnostic["inspected"] += 1
            x,y,w,h=bounds
            alternatives=[]
            for pad in (0,1):
                cl=max(0,x-pad);ct=max(0,y-pad);cr=min(r-l,x+w+pad);cb=min(b-t,y+h+pad)
                crop=source_crop[ct:cb,cl:cr]
                source_rect=[l+cl,t+ct,cr-cl,cb-ct]
                for name, interpolation in (("linear",cv2.INTER_LINEAR),("cubic",cv2.INTER_CUBIC)):
                    scaled=cv2.resize(crop,None,fx=2,fy=2,interpolation=interpolation)
                    for rotation in (0,180):
                        candidate_image=scaled if rotation==0 else cv2.rotate(scaled,cv2.ROTATE_180)
                        digest=crop_sha(candidate_image)
                        diagnostic["recognitionCalls"] += 1
                        with contextlib.redirect_stdout(sys.stderr):
                            answer=recognizer(TextRecInput(img=[candidate_image],return_word_box=False))
                        text=str(answer.txts[0]).strip() if answer.txts else ""
                        confidence=float(answer.scores[0]) if answer.scores else 0
                        if not text or not math.isfinite(confidence) or confidence<0 or confidence>1:
                            continue
                        alternatives.append(dict(text=text,confidence=confidence,
                            origin=CONTRACT,transform="content-pad-"+str(pad)+"|resize-2x-"+name+"|rotation-"+str(rotation),
                            sourceBlockId=block["id"],sourceImageSha256=source_hash,cropSha256=digest,
                            sourceRect=source_rect,cropWidth=int(candidate_image.shape[1]),
                            cropHeight=int(candidate_image.shape[0])))
            block["ocrAlternatives"]=alternatives
            row["status"]="CANDIDATES_RETAINED_MAIN_TEXT_UNCHANGED"
            row["alternatives"]=alternatives
            diagnostic["retainedAlternatives"] += len(alternatives)
        diagnostic["reason"] = "BOUNDED_REAL_RECOGNIZER_EVIDENCE_ONLY"
    except Exception as error:
        diagnostic["reason"]="ORIGINAL_MAIN_TEXT_PRESERVED_AFTER_"+type(error).__name__
    finally:
        diagnostic["totalMs"]=(time.perf_counter()-started)*1000
    return diagnostic
