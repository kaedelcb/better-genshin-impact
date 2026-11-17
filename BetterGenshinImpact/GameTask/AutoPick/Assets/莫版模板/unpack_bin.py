# -*- coding: utf-8 -*-
"""解包莫版模板 bin（pack_to_bin.py 的逆操作）。

bin 格式（小端）：
    int32 magic   = 0x4D424D42（"MBMB"）
    int32 version = 1
    int32 count   = 模板数
    每条记录：
        int32 nameLen,     bytes name(UTF-8)
        int32 colorLen,    bytes color(UTF-8)
        int32 itemNameLen, bytes itemName(UTF-8)
        int32 len, int32 width, int32 height
        bytes gray[width * height]     # 8 位灰度：向该颜色模板的匹配度（0-255，背景 0）

用法：
    python unpack_bin.py [bin路径] [-o 输出目录] [--flat] [--no-json] [--list] [--strict] [-q]

默认：
    bin路径   = 本脚本上一级目录下的「莫版模板.bin」
    输出目录  = bin 同级目录下的「<bin文件名>_unpacked」
    输出结构  = <输出目录>/<颜色>/<名称>.png  +  <输出目录>/templates.json

templates.json 可直接再次交给 pack_to_bin.py 打包，实现无损往返。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys
from collections import Counter

import numpy as np
from PIL import Image

MAGIC = 0x4D424D42
MAGIC_BYTES = b"MBMB"
SUPPORTED_VERSIONS = (1,)
HEADER_SIZE = 12
MAX_STR_BYTES = 64 * 1024
MAX_DIM = 8192

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
BASE = os.path.dirname(SCRIPT_DIR)
DEFAULT_BIN = os.path.join(BASE, "莫版模板.bin")

_ILLEGAL = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_RESERVED = {"CON", "PRN", "AUX", "NUL"} | {f"COM{i}" for i in range(1, 10)} | {f"LPT{i}" for i in range(1, 10)}


class BinError(Exception):
    """bin 结构不合法，无法解包。"""


class Record(object):
    __slots__ = ("index", "offset", "name", "color", "item_name", "match_len", "width", "height", "gray")

    def __init__(self, index, offset, name, color, item_name, match_len, width, height, gray):
        self.index = index
        self.offset = offset
        self.name = name
        self.color = color
        self.item_name = item_name
        self.match_len = match_len
        self.width = width
        self.height = height
        self.gray = gray


def _read_str(buf, pos, label, index, warnings):
    """读取一个 int32 长度前缀的 UTF-8 字符串，返回 (文本, 新偏移)。"""
    if pos + 4 > len(buf):
        raise BinError("记录 #%d 的 %s 长度字段不完整（偏移 %d，文件共 %d 字节）"
                       % (index, label, pos, len(buf)))
    (n,) = struct.unpack_from("<i", buf, pos)
    pos += 4
    if n < 0 or n > MAX_STR_BYTES:
        raise BinError("记录 #%d 的 %s 长度非法：%d" % (index, label, n))
    if pos + n > len(buf):
        raise BinError("记录 #%d 的 %s 内容不完整：需要 %d 字节，仅剩 %d 字节"
                       % (index, label, n, len(buf) - pos))
    text = buf[pos:pos + n].decode("utf-8", errors="replace")
    if "\ufffd" in text:
        warnings.append("记录 #%d 的 %s 不是合法 UTF-8，已用替换字符解码" % (index, label))
    return text, pos + n


def parse_bin(buf, strict=False):
    """解析 bin 内容，返回 (records, header, warnings)。任何结构错误抛 BinError。"""
    if len(buf) < HEADER_SIZE:
        raise BinError("文件过小（%d 字节），不可能是莫版模板 bin" % len(buf))

    magic, version, count = struct.unpack_from("<iii", buf, 0)
    if magic != MAGIC:
        raise BinError('magic 不匹配：0x%08X，期望 0x%08X（"%s"）'
                       % (magic & 0xFFFFFFFF, MAGIC, MAGIC_BYTES.decode()))
    if version not in SUPPORTED_VERSIONS:
        raise BinError("不支持的版本号：%d（本工具支持 %s）" % (version, list(SUPPORTED_VERSIONS)))
    if count < 0:
        raise BinError("模板数非法：%d" % count)

    warnings = []
    if count == 0:
        warnings.append("模板数为 0，bin 中没有图片")

    records = []
    pos = HEADER_SIZE
    for i in range(count):
        start = pos
        name, pos = _read_str(buf, pos, "name", i, warnings)
        color, pos = _read_str(buf, pos, "color", i, warnings)
        item_name, pos = _read_str(buf, pos, "itemName", i, warnings)

        if pos + 12 > len(buf):
            raise BinError("记录 #%d（%s）的尺寸字段不完整（偏移 %d）" % (i, name, pos))
        match_len, width, height = struct.unpack_from("<iii", buf, pos)
        pos += 12

        if width <= 0 or height <= 0 or width > MAX_DIM or height > MAX_DIM:
            raise BinError("记录 #%d（%s）尺寸非法：%dx%d" % (i, name, width, height))
        if match_len < 0:
            warnings.append("记录 #%d（%s）的 len 字段为负：%d" % (i, name, match_len))

        need = width * height
        if pos + need > len(buf):
            raise BinError("记录 #%d（%s）像素数据不完整：需要 %d 字节，仅剩 %d 字节"
                           % (i, name, need, len(buf) - pos))
        gray = np.frombuffer(buf, dtype=np.uint8, count=need, offset=pos).reshape(height, width).copy()
        pos += need

        records.append(Record(i, start, name, color, item_name, match_len, width, height, gray))

    if pos != len(buf):
        msg = "解析结束于偏移 %d，但文件长 %d 字节，尾部有 %d 字节多余数据" % (pos, len(buf), len(buf) - pos)
        if strict:
            raise BinError(msg)
        warnings.append(msg)

    header = {"magic": magic, "version": version, "count": count, "bin_bytes": len(buf),
              "parsed_bytes": pos}
    return records, header, warnings


def safe_component(text, fallback="unnamed"):
    """把任意字符串变成安全的单层文件名/目录名。"""
    cleaned = _ILLEGAL.sub("_", text).strip().rstrip(". ")
    if not cleaned:
        cleaned = fallback
    if cleaned.split(".")[0].upper() in _RESERVED:
        cleaned = "_" + cleaned
    return cleaned[:120]


def unique_stem(used, directory, stem):
    """在同一目录内避免重名，返回 (实际文件名 stem, 是否改名)。"""
    candidate = stem
    n = 2
    while os.path.join(directory, candidate).lower() in used:
        candidate = "%s (%d)" % (stem, n)
        n += 1
    used.add(os.path.join(directory, candidate).lower())
    return candidate, candidate != stem


def unpack(bin_path, out_dir, flat=False, write_json=True, strict=False, quiet=False):
    """执行解包，返回统计信息 dict。"""
    if not os.path.isfile(bin_path):
        raise BinError("找不到 bin 文件：%s" % bin_path)

    with open(bin_path, "rb") as f:
        buf = f.read()

    records, header, warnings = parse_bin(buf, strict=strict)

    if not quiet:
        print("bin      : %s" % bin_path)
        print("版本     : v%d，模板数 %d，文件大小 %d 字节"
              % (header["version"], header["count"], header["bin_bytes"]))

    os.makedirs(out_dir, exist_ok=True)

    used = set()
    entries = []
    renamed = []
    color_count = Counter()
    total_px = 0
    min_w = min_h = None
    max_w = max_h = 0

    for rec in records:
        color_dir_name = "" if flat else safe_component(rec.color, "未分类")
        target_dir = os.path.join(out_dir, color_dir_name) if color_dir_name else out_dir
        os.makedirs(target_dir, exist_ok=True)

        stem, was_renamed = unique_stem(used, target_dir, safe_component(rec.name, "unnamed"))
        if was_renamed:
            renamed.append((rec.index, rec.name, rec.color, stem))
        file_name = stem + ".png"

        Image.fromarray(rec.gray, "L").save(os.path.join(target_dir, file_name))

        rel = ("%s/%s" % (color_dir_name, file_name)) if color_dir_name else file_name
        entries.append({
            "name": rec.name,
            "color": rec.color,
            "file": rel.replace(os.sep, "/"),
            "itemName": rec.item_name,
            "len": rec.match_len,
        })

        color_count[rec.color] += 1
        total_px += rec.width * rec.height
        min_w = rec.width if min_w is None else min(min_w, rec.width)
        min_h = rec.height if min_h is None else min(min_h, rec.height)
        max_w = max(max_w, rec.width)
        max_h = max(max_h, rec.height)

    entries.sort(key=lambda e: (e["color"], e["name"]))
    json_path = None
    if write_json:
        json_path = os.path.join(out_dir, "templates.json")
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump(entries, f, ensure_ascii=False, indent=2)

    if not quiet:
        print("输出目录 : %s" % out_dir)
        print("已解出   : %d 张 PNG，共 %d 像素" % (len(records), total_px))
        if records:
            print("尺寸范围 : %dx%d ~ %dx%d" % (min_w, min_h, max_w, max_h))
        print("颜色分布 : %s" % dict(sorted(color_count.items(), key=lambda kv: -kv[1])))
        if json_path:
            print("索引文件 : %s" % json_path)
        if renamed:
            print("重名改名 : %d 处" % len(renamed))
            for idx, name, color, stem in renamed:
                print("    #%d [%s] %s -> %s.png" % (idx, color, name, stem))
        for w in warnings:
            print("警告     : %s" % w)

    return {"count": len(records), "out_dir": out_dir, "json": json_path,
            "colors": dict(color_count), "warnings": warnings, "renamed": renamed}


def list_records(bin_path, strict=False, quiet=False):
    """只列出 bin 内容，不写出任何文件。"""
    if not os.path.isfile(bin_path):
        raise BinError("找不到 bin 文件：%s" % bin_path)
    with open(bin_path, "rb") as f:
        buf = f.read()
    records, header, warnings = parse_bin(buf, strict=strict)

    if not quiet:
        print("# bin: %s  v%d  模板数 %d  %d 字节"
              % (bin_path, header["version"], header["count"], header["bin_bytes"]))
        print("%-5s %-4s %-22s %-22s %-4s %s" % ("序号", "颜色", "名称", "物品名", "len", "尺寸"))
    for rec in records:
        print("%-5d %-4s %-22s %-22s %-4d %dx%d"
              % (rec.index, rec.color, rec.name, rec.item_name, rec.match_len, rec.width, rec.height))
    if not quiet:
        for w in warnings:
            print("警告     : %s" % w)
    return records


def build_parser():
    ap = argparse.ArgumentParser(
        prog="unpack_bin.py",
        description="解包莫版模板 bin 为 <颜色>/<名称>.png + templates.json",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="示例：\n"
               "  python unpack_bin.py\n"
               "  python unpack_bin.py ..\\莫版模板.bin -o ..\\莫版模板_out\n"
               "  python unpack_bin.py --list\n")
    ap.add_argument("bin", nargs="?", default=DEFAULT_BIN, help="bin 文件路径（默认 %(default)s）")
    ap.add_argument("-o", "--out", default=None, help="输出目录（默认 <bin同级>/<bin名>_unpacked）")
    ap.add_argument("--flat", action="store_true", help="不按颜色建子目录，全部平铺在输出目录")
    ap.add_argument("--no-json", action="store_true", help="不写出 templates.json")
    ap.add_argument("--list", action="store_true", help="只列出内容，不写出文件")
    ap.add_argument("--strict", action="store_true", help="把尾部多余数据等异常视为错误")
    ap.add_argument("-q", "--quiet", action="store_true", help="只输出错误")
    return ap


def default_out_dir(bin_path):
    base = os.path.basename(os.path.abspath(bin_path))
    stem = os.path.splitext(base)[0]
    return os.path.join(os.path.dirname(os.path.abspath(bin_path)), stem + "_unpacked")


def main(argv=None):
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    args = build_parser().parse_args(argv)
    bin_path = os.path.abspath(args.bin)
    try:
        if args.list:
            list_records(bin_path, strict=args.strict, quiet=args.quiet)
            return 0
        out_dir = os.path.abspath(args.out) if args.out else default_out_dir(bin_path)
        unpack(bin_path, out_dir, flat=args.flat, write_json=not args.no_json,
               strict=args.strict, quiet=args.quiet)
        return 0
    except BinError as e:
        print("错误：%s" % e, file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
