# 莫版模板

本目录存放自动拾取（AutoPick）识别的**灰度模板**及其生成脚本。

## 图片作用

- `灰/`、`白/`、`绿/`、`蓝/`、`紫/`：按文字颜色分类的灰度模板 PNG（140×26），由 `原图` 素材经 `generate_templates.py` 自动生成；
- `templates.json`：模板清单（名称、颜色、文件名、交互名称、匹配长度），供 `pack_to_bin.py` 打包使用；
- `generate_templates.py`：由 `Assets/原图` 生成灰度模板与 `templates.json`；
- `pack_to_bin.py`：将灰度模板与 `templates.json` 打包为 `Assets/莫版模板.bin`；
- `unpack_bin.py`：`pack_to_bin.py` 的逆操作，把 `Assets/莫版模板.bin` 解包回灰度模板 PNG 与 `templates.json`。

## 生成与打包

1. `python generate_templates.py`
2. `python pack_to_bin.py`

## 解包

`python unpack_bin.py`（默认读上一级的 `Assets/莫版模板.bin`，输出到同级 `Assets/莫版模板_unpacked/`）：

```
Assets/莫版模板_unpacked/
├── 灰/ 白/ 绿/ 蓝/ 紫/    # 与 bin 内颜色字段同名，共 400 张 140x26 灰度 PNG
└── templates.json          # 可直接再次交给 pack_to_bin.py，往返后与原始 bin 逐字节一致
```

常用参数：`-o 输出目录`、`--flat`（不按颜色分目录）、`--list`（只列出内容不写文件）、`--no-json`、`--strict`（尾部多余数据视为错误）、`-q`。

## 入库规则

- 灰度模板 PNG 为生成物，**不入库**（见根目录 `.gitignore`）；
- `generate_templates.py`、`pack_to_bin.py`、`templates.json` 与打包产物 `Assets/莫版模板.bin` 正常入库；
- 运行时识别读取的是 `Assets/莫版模板.bin`。
