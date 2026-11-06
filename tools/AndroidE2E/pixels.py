"""screencap 원본(raw RGBA) 의 가운데 부분 밝기 — 화면 보호(FLAG_SECURE) 검사용.
쓰기: python3 -I pixels.py <raw 파일> → "max=<가장 밝은 값> bright=<밝은 화소 수> total=<화소 수>"
상태 표시줄·탐색 막대(시스템 화면, 가려지지 않는다)는 빼고 위아래 15% 안쪽만 본다."""
import struct
import sys

data = open(sys.argv[1], 'rb').read()
w, h = struct.unpack_from('<II', data, 0)
header = len(data) - w * h * 4
if header < 12 or header > 32:
    sys.exit('unexpected screencap size %d for %dx%d' % (len(data), w, h))
pixels = memoryview(data)[header:]
top, bottom = int(h * 0.15), int(h * 0.85)
peak = bright = total = 0
for y in range(top, bottom, 2):
    row = pixels[y * w * 4:(y + 1) * w * 4]
    for x in range(0, w * 4, 8):
        v = max(row[x], row[x + 1], row[x + 2])
        total += 1
        if v > peak:
            peak = v
        if v > 60:
            bright += 1
print('max=%d bright=%d total=%d size=%dx%d' % (peak, bright, total, w, h))
