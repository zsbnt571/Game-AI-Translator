from __future__ import annotations

import hashlib
import time
from dataclasses import asdict, dataclass, field
from typing import Any

import cv2
import numpy as np


@dataclass(frozen=True)
class Rect:
    x: int
    y: int
    width: int
    height: int

    @property
    def right(self) -> int:
        return self.x + self.width

    @property
    def bottom(self) -> int:
        return self.y + self.height


@dataclass
class Component:
    component_id: str
    rect: Rect
    area: int
    density: float
    center_y: float
    baseline: int
    median_lab: list[float]
    median_hsv: list[float]
    classification: str = "TEXT_CANDIDATE"
    evidence: list[str] = field(default_factory=list)


@dataclass
class InlineBoundaryProposal:
    ocr_line_id: str
    original_polygon: list[list[float]]
    original_text: str
    text_segments: list[dict[str, Any]]
    structural_separators: list[dict[str, Any]]
    non_text_components: list[dict[str, Any]]
    unknown_components: list[dict[str, Any]]
    proposed_text_crops: list[dict[str, Any]]
    evidence: list[str]
    confidence: str
    decision_reason: str
    proposal_kind: str
    deep_path: bool
    proposal_cpu_ms: float

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


class InlineBoundaryAnalyzer:
    """Image-only OCR-line boundary observer.

    The analyzer intentionally receives no fixture id, source-truth string,
    translation, or expected refined OCR result.
    """

    def __init__(self) -> None:
        cv2.setRNGSeed(20260830)

    def analyze(
        self,
        image_bgr: np.ndarray,
        line_id: str,
        original_text: str,
        bbox: dict[str, float],
        polygon: list[dict[str, float]] | list[list[float]],
    ) -> tuple[InlineBoundaryProposal, list[dict[str, Any]]]:
        started = time.perf_counter()
        height, width = image_bgr.shape[:2]
        x1 = max(0, int(np.floor(float(bbox["X"]))))
        y1 = max(0, int(np.floor(float(bbox["Y"]))))
        x2 = min(width, int(np.ceil(float(bbox.get("Right", bbox["X"] + bbox["Width"])))) + 1)
        y2 = min(height, int(np.ceil(float(bbox.get("Bottom", bbox["Y"] + bbox["Height"])))) + 1)
        crop = image_bgr[y1:y2, x1:x2]
        if crop.size == 0 or crop.shape[0] < 5 or crop.shape[1] < 5:
            return self._abstain(line_id, original_text, polygon, "EMPTY_OR_TINY_LINE", started), []

        mask, background_lab, mask_evidence = self._foreground_mask(crop)
        components = self._components(crop, mask)
        if len(components) < 2:
            proposal = self._abstain(line_id, original_text, polygon, "INSUFFICIENT_COMPONENTS", started, mask_evidence)
            return proposal, self._component_trace(line_id, components, x1, y1, background_lab)

        analysis_components: list[Component] = []
        for component in components:
            if self._is_context_artifact(component, crop.shape[0], crop.shape[1]):
                component.classification = "UNKNOWN"
                component.evidence.append("spans_multiple_crop_edges_or_full_line_context")
            else:
                analysis_components.append(component)

        separators, median_height = self._separator_candidates(analysis_components, crop.shape[0])
        high_separators = [item for item in separators if item["Confidence"] == "HIGH"]
        if len(high_separators) >= 2:
            proposal = self._separator_proposal(
                line_id,
                original_text,
                polygon,
                crop,
                mask,
                analysis_components,
                high_separators,
                x1,
                y1,
                median_height,
                mask_evidence,
                started,
            )
            return proposal, self._component_trace(line_id, components, x1, y1, background_lab)

        terminal = self._terminal_candidate(analysis_components, crop.shape[0], crop.shape[1])
        residual = self._residual_color_terminal(crop, analysis_components)
        if residual is not None:
            synthetic = residual.pop("Component")
            components.append(synthetic)
            analysis_components.append(synthetic)
            terminal = residual
        if terminal is not None and terminal["Confidence"] == "HIGH":
            proposal = self._terminal_proposal(
                line_id,
                original_text,
                polygon,
                analysis_components,
                terminal,
                x1,
                y1,
                crop.shape[0],
                crop.shape[1],
                mask_evidence,
                started,
            )
            return proposal, self._component_trace(line_id, components, x1, y1, background_lab)

        evidence = list(mask_evidence)
        if separators:
            evidence.append(f"separator_candidates={len(separators)} high={len(high_separators)}")
        if terminal:
            evidence.extend(terminal["Evidence"])
        reason = "NO_HIGH_CONFIDENCE_IMAGE_BOUNDARY"
        proposal = self._abstain(line_id, original_text, polygon, reason, started, evidence)
        return proposal, self._component_trace(line_id, components, x1, y1, background_lab)

    @staticmethod
    def _is_context_artifact(component: Component, crop_height: int, crop_width: int) -> bool:
        touches_left = component.rect.x <= 1
        touches_right = component.rect.right >= crop_width - 1
        touches_top = component.rect.y <= 1
        touches_bottom = component.rect.bottom >= crop_height - 1
        edge_count = sum((touches_left, touches_right, touches_top, touches_bottom))
        very_wide = component.rect.width >= crop_height * 1.55
        full_span = component.rect.width >= crop_width * 0.84 and component.rect.height >= crop_height * 0.74
        return full_span or (edge_count >= 2 and very_wide)

    @staticmethod
    def _component_trace(
        line_id: str,
        components: list[Component],
        origin_x: int,
        origin_y: int,
        background_lab: list[float],
    ) -> list[dict[str, Any]]:
        return [
            {
                "LineId": line_id,
                "ComponentId": component.component_id,
                "X": component.rect.x + origin_x,
                "Y": component.rect.y + origin_y,
                "Width": component.rect.width,
                "Height": component.rect.height,
                "Area": component.area,
                "Density": round(component.density, 5),
                "CenterY": round(component.center_y + origin_y, 3),
                "Baseline": component.baseline + origin_y,
                "MedianLab": component.median_lab,
                "MedianHsv": component.median_hsv,
                "Classification": component.classification,
                "Evidence": " | ".join(component.evidence),
                "BackgroundLab": background_lab,
            }
            for component in components
        ]

    def _foreground_mask(self, crop: np.ndarray) -> tuple[np.ndarray, list[float], list[str]]:
        lab = cv2.cvtColor(crop, cv2.COLOR_BGR2LAB).astype(np.float32)
        h, w = lab.shape[:2]
        border = np.concatenate(
            [lab[: max(1, h // 10)].reshape(-1, 3), lab[-max(1, h // 10) :].reshape(-1, 3), lab[:, : max(1, w // 30)].reshape(-1, 3), lab[:, -max(1, w // 30) :].reshape(-1, 3)],
            axis=0,
        )
        background = np.median(border, axis=0)
        distance = np.linalg.norm(lab - background, axis=2)
        gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY).astype(np.float32)
        border_gray = float(np.median(np.concatenate([gray[0], gray[-1], gray[:, 0], gray[:, -1]])))
        gray_delta = np.abs(gray - border_gray)
        threshold = 24.0
        mask = ((distance >= threshold) | (gray_delta >= 34.0)).astype(np.uint8) * 255
        mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((1, 1), np.uint8))
        count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
        clean = np.zeros_like(mask)
        minimum = max(2, int(round(h * h * 0.0015)))
        for index in range(1, count):
            if int(stats[index, cv2.CC_STAT_AREA]) >= minimum:
                clean[labels == index] = 255
        ratio = float(np.count_nonzero(clean)) / float(clean.size)
        return clean, [round(float(x), 3) for x in background], [f"foreground_ratio={ratio:.5f}", f"lab_threshold={threshold:.1f}"]

    def _components(self, crop: np.ndarray, mask: np.ndarray) -> list[Component]:
        h, _ = mask.shape
        count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
        lab = cv2.cvtColor(crop, cv2.COLOR_BGR2LAB)
        hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
        result: list[Component] = []
        minimum = max(2, int(round(h * h * 0.0015)))
        for index in range(1, count):
            x = int(stats[index, cv2.CC_STAT_LEFT])
            y = int(stats[index, cv2.CC_STAT_TOP])
            w = int(stats[index, cv2.CC_STAT_WIDTH])
            ch = int(stats[index, cv2.CC_STAT_HEIGHT])
            area = int(stats[index, cv2.CC_STAT_AREA])
            if area < minimum or w <= 0 or ch <= 0:
                continue
            pixels = labels == index
            lab_values = lab[pixels]
            hsv_values = hsv[pixels]
            result.append(
                Component(
                    component_id=f"C{len(result)+1:03d}",
                    rect=Rect(x, y, w, ch),
                    area=area,
                    density=area / float(w * ch),
                    center_y=y + ch / 2.0,
                    baseline=y + ch,
                    median_lab=[round(float(v), 3) for v in np.median(lab_values, axis=0)],
                    median_hsv=[round(float(v), 3) for v in np.median(hsv_values, axis=0)],
                )
            )
        return sorted(result, key=lambda item: (item.rect.x, item.rect.y, item.component_id))

    @staticmethod
    def _separator_candidates(components: list[Component], crop_height: int) -> tuple[list[dict[str, Any]], float]:
        body = [item for item in components if item.rect.height >= max(4, crop_height * 0.35)]
        median_height = float(np.median([item.rect.height for item in body])) if body else float(crop_height)
        ordered = sorted(components, key=lambda item: item.rect.x)
        candidates: list[dict[str, Any]] = []
        for index, item in enumerate(ordered):
            left_right = max((x.rect.right for x in ordered[:index]), default=0)
            right_left = min((x.rect.x for x in ordered[index + 1 :]), default=item.rect.right)
            left_gap = item.rect.x - left_right
            right_gap = right_left - item.rect.right
            verticality = item.rect.height / max(1.0, item.rect.width)
            slender = item.rect.width <= max(3, median_height * 0.20)
            tall = item.rect.height >= median_height * 0.62
            isolated = left_gap >= max(2.0, median_height * 0.16) and right_gap >= max(2.0, median_height * 0.16)
            high = verticality >= 3.0 and slender and tall and isolated and item.density >= 0.38
            medium = verticality >= 2.3 and slender and tall and (left_gap + right_gap) >= median_height * 0.25
            if high or medium:
                item.classification = "STRUCTURAL_SEPARATOR_CANDIDATE"
                item.evidence.extend([f"verticality={verticality:.3f}", f"left_gap={left_gap:.2f}", f"right_gap={right_gap:.2f}"])
                candidates.append(
                    {
                        "ComponentId": item.component_id,
                        "Rect": asdict(item.rect),
                        "LeftGap": round(left_gap, 3),
                        "RightGap": round(right_gap, 3),
                        "Verticality": round(verticality, 3),
                        "Confidence": "HIGH" if high else "MEDIUM",
                    }
                )
        return candidates, median_height

    @staticmethod
    def _component_color(items: list[Component]) -> np.ndarray:
        weights = np.array([max(1, item.area) for item in items], dtype=np.float64)
        values = np.array([item.median_lab for item in items], dtype=np.float64)
        return np.average(values, axis=0, weights=weights)

    def _terminal_candidate(self, components: list[Component], crop_height: int, crop_width: int) -> dict[str, Any] | None:
        body = [item for item in components if item.area >= 3 and item.rect.height >= max(3, crop_height * 0.18)]
        if len(body) < 3:
            return None
        ordered = sorted(body, key=lambda item: item.rect.x)
        best: dict[str, Any] | None = None
        for direction in ("LEADING", "TRAILING"):
            iterable = range(1, len(ordered))
            for split in iterable:
                left = ordered[:split]
                right = ordered[split:]
                gap = min(x.rect.x for x in right) - max(x.rect.right for x in left)
                if gap < max(3.0, crop_height * 0.11):
                    continue
                candidate = left if direction == "LEADING" else right
                main = right if direction == "LEADING" else left
                if len(main) < 2 or len(candidate) > max(3, len(main)):
                    continue
                cand_min = min(x.rect.x for x in candidate)
                cand_max = max(x.rect.right for x in candidate)
                main_min = min(x.rect.x for x in main)
                main_max = max(x.rect.right for x in main)
                if direction == "LEADING" and cand_min > crop_width * 0.42:
                    continue
                if direction == "TRAILING" and cand_max < crop_width * 0.58:
                    continue
                candidate_width = cand_max - cand_min
                candidate_height = max(x.rect.bottom for x in candidate) - min(x.rect.y for x in candidate)
                main_height = float(np.median([x.rect.height for x in main]))
                color_delta = float(np.linalg.norm(self._component_color(candidate) - self._component_color(main)))
                candidate_sat = float(np.average([x.median_hsv[1] for x in candidate], weights=[x.area for x in candidate]))
                main_sat = float(np.average([x.median_hsv[1] for x in main], weights=[x.area for x in main]))
                saturation_delta = abs(candidate_sat - main_sat)
                baseline_delta = abs(float(np.median([x.baseline for x in candidate])) - float(np.median([x.baseline for x in main])))
                width_ratio = candidate_width / max(1.0, crop_height)
                height_ratio = candidate_height / max(1.0, main_height)
                isolated_compact = len(candidate) <= 2 and width_ratio <= 1.55 and gap >= crop_height * 0.20
                geometry_outlier = baseline_delta >= crop_height * 0.16 or height_ratio >= 1.30 or height_ratio <= 0.58
                color_outlier = color_delta >= 24.0 or saturation_delta >= 42.0
                score = 0.0
                score += min(2.0, gap / max(1.0, crop_height * 0.12))
                score += 1.5 if color_outlier else 0.0
                score += 1.0 if geometry_outlier else 0.0
                score += 0.8 if isolated_compact else 0.0
                score -= 1.0 if candidate_width > crop_height * 3.8 else 0.0
                color_icon = saturation_delta >= 38.0 and color_delta >= 20.0 and width_ratio <= 0.75
                achromatic_icon = len(candidate) == 1 and width_ratio <= 0.55 and height_ratio >= 2.15
                high = score >= 3.6 and isolated_compact and height_ratio >= 0.36 and (color_icon or achromatic_icon)
                evidence = [
                    f"direction={direction}",
                    f"gap={gap:.3f}",
                    f"color_delta={color_delta:.3f}",
                    f"saturation_delta={saturation_delta:.3f}",
                    f"baseline_delta={baseline_delta:.3f}",
                    f"width_ratio={width_ratio:.3f}",
                    f"height_ratio={height_ratio:.3f}",
                    f"score={score:.3f}",
                ]
                current = {
                    "Direction": direction,
                    "CandidateIds": [x.component_id for x in candidate],
                    "MainIds": [x.component_id for x in main],
                    "CandidateBounds": {"x": cand_min, "y": min(x.rect.y for x in candidate), "width": candidate_width, "height": candidate_height},
                    "MainBounds": {"x": main_min, "y": min(x.rect.y for x in main), "width": main_max - main_min, "height": max(x.rect.bottom for x in main) - min(x.rect.y for x in main)},
                    "Confidence": "HIGH" if high else "MEDIUM",
                    "Evidence": evidence,
                    "Score": score,
                }
                if best is None or current["Score"] > best["Score"]:
                    best = current
        return best

    def _residual_color_terminal(self, crop: np.ndarray, components: list[Component]) -> dict[str, Any] | None:
        crop_height, crop_width = crop.shape[:2]
        text_like = [
            item
            for item in components
            if item.median_hsv[1] <= 45
            and item.median_lab[0] >= 115
            and item.rect.height >= crop_height * 0.30
            and item.rect.height <= crop_height * 0.92
            and item.rect.width <= crop_height * 1.20
        ]
        if len(text_like) < 2:
            return None
        main_left = min(item.rect.x for item in text_like)
        main_right = max(item.rect.right for item in text_like)
        main_height = float(np.median([item.rect.height for item in text_like]))
        hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
        color_mask = ((hsv[:, :, 1] >= 55) & (hsv[:, :, 2] >= 80)).astype(np.uint8) * 255
        color_mask = cv2.morphologyEx(color_mask, cv2.MORPH_CLOSE, np.ones((3, 3), np.uint8))
        count, labels, stats, _ = cv2.connectedComponentsWithStats(color_mask, 8)
        candidates: list[dict[str, Any]] = []
        lab = cv2.cvtColor(crop, cv2.COLOR_BGR2LAB)
        for index in range(1, count):
            x = int(stats[index, cv2.CC_STAT_LEFT])
            y = int(stats[index, cv2.CC_STAT_TOP])
            width = int(stats[index, cv2.CC_STAT_WIDTH])
            height = int(stats[index, cv2.CC_STAT_HEIGHT])
            area = int(stats[index, cv2.CC_STAT_AREA])
            if area < max(8, int(round(crop_height * crop_height * 0.012))):
                continue
            if width > crop_height * 1.55 or height < main_height * 1.30:
                continue
            if x >= main_right:
                direction = "TRAILING"
                gap = x - main_right
            elif x + width <= main_left:
                direction = "LEADING"
                gap = main_left - (x + width)
            else:
                continue
            if gap < crop_height * 0.12 or gap > crop_height * 0.80:
                continue
            pixels = labels == index
            median_lab = [round(float(value), 3) for value in np.median(lab[pixels], axis=0)]
            median_hsv = [round(float(value), 3) for value in np.median(hsv[pixels], axis=0)]
            synthetic = Component(
                component_id="RES001",
                rect=Rect(x, y, width, height),
                area=area,
                density=area / float(max(1, width * height)),
                center_y=y + height / 2.0,
                baseline=y + height,
                median_lab=median_lab,
                median_hsv=median_hsv,
                classification="ICON_OR_ORNAMENT_CANDIDATE",
                evidence=["isolated_saturated_component_adjacent_to_achromatic_text"],
            )
            score = 5.0 - min(1.0, gap / max(1.0, crop_height))
            candidates.append(
                {
                    "Direction": direction,
                    "CandidateIds": [synthetic.component_id],
                    "MainIds": [item.component_id for item in text_like],
                    "CandidateBounds": asdict(synthetic.rect),
                    "MainBounds": asdict(self._union(text_like)),
                    "Confidence": "HIGH",
                    "Evidence": [
                        f"direction={direction}",
                        f"gap={gap:.3f}",
                        f"color_saturation={median_hsv[1]:.3f}",
                        f"height_ratio={height / max(1.0, main_height):.3f}",
                        "residual_color_projection=true",
                    ],
                    "Score": score,
                    "Gap": gap,
                    "Component": synthetic,
                }
            )
        if not candidates:
            return None
        return sorted(candidates, key=lambda item: (item["Gap"], item["CandidateBounds"]["x"]))[0]

    def _separator_proposal(
        self,
        line_id: str,
        original_text: str,
        polygon: list[Any],
        crop: np.ndarray,
        mask: np.ndarray,
        components: list[Component],
        separators: list[dict[str, Any]],
        origin_x: int,
        origin_y: int,
        median_height: float,
        evidence: list[str],
        started: float,
    ) -> InlineBoundaryProposal:
        sep_components = {item["ComponentId"] for item in separators}
        ordered_seps = sorted(separators, key=lambda item: item["Rect"]["x"])
        boundaries = [0] + [item["Rect"]["x"] for item in ordered_seps] + [crop.shape[1]]
        text_segments: list[dict[str, Any]] = []
        non_text: list[dict[str, Any]] = []
        unknown: list[dict[str, Any]] = []
        crops: list[dict[str, Any]] = []
        usable = [item for item in components if item.component_id not in sep_components]
        for index in range(len(boundaries) - 1):
            left = boundaries[index]
            right = boundaries[index + 1]
            local = [item for item in usable if item.rect.x >= left and item.rect.right <= right]
            if not local:
                continue
            shifted = [
                Component(
                    component_id=item.component_id,
                    rect=Rect(item.rect.x - left, item.rect.y, item.rect.width, item.rect.height),
                    area=item.area,
                    density=item.density,
                    center_y=item.center_y,
                    baseline=item.baseline,
                    median_lab=item.median_lab,
                    median_hsv=item.median_hsv,
                )
                for item in local
            ]
            terminal = self._terminal_candidate(shifted, crop.shape[0], max(1, right - left))
            excluded: set[str] = set()
            if terminal is not None and terminal["Confidence"] == "HIGH":
                excluded.update(terminal["CandidateIds"])
                for item in local:
                    if item.component_id in excluded:
                        item.classification = "ICON_OR_ORNAMENT"
                        item.evidence.extend(terminal["Evidence"])
                        non_text.append(
                            {
                                "ComponentId": item.component_id,
                                "Rect": self._absolute_rect(item.rect, origin_x, origin_y),
                                "Classification": "ICON_OR_ORNAMENT",
                                "Evidence": terminal["Evidence"],
                                "SegmentIndex": index,
                            }
                        )
            text_items = [item for item in local if item.component_id not in excluded]
            if not text_items:
                for item in local:
                    unknown.append({"ComponentId": item.component_id, "Rect": self._absolute_rect(item.rect, origin_x, origin_y), "Classification": "UNKNOWN"})
                continue
            bounds = self._union(text_items)
            pad = max(1, int(round(median_height * 0.10)))
            rect = Rect(max(0, bounds.x - pad), max(0, bounds.y - pad), min(crop.shape[1], bounds.right + pad) - max(0, bounds.x - pad), min(crop.shape[0], bounds.bottom + pad) - max(0, bounds.y - pad))
            absolute = self._absolute_rect(rect, origin_x, origin_y)
            text_segments.append({"SegmentIndex": len(text_segments), "Rect": absolute, "ComponentIds": [x.component_id for x in text_items], "Classification": "TEXT"})
            crops.append({"SegmentIndex": len(crops), "Rect": absolute, "Purpose": "INLINE_TEXT_SEGMENT"})
        structural = [
            {
                "ComponentId": item["ComponentId"],
                "Rect": self._absolute_rect(Rect(**item["Rect"]), origin_x, origin_y),
                "Classification": "STRUCTURAL_SEPARATOR",
                "Confidence": item["Confidence"],
                "LeftGap": item["LeftGap"],
                "RightGap": item["RightGap"],
            }
            for item in ordered_seps
        ]
        for item in components:
            if item.component_id in sep_components:
                item.classification = "STRUCTURAL_SEPARATOR"
                item.evidence.append("high_isolated_vertical_component")
        elapsed = (time.perf_counter() - started) * 1000.0
        final_evidence = list(evidence) + [f"high_separator_count={len(ordered_seps)}", f"text_segment_count={len(text_segments)}"]
        minimum_segment_components = min((len(item["ComponentIds"]) for item in text_segments), default=0)
        final_evidence.append(f"minimum_segment_components={minimum_segment_components}")
        confidence = "HIGH" if len(text_segments) >= 3 and minimum_segment_components >= 2 else "MEDIUM"
        reason = "MULTIPLE_ISOLATED_VERTICAL_COMPONENTS_DEFINE_FIELD_STRUCTURE" if confidence == "HIGH" else "SEPARATORS_FOUND_BUT_TEXT_SPANS_INCOMPLETE"
        return InlineBoundaryProposal(line_id, self._polygon(polygon), original_text, text_segments, structural, non_text, unknown, crops, final_evidence, confidence, reason, "INLINE_STRUCTURAL_SEPARATOR", True, round(elapsed, 4))

    def _terminal_proposal(
        self,
        line_id: str,
        original_text: str,
        polygon: list[Any],
        components: list[Component],
        terminal: dict[str, Any],
        origin_x: int,
        origin_y: int,
        crop_height: int,
        crop_width: int,
        evidence: list[str],
        started: float,
    ) -> InlineBoundaryProposal:
        candidate_ids = set(terminal["CandidateIds"])
        main = [item for item in components if item.component_id in set(terminal["MainIds"])]
        candidate = [item for item in components if item.component_id in candidate_ids]
        bounds = self._union(main)
        pad = max(3, int(round(crop_height * 0.25)))
        candidate_bounds = terminal["CandidateBounds"]
        candidate_left = int(candidate_bounds["x"])
        candidate_right = candidate_left + int(candidate_bounds["width"])
        left_pad = pad
        right_pad = pad
        if terminal["Direction"] == "LEADING":
            left_pad = min(pad, max(1, int(round((bounds.x - candidate_right) * 0.35))))
        else:
            right_pad = min(pad, max(1, int(round((candidate_left - bounds.right) * 0.35))))
        local_left = max(0, bounds.x - left_pad)
        local_right = min(crop_width, bounds.right + right_pad)
        local_rect = Rect(local_left, 0, max(1, local_right - local_left), crop_height)
        absolute = self._absolute_rect(local_rect, origin_x, origin_y)
        non_text = []
        for item in candidate:
            item.classification = "ICON_OR_ORNAMENT"
            item.evidence.extend(terminal["Evidence"])
            non_text.append({"ComponentId": item.component_id, "Rect": self._absolute_rect(item.rect, origin_x, origin_y), "Classification": "ICON_OR_ORNAMENT", "Evidence": terminal["Evidence"]})
        elapsed = (time.perf_counter() - started) * 1000.0
        return InlineBoundaryProposal(
            line_id,
            self._polygon(polygon),
            original_text,
            [{"SegmentIndex": 0, "Rect": absolute, "ComponentIds": terminal["MainIds"], "Classification": "TEXT"}],
            [],
            non_text,
            [],
            [{"SegmentIndex": 0, "Rect": absolute, "Purpose": "TERMINAL_TEXT_ONLY"}],
            list(evidence) + terminal["Evidence"],
            "HIGH",
            f"{terminal['Direction']}_COMPONENT_OUTSIDE_DOMINANT_TEXT_BAND",
            "TERMINAL_NON_TEXT_BOUNDARY",
            True,
            round(elapsed, 4),
        )

    @staticmethod
    def _union(items: list[Component]) -> Rect:
        left = min(item.rect.x for item in items)
        top = min(item.rect.y for item in items)
        right = max(item.rect.right for item in items)
        bottom = max(item.rect.bottom for item in items)
        return Rect(left, top, right - left, bottom - top)

    @staticmethod
    def _absolute_rect(rect: Rect, x: int, y: int) -> dict[str, int]:
        return {"x": rect.x + x, "y": rect.y + y, "width": rect.width, "height": rect.height}

    @staticmethod
    def _polygon(polygon: list[Any]) -> list[list[float]]:
        result: list[list[float]] = []
        for point in polygon:
            if isinstance(point, dict):
                result.append([float(point.get("X", point.get("x", 0))), float(point.get("Y", point.get("y", 0)))])
            else:
                result.append([float(point[0]), float(point[1])])
        return result

    def _abstain(
        self,
        line_id: str,
        original_text: str,
        polygon: list[Any],
        reason: str,
        started: float,
        evidence: list[str] | None = None,
    ) -> InlineBoundaryProposal:
        elapsed = (time.perf_counter() - started) * 1000.0
        return InlineBoundaryProposal(line_id, self._polygon(polygon), original_text, [], [], [], [], [], evidence or [], "LOW", reason, "NONE", False, round(elapsed, 4))


def crop_hash(image: np.ndarray) -> str:
    ok, encoded = cv2.imencode(".png", image)
    if not ok:
        raise RuntimeError("Unable to encode crop")
    return hashlib.sha256(encoded.tobytes()).hexdigest().upper()

