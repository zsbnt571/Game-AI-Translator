"""Persistent PaddleOCR PP-OCRv6 medium CPU worker; oneDNN is intentionally disabled."""
import contextlib, json, os, sys, time, traceback
from rapid_health import no_network
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8", errors="strict", line_buffering=True)
sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace", line_buffering=True)
os.environ["FLAGS_use_mkldnn"] = "0"
ocr = None
load_ms = 0

def emit(value): print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)
def memory():
    try:
        import psutil
        return psutil.Process().memory_info().rss
    except Exception: return 0
def ensure_engine():
    global ocr, load_ms
    if ocr is not None: return
    started=time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr), no_network():
        from paddleocr import PaddleOCR
        ocr=PaddleOCR(text_detection_model_name="PP-OCRv6_medium_det",
            text_recognition_model_name="PP-OCRv6_medium_rec", use_doc_orientation_classify=False,
            use_doc_unwarping=False, use_textline_orientation=False, device="cpu", enable_mkldnn=False)
    load_ms=int((time.perf_counter()-started)*1000)
def as_dict(item):
    candidate=getattr(item,"json",item)
    if callable(candidate): candidate=candidate()
    if isinstance(candidate,str): candidate=json.loads(candidate)
    if isinstance(candidate,dict) and "res" in candidate: candidate=candidate["res"]
    return candidate if isinstance(candidate,dict) else {}
def recognize(path):
    total=time.perf_counter(); cold=ocr is None; ensure_engine()
    with contextlib.redirect_stdout(sys.stderr): output=list(ocr.predict(path))
    blocks=[]; order=0
    for item in output:
        data=as_dict(item); texts=data.get("rec_texts",[]); scores=data.get("rec_scores",[])
        polygons=data.get("dt_polys",data.get("rec_polys",[])); boxes=data.get("rec_boxes",[])
        for i,text in enumerate(texts):
            poly=polygons[i] if i<len(polygons) else None
            if poly is not None:
                points=[[float(p[0]),float(p[1])] for p in poly]
            elif i<len(boxes):
                x1,y1,x2,y2=map(float,boxes[i]); points=[[x1,y1],[x2,y1],[x2,y2],[x1,y2]]
            else: points=[]
            xs=[p[0] for p in points] or [0.0]; ys=[p[1] for p in points] or [0.0]; order+=1
            blocks.append({"id":f"P{order:03d}","text":str(text),
                "confidence":float(scores[i]) if i<len(scores) else None,
                "x":min(xs),"y":min(ys),"width":max(xs)-min(xs),"height":max(ys)-min(ys),
                "polygon":points,"lineIndex":order-1,"readingOrder":order})
    import importlib.metadata as metadata
    return {"engineStatus":"OK","errorMessage":"","model":"PP-OCRv6_medium det+rec, Paddle CPU, oneDNN disabled",
        "modelVersion":f"paddleocr={metadata.version('paddleocr')};paddlepaddle={metadata.version('paddlepaddle')}",
        "totalMs":int((time.perf_counter()-total)*1000),"coldStartMs":load_ms if cold else 0,
        "workingSetBytes":memory(),"rawResult":"\n".join(x["text"] for x in blocks),"blocks":blocks}
for line in sys.stdin:
    try:
        req=json.loads(line); command=req.get("command")
        if command=="shutdown": break
        if command=="check":
            with no_network():
                import paddle, paddleocr, paddlex
            emit({"engineStatus":"ENVIRONMENT_READY","errorMessage":"","model":
                f"paddle={paddle.__version__}; paddleocr={getattr(paddleocr,'__version__','unknown')}; paddlex={getattr(paddlex,'__version__','unknown')}; oneDNN=disabled","blocks":[]})
        elif command=="recognize": emit(recognize(req["imagePath"]))
        else: emit({"engineStatus":"ERROR","errorMessage":"unknown command","blocks":[]})
    except Exception as exc:
        print(traceback.format_exc(),file=sys.stderr,flush=True)
        emit({"engineStatus":"ERROR","errorMessage":f"{type(exc).__name__}: {exc}","blocks":[]})
