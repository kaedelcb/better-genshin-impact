# 莫版模板

本目录存放自动拾取（AutoPick）识别的**灰度模板**及其生成脚本。

## 图片作用

- `灰/`、`白/`、`绿/`、`蓝/`、`紫/`：按文字颜色分类的灰度模板 PNG（140×26），由 `原图` 素材经 `generate_templates.py` 自动生成；
- `templates.json`：模板清单（名称、颜色、文件名、交互名称、匹配长度），供 `pack_to_bin.py` 打包使用；
- `generate_templates.py`：由 `Assets/原图` 生成灰度模板与 `templates.json`；
- `pack_to_bin.py`：将灰度模板与 `templates.json` 打包为 `Assets/莫版模板.bin`。

## 生成与打包

1. `python generate_templates.py`
2. `python pack_to_bin.py`

## 入库规则

- 灰度模板 PNG 为生成物，**不入库**（见根目录 `.gitignore`）；
- `generate_templates.py`、`pack_to_bin.py`、`templates.json` 与打包产物 `Assets/莫版模板.bin` 正常入库；
- 运行时识别读取的是 `Assets/莫版模板.bin`。
