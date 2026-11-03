# 茶包分支构建后自动发布

使用原有 **Actions → BetterGI Publish → Run workflow**。这项功能只随包含该修改的茶包分支生效；GitHub 默认分支 main 及默认分支设置无需改动。

1. 在 **Use workflow from** 选要打包的茶包分支，例如 `main-OldTeaBag-B188`。
2. 在版本号输入框填 `release:0.66.3+lcb.23.0-NexusBGI-fix9`，把示例版本换成这次要发布的版本。
3. 安装器渠道保留 `release`，取消勾选「创建 GitHub Release 草稿」，点击 **Run workflow**。
4. 构建任务全部成功后，同一次运行里的 **公开发布 GitHub Release** 会上传 7z 压缩包和 exe 安装程序并公开。运行摘要会给出 Release 链接。

`release:` 只用于选择公开发布，构建产物、程序版本、Release 标题和 Git 标签均使用去掉该前缀后的版本号。仅填版本号且不勾选草稿时只打包；仅填版本号并勾选草稿时沿用原草稿流程。此前启动的构建不受新设置影响。

公开发布只接受本仓库的 `main-OldTeaBag-B<数字>` 分支，使用当前运行的原始构建附件及源提交。先上传并校验两个附件，再公开 Release；遇到已有标签或草稿冲突不会覆盖。正式版由 GitHub 决定 Latest，`-alpha.1` 等预发布不抢占正式 Latest，`+lcb` 后面的茶包版后缀不影响分类。

发布任务使用短期 `GITHUB_TOKEN`，无需个人令牌。本地可运行 `python -B -m unittest discover -s Test/ReleaseActions` 做离线检查，不会创建标签或 Release。
