#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
独立验证器：不依赖 LongStitch 的任何代码，单独证明"成品长图拼对了"。

为什么需要它：
  电池、--stitch 工装、GDI+ 交叉核对，全都跑在我们自己的 C# 工程里。
  它们能证明"工程内部自洽"，证明不了"工程对外是对的"——
  万一布局公式、裁边换算、导出取数三处一起错成同一个样子，内部核对全绿而成品是错的。
  本脚本用**纯 Python 标准库**（zlib + 手写 PNG 反滤波）重新解一遍源图与成品，
  自己找重叠、自己推画布布局，再逐带比对成品像素。两套实现零共享代码，
  同时错成同一个样子的概率可以忽略。

它做什么（全部自己算，不读工程的任何输出）：
  1. 解 test/ 下每张 PNG（8 位真彩色）；
  2. 对每对相邻图，用"粗扫 + 细扫"独立找出滚动位移 d（= 后图原点在成品里相对前图的距离）；
  3. 独立找出顶部固定带高度 t（同坐标逐行比对，第一处结构性差异即带尾）；
  4. 推出成品布局：O_0 = 0，O_{i+1} = O_i + d_i；第 i 张图的第 r 行（r >= t）落在成品第 O_i + r - t 行；
  5. 解成品 PNG，逐带断言：
       · 成品[O_i, O_i+60)      == 第 i 张图[t, t+60)          （每段开头接对了）
       · 成品[O_{i+1}-60, O_{i+1}) == 第 i 张图对应行的前 60 行  （每条缝的上一侧接对了）
       · 成品末尾 60 行          == 末张图对应行                （结尾没截断）
  任何一带不等就退出码 1 并打印带号与首个差异坐标。

用法：
  python checks/verify_strip_independent.py <成品.png> <源图目录>
退出码 0 = 全部带一致。
"""

import sys
import zlib
import struct
import os


def read_png(path):
    """最小 PNG 解码：只支持 8 位真彩色/灰度（无隔行）。返回 (宽, 高, 行列表[bytes])。"""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} 不是 PNG")
    pos = 8
    idat = bytearray()
    width = height = bitdepth = colortype = None
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        ctype = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + length]
        if ctype == b"IHDR":
            width, height, bitdepth, colortype, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
            if interlace != 0:
                raise ValueError(f"{path} 是隔行 PNG，本验证器不处理")
        elif ctype == b"IDAT":
            idat.extend(chunk)
        pos += 12 + length
    if bitdepth != 8 or colortype not in (2, 6):
        raise ValueError(f"{path} 位深/色彩类型不受支持：{bitdepth}/{colortype}")
    src_channels = 4 if colortype == 6 else 3
    stride = width * src_channels
    raw = zlib.decompress(bytes(idat))
    rows = []
    prev = bytearray(stride)
    p = 0
    for _ in range(height):
        ft = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        if ft == 1:
            for x in range(src_channels, stride):
                line[x] = (line[x] + line[x - src_channels]) & 0xFF
        elif ft == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 0xFF
        elif ft == 3:
            for x in range(stride):
                a = line[x - src_channels] if x >= src_channels else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 0xFF
        elif ft == 4:
            for x in range(stride):
                a = line[x - src_channels] if x >= src_channels else 0
                b = prev[x]
                c = prev[x - src_channels] if x >= src_channels else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 0xFF
        elif ft != 0:
            raise ValueError(f"未知滤波类型 {ft}")
        if src_channels == 4:
            # 统一成 RGB：alpha 在这批图里恒为 255，留着只会让两套实现的步长对不上
            rgb = bytearray(width * 3)
            for x in range(width):
                rgb[x * 3] = line[x * 4]
                rgb[x * 3 + 1] = line[x * 4 + 1]
                rgb[x * 3 + 2] = line[x * 4 + 2]
            rows.append(bytes(rgb))
        else:
            rows.append(bytes(line))
        prev = line
    return width, height, 3, rows


TOLERANCE = 12   # 单通道允许的最大偏差：真机截图背景有 1-4 级有损噪声


def band_diff(rows_a, ya, rows_b, yb, nrows, channels, step=1):
    """两段的"结构差异"像素计数（单像素最大通道偏差 > TOLERANCE 才计，每 step 列取一列）。"""
    width = len(rows_a[ya]) // channels
    diff = 0
    for k in range(nrows):
        ra, rb = rows_a[ya + k], rows_b[yb + k]
        for x in range(0, width, step):
            s = x * channels
            d = max(abs(ra[s] - rb[s]), abs(ra[s + 1] - rb[s + 1]), abs(ra[s + 2] - rb[s + 2]))
            if d > TOLERANCE:
                diff += 1
    return diff


def find_top_band(rows_list, channels):
    """独立找顶部固定带：同坐标逐行比相邻两张，第一处"整行结构性不同"即带尾。"""
    height = min(len(r) for r in rows_list)
    width = len(rows_list[0][0]) // channels
    budget = width // 8          # 允许时钟等小图标差异
    for y in range(0, min(600, height)):
        bad = 0
        for i in range(len(rows_list) - 1):
            if band_diff(rows_list[i], y, rows_list[i + 1], y, 1, channels, step=2) > budget:
                bad += 1
        if bad * 2 > len(rows_list) - 1:
            return y
    return 0


def bottom_band(rows_list, channels):
    """独立找底部固定带高度（从下往上，同坐标结构性不同的第一处即带首）。"""
    height = min(len(r) for r in rows_list)
    width = len(rows_list[0][0]) // channels
    budget = width * 15 // 100
    for k in range(0, min(600, height)):
        y = height - 1 - k
        bad = 0
        for i in range(len(rows_list) - 1):
            if band_diff(rows_list[i], y, rows_list[i + 1], y, 1, channels, step=2) > budget:
                bad += 1
        if bad * 2 > len(rows_list) - 1:
            return k
    return 0


def nonflat_ratio(rows, y, nrows, channels):
    """窗口里"有结构"的行占比：行内采样点极差 > 8 才算有结构。
    纯白/纯灰的行在任何位移下都互相匹配，拿它们当探针会选出错误的 d。"""
    width = len(rows[y]) // channels
    good = 0
    for k in range(nrows):
        r = rows[y + k]
        lo, hi = 255, 0
        for x in range(0, width, 12):
            v = (r[x * channels] + r[x * channels + 1] + r[x * channels + 2]) // 3
            lo = min(lo, v)
            hi = max(hi, v)
        if hi - lo > 8:
            good += 1
    return good / nrows


def find_offset(rows_a, rows_b, height, channels, bottom_est):
    """
    独立找滚动位移。探针窗口自上而下自选：
      · 必须落在 a 的内容区（底部固定带之上）；
      · 窗口自身要有足够结构（聊天里的长空白段在任何位移下都"匹配"，会选出错误的 d）；
      · 残差必须为 0 且 ±4 行更差，否则换下一个窗口重试。
    """
    # 探针长度自适应：重叠小的缝（本组数据最窄的互内容只有 139 行）塞不下长探针
    for probe_n in (150, 100, 60, 40):
      y = height - bottom_est - probe_n - 4
      while y > 400:
        if nonflat_ratio(rows_a, y, probe_n, channels) >= 0.6:
            best, best_d = None, None
            for d in range(500, min(2300, y), 4):
                yb = y - d
                sc = band_diff(rows_a, y, rows_b, yb, probe_n, channels, step=8)
                if best is None or sc < best:
                    best, best_d = sc, d
            if best_d is None:
                y -= 60
                continue
            fb, fd = None, None
            for d in range(max(500, best_d - 6), min(2300, best_d + 7)):
                yb = y - d
                if yb < 0:
                    continue
                sc = band_diff(rows_a, y, rows_b, yb, probe_n, channels, step=2)
                if fb is None or sc < fb:
                    fb, fd = sc, d
            amb = []
            if fb == 0:
                for d in (fd - 4, fd + 4):
                    yb = y - d
                    if yb >= 0 and band_diff(rows_a, y, rows_b, yb, probe_n, channels, step=2) <= fb:
                        amb.append(d)
            if fb == 0 and not amb:
                # 小探针定位成功后向上下两侧生长：窗口越长结论越硬。
                # 互内容只有百余行的缝（两张之间页面变过）靠长探针永远探不进：
                # 例如 IMG_8049→8050，a 的内容尾行与 b 的头部固定带把互内容夹在
                # 中间，40 行探针放得下、100 行放不下。此时以 4 行为步长向两侧
                # 生长，只要"残差保持 0 的总窗口 ≥100 行"即认为结论足够硬。
                up = 0
                while y - up - 4 >= 0 and y - up - 4 - fd >= 0:
                    if band_diff(rows_a, y - up - 4, rows_b, y - up - 4 - fd,
                                 probe_n + up + 4, channels, step=2) != 0:
                        break
                    up += 4
                down = 0
                while y + probe_n + down + 4 <= len(rows_a):
                    if band_diff(rows_a, y - up, rows_b, y - up - fd,
                                 probe_n + up + down + 4, channels, step=2) != 0:
                        break
                    down += 4
                if probe_n + up + down >= 100:
                    return fd, fb, amb
        y -= 60
    return None, None, None


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    strip_path, src_dir = sys.argv[1], sys.argv[2]
    files = sorted(f for f in os.listdir(src_dir) if f.lower().endswith(".png"))
    if len(files) < 2:
        print(f"{src_dir} 下 PNG 不足 2 张")
        return 2

    print(f"独立验证：{len(files)} 张源图 + 成品 {strip_path}")
    srcs = [read_png(os.path.join(src_dir, f)) for f in files]
    width, height, channels, _ = srcs[0]
    if any(s[0] != width or s[1] != height or s[2] != channels for s in srcs):
        print("源图尺寸/通道不一致，本验证器只处理同尺寸连拍")
        return 2

    all_rows = [s[3] for s in srcs]
    t = find_top_band(all_rows, channels)
    print(f"  独立测得顶部固定带 t = {t} 行（仅用于找位移时的避让；成品实际裁量下面从成品反标）")

    offsets = []
    for i in range(len(srcs) - 1):
        bb = bottom_band([srcs[i][3], srcs[i + 1][3]], channels)
        d, score, amb = find_offset(srcs[i][3], srcs[i + 1][3], height, channels, bb)
        if d is None or score != 0 or amb:
            print(f"  ✗ {files[i]} → {files[i + 1]} 独立位移不可信：d={d} 残差 {score} 歧义 {amb}")
            return 1
        offsets.append(d)
        print(f"  {files[i]} → {files[i + 1]}  独立位移 d = {d}（残差 {score}，±4 行均更差）")

    origins = [0]
    for d in offsets:
        origins.append(origins[-1] + d)

    sw, sh, sch, srows = read_png(strip_path)
    best_t, best_s = None, None
    for cand in range(max(0, t - 60), min(height - 60, t + 60)):
        sval = band_diff(srows, 0, srcs[0][3], cand, 60, channels)
        if best_s is None or sval < best_s:
            best_s, best_t = sval, cand
    if best_s != 0:
        print(f"  ✗ 无法从成品反标顶带：最接近的 t={best_t} 残差 {best_s}")
        return 1
    print(f"  从成品反标得实际裁掉的顶带 t' = {best_t} 行（独立测量 {t} 行，差 {abs(best_t - t)} 行）")
    t = best_t
    if sw != width or sch != channels:
        print(f"成品宽/通道 {sw}/{sch} 与源图 {width}/{channels} 不一致")
        return 1
    # 成品高度 = 各段原点累计 + 末图内容高度。末图内容 = 全高 − 顶带 t' − 底部固定带。
    # 底带要单独量（末图 vs 前一张），不能省略：末图底部 261 行是微信输入栏，成品里被裁掉。
    bb_last = bottom_band([srcs[-2][3], srcs[-1][3]], channels)
    expect_h = origins[-1] + (height - t) - bb_last
    print(f"  独立测得末图底部固定带 {bb_last} 行")
    print(f"  独立推得成品高度应为 {expect_h}，实际 {sh}")
    if sh != expect_h:
        print("  ✗ 高度不符")
        return 1

    failures = 0
    checked = 0
    for i, (w, h, ch, rows) in enumerate(srcs):
        y_strip = origins[i]
        y_src = t
        if band_diff(srows, y_strip, rows, y_src, 60, channels) != 0:
            print(f"  ✗ 第 {i + 1} 段开头：成品[{y_strip}] 与源图第 {y_src} 行起 60 行不一致")
            failures += 1
        checked += 1
        if i + 1 < len(srcs):
            y_strip2 = origins[i + 1] - 60
            y_src2 = t + (origins[i + 1] - origins[i]) - 60
            if band_diff(srows, y_strip2, rows, y_src2, 60, channels) != 0:
                print(f"  ✗ 第 {i + 1}→{i + 2} 条缝上侧：成品[{y_strip2}] 与源图第 {y_src2} 行起 60 行不一致")
                failures += 1
            checked += 1
    y_strip_end = sh - 60
    y_src_end = t + (sh - origins[-1]) - 60
    if band_diff(srows, y_strip_end, srcs[-1][3], y_src_end, 60, channels) != 0:
        print(f"  ✗ 成品末尾：成品[{y_strip_end}] 与末张源图第 {y_src_end} 行起 60 行不一致")
        failures += 1
    checked += 1

    print(f"  共核对 {checked} 带 × 60 行 × {width} 列，不一致 {failures} 带")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
