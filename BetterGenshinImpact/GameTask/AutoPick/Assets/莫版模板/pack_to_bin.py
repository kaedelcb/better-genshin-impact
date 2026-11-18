# -*- coding: utf-8 -*-
"""由莫版模板文件夹生成单个 bin 文件。

用法：先运行 generate_templates.py，再运行本脚本。
输入：Assets/莫版模板/templates.json + 各颜色文件夹下灰度 PNG。
输出：Assets/莫版模板.bin。

bin 格式（小端）：
    int32 magic   = 0x4D424D42
    int32 version = 1
    int32 count   = 模板数
    每条记录：
        int32 nameLen,     bytes name(UTF-8)
        int32 colorLen,    bytes color(UTF-8)
        int32 itemNameLen, bytes itemName(UTF-8)
        int32 len, int32 width, int32 height
        bytes gray[width*height]
"""
import os
import json
import struct

import numpy as np
from PIL import Image

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))  # Assets/莫版模板
BASE = os.path.dirname(SCRIPT_DIR)                       # Assets
TEMPLATE_DIR = SCRIPT_DIR
BIN_PATH = os.path.join(BASE, "莫版模板.bin")

MAGIC = 0x4D424D42  # "MBMB"
VERSION = 1


def write_str(buf, s):
    b = s.encode("utf-8")
    buf += struct.pack("<i", len(b))
    buf += b


def main():
    json_path = os.path.join(TEMPLATE_DIR, "templates.json")
    with open(json_path, encoding="utf-8") as f:
        entries = json.load(f)

    seen = set()
    errors = []
    loaded = []
    for idx, e in enumerate(entries):
        key = (e["color"], e["name"])
        if key in seen:
            errors.append(f"#{idx} 重复键: [{e['color']}] {e['name']}")
            continue
        seen.add(key)

        p = os.path.join(TEMPLATE_DIR, e["file"].replace("/", os.sep))
        if not os.path.isfile(p):
            errors.append(f"#{idx} 缺少模板 PNG: {e['file']}")
            continue
        with Image.open(p) as im:
            gray = np.asarray(im.convert("L"))
        h, w = gray.shape
        if (w, h) != (140, 26):
            errors.append(f"#{idx} 尺寸异常 ({w}x{h} != 140x26): {e['file']}")
            continue
        loaded.append((e, w, h, gray))

    if errors:
        print(f"打包前校验失败（共 {len(errors)} 处错误），已中止：")
        for err in errors[:20]:
            print(" ", err)
        raise SystemExit(1)

    buf = bytearray()
    buf += struct.pack("<iii", MAGIC, VERSION, len(loaded))
    for e, w, h, gray in loaded:
        write_str(buf, e["name"])
        write_str(buf, e["color"])
        write_str(buf, e["itemName"])
        buf += struct.pack("<iii", int(e["len"]), w, h)
        buf += gray.astype(np.uint8).tobytes()

    with open(BIN_PATH, "wb") as f:
        f.write(bytes(buf))
    print(f"已生成 {BIN_PATH}：{len(loaded)} 个模板")


if __name__ == "__main__":
    main()
