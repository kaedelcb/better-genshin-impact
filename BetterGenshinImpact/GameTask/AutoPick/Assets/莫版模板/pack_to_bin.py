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

    buf = bytearray()
    buf += struct.pack("<iii", MAGIC, VERSION, len(entries))
    for e in entries:
        p = os.path.join(TEMPLATE_DIR, e["file"].replace("/", os.sep))
        with Image.open(p) as im:
            gray = np.asarray(im.convert("L"))
        h, w = gray.shape
        write_str(buf, e["name"])
        write_str(buf, e["color"])
        write_str(buf, e["itemName"])
        buf += struct.pack("<iii", int(e["len"]), w, h)
        buf += gray.astype(np.uint8).tobytes()

    with open(BIN_PATH, "wb") as f:
        f.write(bytes(buf))
    print(f"已生成 {BIN_PATH}：{len(entries)} 个模板")


if __name__ == "__main__":
    main()
