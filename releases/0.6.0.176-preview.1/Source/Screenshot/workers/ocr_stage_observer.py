"""Capture detector and recognizer evidence before normal score filtering.

No threshold, engine selection, OCR output, or image mutation is changed here.
"""
import copy
from rapidocr import RapidOCR
from rapidocr.utils.process_img import map_boxes_to_original

class ObservedRapidOCR(RapidOCR):
    def build_final_output(self, ori_img, det_res, cls_res, rec_res, cropped_img_list, op_record):
        rows=[]
        if det_res.boxes is not None:
            boxes=map_boxes_to_original(det_res.boxes.copy(),copy.deepcopy(op_record),*ori_img.shape[:2])
            texts=[] if rec_res.txts is None else rec_res.txts
            scores=[] if rec_res.scores is None else rec_res.scores
            detector_scores=[] if det_res.scores is None else det_res.scores
            cls=[] if cls_res.cls_res is None else cls_res.cls_res
            for index,box in enumerate(boxes):
                rows.append(dict(index=index,polygon=box.tolist(),
                    detectorScore=float(detector_scores[index]) if index<len(detector_scores) else None,
                    rawRecognition=str(texts[index]) if index<len(texts) else '',
                    recognitionScore=float(scores[index]) if index<len(scores) else None,
                    orientation=[str(cls[index][0]),float(cls[index][1])] if index<len(cls) else None))
        self.last_source_stages=dict(contract='DETECTOR_RECOGNIZER_PREFILTER_SOURCE_V1',
            width=int(ori_img.shape[1]),height=int(ori_img.shape[0]),textThreshold=float(self.text_score),
            candidates=rows)
        return super().build_final_output(ori_img,det_res,cls_res,rec_res,cropped_img_list,op_record)
