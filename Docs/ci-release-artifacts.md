# 构建后自动发布与已有构建补发

两个入口共用 `tools/release_actions.py`，均在 GitHub hosted runner 下载、校验并上传原始产物。本机不再搬运附件，也不需要提供个人 token。

## 自动发布

照常运行 **BetterGI Publish**。完整构建成功后，默认分支上的 **发布构建产物（自动 / 手动补发）** 自动处理对应的构建编号，不重新编译。

现有 `create-release` 勾选项的含义仍为“创建草稿”：如果用户明确勾选且原 `create_release` job 成功，自动入口保留该草稿选择，不擅自公开。日常需要构建后自动公开时保持它关闭（当前默认值）。

可信来源仅为本仓库 `.github/workflows/publish.yml`，触发方式须为 `workflow_dispatch`，分支须为 `main` 或 `main-OldTeaBag-B` 加数字。其他分支需要维护者明确修改白名单后才能发布。

## 手动补发

1. 打开 Actions → **发布构建产物（自动 / 手动补发）**。
2. 选择默认分支，点击 **Run workflow**。
3. 输入成功构建的编号，即 BetterGI Publish 运行页面地址末尾的数字；更新说明可选。

版本取自该构建的 `BetterGI <版本>` 标题，并与两份 ZIP 内部文件名严格核对。输入的编号不能指向失败、未完成、其他 workflow 或 fork 构建。该入口只复用已有产物，不重新编译。

## 发布约定

- 仅发布 `BetterGI_7z` 内的 `BetterGI_v<版本>.7z` 和 `BetterGI_Install` 内的 `BetterGI.Install.<版本>.exe`；两者缺一不可。
- ZIP 大小与 Actions SHA256 必须一致，内部只允许一个预期文件；检查 CRC、文件类型和最终 SHA256，不执行附件内容。
- tag 使用原版本字符串，精确指向源构建 `head_sha`，从不移动已有标签。
- 先创建草稿，服务器确认两个附件的大小和 SHA256 后才公开。
- `0.66.3-alpha.1+lcb.23.0-NexusBGI-fix8` 为预发布，不改正式 Latest；`0.66.3+lcb.23.0-NexusBGI-fix8` 为正式版，由 GitHub 的版本规则选择 Latest。`+` 后的茶包版元数据不改变发布分类。
- 已有完整公开版本且标签指向相同提交时跳过，不下载或覆盖。冲突标签、已有草稿、不完整公开版本、重复或过期产物均失败关闭。
- 同版本操作使用共享并发组且不取消在途发布；队列最多保留 100 个待执行任务。标签创建使用 GitHub 原子建 ref 操作，竞争失败不覆盖。
- 网络写入结果未知时不重复提交；失败可能留下版本标签或未公开草稿，需先核对服务器状态，不自动删除或续传已有草稿。

## 权限与部署

检查 job 仅有 `actions: read`、`contents: read`；发布 job 使用短期 `GITHUB_TOKEN` 的 `actions: read`、`contents: write`。不保存 CLI 登录，不新增个人凭据。

发布控制器从默认分支运行，checkout 的是控制器自身提交，不 checkout 源构建分支，也不执行产物。手动入口和 `workflow_run` 都要求 workflow 文件存在于默认分支。启用时只需将发布配置的独立提交纳入默认分支 `main`，之后仍可从 `main-OldTeaBag-B*` 运行原有构建。

如果使用 OAuth CLI 推送新的 workflow 文件，GitHub 额外要求 `workflow` scope。追加授权必须由用户确认；这与 runner 使用的最小 `GITHUB_TOKEN` 权限是两件事。

## 验证

本地执行 `python -B -m unittest discover -s Test/ReleaseActions -v`。测试使用内存中的虚构仓库和微型 ZIP，不调用 GitHub API，不创建 tag/Release。配置推送只触发 **发布配置离线检查**；不得为了验证入口而补发任何真实构建。
