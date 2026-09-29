"""Source-supported local surfaces. No fixture, text, model output or reference input."""
import cv2
import numpy as np
from bounded_palette import prove_palette

def supported_surfaces(source, authority, excluded):
    h,w=authority.shape
    count,labels,stats,_=cv2.connectedComponentsWithStats(excluded.astype(np.uint8),8)
    result=source.copy()
    owned=np.zeros((h,w),bool)
    records=[]
    for label in range(1,count):
        x,y,ww,hh,area=map(int,stats[label])
        own=authority[y:y+hh,x:x+ww] & (labels[y:y+hh,x:x+ww]==label)
        if int(own.sum())<8:continue
        margin=max(10,min(48,int(min(ww,hh)*.32)))
        l=max(0,x-margin);r=min(w,x+ww+margin)
        t=max(0,y-margin);b=min(h,y+hh+margin)
        rgb=source[t:b,l:r].astype(np.float32)
        valid=~excluded[t:b,l:r]
        yy,xx=np.indices(valid.shape)
        # Evidence surrounds the observation rather than sampling generated pixels.
        outer=(xx<x-l)|(xx>=x+ww-l)|(yy<y-t)|(yy>=y+hh-t)
        valid &= outer
        n=int(valid.sum())
        record=dict(bounds=[x,y,ww,hh],authorityPixels=int(own.sum()),samplePixels=n,
                    accepted=False,reason="INSUFFICIENT_SURFACE_SUPPORT")
        records.append(record)
        if n<max(100,min(1200,int(own.sum()*.12))):continue
        values=rgb[valid]
        quant=(values.astype(np.int32)//8)
        codes=quant[:,0]*1024+quant[:,1]*32+quant[:,2]
        mode=int(np.bincount(codes,minlength=32768).argmax())
        center=np.median(values[codes==mode],axis=0)
        near=np.max(np.abs(rgb-center),axis=2)<=18
        support=valid & near
        fraction=float(support.sum()/n)
        record["supportFraction"]=fraction
        if fraction<.78:
            # A surrounding ring can cross a card edge. Admit a narrower material
            # only when a connected original-color surface encloses this authority.
            proof,reason=prove_palette(rgb,excluded[t:b,l:r],own,(x-l,y-t,ww,hh),center)
            if proof is not None:
                inside=proof.pop("inside");pred=proof.pop("prediction");write=own & inside
                result[y:y+hh,x:x+ww][write]=pred[write]
                owned[y:y+hh,x:x+ww][write]=True
                record.update(accepted=True,reason="CONNECTED_SOURCE_SURFACE_ENCLOSES_AUTHORITY",
                    mode="SOURCE_ENCLOSED_PALETTE_OR_PLANE",sourceComponentProof=proof)
            else:
                record.update(reason="MIXED_OR_TEXTURED_SURROUND",componentRejection=reason)
            continue
        sides=[valid&(xx<x-l),valid&(xx>=x+ww-l),valid&(yy<y-t),valid&(yy>=y+hh-t)]
        side_rates=[float((s&near).sum()/max(1,s.sum())) for s in sides]
        record["sideSupport"]=side_rates
        if sum(rate>=2./3. for rate in side_rates)<3:
            record["reason"]="MATERIAL_BOUNDARY_OR_ONE_SIDED_SUPPORT";continue
        # Fit a plane from independent source samples, then reject heterogeneous residuals.
        sx=(xx-(x-l+ww/2))/max(1,ww)
        sy=(yy-(y-t+hh/2))/max(1,hh)
        coords=np.stack([np.ones_like(sx),sx,sy],axis=2)
        a=coords[support];v=rgb[support]
        if len(a)>12000:
            take=np.linspace(0,len(a)-1,12000,dtype=int);a=a[take];v=v[take]
        coeff=np.linalg.lstsq(a,v,rcond=None)[0]
        errors=np.max(np.abs(a@coeff-v),axis=1)
        p95=float(np.percentile(errors,95));record["sourceFitError95"]=p95
        if p95>3.5:
            record["reason"]="NON_PLANAR_SOURCE_MATERIAL";continue
        pred=coords@coeff
        corners=pred[[0,0,-1,-1],[0,-1,0,-1]]
        if np.max(np.ptp(corners,axis=0))>36:
            record["reason"]="EXCESSIVE_EXTRAPOLATION";continue
        # Exact palette preservation on uniform pixel-art paper, no gray blur patch.
        if np.percentile(np.max(np.abs(v-center),axis=1),95)<=2:
            pred[:]=center
            record["mode"]="SOURCE_CONSTANT_SURFACE"
        else:
            record["mode"]="SOURCE_GRADIENT_SURFACE"
        local_pred=np.rint(np.clip(pred[y-t:y-t+hh,x-l:x-l+ww],0,255)).astype(np.uint8)
        result[y:y+hh,x:x+ww][own]=local_pred[own]
        owned[y:y+hh,x:x+ww][own]=True
        record.update(accepted=True,reason="THREE_SIDED_SOURCE_SURFACE_PROVED",
                      rgbCenter=center.tolist())
    return result,owned,records
