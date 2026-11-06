"""uiautomator dump(XML)에서 글자(text 또는 content-desc)가 맞는 첫 노드의 가운데 좌표를 찍는다.
쓰기: python3 -I uitap.py <ui.xml> <글자1> [글자2 ...] → "x y" (없으면 아무것도 찍지 않고 끝 코드 1)
글자 앞에 id: 를 붙이면 resource-id 로 찾는다."""
import re
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
wanted = sys.argv[2:]
for node in root.iter('node'):
    for want in wanted:
        if want.startswith('id:'):
            hit = node.get('resource-id', '') == want[3:]
        else:
            hit = want.lower() in (node.get('text', '') + '|' + node.get('content-desc', '')).lower()
        if hit:
            m = re.match(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', node.get('bounds', ''))
            if m:
                x1, y1, x2, y2 = map(int, m.groups())
                print((x1 + x2) // 2, (y1 + y2) // 2)
                sys.exit(0)
sys.exit(1)
