# -*- coding: utf-8 -*-
"""由原图生成莫版模板文件夹与 templates.json（含 len 字段）。

用法：直接运行本脚本（需 Python + numpy + Pillow）。
输入：Assets/原图 下的 PNG；y 必须为 26（允许 28，自动裁掉上下各一行像素），x 不超过 140，否则被拒绝。
输出：Assets/莫版模板/{灰,白,绿,蓝,紫}/*.png 与 templates.json。
同色同名的多张图（如 甜甜花 与 甜甜花(1)）只输出一个：取质量分最高者，其余跳过；
不同颜色的同名图（如 灰色甜甜花 与 绿色甜甜花）各自保留。
质量分 = 前景像素数 * 前景平均匹配度 / 全像素数（背景归零计 0）。
"""
import os
import re
import json
from collections import Counter

import numpy as np
from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))  # Assets/莫版模板
BASE = os.path.dirname(SCRIPT_DIR)                       # Assets
SRC = os.path.join(BASE, "原图", "sorted-final")  # 已筛选去重后的最终素材
OUT = SCRIPT_DIR                                          # Assets/莫版模板

SPEC_W, SPEC_H = 140, 26  # 标准尺寸；x 允许更小，y 必须等于 SPEC_H

# 5 种参考文字颜色（顺序即颜色文件夹顺序）
REFS = [
    ("灰", np.array([204, 204, 204])),
    ("绿", np.array([172, 255, 69])),
    ("蓝", np.array([79, 244, 255])),
    ("紫", np.array([249, 152, 255])),
    ("白", np.array([255, 255, 255])),
]
COLOR_NAMES = [n for n, _ in REFS]
T = 80.0
# 背景归零阈值：HSV 中 V 通道百分制 65（0-255 制 ≈166），V 低于此值的像素视为背景归零
VOTE_MIN_V = int(round(65.0 / 100.0 * 255))


def rgb_to_lab(rgb):
    """rgb: (...,3) uint8 -> lab (...,3) float"""
    c = rgb.astype(np.float64) / 255.0
    lin = np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    r, g, b = lin[..., 0], lin[..., 1], lin[..., 2]
    x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b
    y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b
    z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b
    xn, yn, zn = 0.95047, 1.0, 1.08883

    def f(t):
        d = 6.0 / 29.0
        return np.where(t > d ** 3, np.cbrt(t), t / (3 * d * d) + 4.0 / 29.0)

    fx, fy, fz = f(x / xn), f(y / yn), f(z / zn)
    ll = 116.0 * fy - 16.0
    aa = 500.0 * (fx - fy)
    bb = 200.0 * (fy - fz)
    return np.stack([ll, aa, bb], axis=-1)


ref_labs = {name: rgb_to_lab(rgb.reshape(1, 1, 3))[0, 0] for name, rgb in REFS}
ref_lab_matrix = np.stack([ref_labs[n] for n in COLOR_NAMES], axis=0)


def main():
    rejected = []     # 尺寸不符
    dropped = []      # 无亮像素
    candidates = {}   # (name, color) -> [候选 dict]（同色同名多图全部保留，最后择优；不同颜色各自保留）
    total = 0

    for root, dirs, files in os.walk(SRC):
        dirs.sort()
        for fn in sorted(files):
            if not fn.lower().endswith(".png"):
                continue
            total += 1
            fp = os.path.join(root, fn)

            with Image.open(fp) as im:
                w, h = im.size
                orig = f"{w}x{h}"
                if h == SPEC_H + 2:
                    # 140x28：裁掉上下各一行像素后按 140x26 处理
                    im = im.crop((0, 1, w, h - 1))
                    h -= 2
                if h != SPEC_H or w > SPEC_W:
                    rejected.append((fp, orig))
                    continue
                rgb = np.array(im.convert("RGB"))

            v = rgb.max(axis=-1)
            if int((v >= VOTE_MIN_V).sum()) == 0:
                dropped.append((fp, f"{w}x{h}", "no-v"))
                continue

            stem = os.path.splitext(fn)[0]
            # 归一化文件名，保证同名判断正确：
            # 1) 剥离 Windows 副本后缀 " (N)" 及 "_N"（连同其前导空格/下划线）；
            # 2) 去除尾随空格/下划线（剥离副本后缀后可能残留）。
            stem = re.sub(r"[ _]*\(\d+\)$", "", stem)
            stem = re.sub(r"[ _]*_\d+$", "", stem)
            stem = stem.rstrip(" _")
            name = stem

            grand = os.path.basename(os.path.dirname(root))
            parent = os.path.basename(root)
            is_z = grand == "Z获得物品与交互名称不一致"
            item_name = parent.strip("[]") if is_z else name

            # 颜色判定：V>=65% 中亮度前 30% 像素的平均色 -> 最近参考色
            lab = rgb_to_lab(rgb)
            vals = v[v >= VOTE_MIN_V]
            thr = np.percentile(vals, 70)  # 前 30% 最高亮
            core = v >= thr
            core_rgb = rgb[core].mean(axis=0)
            core_lab = rgb_to_lab(core_rgb.reshape(1, 1, 3))[0, 0]
            dists = np.linalg.norm(ref_lab_matrix - core_lab, axis=1)
            main_name = COLOR_NAMES[int(np.argmin(dists))]

            # 灰度化：向主颜色的匹配度
            dE = np.linalg.norm(lab - ref_labs[main_name], axis=2)
            match = np.clip(1.0 - dE / T, 0.0, 1.0) * 255.0
            match[v < VOTE_MIN_V] = 0.0  # 背景归零
            gray = match.astype(np.uint8)

            # 质量分 = 前景像素数 * 前景平均匹配度 / 全像素数
            # （即 sum(前景 match) / 全像素数，背景归零计 0）
            # 兼顾文字覆盖量与颜色匹配度，避免只有零星亮点的小图平均得分虚高
            fg = v >= VOTE_MIN_V
            quality = float(match[fg].mean() * int(fg.sum()) / (w * h))

            candidates.setdefault((name, main_name), []).append({
                "fp": fp,
                "color": main_name,
                "itemName": item_name,
                "gray": gray,
                "quality": quality,
            })

    # 同色同名多图只保留质量分最高者，其余跳过；不同颜色的同名图各自保留
    entries = []
    dup_skipped = []
    for (name, color), cands in candidates.items():
        best = max(cands, key=lambda c: c["quality"])
        for c in cands:
            if c is not best:
                dup_skipped.append(c["fp"])

        color_dir = os.path.join(OUT, color)
        os.makedirs(color_dir, exist_ok=True)
        out_fn = name + ".png"
        Image.fromarray(best["gray"], "L").save(os.path.join(color_dir, out_fn))

        entries.append({
            "name": name,
            "color": color,
            "file": f"{color}/{out_fn}",
            "itemName": best["itemName"],
            "len": min(len(name), 5),
        })

    entries.sort(key=lambda e: (e["color"], e["name"]))
    json_path = os.path.join(OUT, "templates.json")
    with open(json_path, "w", encoding="utf-8") as jf:
        json.dump(entries, jf, ensure_ascii=False, indent=2)

    color_count = Counter(e["color"] for e in entries)
    print(f"总输入 PNG: {total}")
    print(f"输出模板数: {len(entries)}")
    print(f"颜色分布: {dict(color_count)}")
    print(f"尺寸不符拒绝: {len(rejected)}")
    for r in rejected:
        print("  REJECT", r)
    print(f"无亮像素丢弃: {len(dropped)}")
    for d in dropped:
        print("  DROP", d)
    print(f"同名择优后跳过: {len(dup_skipped)}")
    for d in dup_skipped:
        print("  DUP", d)
    print(f"JSON: {json_path}")


if __name__ == "__main__":
    main()
