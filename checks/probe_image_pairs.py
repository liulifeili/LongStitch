"""Inspect exact, informative image rows in screenshot pairs; does not modify inputs."""
import collections
import hashlib
import re
import sys
from pathlib import Path

import cv2
import numpy as np


def row_map(image):
    rows = collections.defaultdict(list)
    for y in range(300, min(2300, image.shape[0])):
        row = image[y, 180:1050:2, :3]
        if row.std() < 8:
            continue
        key = hashlib.blake2b(row.tobytes(), digest_size=8).digest()
        rows[key].append(y)
    return rows


def main():
    root = Path(sys.argv[1])
    if len(sys.argv) > 2:
        product = {}
        for line in Path(sys.argv[2]).read_text(encoding="utf-8-sig").splitlines():
            match = re.search(r"IMG_(\d+)\.PNG → IMG_(\d+)\.PNG\s+重叠\s+(\d+)", line)
            if match:
                product[int(match[1]), int(match[2])] = int(match[3])
        files = sorted(root.glob("IMG_*.PNG"))
        previous = None
        disagreements = []
        supported = 0
        for file in files:
            image = cv2.imread(str(file), cv2.IMREAD_COLOR)
            current = (int(file.stem[4:]), row_map(image))
            if previous:
                votes = collections.Counter()
                left, right = previous[1], current[1]
                for key in left.keys() & right.keys():
                    if len(left[key]) > 3 or len(right[key]) > 3:
                        continue
                    for ay in left[key]:
                        for by in right[key]:
                            if 0 < ay - by < image.shape[0]:
                                votes[image.shape[0] - (ay - by)] += 1
                ranked = votes.most_common(2)
                if ranked and ranked[0][1] >= 24 and (len(ranked) == 1 or ranked[0][1] > 2 * ranked[1][1]):
                    supported += 1
                    actual = product.get((previous[0], current[0]))
                    if actual != ranked[0][0]:
                        disagreements.append((previous[0], current[0], actual, ranked))
            previous = current
        print(f"supported={supported}, disagreements={len(disagreements)}")
        for entry in disagreements:
            print(entry)
        return
    geometry = {(8636, 8637): (986, 320, 261), (8641, 8642): (718, 319, 273),
                (8642, 8643): (761, 320, 261), (8645, 8646): (679, 320, 261),
                (8651, 8652): (841, 313, 290)}
    for first, second in [(8636, 8637), (8641, 8642), (8642, 8643),
                          (8645, 8646), (8650, 8651), (8651, 8652)]:
        a = cv2.imread(str(root / f"IMG_{first}.PNG"), cv2.IMREAD_COLOR)
        b = cv2.imread(str(root / f"IMG_{second}.PNG"), cv2.IMREAD_COLOR)
        left, right = row_map(a), row_map(b)
        votes = collections.Counter()
        for key in left.keys() & right.keys():
            if len(left[key]) > 3 or len(right[key]) > 3:
                continue
            for ay in left[key]:
                for by in right[key]:
                    if 0 < ay - by < a.shape[0]:
                        votes[ay - by] += 1
        print(f"{first}->{second}: " + ", ".join(
            f"offset={delta} overlap={a.shape[0]-delta} rows={count}"
            for delta, count in votes.most_common(8)))
        print("edge average differences:", [round(float(np.mean(np.abs(
            a[y:y+40].astype(np.int16)-b[y:y+40].astype(np.int16)))), 1)
            for y in (0, 40, 80, 120, 160, 200, 240, 280, 320, 2150, 2200, 2250, 2300, 2350, 2400, 2450)])
        if (first, second) in geometry:
            overlap, top, bottom = geometry[first, second]
            cut = top + (overlap - bottom - top) // 2
            offset = a.shape[0] - overlap
            diff = np.abs(a[offset+cut-10:offset+cut+10].astype(np.int16)
                          - b[cut-10:cut+10].astype(np.int16))
            print(f"cut={cut}, 20-row mean delta={diff.mean():.3f}, "
                  f"pixels >12={(diff.max(axis=2)>12).mean():.4%}")


if __name__ == "__main__":
    main()
