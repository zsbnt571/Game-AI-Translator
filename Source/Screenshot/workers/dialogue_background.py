"""Source-supported native dark dialogue field; mutation is predeclared by caller.

Fit only original pixels outside character/glow exclusion. Preserve native color,
edge lighting and pixels outside the source-connected glow footprint. No fixture
names, translated text, adjacent artwork, guessed black fill or model output.
"""
import cv2
import numpy as np

def enclosed_dialogue(source,authority,material,surfaces):
    result=source.copy();owned=np.zeros(authority.shape,bool);records=[]
    if material is None:return result,owned,records
    h,w=authority.shape
    for surface in surfaces or []:
        x,y,ww,hh=(int(surface[k]) for k in ('X','Y','Width','Height'))
        if min(x,y)<0 or min(ww,hh)<1 or x+ww>w or y+hh>h:raise ValueError('Invalid source dialogue domain')
        glyph=material[y:y+hh,x:x+ww];own=authority[y:y+hh,x:x+ww]
        if not own.any():continue
        rgb=source[y:y+hh,x:x+ww]
        height=float(surface['LineHeight']);radius=int(surface['GlowRadius'])
        distance=cv2.distanceTransform(np.uint8(~glyph),cv2.DIST_L2,cv2.DIST_MASK_PRECISE)
        yy,xx=np.indices((hh,ww));xx=xx.astype(float);yy=yy.astype(float);u=xx/ww;v=yy/hh
        basis=[np.ones((hh,ww)),u,v,u*u,u*v,v*v]
        for edge_distance in (xx,ww-1-xx,yy,hh-1-yy):
            for scale in (height*.1,height*.2,height*.4):basis.append(np.exp(-edge_distance/max(1,scale)))
        basis=np.stack(basis,axis=-1)
        valid=(distance>height*.15)&(xx%3==0)&(yy%3==0)
        a=basis[valid];values=rgb[valid].astype(float)
        record=dict(bounds=[x,y,ww,hh],mode='SOURCE_VALIDATED_LOW_FREQUENCY_DIALOGUE_FIELD',
            sourceRows=surface.get('SourceIds',[]),glyphPixels=int(glyph.sum()),authorityPixels=int(own.sum()),
            panelPixels=ww*hh,samplePixels=len(a),accepted=False,context='original source inside frame only')
        records.append(record)
        if len(a)<500:record['reason']='INSUFFICIENT_INDEPENDENT_SURFACE';continue
        # Hold out every fifth source sample. Texture and high-contrast art must
        # not be certified simply because a fit can interpolate training points.
        train=np.arange(len(a))%5!=0;aa=a[train];vv=values[train];weights=np.ones(len(aa))
        for _ in range(6):
            coeff=np.linalg.lstsq(aa*weights[:,None],vv*weights[:,None],rcond=None)[0]
            error=np.max(abs(aa@coeff-vv),axis=1);weights=np.minimum(1,3/np.maximum(error,1e-6))
        error=np.max(abs(a[~train]@coeff-values[~train]),axis=1)
        median=float(np.percentile(error,50));p90=float(np.percentile(error,90))
        record.update(heldOutMedianError=median,heldOutError90=p90)
        if median>5 or p90>12:record['reason']='SOURCE_NOT_PROVED_LOW_FREQUENCY';continue
        prediction=np.clip(basis@coeff,0,255)
        fade=np.clip((radius-distance)/max(1,radius*.36),0,1);fade=fade*fade*(3-2*fade)
        candidate=np.rint(np.clip(rgb*(1-fade[...,None])+prediction*fade[...,None],0,255)).astype(np.uint8)
        result[y:y+hh,x:x+ww][own]=candidate[own]
        owned[y:y+hh,x:x+ww][own]=True
        record.update(accepted=True,reason='INDEPENDENT_SOURCE_FIELD_AND_GLYPH_GLOW_SUPPORT',
            unmaskedChangedPixels=int(np.any(candidate!=rgb,axis=2)[~own].sum()),wholePanelBlur=False)
    return result,owned,records
