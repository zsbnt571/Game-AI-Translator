"""Source-color component proof for a local surface crossing an outer sampling ring."""
import cv2,numpy as np
def prove_palette(rgb,excluded,own,offset,center):
 x,y,w,h=offset
 near=np.max(abs(rgb.astype(np.float32)-center),axis=2)<=6
 count,labels,stats,_=cv2.connectedComponentsWithStats(near.astype(np.uint8),8)
 yy,xx=np.indices(near.shape);outer=(xx<x)|(xx>=x+w)|(yy<y)|(yy>=y+h)
 clean=~excluded & outer
 weights=np.bincount(labels[clean&near],minlength=count)
 if len(weights)<=1:return None,'NO_CONNECTED_SOURCE_SUPPORT'
 weights[0]=0;lab=int(weights.argmax());component=labels==lab
 # Two intersecting source-color boundaries prove a bounded surface corner.
 # An unbounded page or a horizontal row strip does not establish that owner.
 edges=[bool(component[:,0].any()),bool(component[:,-1].any()),bool(component[0,:].any()),bool(component[-1,:].any())]
 if all(edges[:2]) or all(edges[2:]):return None,'NO_INTERSECTING_SOURCE_MATERIAL_BOUNDARIES'
 if weights[lab]<1200:return None,'INSUFFICIENT_INDEPENDENT_PALETTE_SAMPLES'
 # Fill only holes fully enclosed by a connected source-color surface.
 mask=np.pad(component.astype(np.uint8),1);flood=mask.copy();cv2.floodFill(flood,None,(0,0),1)
 filled=(mask| (1-flood))[1:-1,1:-1].astype(bool)
 part=filled[y:y+h,x:x+w]
 coverage=float(part[own].mean());rectcoverage=float(part.mean())
 if coverage<.995 or rectcoverage<.98:return None,'SOURCE_MATERIAL_DOES_NOT_ENCLOSE_AUTHORITY'
 if component.sum()/max(1,filled.sum())<.78:return None,'SURFACE_OCCUPANCY_TOO_LOW'
 narrow=8
 sides=[(xx>=x-narrow)&(xx<x)&(yy>=y)&(yy<y+h), (xx>=x+w)&(xx<x+w+narrow)&(yy>=y)&(yy<y+h), (yy>=y-narrow)&(yy<y)&(xx>=x)&(xx<x+w), (yy>=y+h)&(yy<y+h+narrow)&(xx>=x)&(xx<x+w)]
 support=[float((a&component&~excluded).sum()/max(1,(a&~excluded).sum())) for a in sides]
 if sum(v>=.8 for v in support)<3:return None,'LESS_THAN_THREE_ADJACENT_SOURCE_SIDES'
 support_mask=clean&filled
 coords=np.stack([np.ones_like(xx),(xx-x-w/2)/w,(yy-y-h/2)/h],axis=2)
 a=coords[support_mask];evidence=rgb[support_mask].astype(float)
 take=np.linspace(0,len(a)-1,min(12000,len(a)),dtype=int);a=a[take];v=evidence[take]
 heldout=np.arange(len(a))%5==0;coef=np.linalg.lstsq(a[~heldout],v[~heldout],rcond=None)[0]
 err=np.max(abs(a[heldout]@coef-v[heldout]),axis=1)
 if np.percentile(err,95)>3.5:return None,'SOURCE_PLANE_RESIDUAL'
 pred=coords@coef;corners=pred[[0,0,-1,-1],[0,-1,0,-1]]
 if np.max(np.ptp(corners,axis=0))>36:return None,'SOURCE_PLANE_EXTRAPOLATION'
 if np.percentile(np.max(abs(v-center),axis=1),95)<=2:pred[:]=center
 return dict(coverage=coverage,rectCoverage=rectcoverage,sideSupport=support,sourceBoundaryDirections=[name for name,touch in zip(['left','right','top','bottom'],edges) if not touch],independentSamples=len(evidence),componentPixels=int(component.sum()),filledPixels=int(filled.sum()),residual95=float(np.percentile(err,95)),color=center.tolist(),inside=part,prediction=np.rint(np.clip(pred[y:y+h,x:x+w],0,255)).astype(np.uint8)),''
