"""Choose among an already validated orientation family; never fix individual words."""
CONTRACT = "SMALL_LINE_SCORE_PLATEAU_TIGHT_CROP_V1"

def select(variants, medoid):
    original = variants[medoid]
    high = max(float(v["score"]) for v in variants)
    length = sum(c.isalnum() for c in original["text"])
    words = len(original["text"].split())
    eligible = [(i, v) for i, v in enumerate(variants) if float(v["score"]) >= .85 and
                float(v["score"]) + 1e-9 >= high - .02 and
                sum(c.isalnum() for c in v["text"]) >= length and
                len(v["text"].split()) >= words]
    chosen = min(eligible, key=lambda pair: pair[1]["pad"])[0] if eligible else medoid
    return chosen, dict(contract=CONTRACT, previousMedoidText=original["text"],
                        previousMedoidPad=original["pad"], selectedPad=variants[chosen]["pad"],
                        maximumScore=high, scorePlateau=.02,
                        noAlphanumericLoss=True, noWordCountLoss=True,
                        wordSpecificCorrection=False)
