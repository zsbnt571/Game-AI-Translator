"""Bound native OCR session scheduling without changing models or OCR work."""
import contextlib
import os

def thread_limit():
    return min(3 if os.environ.get("ST_OCR_LOAD")=="low" else 6, max(1, (os.cpu_count() or 1) // 2))

@contextlib.contextmanager
def bounded_ocr_sessions():
    # RapidOCR exposes a session-options factory but no spinning parameter.
    # Scope the override to this worker's single-threaded engine construction;
    # restore the library factory immediately after all three sessions exist.
    from rapidocr.inference_engine.onnxruntime.main import OrtInferSession
    original = OrtInferSession._init_sess_opts
    def options(cfg):
        value = original(cfg)
        value.intra_op_num_threads = thread_limit()
        value.inter_op_num_threads = 1
        value.add_session_config_entry("session.intra_op.allow_spinning", "0")
        value.add_session_config_entry("session.inter_op.allow_spinning", "0")
        return value
    OrtInferSession._init_sess_opts = staticmethod(options)
    try:
        yield
    finally:
        OrtInferSession._init_sess_opts = staticmethod(original)
