"""Opt-in local experiment timing. No thread scans; write once at worker exit."""
import os,time,json,atexit,functools
_path=os.environ.get('ST_OCR_TIMING_PATH','')
_events=[];_sessions=[];_installed=False
def mark(name):
    if _path:_events.append(dict(name=name,at=time.perf_counter()))
def wrap(cls,name,label):
    original=getattr(cls,name)
    @functools.wraps(original)
    def invoke(*args,**kwargs):
        start=time.perf_counter()
        try:return original(*args,**kwargs)
        finally:_events.append(dict(name=label,start=start,end=time.perf_counter()))
    setattr(cls,name,invoke)
def install():
    global _installed
    if not _path or _installed:return
    _installed=True
    from rapidocr import RapidOCR
    from rapidocr.inference_engine.onnxruntime.main import OrtInferSession
    for name in ['preprocess_img','detect_and_crop','cls_and_rotate','recognize_txt','build_final_output']:
        wrap(RapidOCR,name,name)
    original=OrtInferSession.__init__
    def initialize(self,cfg):
        start=time.perf_counter();original(self,cfg);end=time.perf_counter()
        options=self.session.get_session_options()
        record=dict(index=len(_sessions),start=start,end=end,intra=options.intra_op_num_threads,
            inter=options.inter_op_num_threads,providers=self.session.get_providers(),
            intraSpinning=options.get_session_config_entry('session.intra_op.allow_spinning'),
            interSpinning=options.get_session_config_entry('session.inter_op.allow_spinning'))
        self._p7_index=record['index'];_sessions.append(record)
    OrtInferSession.__init__=initialize
    call=OrtInferSession.__call__
    def infer(self,*args,**kwargs):
        start=time.perf_counter()
        try:return call(self,*args,**kwargs)
        finally:_events.append(dict(name='ort inference',session=self._p7_index,start=start,end=time.perf_counter()))
    OrtInferSession.__call__=infer
def finish():
    if not _path:return
    import cv2
    from pathlib import Path
    path=Path(_path);path.parent.mkdir(parents=True,exist_ok=True)
    path.with_name(path.stem+'-'+str(os.getpid())+path.suffix).write_text(json.dumps(dict(pid=os.getpid(),logical=os.cpu_count(),cvThreads=cv2.getNumThreads(),sessions=_sessions,events=_events),indent=2),encoding='utf-8')
atexit.register(finish)
