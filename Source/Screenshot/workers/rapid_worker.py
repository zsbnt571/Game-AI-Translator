"""Persistent RapidOCR + ONNX Runtime CPU JSON-lines worker for RC3."""
import contextlib, difflib, hashlib, json, os, sys, time, traceback
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8", errors="strict", line_buffering=True)
sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace", line_buffering=True)

# Local P7 measurement hook is inactive unless an E-drive output is requested.
import ocr_timing_probe as probe
engine = None
load_ms = 0
last_image_path = None
last_image = None
boundary_analyzer = None

def emit(value): print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)
def memory():
    try:
        import psutil
        info = psutil.Process().memory_info()
        return info.rss
    except Exception: return 0

def ensure_engine():
    global engine, load_ms
    if engine is not None: return
    started = time.perf_counter()
    probe.mark("engine preparation start")
    from rapid_health import require_valid_models, no_network
    require_valid_models()
    probe.mark("models checked")
    with contextlib.redirect_stdout(sys.stderr), no_network():
        from ocr_stage_observer import ObservedRapidOCR
        from ocr_session_policy import bounded_ocr_sessions
        probe.install()
        probe.mark("heavy imports complete")
        with bounded_ocr_sessions():
            engine = ObservedRapidOCR()
        probe.mark("sessions ready")
        from small_line_recovery import SmallLineRecoveryRecognizer
        engine.text_rec = SmallLineRecoveryRecognizer(engine.text_rec)
    load_ms = int((time.perf_counter() - started) * 1000)

def _boundary_bbox(block):
    x = float(block.get("x", 0)); y = float(block.get("y", 0))
    width = float(block.get("width", 0)); height = float(block.get("height", 0))
    return {"X":x,"Y":y,"Width":width,"Height":height,"Right":x+width,"Bottom":y+height}

def _recognize_boundary_crop(image, rect):
    x1=max(0,int(rect["x"])); y1=max(0,int(rect["y"]))
    x2=min(image.shape[1],x1+max(1,int(rect["width"])))
    y2=min(image.shape[0],y1+max(1,int(rect["height"])))
    crop=image[y1:y2,x1:x2].copy()
    if crop.size == 0:
        return "", [], 0.0
    started=time.perf_counter(); ensure_engine()
    with contextlib.redirect_stdout(sys.stderr): result=engine(crop,use_det=False)
    elapsed=(time.perf_counter()-started)*1000.0
    texts=[] if getattr(result,"txts",None) is None else [str(v).strip() for v in result.txts if str(v).strip()]
    scores=[] if getattr(result,"scores",None) is None else [float(v) for v in result.scores]
    return " ".join(texts).strip(), scores, elapsed

def refine_ocr_boundaries(image, blocks):
    """Apply the proven suspicion-only boundary relation after full RapidOCR.

    The route and analyzer are generic and image/text-local.  Any analyzer or
    local-recognition failure preserves the original OCR line atomically.
    """
    global boundary_analyzer
    started=time.perf_counter()
    try:
        from boundary_core import InlineBoundaryAnalyzer
        from fastpath_router import route, should_reocr
        if boundary_analyzer is None:
            boundary_analyzer=InlineBoundaryAnalyzer()
        deep_count=0; high_count=0; reocr_lines=0; crop_calls=0; changed=0
        router_ms=0.0; analyzer_ms=0.0; reocr_ms=0.0; traces=[]
        refined_blocks=[]
        for block in blocks:
            current=dict(block)
            decision=route(block.get("text", "")); router_ms += float(decision.cpu_ms)
            trace={"lineId":block.get("id", ""),"originalText":block.get("text", ""),
                   "routerHit":bool(decision.deep_analysis),"routerReason":decision.reason,
                   "routerEvidence":list(decision.evidence),"proposalKind":"NONE",
                   "confidence":"LOW","decisionReason":"CHEAP_ROUTER_ORDINARY_DIRECT",
                   "reOcr":False,"changed":False}
            if decision.deep_analysis:
                deep_count += 1
                proposal,_=boundary_analyzer.analyze(
                    image, str(block.get("id", "")), str(block.get("text", "")),
                    _boundary_bbox(block), block.get("polygon", []))
                analyzer_ms += float(proposal.proposal_cpu_ms)
                trace.update({"proposalKind":proposal.proposal_kind,"confidence":proposal.confidence,
                              "decisionReason":proposal.decision_reason,"evidence":proposal.evidence,
                              "segmentCount":len(proposal.text_segments),
                              "separatorCount":len(proposal.structural_separators),
                              "nonTextCount":len(proposal.non_text_components)})
                if proposal.confidence == "HIGH":
                    high_count += 1
                    allowed,reason=should_reocr(str(block.get("text", "")),proposal.proposal_kind)
                    trace["reOcrRouteReason"]=reason
                    if allowed:
                        reocr_lines += 1; trace["reOcr"]=True
                        texts=[]; scores=[]
                        for item in proposal.proposed_text_crops:
                            crop_calls += 1
                            text,values,elapsed=_recognize_boundary_crop(image,item["Rect"])
                            reocr_ms += elapsed; texts.append(text); scores.extend(values)
                        if texts and all(value.strip() for value in texts):
                            replacement=" | ".join(texts)
                            if replacement != str(block.get("text", "")):
                                current["boundaryOriginalText"]=block.get("text", "")
                                current["text"]=replacement
                                current["boundaryConfidence"]=min(scores) if scores else None
                                changed += 1; trace["changed"]=True; trace["refinedText"]=replacement
            traces.append(trace); refined_blocks.append(current)
        return refined_blocks,{"contract":"OCR_BOUNDARY_FAST_PATH_V1","status":"OK",
            "lineCount":len(blocks),"deepAnalysisCount":deep_count,"highProposalCount":high_count,
            "reOcrLineCount":reocr_lines,"cropCallCount":crop_calls,"changedLineCount":changed,
            "routerMs":router_ms,"analyzerMs":analyzer_ms,"reOcrMs":reocr_ms,
            "totalMs":(time.perf_counter()-started)*1000.0,"trace":traces}
    except Exception as exc:
        print("Boundary fast path preserved A after error: "+traceback.format_exc(),file=sys.stderr,flush=True)
        return blocks,{"contract":"OCR_BOUNDARY_FAST_PATH_V1","status":"FALLBACK_TO_A",
            "errorMessage":f"{type(exc).__name__}: {exc}","lineCount":len(blocks),
            "totalMs":(time.perf_counter()-started)*1000.0,"trace":[]}

def recognize(path):
    global last_image_path, last_image
    total = time.perf_counter(); probe.mark("recognize request"); cold = engine is None; ensure_engine()
    import cv2
    image = cv2.imread(path, cv2.IMREAD_COLOR)
    if image is None: raise FileNotFoundError(path)
    last_image_path, last_image = path, image
    probe.mark("image decoded")
    # Recognition-only title recovery changes the engine's per-call detection
    # switch. State the normal product mode explicitly on every full request so
    # a previous local recovery can never leak into the next screenshot.
    # The local retry is enabled only for a full detector-backed screenshot.
    # Recognition-only title/boundary calls retain their existing behavior.
    engine.text_rec.enabled = True
    try:
        with contextlib.redirect_stdout(sys.stderr): result = engine(image, use_det=True)
    finally:
        engine.text_rec.enabled = False
    probe.mark("main OCR complete")
    source_stages=engine.last_source_stages
    small_line_trace=engine.text_rec.last_trace
    txts = getattr(result, "txts", None) or []
    scores = getattr(result, "scores", None)
    boxes = getattr(result, "boxes", None)
    scores = [] if scores is None else scores
    boxes = [] if boxes is None else boxes
    blocks = []
    for i, text in enumerate(txts):
        poly = boxes[i] if i < len(boxes) else []
        points = [[float(p[0]), float(p[1])] for p in poly] if len(poly) else []
        xs = [p[0] for p in points] or [0.0]; ys = [p[1] for p in points] or [0.0]
        blocks.append({"id":f"R{i+1:03d}","text":str(text),
            "confidence":float(scores[i]) if i < len(scores) else None,
            "x":min(xs),"y":min(ys),"width":max(xs)-min(xs),"height":max(ys)-min(ys),
            "polygon":points,"lineIndex":i,"readingOrder":i+1})
    probe.mark("result converted")
    from short_field_orientation import recover as recover_field_orientation
    try:
        blocks,field_direction_trace=recover_field_orientation(image,blocks,source_stages,engine.text_rec.recognizer)
    except Exception as exc:
        field_direction_trace={'contract':'SOURCE_FIELD_UPRIGHT_CONSENSUS_V1',
                               'status':'PRESERVED_FULL_FRAME_AFTER_ERROR',
                               'errorMessage':f'{type(exc).__name__}: {exc}'}
    # Keep the original prefilter recognition and classifier entries unchanged.
    source_stages['shortFieldOrientationRecovery']=field_direction_trace
    # Missing tiny lines use an independent image-evidence proposal. The C3
    # low-score recognizer above remains intact for detector-backed lines.
    probe.mark("field orientation complete")
    from tiny_line_detection_recovery import recover as recover_tiny_lines
    tiny_blocks,tiny_trace=recover_tiny_lines(image,blocks,engine.text_rec.recognizer)
    for local in tiny_blocks:
        local.update(id=f"R{len(blocks)+1:03d}",lineIndex=len(blocks),readingOrder=len(blocks)+1)
        blocks.append(local)
    # Source-evidenced local detection is separate from the low-confidence
    # recognizer and can establish targets which had no full-frame OCR box.
    probe.mark("tiny recovery complete")
    from bounded_text_detection import recover as recover_coverage
    try:
        blocks,coverage_trace=recover_coverage(image,blocks,engine)
        coverage_trace['status']='OK'
    except Exception as exc:
        # Recovery computes into a separate list. The original full-frame
        # records survive atomically if this optional bounded pass fails.
        print('Coverage recovery preserved full-frame OCR: '+traceback.format_exc(),file=sys.stderr,flush=True)
        coverage_trace={'contract':'BOUNDED_SOURCE_TEXT_DETECTION_V1','status':'PRESERVED_FULL_FRAME_AFTER_ERROR',
                        'errorMessage':f'{type(exc).__name__}: {exc}'}
    probe.mark("coverage complete")
    blocks,boundary=refine_ocr_boundaries(image,blocks)
    probe.mark("boundary complete")
    from ocr_alternatives import collect as collect_ocr_alternatives
    alternatives_trace=collect_ocr_alternatives(image,blocks,engine.text_rec.recognizer,path)
    probe.mark("alternatives complete")
    import importlib.metadata as metadata
    return {"engineStatus":"OK","errorMessage":"","model":"RapidOCR PP-OCRv6 + ONNX Runtime CPU",
        "modelVersion":f"rapidocr={metadata.version('rapidocr')};onnxruntime={metadata.version('onnxruntime')};coverage=source-field-upright-v6",
        "totalMs":int((time.perf_counter()-total)*1000),"coldStartMs":load_ms if cold else 0,
        "workingSetBytes":memory(),"rawResult":"\n".join(x["text"] for x in blocks),"blocks":blocks,
        "boundary":boundary,"tinyLineDetectionRecovery":tiny_trace,
        "sourceStages":source_stages,
        "coverageRecovery":coverage_trace,
        "ocrAlternativesContract":"ocr-alternatives-v1",
        "ocrAlternativesDiagnostics":alternatives_trace,
        "smallLineRecovery":{"contract":"LOW_SCORE_SMALL_LINE_ORIENTATION_CONSENSUS_V1",
            "globalTextScore":float(engine.text_score),"detectorSettingsChanged":False,
            "trace":small_line_trace}}

def _intersection_area(a, b):
    ax, ay, aw, ah = a; bx, by, bw, bh = b
    return max(0, min(ax + aw, bx + bw) - max(ax, bx)) * max(0, min(ay + ah, by + bh) - max(ay, by))

def _normalized(value):
    return "".join(c.lower() for c in str(value) if c.isalnum())

def recover_stylized_title(path, source_blocks):
    """Precision-first residual-stroke router plus selective recognizer call.

    The router is intentionally independent of fixture names, expected text,
    card coordinates, semantic roles, and global detector thresholds.
    """
    total = time.perf_counter()
    import cv2
    import numpy as np

    image = last_image if path == last_image_path and last_image is not None else cv2.imread(path, cv2.IMREAD_COLOR)
    if image is None:
        raise FileNotFoundError(path)
    blocks = [{
        "text": str(b.get("text", "")),
        "x": int(round(float(b.get("x", 0)))),
        "y": int(round(float(b.get("y", 0)))),
        "w": int(round(float(b.get("width", 0)))),
        "h": int(round(float(b.get("height", 0)))),
    } for b in source_blocks]
    heights = sorted(b["h"] for b in blocks if b["h"] >= 8)
    if not heights:
        return {"engineStatus":"OK","errorMessage":"","totalMs":(time.perf_counter()-total)*1000,
                "checked":True,"triggered":False,"reason":"NO_VALID_OCR_HEIGHTS","rawCandidateCount":0,
                "acceptedCandidateCount":0,"localRecognitionCount":0,"blocks":[],"trace":[]}

    median = heights[(len(heights) - 1) // 2]
    anchors = [b for b in blocks if b["h"] >= max(40, median * 1.8) and b["w"] >= b["h"] * 2]
    if not anchors:
        return {"engineStatus":"OK","errorMessage":"","totalMs":(time.perf_counter()-total)*1000,
                "checked":True,"triggered":False,"reason":"NO_LARGE_OCR_ANCHOR","medianOcrHeight":median,
                "anchorCount":0,"rawCandidateCount":0,"acceptedCandidateCount":0,
                "localRecognitionCount":0,"blocks":[],"trace":[]}
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    trace = []
    accepted = []
    raw_count = 0
    for anchor in anchors:
        ah, aw = anchor["h"], anchor["w"]
        sx1 = max(0, int(anchor["x"] - 0.9 * aw)); sx2 = min(image.shape[1], int(anchor["x"] + 1.9 * aw))
        sy1 = max(0, int(anchor["y"] - 2.0 * ah)); sy2 = min(image.shape[0], int(anchor["y"] + 0.2 * ah))
        # Gaussian + Canny only need the anchor-local search envelope. Four
        # pixels of real-image context preserve the full-frame kernel result at
        # the proposal boundary without charging every screenshot for full-frame
        # edge analysis.
        pad=4; px1=max(0,sx1-pad); py1=max(0,sy1-pad); px2=min(image.shape[1],sx2+pad); py2=min(image.shape[0],sy2+pad)
        local_edges=cv2.Canny(cv2.GaussianBlur(gray[py1:py2,px1:px2],(3,3),0),45,135)
        ox=sx1-px1; oy=sy1-py1
        search_edges=local_edges[oy:oy+(sy2-sy1),ox:ox+(sx2-sx1)]
        roi = search_edges.copy()
        for block in blocks:
            x1=max(0, block["x"]-sx1-int(0.06*ah)); y1=max(0, block["y"]-sy1-int(0.06*ah))
            x2=min(roi.shape[1], block["x"]+block["w"]-sx1+int(0.06*ah)); y2=min(roi.shape[0], block["y"]+block["h"]-sy1+int(0.06*ah))
            if x2 > x1 and y2 > y1:
                roi[y1:y2, x1:x2] = 0
        kx=max(9, int(ah*0.32)); ky=max(3, int(ah*0.07))
        grouped=cv2.morphologyEx(roi, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (kx, ky)))
        grouped=cv2.dilate(grouped, cv2.getStructuringElement(cv2.MORPH_RECT, (max(3, int(ah*0.16)), max(1, int(ah*0.04)))))
        contours,_=cv2.findContours(grouped, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        for contour in contours:
            x,y,w,h=cv2.boundingRect(contour); box=(x+sx1,y+sy1,w,h); raw_count += 1
            aspect=w/max(1,h)
            anchor_overlap=max(0,min(box[0]+w,anchor["x"]+aw)-max(box[0],anchor["x"]))/max(1,min(w,aw))
            bottom_gap=anchor["y"]-(box[1]+h)
            covered=max((_intersection_area(box,(b["x"],b["y"],b["w"],b["h"]))/(w*h) for b in blocks),default=0)
            bx=box[0]-sx1; by=box[1]-sy1
            edge_density=float(np.mean(search_edges[by:by+h,bx:bx+w] > 0)) if w*h else 0
            is_accepted=(h>=0.45*ah and h<=1.9*ah and w>=0.55*aw and aspect>=3.0 and
                         anchor_overlap>=0.35 and -0.25*ah<=bottom_gap<=0.75*ah and covered<0.15 and
                         0.025<=edge_density<=0.35)
            evidence={"anchorText":anchor["text"],"anchorX":anchor["x"],"anchorY":anchor["y"],
                      "anchorWidth":aw,"anchorHeight":ah,"x":box[0],"y":box[1],"width":w,"height":h,
                      "aspect":aspect,"anchorOverlap":anchor_overlap,"bottomGapRatio":bottom_gap/max(1,ah),
                      "coveredRatio":covered,"edgeDensity":edge_density,"accepted":is_accepted}
            trace.append(evidence)
            if is_accepted:
                accepted.append(evidence)

    # The proven product gate is deliberately single-proposal. Deterministic
    # ordering makes the decision stable if a future screenshot has two raw hits.
    accepted.sort(key=lambda c: (-c["anchorOverlap"], -c["width"], c["y"], c["x"]))
    accepted = accepted[:1]
    recovered=[]; local_calls=0; reason="NO_HIGH_PRECISION_CANDIDATE"
    for candidate in accepted:
        x,y,w,h=(candidate[k] for k in ("x","y","width","height"))
        crop=image[y:y+h,x:x+w].copy()
        ok, encoded=cv2.imencode(".png",crop)
        if not ok:
            continue
        crop_sha=hashlib.sha256(encoded.tobytes()).hexdigest().upper()
        local_started=time.perf_counter(); ensure_engine(); local_calls += 1
        with contextlib.redirect_stdout(sys.stderr): result=engine(crop,use_det=False)
        local_ms=(time.perf_counter()-local_started)*1000
        texts=[] if getattr(result,"txts",None) is None else [str(v).strip() for v in result.txts if str(v).strip()]
        scores=[] if getattr(result,"scores",None) is None else [float(v) for v in result.scores]
        text=" ".join(texts).strip(); confidence=max(scores) if scores else None
        similarities=[difflib.SequenceMatcher(None,_normalized(text),_normalized(b["text"])).ratio()
                      for b in blocks if _normalized(b["text"])] if _normalized(text) else []
        duplicate=max(similarities,default=0.0)
        proposal_id="REC-"+hashlib.sha256(f"{x},{y},{w},{h}|{crop_sha}".encode()).hexdigest()[:16].upper()
        candidate.update({"proposalId":proposal_id,"cropSha256":crop_sha,"recognizedText":text,
                          "confidence":confidence,"duplicateSimilarity":duplicate,"localRecognitionMs":local_ms})
        if 2 <= len(_normalized(text)) <= 32 and (confidence is None or confidence >= 0.80) and duplicate < 0.90:
            recovered.append({"id":"","text":text,"confidence":confidence,"x":x,"y":y,"width":w,"height":h,
                              "polygon":[[x,y],[x+w,y],[x+w,y+h],[x,y+h]],"lineIndex":-1,"readingOrder":0,
                              "proposalId":proposal_id,"cropSha256":crop_sha})
            reason="RECOVERED_HIGH_PRECISION_LOCAL_RECOGNITION"
        elif duplicate >= 0.90:
            reason="REJECT_DUPLICATE_EXISTING_TEXT"
        else:
            reason="REJECT_LOCAL_RECOGNITION_QUALITY"

    return {"engineStatus":"OK","errorMessage":"","totalMs":(time.perf_counter()-total)*1000,
            "checked":True,"triggered":bool(recovered),"reason":reason,"medianOcrHeight":median,
            "anchorCount":len(anchors),"rawCandidateCount":raw_count,"acceptedCandidateCount":len(accepted),
            "localRecognitionCount":local_calls,"blocks":recovered,"trace":trace}

for line in sys.stdin:
    try:
        req=json.loads(line); command=req.get("command")
        if command == "shutdown": break
        if command == "check":
            from rapid_health import check
            with contextlib.redirect_stdout(sys.stderr): checked = check(recognize)
            emit(checked)
        elif command == "repair":
            from rapid_health import repair
            with contextlib.redirect_stdout(sys.stderr): repaired = repair(req.get("allowNetwork") is True)
            emit(repaired)
        elif command == "p7_flush" and os.environ.get('ST_OCR_TIMING_PATH'):
            probe.finish(); emit({"flushed": True})
        elif command == "recognize": emit(recognize(req["imagePath"]))
        elif command == "recover_stylized_title": emit(recover_stylized_title(req["imagePath"], req.get("blocks", [])))
        else: emit({"engineStatus":"ERROR","errorMessage":"unknown command","blocks":[]})
    except Exception as exc:
        print(traceback.format_exc(), file=sys.stderr, flush=True)
        emit({"engineStatus":"ERROR","errorMessage":f"{type(exc).__name__}: {exc}","blocks":[]})
