"""Compare the official isolated WOLF fixture's unselected second text line.
The first menu row animates its selection color and is deliberately excluded.
This verifies real engine-submitted pixels, not a mocked renderer.
"""
from pathlib import Path
import json,sys,hashlib
from PIL import Image
folder=Path(sys.argv[1]);data=json.loads((folder/'result.json').read_text(encoding='utf-8'))
captures=[r['result']for r in data['rows']if r.get('op')=='testScreen']
assert len(captures)in(9,12) and all(r.get('ok')and r.get('privateDesktop')and not r.get('screenCapture')for r in captures)
roi=tuple(map(int,sys.argv[2].split(',')))if len(sys.argv)>2 else (224,350,401,375)
images=[Image.open(r['path']).convert('RGB').crop(roi).tobytes()for r in captures]
def difference(a,b):return sum(a[i:i+3]!=b[i:i+3]for i in range(0,len(a),3))
checks=[]
def check(name,value,**evidence):checks.append(dict(name=name,passed=bool(value),**evidence))
changed=difference(images[1],images[2])
check('custom-font-changes-engine-text-pixels',changed>0,changedPixels=changed)
check('wrong-hash-retains-current-pixels',images[2]==images[3],changedPixels=difference(images[2],images[3]))
for n in (4,6,8):check(f'default-restore-cycle-{(n-2)//2}',images[1]==images[n],changedPixels=difference(images[1],images[n]))
for n in (5,7):check(f'custom-repeat-cycle-{(n-1)//2}',images[2]==images[n],changedPixels=difference(images[2],images[n]))
font_ops=[r for r in data['rows']if r.get('op')=='fontPreferences']
check('six-successful-native-cache-transactions-and-one-rejection',len(font_ops)==7 and sum(r['ok']for r in font_ops)==6)
check('all-native-font-caches-cleared-on-each-success',font_ops[-1]['result']['fontCache']['nativeCachesCleared']==18)
states=[r['result']for r in data['rows']if r.get('op')=='fontState']
check('translation-toggle-does-not-reset-custom-font',states[2]['customFontActive'])
if len(captures)==12:
 check('real-original-menu-translation-changes-pixels',images[9]!=images[10],changedPixels=difference(images[9],images[10]))
 check('disable-restores-real-original-menu-pixels',images[9]==images[11],changedPixels=difference(images[9],images[11]))
 applied=[r['result']for r in data['rows']if r.get('op')=='translationApply']
 runtime=[r['result']for r in data['rows']if r.get('op')=='translationState']
 check('real-source-pair-stored-and-displayed',applied[-1]['stored']==1 and runtime[-2]['cacheExactHits']>0)
 check('native-layout-never-failed-a-writeback',all(r['writebacksFailed']==0 for r in runtime))
report=dict(scope='Official WOLF sample '+data['version']+'; SoftModeFlag=1; exact native cache contract; own process GDI presentation pixels on non-input desktop. No original game or save used.',
            roi=list(roi),passed=all(c['passed']for c in checks),checks=checks,
            frames=[dict(path=r['path'],sha256=hashlib.sha256(Path(r['path']).read_bytes()).hexdigest())for r in captures])
(folder/'pixel-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(f'{sum(c["passed"]for c in checks)}/{len(checks)} real-WOLF pixel/lifecycle checks passed')
raise SystemExit(0 if report['passed']else 1)
