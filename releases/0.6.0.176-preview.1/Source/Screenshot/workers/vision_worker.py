"""Persistent JSON-lines worker for local V2 visual layout models."""
import contextlib, json, os, re, sys, time, traceback

sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8", errors="strict", line_buffering=True)
sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace", line_buffering=True)

for rel in (r"Lib\site-packages\nvidia\cu13\bin\x86_64", r"Lib\site-packages\nvidia\cudnn\bin"):
    path = os.path.join(sys.prefix, rel)
    if os.path.isdir(path):
        os.environ["PATH"] = path + os.pathsep + os.environ.get("PATH", "")
        if hasattr(os, "add_dll_directory"): os.add_dll_directory(path)

models = {}
MODEL_NAMES = {
    "PPDocLayoutS": "PP-DocLayout-S",
    "PPDocLayoutM": "PP-DocLayout-M",
    "PPDocLayoutPlusL": "PP-DocLayout_plus-L",
}

def emit(value): print(json.dumps(value, ensure_ascii=False, separators=(",", ":")), flush=True)

def memory():
    try:
        import psutil
        return psutil.Process().memory_info().rss
    except Exception: return 0

def package_version(name):
    try:
        import importlib.metadata
        return importlib.metadata.version(name)
    except Exception: return None

def ensure_model(kind):
    if kind not in MODEL_NAMES: raise ValueError(f"model not locally implemented: {kind}")
    if kind in models: return models[kind], 0
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        from paddlex import create_model
        models[kind] = create_model(model_name=MODEL_NAMES[kind], device="gpu:0")
    return models[kind], int((time.perf_counter() - started) * 1000)

def ensure_qwen():
    kind = "Qwen3Vl4BInstruct"
    if kind in models: return models[kind], 0
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        import torch
        from transformers import AutoProcessor, Qwen3VLForConditionalGeneration
        name = "Qwen/Qwen3-VL-4B-Instruct"
        processor = AutoProcessor.from_pretrained(name)
        model = Qwen3VLForConditionalGeneration.from_pretrained(
            name, dtype=torch.bfloat16, device_map="cuda", attn_implementation="sdpa").eval()
        models[kind] = (model, processor)
    return models[kind], int((time.perf_counter() - started) * 1000)

def ensure_paddleocr_vl():
    kind = "PaddleOcrVl16"
    if kind in models: return models[kind], 0
    started = time.perf_counter()
    os.environ["PADDLE_V2_DISABLE_MODELSCOPE"] = "1"
    with contextlib.redirect_stdout(sys.stderr):
        from paddleocr import PaddleOCRVL
        models[kind] = PaddleOCRVL(pipeline_version="v1.6", device="gpu:0",
            use_doc_orientation_classify=False, use_doc_unwarping=False)
    return models[kind], int((time.perf_counter() - started) * 1000)

def ensure_florence():
    kind = "Florence2BaseFt"
    if kind in models: return models[kind], 0
    started = time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr):
        import torch
        from transformers import AutoModelForCausalLM, AutoProcessor
        name = "microsoft/Florence-2-base-ft"
        processor = AutoProcessor.from_pretrained(name, trust_remote_code=True)
        model = AutoModelForCausalLM.from_pretrained(name, trust_remote_code=True,
            torch_dtype=torch.float16, attn_implementation="eager").cuda().eval()
        models[kind] = (model, processor)
    return models[kind], int((time.perf_counter() - started) * 1000)

def role(label):
    value = str(label).lower().replace("_", " ")
    if "title" in value: return "Title"
    if value in ("header", "header image"): return "Header"
    if "caption" in value: return "Caption"
    if value in ("text", "paragraph", "abstract", "content", "list"): return "BodyParagraph"
    if "footnote" in value or "reference" in value: return "Metadata"
    return "Unknown"

def analyze(kind, path):
    if kind == "Qwen3Vl4BInstruct": return analyze_qwen(path)
    if kind == "PaddleOcrVl16": return analyze_paddleocr_vl(path)
    if kind == "Florence2BaseFt": return analyze_florence(path)
    total = time.perf_counter(); model, load_ms = ensure_model(kind)
    inference_started=time.perf_counter()
    with contextlib.redirect_stdout(sys.stderr): result = list(model.predict(path, batch_size=1))[0].json["res"]
    inference_ms=int((time.perf_counter()-inference_started)*1000); post_started=time.perf_counter()
    regions = []
    for i, box in enumerate(result.get("boxes", [])):
        x1, y1, x2, y2 = [float(v) for v in box["coordinate"]]
        label = str(box.get("label", "unknown")); score = float(box.get("score", 0))
        regions.append({"id":f"VIS{i+1:03d}","polygon":[[x1,y1],[x2,y1],[x2,y2],[x1,y2]],
            "role":role(label),"confidence":score,"readingOrder":i+1,"parentId":None,
            "sourceModel":MODEL_NAMES[kind],"optionalTextHint":None,"rawLabel":label})
    postprocess_ms=int((time.perf_counter()-post_started)*1000)
    return {"status":"OK","error":"","model":MODEL_NAMES[kind],"regions":regions,
        "layoutRelations":[],"totalMs":int((time.perf_counter()-total)*1000),"loadMs":load_ms,
        "preprocessMs":0,"inferenceMs":inference_ms,"postprocessMs":postprocess_ms,
        "workingSetBytes":memory()}

def _json_objects(text):
    # A long page can reach the generation ceiling. Preserve every complete
    # object rather than discarding all useful proposals because the final ] is
    # absent. Fusion still validates and clamps every proposal.
    objects = []
    for match in re.finditer(r"\{[^{}]+\}", text, re.S):
        try: objects.append(json.loads(match.group(0)))
        except Exception: pass
    return objects

def analyze_qwen(path):
    from PIL import Image
    from qwen_vl_utils import process_vision_info
    total = time.perf_counter(); (model, processor), load_ms = ensure_qwen()
    image = Image.open(path).convert("RGB"); width, height = image.size
    prompt = ("Detect visible text regions and classify their function. Return only a compact JSON array. "
        "Each item must be {\"bbox_2d\":[x1,y1,x2,y2],\"role\":one of "
        "[Title,CharacterName,Header,BodyParagraph,Dialogue,Narration,Metadata,Button,UILabel,Choice,Caption,Unknown],"
        "\"readingOrder\":integer}. Use image pixel coordinates, tight boxes, and no prose.")
    messages = [{"role":"user","content":[{"type":"image","image":path},{"type":"text","text":prompt}]}]
    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
    images, videos = process_vision_info(messages)
    inputs = processor(text=[text], images=images, videos=videos, padding=True, return_tensors="pt").to("cuda")
    with contextlib.redirect_stdout(sys.stderr):
        generated = model.generate(**inputs, max_new_tokens=1800, do_sample=False)
    trimmed = [out[len(src):] for src, out in zip(inputs.input_ids, generated)]
    answer = processor.batch_decode(trimmed, skip_special_tokens=True)[0]
    regions = []
    allowed = {"Title","CharacterName","Header","BodyParagraph","Dialogue","Narration","Metadata",
               "Button","UILabel","Choice","Caption","Unknown"}
    for item in _json_objects(answer):
        box = item.get("bbox_2d") or item.get("bbox")
        if not isinstance(box, list) or len(box) != 4: continue
        try: x1,y1,x2,y2 = [float(v) for v in box]
        except Exception: continue
        x1,x2 = sorted((max(0,min(width,x1)), max(0,min(width,x2))))
        y1,y2 = sorted((max(0,min(height,y1)), max(0,min(height,y2))))
        if x2-x1 < 2 or y2-y1 < 2: continue
        item_role = str(item.get("role", "Unknown"))
        if item_role not in allowed: item_role = "Unknown"
        order = item.get("readingOrder")
        try: order = int(order)
        except Exception: order = len(regions)+1
        regions.append({"id":f"VIS{len(regions)+1:03d}","polygon":[[x1,y1],[x2,y1],[x2,y2],[x1,y2]],
            "role":item_role,"confidence":0.78,"readingOrder":order,"parentId":None,
            "sourceModel":"Qwen3-VL-4B-Instruct","optionalTextHint":None,"rawLabel":item_role})
    return {"status":"OK","error":"","model":"Qwen3-VL-4B-Instruct","regions":regions,
        "layoutRelations":[],"totalMs":int((time.perf_counter()-total)*1000),"loadMs":load_ms,
        "workingSetBytes":memory(),"rawOutputLength":len(answer)}

def analyze_paddleocr_vl(path):
    total = time.perf_counter(); pipeline, load_ms = ensure_paddleocr_vl()
    with contextlib.redirect_stdout(sys.stderr):
        results = list(pipeline.predict(path, use_doc_orientation_classify=False,
            use_doc_unwarping=False, use_chart_recognition=False,
            use_seal_recognition=False, max_new_tokens=1024))
    parsed = results[0].json.get("res", {}) if results else {}
    regions = []
    for block in parsed.get("parsing_res_list", []):
        label = str(block.get("block_label", "unknown"))
        content = str(block.get("block_content", "") or "").strip()
        # Image-only layout blocks remain useful to the layout engine but must
        # not become translatable recognition regions.
        if not content and label in ("image", "chart", "seal"): continue
        points = block.get("block_polygon_points") or []
        if len(points) < 3:
            box = block.get("block_bbox") or []
            if len(box) != 4: continue
            x1,y1,x2,y2 = [float(v) for v in box]
            points = [[x1,y1],[x2,y1],[x2,y2],[x1,y2]]
        mapped_role = role(label)
        if label in ("paragraph_title", "doc_title"): mapped_role = "Title"
        order = block.get("block_order")
        try: order = int(order)
        except Exception: order = len(regions)+1
        regions.append({"id":f"VIS{len(regions)+1:03d}","polygon":points,
            "role":mapped_role,"confidence":0.88 if content else 0.62,
            "readingOrder":order,"parentId":str(block.get("group_id")) if block.get("group_id") is not None else None,
            "sourceModel":"PaddleOCR-VL-1.6","optionalTextHint":content or None,"rawLabel":label})
    return {"status":"OK","error":"","model":"PaddleOCR-VL-1.6","regions":regions,
        "layoutRelations":[],"totalMs":int((time.perf_counter()-total)*1000),"loadMs":load_ms,
        "workingSetBytes":memory()}

def analyze_florence(path):
    import torch
    from PIL import Image
    total = time.perf_counter(); (model, processor), load_ms = ensure_florence()
    image = Image.open(path).convert("RGB"); task = "<OCR_WITH_REGION>"
    inputs = processor(text=task, images=image, return_tensors="pt")
    inputs = {k:(v.half().cuda() if k == "pixel_values" else v.cuda()) for k,v in inputs.items()}
    with contextlib.redirect_stdout(sys.stderr):
        generated = model.generate(**inputs, max_new_tokens=1536, num_beams=3)
    text = processor.batch_decode(generated, skip_special_tokens=False)[0]
    parsed = processor.post_process_generation(text, task=task, image_size=image.size).get(task, {})
    boxes = parsed.get("quad_boxes", []); labels = parsed.get("labels", [])
    regions = []
    for i, values in enumerate(boxes):
        if len(values) != 8: continue
        points = [[float(values[n]),float(values[n+1])] for n in range(0,8,2)]
        hint = str(labels[i]).replace("</s>", "").strip() if i < len(labels) else ""
        regions.append({"id":f"VIS{i+1:03d}","polygon":points,"role":"Unknown","confidence":0.72,
            "readingOrder":i+1,"parentId":None,"sourceModel":"Florence-2-base-ft",
            "optionalTextHint":hint or None,"rawLabel":"ocr_region"})
    return {"status":"OK","error":"","model":"Florence-2-base-ft","regions":regions,
        "layoutRelations":[],"totalMs":int((time.perf_counter()-total)*1000),"loadMs":load_ms,
        "workingSetBytes":memory()}

for line in sys.stdin:
    try:
        req=json.loads(line); command=req.get("command")
        if command == "shutdown": break
        if command == "check":
            emit({"status":"READY","error":"","python":sys.version.split()[0],
                  "paddlex":package_version("paddlex"),"transformers":package_version("transformers")})
        elif command == "analyze": emit(analyze(req["model"], req["imagePath"]))
        else: emit({"status":"ERROR","error":"unknown command","regions":[]})
    except Exception as exc:
        print(traceback.format_exc(), file=sys.stderr, flush=True)
        emit({"status":"ERROR","error":f"{type(exc).__name__}: {exc}","regions":[]})
