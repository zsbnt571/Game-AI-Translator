from __future__ import annotations

import re
import time
from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class RouterDecision:
    deep_analysis: bool
    reason: str
    evidence: tuple[str, ...]
    cpu_ms: float

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


_TERMINAL_ORNAMENTS = ("→", "•")
_LEADING_NUMERIC_ARTIFACT = re.compile(r"^[^\w\s]+\s*\d")
_MIXED_PREFIX = re.compile(r"^[\u3400-\u9fff]{1,2}[A-Za-z]{3,}")
_MULTI_SPACE_FIELD = re.compile(r"\S\s{2,}\S")


def route(original_text: str) -> RouterDecision:
    """Cheap, fixture-independent suspicion gate for the proven analyzer.

    It proposes no boundary and changes no semantic rule.  A positive decision
    merely permits the existing image analyzer to run.
    """

    started = time.perf_counter()
    text = " ".join(str(original_text).replace("\r", " ").replace("\n", " ").split(" "))
    compact = text.strip()
    evidence: list[str] = []

    ornaments = [value for value in _TERMINAL_ORNAMENTS if value in compact]
    if ornaments:
        evidence.append("terminal_ornament_token=" + ",".join(ornaments))

    if _LEADING_NUMERIC_ARTIFACT.search(compact):
        evidence.append("leading_nontext_before_numeric_payload")

    if len(compact) <= 24 and _MIXED_PREFIX.search(compact):
        evidence.append("short_non_ascii_prefix_before_ascii_label")

    pipe_count = compact.count("|")
    if pipe_count >= 2:
        evidence.append(f"multiple_explicit_field_separators={pipe_count}")

    # Detector flattening commonly leaves large horizontal gaps between a
    # label, compact numeric field, and a following label.  This is only a
    # suspicion signal; the unchanged analyzer must still prove separators.
    raw = str(original_text).replace("\r", " ").replace("\n", " ")
    if _MULTI_SPACE_FIELD.search(raw) and any(ch.isdigit() for ch in raw) and any(ch.isalpha() for ch in raw):
        evidence.append("multi_gap_mixed_field_structure")

    elapsed = (time.perf_counter() - started) * 1000.0
    return RouterDecision(
        deep_analysis=bool(evidence),
        reason="CHEAP_SUSPICION_HIT" if evidence else "ORDINARY_LINE_DIRECT",
        evidence=tuple(evidence),
        cpu_ms=elapsed,
    )


def should_reocr(original_text: str, proposal_kind: str) -> tuple[bool, str]:
    """Route proven HIGH proposals to the smallest necessary OCR work.

    Existing explicit separators already carry their structure, while a small
    performance telemetry row is not a semantic text-refinement target.  Both
    retain the analyzer's HIGH relation and current A-side text unchanged.
    """

    text = str(original_text)
    if proposal_kind == "INLINE_STRUCTURAL_SEPARATOR" and text.count("|") >= 2:
        return False, "EXISTING_EXPLICIT_SEPARATORS_PRESERVE_A"
    upper = text.upper()
    if proposal_kind == "INLINE_STRUCTURAL_SEPARATOR" and "FPS" in upper and "GPU" in upper and "CPU" in upper:
        return False, "TELEMETRY_STRUCTURE_PRESERVE_A"
    return True, "LOCAL_RECOGNIZER_REQUIRED"

