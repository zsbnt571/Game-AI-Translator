"""Bounded recognition retry for nearly intact small dark text; detector/score gate unchanged."""
import difflib
import re
import time

import cv2
import numpy as np


class SmallLineRecoveryRecognizer:
    def __init__(self, recognizer):
        self.recognizer = recognizer
        self.enabled = False
        self.last_trace = []

    def __getattr__(self, name):
        return getattr(self.recognizer, name)

    def __call__(self, args):
        original = self.recognizer(args)
        if not self.enabled or args.return_word_box:
            return original
        self.last_trace = []
        images = [args.img] if isinstance(args.img, np.ndarray) else args.img
        if original.txts is None or original.scores is None or len(images) != len(original.txts):
            return original
        texts, scores = list(original.txts), list(original.scores)
        changed = False
        elapsed = 0.0
        for index, (image, text, score) in enumerate(zip(images, texts, scores)):
            # Accepted original recognitions are returned byte-for-byte unchanged.
            if float(score) >= .5:
                continue
            h, w = image.shape[:2]
            if h > 32 or h < 6 or w < 48 or w / h < 3:
                continue
            row = dict(index=index, originalText=text, originalScore=float(score), width=w, height=h,
                       recovered=False, recognitionCalls=0)
            self.last_trace.append(row)
            try:
                crop_bounds, retention, component_count = self.content_bounds(image)
                row.update(inkRetention=retention, retainedComponents=component_count, contentBounds=crop_bounds)
                if crop_bounds is None:
                    row['reason'] = 'CONTENT_RETENTION_OR_GEOMETRY_NOT_PROVEN'
                    continue
                x, y, cw, ch = crop_bounds
                families = []; row['orientationFamilies'] = []
                # A tiny line can be misrotated by the existing angle classifier.
                # Compare both orientations locally; never disable or globally retune it.
                for rotation in (0, 180):
                    variants = []
                    for pad in (0, 1, 2):
                        left, top = max(0, x-pad), max(0, y-pad)
                        right, bottom = min(w, x+cw+pad), min(h, y+ch+pad)
                        crop = image[top:bottom, left:right].copy()
                        if rotation:crop = cv2.rotate(crop, cv2.ROTATE_180)
                        from rapidocr.ch_ppocr_rec.typings import TextRecInput
                        start = time.perf_counter()
                        local = self.recognizer(TextRecInput(img=[crop], return_word_box=False))
                        elapsed += time.perf_counter() - start
                        row['recognitionCalls'] += 1
                        candidate = str(local.txts[0]).strip() if local.txts else ''
                        confidence = float(local.scores[0]) if local.scores else 0.0
                        variants.append(dict(pad=pad, text=candidate, score=confidence))
                    normalized = [self.normalize(item['text']) for item in variants]
                    agreement = [[difflib.SequenceMatcher(None, a, b).ratio() for b in normalized] for a in normalized]
                    minimum = min(min(values) for values in agreement)
                    valid = minimum >= .9 and all(item['score'] >= .85 and len(norm) >= 12 and
                        len(re.findall(r'[^\W\d_]{2,}', item['text'], flags=re.UNICODE)) >= 3
                        for item, norm in zip(variants, normalized))
                    row['orientationFamilies'].append(dict(rotation=rotation, variants=variants,
                        minimumAgreement=minimum, valid=valid))
                    if valid:families.append((rotation, variants, agreement))
                if len(families) != 1:
                    row['reason'] = 'NO_UNAMBIGUOUS_COMPLETE_TEXT_CONSENSUS'
                    continue
                rotation, variants, agreement = families[0]
                medoid = max(range(3), key=lambda i: (sum(agreement[i]), i == 1))
                from small_line_candidate_selection import select as select_candidate
                medoid, selection = select_candidate(variants, medoid)
                row['candidateSelection'] = selection
                texts[index] = variants[medoid]['text']
                scores[index] = min(item['score'] for item in variants)
                row.update(recovered=True, reason='INTACT_SMALL_LINE_ORIENTATION_CONSENSUS', localRotation=rotation,
                           acceptedText=texts[index], acceptedScore=scores[index])
                changed = True
            except Exception as error:
                row['reason'] = 'ORIGINAL_PRESERVED_AFTER_' + type(error).__name__
        if changed:
            original.txts, original.scores = tuple(texts), tuple(scores)
        if elapsed and original.elapse is not None:
            original.elapse += elapsed
        return original

    @staticmethod
    def normalize(text):
        return ''.join(c.lower() for c in text if c.isalnum())

    @staticmethod
    def content_bounds(image):
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        _, ink = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)
        total = int(np.count_nonzero(ink)); h, w = ink.shape
        if total < 12 or total / (h*w) > .4:
            return None, 0.0, 0
        count, labels, stats, _ = cv2.connectedComponentsWithStats(ink, connectivity=8)
        kept = []
        for component in range(1, count):
            x, y, cw, ch, area = map(int, stats[component])
            if x > 0 and y > 0 and x+cw < w and y+ch < h:
                kept.append(component)
        retained = np.isin(labels, kept).astype(np.uint8)
        retention = float(np.count_nonzero(retained) / total)
        points = cv2.findNonZero(retained)
        if retention < .98 or len(kept) < 3 or points is None:
            return None, retention, len(kept)
        x, y, cw, ch = map(int, cv2.boundingRect(points))
        if cw < w*.7 or ch < 4 or cw/ch < 3 or ch >= h-1:
            return None, retention, len(kept)
        return [x, y, cw, ch], retention, len(kept)
