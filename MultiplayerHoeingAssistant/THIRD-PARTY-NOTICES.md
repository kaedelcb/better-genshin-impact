# 第三方组件与资源声明

本文件记录助手当前依赖及资源的来源和授权边界。项目的 GPL v3 声明不覆盖第三方原有权利，也不授予原权利人未曾授予的权限。以下 NuGet 版本以本目录 `packages.lock.json` 为依据；升级依赖后应同步更新本文件及随包许可证。

## BetterGI 与仓库共享源码

- BetterGI 上游：<https://github.com/babalae/better-genshin-impact>，GNU GPL v3。
- 茶包版：<https://github.com/kaedelcb/better-genshin-impact>。
- 助手直接编译使用 `Mistletoe.Shared`、`BetterGenshinImpact/Service/ExternalInterface/TerminalEffectJournal.cs`、`Test/OneDragonMigration/Core` 的指定共享源码，并内嵌 `BgiCoordinatorServer/wwwroot` 的指定网页资源。分发对应源码时须一并提供这些构建输入，保留原版权和许可声明。

## NuGet 依赖

| 组件 | 版本 | 发布包声明的许可证 | 权利人或作者及随附文本 |
| --- | --- | --- | --- |
| AlphaVSS、AlphaVSS.Native.NetCore | 2.0.3 | Apache-2.0 | Copyright © Peter Palotas；`licenses/AlphaVSS-2.0.3-LICENSE.txt` |
| Hardcodet.NotifyIcon.Wpf | 1.1.0 | CPOL 1.02 | Copyright (c) 2009-2021 Philipp Sumi；作者 Philipp Sumi、Robin Krom、Jan Karger；`licenses/Hardcodet.NotifyIcon.Wpf-1.1.0-LICENSE.txt` |
| Newtonsoft.Json | 13.0.3 | MIT | James Newton-King；保留包内原始署名，见 `licenses/Newtonsoft.Json-13.0.3-LICENSE.md` |
| SharpCompress | 0.38.0 | MIT | Copyright (c) 2014 Adam Hathcock；`licenses/SharpCompress-0.38.0-LICENSE.txt`；另保留上游参考实现的 `licenses/SharpCompress-UnRAR-LICENSE.txt` |
| ZstdSharp.Port | 0.8.1 | MIT | Oleg Stepanischev；保留上游原始署名，见 `licenses/ZstdSharp.Port-0.8.1-LICENSE.txt` |
| 下表列出的 Microsoft/.NET 组件 | 8.0.0 | MIT | Microsoft、.NET Foundation 及贡献者；`licenses/dotnet-MIT-LICENSE.txt` 及相应第三方声明 |

Microsoft/.NET 组件清单及其包内第三方声明如下。声明文件在内容完全相同时合并保存，不代表助手使用了声明中列举的每个内部组件。

| 组件（均为 8.0.0） | 随附第三方声明 |
| --- | --- |
| Microsoft.AspNetCore.SignalR.Client、Microsoft.AspNetCore.SignalR.Client.Core、Microsoft.AspNetCore.SignalR.Common、Microsoft.AspNetCore.SignalR.Protocols.Json | `licenses/aspnetcore-8.0.0-THIRD-PARTY-NOTICES.txt` |
| Microsoft.AspNetCore.Connections.Abstractions、Microsoft.AspNetCore.Http.Connections.Client、Microsoft.AspNetCore.Http.Connections.Common、Microsoft.Extensions.Features | `licenses/aspnetcore-8.0.0-THIRD-PARTY-NOTICES.txt` |
| Microsoft.Extensions.DependencyInjection、Microsoft.Extensions.DependencyInjection.Abstractions、Microsoft.Extensions.Logging、Microsoft.Extensions.Logging.Abstractions、Microsoft.Extensions.Options、Microsoft.Extensions.Primitives | `licenses/dotnet-runtime-8.0.0-THIRD-PARTY-NOTICES.txt` |
| Microsoft.Win32.SystemEvents、System.IO.Pipelines、System.Threading.Channels | `licenses/dotnet-runtime-8.0.0-THIRD-PARTY-NOTICES.txt` |
| System.Drawing.Common | `licenses/System.Drawing.Common-8.0.0-THIRD-PARTY-NOTICES.txt` |

NuGet 官方包页采用 `https://www.nuget.org/packages/<组件名>/<版本>` 的地址形式。未随包提供独立许可证文件的依赖，采用包元数据所记录源码提交中的原文：

- [AlphaVSS 2.0.3](https://github.com/alphaleonis/AlphaVSS/blob/2800471697f06364d7a57ff891a96f343a044a21/LICENSE)
- [SharpCompress 0.38.0](https://github.com/adamhathcock/sharpcompress/blob/7fe27ac310e35f37df96178b9255d9c7063b3f7a/LICENSE.txt) 及 [UnRAR 参考实现声明](https://github.com/adamhathcock/sharpcompress/blob/7fe27ac310e35f37df96178b9255d9c7063b3f7a/reference/unrar/license.txt)
- [ZstdSharp.Port 0.8.1](https://github.com/oleg-st/ZstdSharp/blob/f8ec37192fa4037b6580a80ce83c5a7664a322f7/LICENSE)

### 当前许可兼容事项

助手目前锁定的 Hardcodet.NotifyIcon.Wpf **1.1.0** 包内许可证是 **The Code Project Open License (CPOL) 1.02**，不能套用新版的 MIT 声明。CPOL 带有额外使用及分发限制；[GNU 的许可证说明](https://www.gnu.org/licenses/license-list.en.html#cpol)将其列为非自由软件许可证。当前将该依赖链接到 GPL 程序的许可兼容问题尚未解决，不能把本文件视为已完成合规确认。

发布前应取得足够的单独授权，或将该依赖替换为经确认兼容的版本/实现并完成必要验证。本次只补声明和随包许可证，没有升级或替换运行依赖。

## 内嵌网页组件

`BgiCoordinatorServer/wwwroot/signalr.min.js` 内含版本号 8.0.0，为 Microsoft ASP.NET Core SignalR JavaScript 客户端，MIT 授权。来源为 [ASP.NET Core v8.0.0](https://github.com/dotnet/aspnetcore/tree/v8.0.0/src/SignalR/clients/ts/signalr)，采用同附的 `licenses/dotnet-MIT-LICENSE.txt` 与 ASP.NET Core 第三方声明。本文件不宣称已对当前压缩文件与上游制品逐字比对。

## 字体

助手使用 **HarmonyOS Sans SC Regular / Medium**。

Copyright 2021 Huawei Device Co., Ltd. 授权为 HarmonyOS Sans Fonts License Agreement，完整原文见源码 `Assets/Fonts/HarmonyOS-LICENSE.txt`，随包路径为 `licenses/HarmonyOS-LICENSE.txt`。协议允许在其条件下随软件嵌入、捆绑和分发未修改字体，禁止修改字体或将字体单独分发/销售。字体不改为 GPL 授权。

## 图片、桌宠与音效

- **游戏角色与派蒙图片**：助手的 `Assets/Images` 中相应角色图片，以及内嵌网页 `gengdi-avatars` 中的角色图片，涉及原神游戏素材。现有工程注释记录图片来源为 enka.network；来源可公开访问不等于获得开源或再分发授权。相关权利属于米哈游/HoYoverse 等原权利人，当前仓库未附足以确认这些图片再分发范围的授权文件，本声明不将其许可为 GPL。
- **Logo、奥黛塔桌宠图像与动画**：涉及 `Assets/Images/logo.png`、`logo.ico`、`Assets/Pet/odette_base.png`、`Assets/Pet/Anims`。现有工具记录它们由本机提供的图像导入或加工；加工脚本不能证明原图的权利来源。当前仓库未附明确的原图来源、权利归属及再分发授权，待权利人确认；不据此宣称素材为原创或采用 GPL。
- **桌宠提示音**：`Assets/Pet/Sounds` 中的五个 WAV 文件在 `Tools/Make-PetSounds.py` 中有对应的程序合成方法，不使用外部录音输入；保留该生成脚本作为来源说明。本次未重新生成或逐字验证现有 WAV 文件。

公开分发前，发布者须确认上述待核实素材的实际授权范围，取得授权、补充证据或使用可合法分发的替代素材。补充署名和免责声明不能替代授权。

## 维护范围

本清单针对助手项目、其锁定的直接/间接 NuGet 依赖以及本次检查到的内嵌资源。它不替代整个 BetterGI 及独立协调服务的依赖/资源清单，也不覆盖未来新增的插件或素材。自包含发布还应保留随实际 .NET 运行时提供的许可证及第三方声明。
