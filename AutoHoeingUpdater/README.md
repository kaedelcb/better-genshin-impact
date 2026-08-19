# 联机锄地路线更新工具 使用说明

本目录中的 `AutoHoeingUpdater.exe` 是可直接运行的单文件版工具。

## 图形界面使用

双击运行：

```text
AutoHoeingUpdater.exe
```

常用流程：

1. 点击“获取云端版本”，工具会自动下载或复用本地已有更新包。
2. 点击“选择根目录”，选择 BetterGI 安装目录或其下级目录。
3. 点击“添加到常用列表”，下次可直接勾选使用。
4. 勾选一个或多个常用路径。
5. 点击“开始更新：清空旧文件并解压”。

工具会自动查找 `BetterGI.exe`，并更新：

```text
BetterGI目录\GameTask\AutoHoeing\Assets
```

如果找不到 `BetterGI.exe`，不会执行更新。

## 静默更新

静默模式不会打开窗口，适合定时任务或脚本调用。

更新全部常用路径：

```bat
AutoHoeingUpdater.exe --silent --all
```

更新指定路径：

```bat
AutoHoeingUpdater.exe --silent --target "D:\Program Files\BetterGI"
```

强制重新下载云端更新包：

```bat
AutoHoeingUpdater.exe --silent --all --force-download
```

退出码：

```text
0 = 全部成功
1 = 部分成功，部分失败
2 = 全部失败或参数/配置错误
```

## 文件说明

运行后，本目录可能出现：

```text
settings.json
AutoHoeingUpdater.log
downloads
```

说明：

- `settings.json`：保存常用路径，中文路径会直接显示为中文。
- `AutoHoeingUpdater.log`：保存操作日志。
- `downloads`：保存云端下载的 ZIP 更新包。

如果 `downloads` 中已经有同名 ZIP，工具会直接复用，不会重复下载。

## 注意事项

- 更新前会清空目标 `Assets` 目录。
- 如果 BetterGI 或相关文件正在运行，个别文件可能无法删除或覆盖。
- 建议先用图形界面添加常用路径，再使用 `--silent --all` 静默更新。
- 运行需要 Windows 和 .NET 8 Desktop Runtime。
