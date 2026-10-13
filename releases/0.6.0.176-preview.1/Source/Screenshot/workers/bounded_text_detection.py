"""Bounded source-resolution detector recovery, without semantic answers.

Existing small text establishes a region worth re-detecting; residual source
glyph groups also propose regions with no accepted OCR at all. Candidates keep
their source-coordinate transform and need detector, recognition and ink proof.
"""
import contextlib, hashlib, math, re, sys, time
import cv2
import numpy as np

CONTRACT='BOUNDED_SOURCE_TEXT_DETECTION_V3_ORIENTED_NEIGHBORS'
MAX_REGIONS=6
MAX_PIXELS=2_500_000

def bounds(block):return [float(block[k]) for k in ('x','y','width','height')]
def overlap(a,b):
 x,y,w,h=a;u,v,p,q=b
 return max(0,min(x+w,u+p)-max(x,u))*max(0,min(y+h,v+q)-max(y,v))
def norm(text):return ''.join(c.casefold() for c in text if c.isalnum())

def large_glyph_sequences(image,blocks):
 # Large source letters can lie on an arc which a line detector never proposes.
 # Use source component chains, without an OCR anchor or a language answer.
 gray=cv2.cvtColor(image,cv2.COLOR_BGR2GRAY);height,width=gray.shape;out=[]
 for bright in (True,False):
  threshold=float(np.percentile(gray,90 if bright else 10))
  binary=((gray>=threshold) if bright else (gray<=threshold)).astype(np.uint8)
  _,_,stats,centers=cv2.connectedComponentsWithStats(binary,8)
  ids=[i for i in range(1,len(stats)) if 18<=stats[i,3]<=min(150,height*.18) and
       .12<=stats[i,2]/stats[i,3]<=1.65 and .09<=stats[i,4]/(stats[i,2]*stats[i,3])<=.65]
  if len(ids)>800:continue
  parents=list(range(len(ids)))
  def find(i):
   while parents[i]!=i:parents[i]=parents[parents[i]];i=parents[i]
   return i
  for i,first in enumerate(ids):
   x,y,w,h,_=stats[first]
   for j in range(i+1,len(ids)):
    u,v,p,q,_=stats[ids[j]]
    if not .55<=h/q<=1.8:continue
    gap=max(x,u)-min(x+w,u+p)
    if -.15*min(w,p)<=gap<=max(h,q)*2.1 and abs((y+h/2)-(v+q/2))<=max(h,q)*.75:
     parents[find(i)]=find(j)
  groups={}
  for i,idx in enumerate(ids):groups.setdefault(find(i),[]).append(idx)
  for members in groups.values():
   if len(members)<5:continue
   boxes=stats[members];l=int(min(boxes[:,0]));t=int(min(boxes[:,1]));r=int(max(boxes[:,0]+boxes[:,2]));b=int(max(boxes[:,1]+boxes[:,3]));mid=float(np.median(boxes[:,3]))
   if r-l<mid*4 or b-t>mid*3 or r-l>width*.9:continue
   box=[l,t,r-l,b-t]
   if sum(overlap(box,bounds(o)) for o in blocks)/max(1,box[2]*box[3])>.45:continue
   pad=int(mid*.28);x=max(0,l-pad);y=max(0,t-pad)
   out.append(dict(rect=[x,y,min(width,r+pad)-x,min(height,b+pad)-y],angle=0.,scale=1,
       reason='RESIDUAL_LARGE_SOURCE_GLYPH_CHAIN_WITHOUT_DETECTION',support=len(members),
       sourceGlyphBoxes=boxes[:,:4].tolist(),sourcePolarity='bright' if bright else 'dark'))
 return out

def proposals(image,blocks):
 height,width=image.shape[:2];out=[];seen=set()
 for block in blocks:
  p=np.asarray(block.get('polygon',[]),dtype=float)
  if p.shape!=(4,2) or not 2<=len(block.get('text',''))<=16:continue
  u=p[1]-p[0];w=float(np.linalg.norm(u));v=p[3]-p[0];h=float(np.linalg.norm(v))
  if w<1 or h<1:continue
  u/=w;v/=h;angle=math.degrees(math.atan2(u[1],u[0]))
  if not 7<=abs(angle)<=35 or not 16<=h<=240 or abs(float(u@v))>.12:continue
  origin=p[0]-.65*w*u-2.4*h*v;cw=int(w*2.8);ch=int(h*3.8)
  if cw*ch>800000:continue
  inv=np.array([[u[0],v[0],origin[0]],[u[1],v[1],origin[1]]],dtype=float)
  corners=np.array([[0,0,1],[cw,0,1],[cw,ch,1],[0,ch,1]])@inv.T
  if corners[:,0].min()<0 or corners[:,1].min()<0 or corners[:,0].max()>width or corners[:,1].max()>height:continue
  lo=corners.min(axis=0);hi=corners.max(axis=0)
  out.append(dict(rect=[int(lo[0]),int(lo[1]),int(hi[0]-lo[0]),int(hi[1]-lo[1])],angle=angle,scale=1,
      reason='SOURCE_ORIENTED_ROW_ADJACENT_COVERAGE',support=1,anchorId=block.get('id',''),warpSize=[cw,ch],cropToSource=inv.tolist()))
 # Source coordinates stay native. A 4K screenshot can contain 80px labels
 # which were small after the full-frame detector's reduction; do not apply a
 # 24 source-pixel ceiling to all canvas resolutions. Only a repeated text band
 # triggers this local detector, under the same shared region/pixel budgets.
 small_limit=24*max(1,min(4,max(width,height)/960))
 small=[bounds(b) for b in blocks if 5<=float(b['height'])<=small_limit and float(b['width'])>=float(b['height'])*1.3]
 for x,y,w,h in small:
  peers=[b for b in small if abs(b[1]-y)<=max(10,h*1.6) and b[3]>=h*.55 and b[3]<=h*1.8]
  if len(peers)<4:continue
  l=max(0,int(min(b[0] for b in peers)-h*2));r=min(width,int(max(b[0]+b[2] for b in peers)+h*4))
  t=max(0,int(min(b[1] for b in peers)-h*1.5));bt=min(height,int(max(b[1]+b[3] for b in peers)+h*3))
  if r-l<width*.15:continue
  key=(round(t/12),round(bt/12))
  if key in seen:continue
  seen.add(key)
  for start in range(l,r,480):
   box=[max(l,start-24),t,min(r,start+504)-max(l,start-24),bt-t]
   out.append(dict(rect=box,angle=0.,scale=3 if h<12 else 2 if h<24 else 1,reason='SOURCE_SMALL_TEXT_BAND_WITH_UNCOVERED_MARGINS',support=len(peers),nativePeerHeight=h,sourceSmallLimit=small_limit))
 # Polarity-independent source strokes. This proposal pass neither classifies
 # an image as language nor permits erasure; false proposals are expected.
 gray=cv2.cvtColor(image,cv2.COLOR_BGR2GRAY)
 kernel=cv2.getStructuringElement(cv2.MORPH_RECT,(23,15))
 residual=np.maximum(cv2.morphologyEx(gray,cv2.MORPH_TOPHAT,kernel),cv2.morphologyEx(gray,cv2.MORPH_BLACKHAT,kernel))
 ink=(residual>48).astype(np.uint8)
 n,labels,stats,_=cv2.connectedComponentsWithStats(ink,8)
 ids=[i for i in range(1,n) if 3<=stats[i,3]<=100 and 1<=stats[i,2]<=100 and stats[i,4]>=6 and stats[i,2]/stats[i,3]<=5]
 eligible=np.isin(labels,ids).astype(np.uint8)
 joined=cv2.morphologyEx(eligible,cv2.MORPH_CLOSE,cv2.getStructuringElement(cv2.MORPH_RECT,(29,7)))
 _,_,groups,_=cv2.connectedComponentsWithStats(joined,8)
 retained=stats[ids]
 for x,y,w,h,area in sorted(groups[1:],key=lambda a:-a[2])[:256]:
  x,y,w,h=map(int,(x,y,w,h));box=[x,y,w,h]
  if w<80 or h<12 or h>170 or w/h<3 or w>width*.85:continue
  members=retained[(retained[:,0]>=x)&(retained[:,1]>=y)&(retained[:,0]+retained[:,2]<=x+w)&(retained[:,1]+retained[:,3]<=y+h)]
  if len(members)<6:continue
  # A source row has several comparable glyph heights. Isolated decorative
  # curves and long diagram strokes do not provide the same evidence.
  heights=np.array([m[3] for m in members]);mid=float(np.median(heights))
  if mid<8 or np.mean((heights>=mid*.55)&(heights<=mid*1.8))<.7:continue
  covered=sum(overlap(box,bounds(b)) for b in blocks)/(w*h)
  if covered>.60:continue
  pad=max(4,int(mid*.35));l=max(0,x-pad);t=max(0,y-pad)
  out.append(dict(rect=[l,t,min(width,x+w+pad)-l,min(height,y+h+pad)-t],angle=0.,scale=2 if h<50 else 1,
                  reason='RESIDUAL_SOURCE_GLYPH_SEQUENCE_WITHOUT_OCR_COVERAGE',support=len(members)))
 out.extend(large_glyph_sequences(image,blocks))
 result=[]
 # Reserve opportunities for genuinely missing regions instead of spending
 # the entire budget re-reading a long row of already detected small labels.
 ordered=sorted(out,key=lambda p:(0 if 'LARGE_SOURCE' in p['reason'] else 1,-p['support'],p['rect'][1],p['rect'][0]))
 large=[p for p in ordered if 'LARGE_SOURCE' in p['reason']][:2]
 oriented=[p for p in ordered if p['reason']=='SOURCE_ORIENTED_ROW_ADJACENT_COVERAGE'][:2]
 ordered=oriented+large+[p for p in ordered if 'LARGE_SOURCE' not in p['reason'] and p not in oriented]
 for item in ordered:
  box=item['rect']
  if any(('LARGE_SOURCE' in item['reason'])==('LARGE_SOURCE' in p['reason']) and
         overlap(box,p['rect'])/max(1,min(box[2]*box[3],p['rect'][2]*p['rect'][3]))>.7 for p in result):continue
  result.append(item)
 return result

def glyph_support(image,box,text):
 x,y,w,h=[int(v) for v in box];l=max(0,x);t=max(0,y);r=min(image.shape[1],x+w);b=min(image.shape[0],y+h)
 crop=image[t:b,l:r]
 if crop.size==0 or r-l<3 or b-t<4:return False,{}
 gray=cv2.cvtColor(crop,cv2.COLOR_BGR2GRAY)
 contrast=float(np.percentile(gray,90)-np.percentile(gray,10))
 _,binary=cv2.threshold(gray,0,255,cv2.THRESH_BINARY+cv2.THRESH_OTSU)
 if np.mean(binary>0)>.5:binary=255-binary
 n,_,stats,_=cv2.connectedComponentsWithStats(binary,8)
 chars=sum(c.isalnum() for c in text)
 components=sum(1 for a in stats[1:] if a[4]>=2 and a[3]>=max(3,h*.25) and a[2]<=max(h*1.8,w*.45))
 return contrast>=30 and components>=max(2,chars*.35),dict(sourceContrast=contrast,glyphComponents=components,characters=chars)

def recover(image,blocks,engine):
 started=time.perf_counter();items=proposals(image,blocks)
 trace=dict(contract=CONTRACT,proposedRegions=len(items),maxRegions=MAX_REGIONS,maxInputPixels=MAX_PIXELS,
            processedRegions=0,inputPixels=0,added=0,replaced=0,globalThresholdChanged=False,trace=[])
 result=list(blocks)
 previous=(engine.use_det,engine.use_cls,engine.use_rec,engine.text_rec.enabled)
 try:
  for proposal in items[:MAX_REGIONS]:
   x,y,w,h=proposal['rect'];scale=proposal['scale'];oriented='warpSize' in proposal
   area=int(np.prod(proposal['warpSize'])) if oriented else w*h*scale*scale
   row=dict(proposal,accepted=[],rejected=[]);trace['trace'].append(row)
   if trace['inputPixels']+area>MAX_PIXELS:row['reason']='INPUT_PIXEL_BUDGET';continue
   crop=cv2.warpAffine(image,np.array(proposal['cropToSource'],dtype=np.float32),tuple(proposal['warpSize']),flags=cv2.INTER_LINEAR|cv2.WARP_INVERSE_MAP) if oriented else image[y:y+h,x:x+w].copy()
   if scale!=1:crop=cv2.resize(crop,None,fx=scale,fy=scale,interpolation=cv2.INTER_NEAREST)
   trace['processedRegions']+=1;trace['inputPixels']+=area
   row['cropToSource']=proposal['cropToSource'] if oriented else [[1/scale,0,x],[0,1/scale,y],[0,0,1]]
   row['sourceToCrop']=cv2.invertAffineTransform(np.asarray(proposal['cropToSource'],dtype=float)).tolist() if oriented else [[scale,0,-x*scale],[0,scale,-y*scale],[0,0,1]]
   engine.text_rec.enabled=False
   with contextlib.redirect_stdout(sys.stderr):local=engine(crop,use_det=True,use_cls=False,use_rec=True)
   row['sourceStages']=engine.last_source_stages
   texts=[] if local.txts is None else local.txts;scores=[] if local.scores is None else local.scores;polys=[] if local.boxes is None else local.boxes
   if len(texts)==0 and 'LARGE_SOURCE' in proposal['reason']:
    # The recognizer expects a straight baseline. A source-only quadratic
    # baseline remap is reversible and can recover a curved word for which the
    # detector emitted no box. It never uses a dictionary or expected text.
    glyphs=np.asarray(proposal['sourceGlyphBoxes'],dtype=float)
    centers_x=glyphs[:,0]+glyphs[:,2]/2-x;centers_y=glyphs[:,1]+glyphs[:,3]/2-y
    fit=np.polyfit(centers_x,centers_y,2);curve=np.polyval(fit,np.arange(w));anchor=float(np.median(curve))
    error=float(np.median(np.abs(np.polyval(fit,centers_x)-centers_y)))
    if error<=float(np.median(glyphs[:,3]))*.3:
     mapx,mapy=np.meshgrid(np.arange(w,dtype=np.float32),np.arange(h,dtype=np.float32))
     mapy+=np.asarray(curve-anchor,dtype=np.float32)[None,:]
     straight=cv2.remap(crop,mapx,mapy,cv2.INTER_LINEAR,borderMode=cv2.BORDER_REPLICATE)
     with contextlib.redirect_stdout(sys.stderr):read=engine(straight,use_det=False,use_cls=False,use_rec=True)
     raw=[] if read.txts is None else list(read.txts);conf=[] if read.scores is None else list(read.scores)
     row['curvedSourceRecognition']=dict(raw=raw,scores=[float(v) for v in conf],baselinePolynomial=fit.tolist(),anchor=anchor,medianSourceFitError=error,
       inverse='sourceX=cropX+originX;sourceY=straightY+poly(cropX)-anchor+originY',sourceCropOrigin=[x,y])
     if len(raw)==1 and float(conf[0])>=.95 and len(norm(str(raw[0])))>=max(4,len(glyphs)*.75) and len(norm(str(raw[0])))<=len(glyphs)*1.6:
      # The padded recognition crop is not a source text rectangle. Return a
      # corridor around the observed glyphs, so downstream mask admission does
      # not acquire the empty concavity or the graphics crossed by the crop.
      ordered=sorted(glyphs.tolist(),key=lambda b:b[0])
      upper=[];lower=[]
      for gx,gy,gw,gh in ordered:
       upper.extend([[gx-x-1,gy-y-1],[gx+gw-x+1,gy-y-1]])
       lower.extend([[gx-x-1,gy+gh-y+1],[gx+gw-x+1,gy+gh-y+1]])
      texts=raw;scores=conf;polys=[np.array(upper+lower[::-1],dtype=float)]
   # A recognition-only local call changes RapidOCR's per-call mode; the next
   # region always states all three modes explicitly above.
   if not oriented and 'LARGE_SOURCE' not in proposal['reason']:
    from source_cell_detection import split_source_cells
    split_source_cells(image,result,texts,scores,polys,[x,y],scale,engine,trace,row)
   for text,score,poly in zip(texts,scores,polys):
    text=str(text).strip();score=float(score);local_poly=np.asarray(poly,dtype=float)
    poly=np.column_stack([local_poly,np.ones(len(local_poly))])@np.asarray(proposal['cropToSource']).T if oriented else local_poly/scale+[x,y]
    l,t=poly.min(axis=0);r,b=poly.max(axis=0);box=[float(l),float(t),float(r-l),float(b-t)]
    quantity=oriented and score>=.97 and re.fullmatch(r'[+\-]?\d+(?:[.,]\d+)?\s*[%％]',text) is not None
    if score<.90 or not quantity and (len(norm(text))<2 or sum(c.isalpha() for c in text)<2):continue
    if not oriented and 'LARGE_SOURCE' not in proposal['reason'] and ((x>0 and l<=x+1) or (x+w<image.shape[1] and r>=x+w-1)):
     row['rejected'].append(dict(text=text,score=score,box=box,reason='CROP_EDGE_CANNOT_PROVE_COMPLETE_SOURCE_WORD'));continue
    # Detector padding can overlap the row above. Equal source baselines, not
    # intersection alone, establish whether the candidate replaces that row.
    same=[old for old in result if abs(box[1]+box[3]/2-float(old['y'])-float(old['height'])/2)<=max(box[3],float(old['height']))*.36 and
          overlap(box,bounds(old))/max(1,min(box[2]*box[3],float(old['width'])*float(old['height'])))>.65]
    if any(norm(old['text'])==norm(text) for old in same):continue
    if oriented:
     ll,tt=local_poly.min(axis=0);rr,bb=local_poly.max(axis=0)
     if ll<=1 or tt<=1 or rr>=crop.shape[1]-1 or bb>=crop.shape[0]-1:continue
     supported,proof=glyph_support(crop,[ll,tt,rr-ll,bb-tt],text)
    else:supported,proof=glyph_support(image,box,text)
    if not supported:row['rejected'].append(dict(text=text,score=score,box=box,reason='SOURCE_GLYPH_SUPPORT_NOT_PROVED',**proof));continue
    # Existing different text needs a strict confidence improvement; a merged
    # detection spanning multiple cell labels is kept for structure recovery.
    if same:
     if len(same)!=1 or score<float(same[0].get('confidence') or 0)+.10 or overlap(box,bounds(same[0]))/max(1,float(same[0]['width'])*float(same[0]['height']))<.5:continue
     old=same[0];result.remove(old);trace['replaced']+=1
    else:old=None;trace['added']+=1
    identity=hashlib.sha256((text+'|'+','.join(f'{v:.1f}' for v in box)).encode()).hexdigest()[:14]
    added=dict(id='RC-'+identity,text=text,confidence=score,x=box[0],y=box[1],width=box[2],height=box[3],polygon=poly.tolist(),
               lineIndex=len(result),readingOrder=len(result)+1,coverageRecovery=CONTRACT)
    result.append(added);row['accepted'].append(dict(text=text,score=score,box=box,replaced=old,**proof))
 finally:
  engine.use_det,engine.use_cls,engine.use_rec,engine.text_rec.enabled=previous
 trace['totalMs']=(time.perf_counter()-started)*1000
 return result,trace
