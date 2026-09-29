"""Local background reconstruction. Inputs are source pixels and predeclared masks.
No OCR, translation, layout, filenames, or reference images influence pixel synthesis.
LaMa ONNX export: Carve/LaMa-ONNX, Apache-2.0; see model identity/license.
"""
import sys,os,json,time,traceback
import numpy as np
import cv2
cv2.setNumThreads(1)
import onnxruntime as ort
from PIL import Image
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
from surface_background import supported_surfaces
from dialogue_background import enclosed_dialogue

MODEL_SHA256="1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6"
_session=None
_BACKEND=os.environ.get("ST_BACKGROUND_DEVICE","cpu")
if _BACKEND not in ("cpu","cuda"):raise ValueError("Unsupported background device")
_session_info={}

def intra_threads():
    cpus=os.cpu_count() or 1
    # Budget the shared ORT CPU pool; the existing tile callers remain separate.
    # CUDA host-side threading retains the Fix2 budget.
    return min(6 if _BACKEND=="cpu" else 8,max(min(4,cpus),cpus//2))

def tile_parallelism(tile_count):
    cpus=os.cpu_count() or 1
    workers=4 if cpus>=12 else 2 if cpus>=6 else 1
    if sys.platform=='win32':
        import ctypes
        class MemoryStatus(ctypes.Structure):
            _fields_=[('length',ctypes.c_ulong),('load',ctypes.c_ulong),
                      ('totalPhysical',ctypes.c_ulonglong),('availablePhysical',ctypes.c_ulonglong),
                      ('totalPageFile',ctypes.c_ulonglong),('availablePageFile',ctypes.c_ulonglong),
                      ('totalVirtual',ctypes.c_ulonglong),('availableVirtual',ctypes.c_ulonglong),
                      ('extended',ctypes.c_ulonglong)]
        status=MemoryStatus();status.length=ctypes.sizeof(status)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            if status.availablePhysical<1024**3:workers=1
            elif status.availablePhysical<2*1024**3:workers=min(workers,2)
    return min(tile_count,1 if _BACKEND=="cuda" else workers)
def session(model):
    global _session,_session_info
    if _session is None:
        import hashlib
        if hashlib.sha256(Path(model).read_bytes()).hexdigest()!=MODEL_SHA256:
            raise ValueError("Background model identity mismatch")
        options=ort.SessionOptions()
        # Every inference below is exactly one 512x512 tile. Resolving the
        # exported symbolic batch lets ORT fold shape-only work at session load.
        # Model weights, FP32 inputs, tile coverage and accumulation are unchanged.
        options.add_free_dimension_override_by_name("batch",1)
        options.intra_op_num_threads=intra_threads()
        options.inter_op_num_threads=1
        options.add_session_config_entry("session.intra_op.allow_spinning","0")
        options.add_session_config_entry("session.inter_op.allow_spinning","0")
        # A short-lived CPU session does not benefit from retaining oversized
        # arena/pattern buffers across tiles. Measurements cover all four workers.
        options.enable_mem_pattern=(_BACKEND=="cuda")
        options.enable_cpu_mem_arena=(_BACKEND=="cuda")
        started=time.perf_counter()
        providers=["CPUExecutionProvider"]
        if _BACKEND=="cuda":
            if "CUDAExecutionProvider" not in ort.get_available_providers():raise RuntimeError("CUDA provider is not installed")
            ort.preload_dlls(directory="")
            providers=[("CUDAExecutionProvider",{"device_id":0,"use_tf32":0,
                "cudnn_conv_algo_search":"HEURISTIC","gpu_mem_limit":6*1024**3,
                "arena_extend_strategy":"kSameAsRequested"}),"CPUExecutionProvider"]
        _session=ort.InferenceSession(model,options,providers=providers)
        _session.disable_fallback()
        if _BACKEND=="cuda" and "CUDAExecutionProvider" not in _session.get_providers():
            _session=None
            raise RuntimeError("CUDA initialization failed; select CPU explicitly")
        _session_info=dict(device=_BACKEND,providers=_session.get_providers(),fixedBatch=1,
            precision="FP32",tf32=False,initializationMs=(time.perf_counter()-started)*1000)
    return _session

def recover(source,authority,exclusion,model,dialogue_material=None,dialogue_surfaces=None):
    started=time.perf_counter()
    dialogue,dialogue_owned,dialogue_records=enclosed_dialogue(source,authority,dialogue_material,dialogue_surfaces)
    surface,surface_owned,surface_records=supported_surfaces(source,authority & ~dialogue_owned,exclusion)
    surface[dialogue_owned]=dialogue[dialogue_owned]
    surface_owned|=dialogue_owned
    pending=authority & ~surface_owned
    material_ms=(time.perf_counter()-started)*1000
    sess=session(model) if np.any(pending) else None
    load_ms=(time.perf_counter()-started)*1000-material_ms
    h,w=authority.shape
    accum=np.zeros((h,w,3),np.float32);weights=np.zeros((h,w),np.float32)
    tile_size=512;step=384;tiles=[]
    # Overlap provides context on both sides; only predeclared authority is committed.
    def positions(length):
        if length<=tile_size:return [0]
        return list(dict.fromkeys(list(range(0,length-tile_size+1,step))+[length-tile_size]))
    axis=np.minimum(np.arange(tile_size)+1,tile_size-np.arange(tile_size)).astype(np.float32)
    ramp=np.minimum(axis/64,1)
    def infer_tile(position):
            tile_started=time.perf_counter()
            x,y=position
            hh=min(tile_size,h-y);ww=min(tile_size,w-x)
            allowed=pending[y:y+hh,x:x+ww]
            rgb=source[y:y+hh,x:x+ww]
            unknown=np.logical_or(exclusion[y:y+hh,x:x+ww],authority[y:y+hh,x:x+ww])
            image=np.pad(rgb,((0,tile_size-hh),(0,tile_size-ww),(0,0)),mode="edge")
            mask=np.pad(unknown,((0,tile_size-hh),(0,tile_size-ww)),mode="edge")
            tensor=np.ascontiguousarray(image.transpose(2,0,1)[None],dtype=np.float32)/255.
            mt=np.ascontiguousarray(mask[None,None],dtype=np.float32)
            t=time.perf_counter()
            pred=sess.run(None,{"image":tensor,"mask":mt})[0][0].transpose(1,2,0)
            model_finished=time.perf_counter()
            if not np.isfinite(pred).all():raise ValueError("Non-finite background output")
            # Export includes final [0,255] scaling; preserve actual output contract.
            pred=np.clip(pred,0,255)[:hh,:ww]
            weight=(ramp[:hh,None]*ramp[None,:ww])*allowed
            record=dict(x=x,y=y,width=ww,height=hh,authorityPixels=int(allowed.sum()),unknownPixels=int(unknown.sum()),inferenceMs=(time.perf_counter()-t)*1000,outputMin=float(pred.min()),outputMax=float(pred.max()))
            record.update(prepareMs=(t-tile_started)*1000,modelRunMs=(model_finished-t)*1000,postMs=(time.perf_counter()-model_finished)*1000)
            return pred,weight,record
    positions_to_run=[(x,y) for y in positions(h) for x in positions(w)
                      if np.any(pending[y:y+tile_size,x:x+tile_size])]
    parallelism=tile_parallelism(len(positions_to_run))
    # Bound in-flight predictions and retain the original row-major accumulation
    # order. Only scheduling changes; context, masks, model and blending do not.
    def commit(predictions):
        for pred,weight,record in predictions:
            x,y,ww,hh=(record[k] for k in ('x','y','width','height'))
            accum[y:y+hh,x:x+ww]+=pred*weight[...,None]
            weights[y:y+hh,x:x+ww]+=weight
            tiles.append(record)
    if parallelism>1:
        with ThreadPoolExecutor(max_workers=parallelism) as pool:
            for start in range(0,len(positions_to_run),parallelism):
                commit(pool.map(infer_tile,positions_to_run[start:start+parallelism]))
    else:
        commit(map(infer_tile,positions_to_run))
    if np.any(pending & (weights<=0)):raise ValueError("Uncovered background authority")
    result=surface;result[pending]=np.rint(accum[pending]/weights[pending,None]).astype(np.uint8)
    return result,dict(backend=_session_info,mode="SOURCE_SURFACE_OR_LOCAL_LAMA_V3",parallelism=parallelism,intraOpThreads=intra_threads(),dialogueEvidence=dialogue_records,surfaceMs=material_ms,surfacePixels=int(surface_owned.sum()),surfaceEvidence=surface_records,loadMs=load_ms,totalMs=(time.perf_counter()-started)*1000,tiles=tiles,authorityPixels=int(authority.sum()),contextExcludedPixels=int(exclusion.sum()),outsideAuthorityChanges=int(np.any(result!=source,axis=2)[~authority].sum()))
def run(request):
    request_started=time.perf_counter()
    source=np.array(Image.open(request["source"]).convert("RGB"))
    authority=np.array(Image.open(request["authority"]).convert("L"))>127
    exclusion=np.array(Image.open(request["exclusion"]).convert("L"))>127
    if source.shape[:2]!=authority.shape or authority.shape!=exclusion.shape:raise ValueError("Mask dimensions mismatch")
    dialogue_material=np.array(Image.open(request['dialogueMask']).convert('L'))>127 if request.get('dialogueMask') else None
    decode_ms=(time.perf_counter()-request_started)*1000
    result,metrics=recover(source,authority,exclusion,request["model"],dialogue_material,request.get('dialogueSurfaces'))
    encode_started=time.perf_counter()
    Image.fromarray(result).save(request["output"])
    metrics.update(inputDecodeMs=decode_ms,outputEncodeMs=(time.perf_counter()-encode_started)*1000,requestMs=(time.perf_counter()-request_started)*1000)
    metrics.update(operationId=request.get('operationId'),imageSessionId=request.get('imageSessionId'),backgroundRequestId=request.get('backgroundRequestId'),pid=os.getpid())
    Path(request["trace"]).write_text(json.dumps(metrics,indent=2),encoding="utf-8")
    return dict(status="OK",**metrics)
if __name__=="__main__":
    # Each line is one bounded request. EOF exits normally; diagnostics never go to stdout.
    for line in sys.stdin:
        try:
            request=json.loads(line)
            if request.get("command")=="stop":break
            if request.get("command")=="warmup":
                started=time.perf_counter();session(request["model"])
                print(json.dumps(dict(status="OK",pid=os.getpid(),warmupMs=(time.perf_counter()-started)*1000,backend=_session_info)),flush=True)
                continue
            print(json.dumps(run(request)),flush=True)
        except Exception as exc:
            print(json.dumps(dict(status="ERROR",pid=os.getpid(),error=type(exc).__name__)),flush=True)
            print(traceback.format_exc(),file=sys.stderr,flush=True)

