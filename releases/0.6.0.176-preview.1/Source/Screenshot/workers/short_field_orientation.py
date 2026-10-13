"""Bounded upright retry for ambiguous short values in proved source field rows.

The recognizer's high text score does not disprove an erroneous 180-degree
classifier decision. Preserve raw evidence; use independent source row/column
orientation evidence before rereading two tightly bounded original crops.
"""
import math
import time

MAX_FIELDS = 8
MAX_INPUT_PIXELS = 300_000


def _box(row):
    p = row.get('polygon') or []
    if len(p) != 4:
        return None
    x = min(v[0] for v in p); y = min(v[1] for v in p)
    w = max(v[0] for v in p)-x; h = max(v[1] for v in p)-y
    if w <= 0 or h <= 0 or abs(p[1][1]-p[0][1]) > h*.12:
        return None
    return x,y,w,h


def _letters(text):
    return ''.join(c for c in str(text) if c.isascii() and c.isalpha())


def propose(candidates, width):
    rows = [(c,_box(c)) for c in candidates]
    rows = [(c,b) for c,b in rows if b]
    short = [(c,b) for c,b in rows if 2 <= len(str(c.get('rawRecognition',''))) <= 4
             and str(c.get('rawRecognition','')).isascii()
             and str(c.get('rawRecognition','')).isalpha()]
    proposals=[]
    for current,b in short[:64]:
        direction=current.get('orientation') or []
        if len(direction)!=2 or direction[0]!='180' or not .9 <= direction[1] < .98:
            continue
        x,y,w,h=b
        if not 8 <= h <= 120 or not .5 <= w/h <= 5:
            continue
        # A second separate short value establishes a column, rather than an
        # isolated rotated mark next to unrelated upright prose.
        column=[c for c,q in short if c is not current and abs(q[0]-x)<h*1.5
                and h*1.3 < abs(q[1]-y) < h*20 and .6 < q[3]/h < 1.6]
        if not column:
            continue
        labels=[]
        for c,q in rows:
            angle=c.get('orientation') or []
            if len(angle)!=2 or angle[0]!='0' or angle[1]<.995 or c.get('recognitionScore',0)<.95:
                continue
            if len(_letters(c.get('rawRecognition','')))<5 or not .6 < q[3]/h < 1.6:
                continue
            if q[0]+q[2] < x and x-q[0] < width*.7 and abs(q[1]+q[3]/2-y-h/2)<h*.5:
                labels.append((c,q))
        if len(labels)!=1:
            continue
        label,q=labels[0]
        neighbors=[]
        for c,z in rows:
            angle=c.get('orientation') or []
            if c is label or len(angle)!=2 or angle[0]!='0' or angle[1]<.995:
                continue
            if c.get('recognitionScore',0)<.95 or len(_letters(c.get('rawRecognition','')))<5:
                continue
            if abs(z[0]-q[0])<h*1.5 and h*1.3<abs(z[1]-q[1])<h*20 and .6<z[3]/h<1.6:
                neighbors.append(c)
        if len(neighbors)>=2:
            proposals.append(dict(candidate=current,box=list(b),rowLabelIndex=label['index'],
                                  otherUprightLabelIndices=[v['index'] for v in neighbors],
                                  peerValueIndices=[v['index'] for v in column]))
    return proposals[:MAX_FIELDS]


def recover(image,blocks,source_stages,recognizer):
    from rapidocr.ch_ppocr_rec.typings import TextRecInput
    started=time.perf_counter(); result=[dict(b) for b in blocks]
    trace=dict(contract='SOURCE_FIELD_UPRIGHT_CONSENSUS_V1',maxFields=MAX_FIELDS,
               maxInputPixels=MAX_INPUT_PIXELS,inputPixels=0,recognitionCalls=0,changed=0,rows=[])
    for proposal in propose(source_stages.get('candidates',[]),image.shape[1]):
        raw=proposal['candidate'];x,y,w,h=proposal['box']
        matches=[b for b in result if abs(b['x']-x)<h*.1 and abs(b['y']-y)<h*.1
                 and abs(b['width']-w)<h*.1 and abs(b['height']-h)<h*.1]
        if len(matches)!=1:
            continue
        block=matches[0]
        row={k:v for k,v in proposal.items() if k!='candidate'}
        row.update(sourceId=block['id'],originalText=block['text'],originalOrientation=raw['orientation'],
                   variants=[],changed=False,reason='UPRIGHT_CONSENSUS_NOT_PROVEN')
        trace['rows'].append(row)
        try:
            for pad in (0,2):
                left=max(0,math.floor(x)-pad);top=max(0,math.floor(y)-pad)
                right=min(image.shape[1],math.ceil(x+w)+pad);bottom=min(image.shape[0],math.ceil(y+h)+pad)
                pixels=(right-left)*(bottom-top)
                if pixels<=0 or trace['inputPixels']+pixels>MAX_INPUT_PIXELS:
                    row['reason']='SOURCE_FIELD_PIXEL_BUDGET';break
                crop=image[top:bottom,left:right].copy()
                trace['inputPixels']+=pixels;trace['recognitionCalls']+=1
                local=recognizer(TextRecInput(img=[crop],return_word_box=False))
                text=str(local.txts[0]).strip() if local.txts else ''
                score=float(local.scores[0]) if local.scores else 0.
                row['variants'].append(dict(text=text,score=score,pad=pad,cropToSource=[1,0,left,0,1,top]))
            v=row['variants']
            if len(v)==2 and v[0]['text']==v[1]['text'] and all(z['score']>=.98 for z in v):
                text=v[0]['text']
                if 2<=len(text)<=4 and text.isascii() and text.isalpha():
                    row['reason']='SOURCE_UPRIGHT_LABEL_ROW_AND_VALUE_COLUMN_WITH_TWO_CROP_AGREEMENT'
                    if text!=block['text']:
                        block['orientationOriginalText']=block['text'];block['text']=text
                        block['confidence']=min(z['score'] for z in v)
                        row['changed']=True;trace['changed']+=1
                    row['selectedText']=text
        except Exception as error:
            row['reason']='SOURCE_FIELD_ORIGINAL_PRESERVED_AFTER_'+type(error).__name__
    trace['totalMs']=(time.perf_counter()-started)*1000
    return result,trace
