"""Cross-checks the QR code generator and reader against independent programs.

Setup (once):   py -3.14 -m venv .venv-qr  &&  .venv-qr\Scripts\pip install segno zxing-cpp pillow
Run:            npm run test:qr    (builds the tests)   then   .venv-qr\Scripts\python scripts\qr-cross-check.py

segno (an independent generator) makes codes that Ledjoro must read, including rotated ones; zxing-cpp (an independent professional reader)
must read the codes Ledjoro makes. Neither is part of Ledjoro.
"""
import os, shutil, subprocess, sys, glob
import segno, zxingcpp
from PIL import Image
d = os.path.join(os.environ.get('TEMP', '.'), 'ledjoro-qr-cross'); shutil.rmtree(d, ignore_errors=True); os.makedirs(d)
texts = ['hello', 'ledjoro:recover?v=1&c=Birchwood%20Design%20Studio%20LLC&u=selftest&k=7K2QF-9XMWB-C4HTN-8VR3D', 'A' * 40, 'x' * 100, 'Caf\u00e9 \u00dcmlaut | 12345', 'ledjoro:recover?v=1&c=Acme&u=owner&k=ABCDE-FGHJK-MNPQR-STVWX']
n = 0
for t in texts:
    for scale in (5, 9):
        q = segno.make(t, error='m', micro=False, boost_error=False, encoding='utf-8', mode='byte')
        if q.version > 10: continue
        p = os.path.join(d, f'ext{n:02d}.png'); q.save(p, scale=scale, border=4)
        open(os.path.join(d, f'ext{n:02d}.txt'), 'w', encoding='utf-8').write(t); n += 1
# rotated / shrunk / paper-like variants of the external codes
for f in sorted(glob.glob(os.path.join(d, 'ext*.png')))[:6]:
    im = Image.open(f).convert('L')
    for ang in (20, 45, 90):
        canvas = Image.new('L', (im.width * 2, im.height * 2), 230); r = im.rotate(ang, expand=True, resample=Image.BICUBIC, fillcolor=230)
        canvas.paste(r, (im.width // 3, im.height // 3)); base = os.path.splitext(f)[0]
        out = f'{base}_rot{ang}.png'; canvas.save(out); open(out.replace('.png', '.txt'), 'w', encoding='utf-8').write(open(f.replace('.png', '.txt'), encoding='utf-8').read())
print('external images made:', len(glob.glob(os.path.join(d, '*.png'))))
exe = glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'native', 'QrTests', 'bin', 'Release', 'net48', 'QrTests.exe'))[0]
r = subprocess.run([exe, d], capture_output=True, text=True); print(r.stdout.strip())
# our codes, read by an independent reader
bad = 0
for f in sorted(glob.glob(os.path.join(d, 'ours', '*.png'))):
    expected = open(f.replace('.png', '.txt'), encoding='utf-8').read()
    res = zxingcpp.read_barcodes(Image.open(f))
    got = res[0].text if res else None
    ok = got == expected; bad += (not ok)
    print(('OK   ' if ok else 'FAIL ') + os.path.basename(f), '(zxing-cpp read it %s)' % ('correctly' if ok else 'as %r' % got))
sys.exit(1 if bad or r.returncode else 0)
