# 协调服务第三方组件与资源声明

本清单针对 BgiCoordinatorServer 服务端和随服务发布的网页。项目的 GPL v3 声明不改变第三方原有授权。本文件不将字体、图片或其他素材统一改为 GPL 授权。

## 项目来源

茶包版源码：<https://github.com/kaedelcb/better-genshin-impact>；BetterGI 上游：<https://github.com/babalae/better-genshin-impact>。保留来自上游及其他贡献者的版权和许可声明。GPL 全文见 `LICENSE`。

## 服务端依赖

以当前 `BgiCoordinatorServer.csproj` 及 .NET 8 还原依赖为依据：

| 组件 | 版本 | 许可证及署名 | 随附文本 |
| --- | --- | --- | --- |
| Semver | 3.0.0 | MIT；Copyright © 2013-2024 Jeff Walker, Max Hauser | `licenses/Semver-3.0.0-LICENSE.txt` |
| Microsoft.Extensions.Primitives（Semver 的间接依赖） | 5.0.1 | MIT；Microsoft Corporation、.NET Foundation 及贡献者 | `licenses/Microsoft.Extensions.Primitives-5.0.1-LICENSE.txt`、对应第三方声明 |
| .NET / ASP.NET Core 共享框架 | 8.0 系列，具体补丁随运行环境或基础镜像 | MIT 及其内含组件各自条款 | 保留实际运行时或基础镜像原有的许可资料 |

Semver 原文来自 NuGet 包元数据所记录的 [源码提交 801ae3651f0d12580a5e828cfb371b6be9d49069](https://github.com/WalkerCodeRanger/semver/tree/801ae3651f0d12580a5e828cfb371b6be9d49069)。Microsoft.Extensions.Primitives 文本来自 5.0.1 NuGet 包。自包含发布或更换基础镜像时，应保留该实际运行时版本的许可证及第三方声明，不能把本清单当成所有未来镜像的完整组件清单。

## 网页组件

`wwwroot/signalr.min.js` 为 ASP.NET Core SignalR JavaScript 客户端，文件内版本号为 8.0.0，MIT 授权，Copyright (c) .NET Foundation and Contributors。来源项目为 [ASP.NET Core v8.0.0](https://github.com/dotnet/aspnetcore/tree/v8.0.0/src/SignalR/clients/ts/signalr)，随附 `licenses/dotnet-MIT-LICENSE.txt`、`licenses/aspnetcore-8.0.0-THIRD-PARTY-NOTICES.txt`。本文件不宣称已将压缩文件与上游制品逐字比对。

网页样式使用浏览器/系统字体回退；当前服务端目录未随包分发 HarmonyOS 字体文件。助手程序内嵌字体的授权见助手目录的第三方声明。

## 图片与商标

`wwwroot/gengdi-avatars` 中的原神角色图片涉及米哈游/HoYoverse 等原权利人的素材。助手现有工程注释记录同类图片来源为 enka.network，但可公开访问不等于取得开源或再分发授权。当前仓库未附足以确认这些图片再分发范围的授权文件；公开分发前须确认实际授权、取得许可或使用可合法分发的替代素材。补充署名和免责声明不能代替授权。

项目及游戏名称、标识和商标属于各自权利人。本服务是社区项目，不表示获得相关权利人的隶属、赞助或认可。

## 输出位置与维护

源码中的 `LICENSE`、`README.md`、本文件及 `licenses` 会被发布到 `wwwroot/legal`，其中前三个文件分别使用 `LICENSE.txt`、`README.txt`、`THIRD-PARTY-NOTICES.txt` 名称，便于浏览器按纯文本查看。升级依赖或增加素材后，应同步更新本文件和随附文本。

助手使用的 Hardcodet.NotifyIcon.Wpf、AlphaVSS 等依赖并非服务端项目的依赖；助手的 CPOL 兼容问题仍见助手声明，不能据本服务端清单宣称整个套件已完成许可兼容处理。
