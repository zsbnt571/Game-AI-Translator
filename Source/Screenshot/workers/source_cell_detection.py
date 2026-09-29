"""Bounded source divider split support for the current OCR worker."""
import contextlib, hashlib, math, sys
import cv2
import numpy as np
from bounded_text_detection import bounds, overlap, norm, glyph_support, MAX_PIXELS, CONTRACT

def split_source_cells(image,result,local_texts,local_scores,local_polys,origin,scale,engine,trace,row):
 # A detector may join two cells. Separate local detections alone are not
 # enough: require a persistent source divider before spending bounded reads.
 import difflib
 mapped=[]
 for text,score,poly in zip(local_texts,local_scores,local_polys):
  pts=np.asarray(poly,dtype=float)/scale+origin
  lo=pts.min(axis=0);hi=pts.max(axis=0);box=[*lo,*(hi-lo)]
  if float(score)>=.90:mapped.append((str(text).strip(),float(score),box))
 for old in list(result):
  if trace.get('splitRecognitionCalls',0)>=4:break
  ob=bounds(old)
  if ob[3]>24 or ob[2]<ob[3]*5 or len(str(old['text']).split())<2:continue
  parts=sorted([p for p in mapped if overlap(ob,p[2])/max(1,p[2][2]*p[2][3])>=.8 and
    abs(ob[1]+ob[3]/2-p[2][1]-p[2][3]/2)<max(ob[3],p[2][3])*.36],key=lambda p:p[2][0])
  if not 2<=len(parts)<=3 or trace.get('splitRecognitionCalls',0)+len(parts)>4:continue
  if any(a[2][0]+a[2][2]>b[2][0] for a,b in zip(parts,parts[1:])):continue
  if difflib.SequenceMatcher(None,norm(old['text']),norm(''.join(p[0] for p in parts))).ratio()<.85:continue
  top=max(0,int(math.floor(min(p[2][1] for p in parts)))-1)
  bottom=min(image.shape[0],int(math.ceil(max(p[2][1]+p[2][3] for p in parts)))+1)
  dividers=[]
  for left,right in zip(parts,parts[1:]):
   best=None
   for x in range(int(math.ceil(left[2][0]+left[2][2])),int(math.floor(right[2][0]))):
    if x<3 or x+3>=image.shape[1]:continue
    center=image[top:bottom,x].astype(float);color=np.median(center,axis=0)
    l=np.median(image[top:bottom,x-3:x-1].reshape(-1,3),axis=0)
    r=np.median(image[top:bottom,x+2:x+4].reshape(-1,3),axis=0)
    contrast=min(float(np.max(abs(color-l))),float(np.max(abs(color-r))))
    stable=float(np.mean(np.max(abs(center-color),axis=1)<=24))
    if contrast>=26 and stable>=.65 and (best is None or contrast*stable>best[1]):best=(x,contrast*stable)
   if best is None:break
   dividers.append(best[0])
  if len(dividers)!=len(parts)-1:continue
  limits=[max(0,int(math.floor(ob[0]))-1),*dividers,min(image.shape[1],int(math.ceil(ob[0]+ob[2]))+1)]
  evidence=dict(original=old,dividers=dividers,reads=[],reason='SOURCE_VERTICAL_CELL_DIVIDERS_AND_SEPARATE_DETECTIONS')
  complete=[]
  for index,part in enumerate(parts):
   left=limits[index]+(1 if index else 0);right=limits[index+1]
   area=(right-left)*(bottom-top)*16
   if trace['inputPixels']+area>MAX_PIXELS:break
   crop=image[top:bottom,left:right];trace['inputPixels']+=area
   trace['splitRecognitionCalls']=trace.get('splitRecognitionCalls',0)+1
   enlarged=cv2.resize(crop,None,fx=4,fy=4,interpolation=cv2.INTER_NEAREST)
   with contextlib.redirect_stdout(sys.stderr):read=engine(enlarged,use_det=False,use_cls=False,use_rec=True)
   texts=[] if read.txts is None else list(read.txts);scores=[] if read.scores is None else list(read.scores)
   evidence['reads'].append(dict(crop=[left,top,right-left,bottom-top],scale=4,raw=texts,scores=[float(v) for v in scores],
     cropToSource=[[.25,0,left],[0,.25,top],[0,0,1]]))
   if len(texts)!=1 or float(scores[0])<.90:break
   text=str(texts[0]).strip()
   if difflib.SequenceMatcher(None,norm(part[0]),norm(text)).ratio()<.75:break
   original_words=str(old['text']).split()
   if len(original_words)==len(parts) and norm(text)==norm(original_words[index]):
    # The independent source divider establishes the original token's owner;
    # a recognizer-inserted intra-word space is not new source content.
    text=original_words[index]
   # Crop bounds are evidence, not new line geometry. Use the full source cell
   # segment of the original detection so no leading letter is discarded.
   box=[float(left),float(top),float(right-left),float(bottom-top)]
   supported,proof=glyph_support(image,box,text)
   if not supported:break
   key=hashlib.sha256((text+'|'+','.join(f'{v:.1f}' for v in box)).encode()).hexdigest()[:14]
   complete.append(dict(id='RC-'+key,text=text,confidence=float(scores[0]),x=box[0],y=box[1],width=box[2],height=box[3],
     polygon=[[left,top],[right,top],[right,bottom],[left,bottom]],lineIndex=old.get('lineIndex',0),
     # Equal integer order preserves the public worker contract. The formal
     # source normalizer breaks this tie by source Y/X, never by a fractional ID.
     readingOrder=int(old.get('readingOrder',0)),coverageRecovery=CONTRACT))
  evidence['complete']=len(complete)==len(parts)
  row.setdefault('sourceCellSplits',[]).append(evidence)
  if len(complete)!=len(parts):continue
  result.remove(old);result.extend(complete);trace['replaced']+=1;trace['added']+=len(complete)-1
  trace['splitSources']=trace.get('splitSources',0)+1
