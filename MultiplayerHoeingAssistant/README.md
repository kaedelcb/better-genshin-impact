# 茶包联机助手（NexusBGI）

本助手为 BetterGI 茶包版提供联机房间、任务调度、启动流程和本机网页等功能。助手主程序、登录启动器和协调服务是独立项目，构建助手时仍需要本仓库中的共享源码及网页资源。

## 许可证与版权

本助手的原创代码及本仓库中与助手配套的共享代码采用 GNU General Public License 第 3 版（GPL v3）授权，明确另有声明的文件及第三方组件除外。完整条款见随附的 [LICENSE](LICENSE)。本声明不改变已有代码、第三方组件及资源的原有授权。

助手新增与修改部分的版权属于茶包及各自贡献者；源自 BetterGI 或其他项目的部分保留原版权及许可声明，不能将整个项目标注为单一作者原创。贡献历史以 Git 记录及文件中的原有声明为准。

在遵守 GPL v3 的条件下，接收者可以使用、修改和再分发相应代码，包括商业使用和收费分发。软件按许可证规定不提供担保；具体权利、条件和责任限制以许可证全文为准。

本助手是社区项目，非 BetterGI 官方项目，不表示获得 BetterGI、米哈游或 HoYoverse 的隶属、赞助或认可。有关名称及商标属于各自权利人。

第三方库、HarmonyOS Sans 字体、游戏图片及桌宠素材不因本说明而统一改为 GPL 授权。版本、来源、独立许可证和当前待核实事项见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。本助手使用 HarmonyOS Sans 字体，Copyright 2021 Huawei Device Co., Ltd.；完整字体协议在源码的 `Assets/Fonts/HarmonyOS-LICENSE.txt`，发布包中位于 `licenses/HarmonyOS-LICENSE.txt`。

## 源码与构建

- 茶包版仓库：<https://github.com/kaedelcb/better-genshin-impact>
- BetterGI 上游：<https://github.com/babalae/better-genshin-impact>
- 助手工程：`MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj`
- 启动器工程：`MultiplayerHoeingAssistant/StartupLauncher/MultiplayerHoeingAssistant.Startup.csproj`
- 协调服务：`BgiCoordinatorServer/BgiCoordinatorServer.csproj`

构建源码应取得对应版本的整个仓库，不能只下载助手子目录。必要输入还包括 `Mistletoe.Shared`、BGI 的共享终态记录源码、`Test/OneDragonMigration/Core` 的共享迁移源码、`BgiCoordinatorServer/wwwroot` 的内嵌网页资源，以及项目导入的构建文件。这里引用迁移源码是构建依赖说明，不要求运行测试工程。

在 Windows 上安装支持项目目标框架的 .NET 8 SDK，从仓库根目录执行：

```powershell
dotnet restore MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj --locked-mode -p:Platform=x64
dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -c Release -p:Platform=x64 -p:DeployToBgiTools=false --no-restore
```

`DeployToBgiTools=false` 用于仅构建助手，跳过向本机 BGI Tools 目录复制。项目已配置将本说明、GPL 全文、第三方声明及 `licenses` 中的许可证复制到构建和发布输出。

## 更新 BGI：下载源

在助手的「更新 BGI」窗口用「下载来源」下拉框切换。列表只显示当前来源的茶包版 .7z，更新前会显示所选来源和目标目录。

「助手设置 → 助手启动策略 → 启动时检查 BGI 更新」默认关闭。开启后，每次启动助手会检查一次，优先使用上次选的网络来源；未选时，联机使用协调服务器，单机或没有可用协调服务器地址时使用 GitHub Releases。有更新只在耕地机页面自己的版本号右边显示小点，点版本号即可查看。

- **本地安装包**：扫描安装包目录，也可手动选择 .7z。
- **协调服务器**：读取助手当前配置的协调服务器的 /bgi-update/latest.json。旧服务端若返回 404，需要更新协调服务并保留已挂载的安装包目录。
- **GitHub Releases**：读取茶包仓库中已正式发布的 .7z 附件。
- **GitHub Actions 构建**：读取茶包仓库 BetterGI Publish 工作流最近成功且产物未过期的运行；通过 [nightly.link](https://nightly.link/) 下载 BetterGI_7z 的外层 ZIP，只取对应版本的 .7z。发布版本和构建版本可分别选择。
- **CNB（茶包版）**：读取 [CNB 版本文件](https://cnb.cool/wjdsg/wjdsg-bgi/-/releases/download/wjdsg-bgi/runForVersion.txt) 和固定地址的安装包。文件在检查后被替换，或包内的 BGI 版本与选中版本不一致，助手会中止并提示刷新。

网络包保存在安装包目录的「网络安装包」子目录中，各来源分开保存；Actions 的不同构建也分开保存。文件名相同不代表内容相同。

选中网络包后，点「仅下载」可先保存，不会关闭 BGI 或安装。下载完成的包也能在「本地安装包」里找到，可离线使用。下次从原来源点「更新」时，如果清单仍能确认是同一份文件，校验通过后会直接复用。两种操作在下载安装包期间都可以点「取消下载」。

### 接入自建下载源

维护者可以使用 HTTPS 静态站点、对象存储或现有协调服务。把 .7z 上传完毕后，再发布或替换同一个清单网址。例如把以下内容放到 https://mirror.example.com/bgi-update/latest.json：

```json
{
  "fileName": "BetterGI_v0.66.3+lcb.23.0-NexusBGI-fix8.7z",
  "url": "BetterGI_v0.66.3%2Blcb.23.0-NexusBGI-fix8.7z"
}
```

fileName 为实际安装包名；url 可以是相对清单的路径，也可以是 HTTPS 下载直链。可加 sizeBytes（实际字节数）、sha256（实际 SHA-256 的 64 位十六进制值）和 updatedAt（ISO 8601 时间）。填写了大小或哈希，助手会在安装前校验。使用协调服务的现有清单时无需手写 JSON。

把**清单的完整 HTTPS 网址**发给用户。用户在「管理自定义下载源」中填写名称（可选）和网址，点「添加并选中」；此操作不会改动联机服务器地址。

## 发布对应源码

每次分发助手二进制时，发布者须在下载页面或随包说明中给出该二进制对应的完整源码下载地址，并标明准确的提交号或发布标记。提供必要的构建、安装及修改所需脚本，保留版权、许可证及修改说明。仅给出仓库首页或不断变化的最新分支，不代替该版本的对应源码。

本 README 中的仓库地址用于说明项目来源，不保证任一本地提交已经上传。发布者应先确认对应源码可取得，再分发二进制；未提交改动也必须纳入实际对应源码。不得将用户配置、账号数据、授权码或服务端密钥作为源码发布。

当前待补的是：处理第三方声明中列出的 CPOL 依赖兼容问题，确认图片和桌宠素材的再分发授权，并为每个实际发布版本提供对应源码。这些事项不因新增许可证文件而自动完成。
