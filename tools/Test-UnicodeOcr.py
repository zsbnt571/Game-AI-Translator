"""Offline packaged-worker regression; run with the package's Python and site-packages."""
from pathlib import Path
import argparse, json, os, subprocess
import cv2
import numpy as np

parser = argparse.ArgumentParser()
parser.add_argument('--app', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
app, output = args.app.resolve(), args.output.resolve()
output.mkdir(parents=True, exist_ok=True)
temp = output/'临时 チェック'
temp.mkdir(exist_ok=True)
image = np.full((100, 480, 3), 255, np.uint8)
cv2.putText(image, 'OCR TEST 123', (18,66), cv2.FONT_HERSHEY_SIMPLEX, 1.5, (0,0,0), 3, cv2.LINE_AA)
encoded_ok, encoded = cv2.imencode('.png', image)
assert encoded_ok
cases = [dict(name='health-in-unicode-temp', request={'command':'check'}, kind='health')]
for label in ('ascii space', '中文截图', '日本語画像', '한국어 화면', '🎮 café'):
    path = output/label/'画面 test.png'
    path.parent.mkdir(exist_ok=True)
    path.write_bytes(encoded.tobytes())
    cases.append(dict(name=label+'-uncached-title-read', request={'command':'recover_stylized_title','imagePath':str(path),'blocks':[]}, kind='title'))
    cases.append(dict(name=label+'-recognition', request={'command':'recognize','imagePath':str(path)}, kind='recognize'))
for label, data in [('missing',None),('empty',b''),('invalid',b'not an image')]:
    path = output/(label+' 无效.png')
    if data is not None:
        path.write_bytes(data)
    cases.append(dict(name=label+'-handled', request={'command':'recognize','imagePath':str(path)}, kind='error'))
cases.append(dict(name='recognition-after-errors', request=cases[2]['request'], kind='recognize'))
env = os.environ.copy()
for key in list(env):
    if key.startswith(('DOTNET','PYTHON','ST_','FUSION_','COREHOST_')):
        env.pop(key)
env.update(PATH=r'C:\Windows\system32;C:\Windows', PYTHONPATH=str(app/'runtime/venv/Lib/site-packages'),
           PYTHONUTF8='1', PYTHONNOUSERSITE='1', PYTHONDONTWRITEBYTECODE='1',
           ST_FUSION_RUNTIME_ROOT=str(app/'runtime'), TEMP=str(temp), TMP=str(temp),
           HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1')
payload = ''.join(json.dumps(case['request'],ensure_ascii=False)+'\n' for case in cases) + '{"command":"shutdown"}\n'
process = subprocess.run([str(app/'runtime/python/python.exe'),'-u',str(app/'workers/rapid_worker.py')],
                         input=payload,text=True,encoding='utf-8',errors='replace',capture_output=True,
                         env=env,cwd=output,timeout=120,creationflags=0x08000000)
(output/'worker-stderr.txt').write_text(process.stderr,encoding='utf-8')
assert process.returncode == 0, process.returncode
observed = [json.loads(line) for line in process.stdout.splitlines() if line.strip()]
assert len(observed) == len(cases), (len(observed), len(cases))
results = []
for case, result in zip(cases, observed):
    kind = case['kind']
    if kind == 'health':
        passed = result.get('recognitionPassed') is True and result.get('downloadsAttempted') == 0
    elif kind == 'error':
        passed = result.get('engineStatus') == 'ERROR'
    elif kind == 'title':
        passed = result.get('engineStatus') == 'OK' and result.get('reason') == 'NO_VALID_OCR_HEIGHTS'
    else:
        text = ''.join(str(row.get('text','')) for row in result.get('blocks',[]))
        passed = ''.join(char for char in text.upper() if char.isalnum()) == 'OCRTEST123'
    results.append(dict(name=case['name'],passed=passed))
report = dict(passed=all(row['passed'] for row in results),checks=len(results),apiRequests=0,downloadsAttempted=0,cases=results)
(output/'result.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
assert report['passed']
