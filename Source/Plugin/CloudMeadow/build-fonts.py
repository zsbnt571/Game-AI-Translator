"""Build legacy TMP SDF fallback assets, without loading Unity or any game code."""
from pathlib import Path
import sys,json,gzip,hashlib,shutil,argparse,os
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source-root', default=os.environ.get('FUSION_FONT_BUILD_ROOT'), help='Local directory containing FontSource and optional Tools/font-build-python')
args=parser.parse_args()
if not args.source_root: parser.error('--source-root or FUSION_FONT_BUILD_ROOT is required')
phase=Path(args.source_root).resolve(strict=True)
sys.path.insert(0,str(phase/'Tools/font-build-python'))
import numpy as np
from scipy.ndimage import distance_transform_edt
from PIL import Image,ImageFont,ImageDraw
from fontTools.ttLib import TTFont
src=phase/'FontSource/NotoSansCJKsc-Regular.otf'
dest=Path(__file__).parent/'FontAssets';dest.mkdir(exist_ok=True)
font=ImageFont.truetype(str(src),36); cmap=TTFont(src).getBestCmap()
ranges=[(0x20,0x24f),(0x2000,0x206f),(0x3000,0x30ff),(0x3400,0x9fff),(0xac00,0xd7af),(0xff00,0xffef)]
chars=[n for n in sorted(cmap) if any(a<=n<=b for a,b in ranges)]
size=2048; cell=56; pad=7; spread=6;cols=size//cell;per=cols*cols
omitted=[n for n in chars if (lambda b: b[2]-b[0]+2*pad>cell or b[3]-b[1]+2*pad>cell)(font.getbbox(chr(n),anchor="ls"))]
chars=[n for n in chars if n not in omitted]
asc,desc=font.getmetrics(); glyphs=[];pages=[]
for start in range(0,len(chars),per):
 page=start//per;atlas=np.zeros((size,size),dtype=np.uint8)
 for i,n in enumerate(chars[start:start+per]):
  l,t,r,b=font.getbbox(chr(n),anchor='ls');w=r-l;h=b-t
  if w+2*pad>cell or h+2*pad>cell:raise ValueError(('glyph does not fit',n,w,h))
  tile=Image.new('L',(w+2*pad,h+2*pad));ImageDraw.Draw(tile).text((pad-l,pad-t),chr(n),font=font,fill=255,anchor='ls')
  mask=np.asarray(tile)>127
  sdf=np.zeros(mask.shape,dtype=np.uint8) if not mask.any() else np.clip(127.5+(distance_transform_edt(mask)-distance_transform_edt(~mask))*127.5/spread,0,255).astype(np.uint8)
  x=(i%cols)*cell;y=(i//cols)*cell;atlas[y:y+sdf.shape[0],x:x+sdf.shape[1]]=sdf
  glyphs.append([n,page,x+pad,y+pad,w,h,l,-t,round(font.getlength(chr(n)),3)])
 name=f'page-{page:02d}.sdf.gz'
 # Unity raw textures start at the bottom left; TMP's glyph rectangles use top-left coordinates.
 with (dest/name).open('wb') as out:
  with gzip.GzipFile(filename='',mode='wb',fileobj=out,mtime=0) as gz:gz.write(np.flipud(atlas).tobytes())
 pages.append({'file':name,'sha256':hashlib.sha256((dest/name).read_bytes()).hexdigest().upper()})
 print(f'Font build: page {page+1}, glyphs {min(start+per,len(chars))}/{len(chars)}',flush=True)
index={'schema':1,'omittedGlyphs':omitted,'name':'Fusion Cloud Fallback','size':size,'pointSize':36,'ascender':asc,'descender':-desc,'lineHeight':asc+desc,'spread':spread,'glyphs':glyphs,'pages':pages}
(dest/'font-index.json').write_text(json.dumps(index,separators=(',',':')),encoding='utf-8')
shutil.copy2(phase/'FontSource/OFL.txt',dest/'OFL.txt')
(dest/'SOURCE.txt').write_text('Source: https://github.com/notofonts/noto-cjk/tree/Sans2.004\nNotoSansCJKsc-Regular.otf SHA256: '+hashlib.sha256(src.read_bytes()).hexdigest().upper()+'\nDerived asset name: Fusion Cloud Fallback. Glyph atlas generated at build time; only missing glyph fallback pages are loaded at runtime.\n',encoding='utf-8')
