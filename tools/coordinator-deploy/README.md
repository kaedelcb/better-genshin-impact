# 腾讯云协调服务器一键更新

在这台 Windows 电脑上双击 [Deploy-Coordinator.cmd](Deploy-Coordinator.cmd)，就会更新 `bgi-coordinator:/opt/bgi-coordinator` 的协调服务。工具使用本机现有的 Git、Python 和 SSH 配置，无需打开腾讯云控制台。

工具从当前仓库的 **HEAD 提交** 打包 `BgiCoordinatorServer`。请先确认切到了要发布的版本并提交改动；暂存但未提交的内容也不会上传。终端会显示实际提交号。同一提交已经在线且健康时不会重启。

上传校验和 Docker 构建完成后，工具才会重建 `coordinator`，重建期间联机会短暂断开。服务器上的 Compose 主文件、override、房间日志和 `bgi-update` 安装包均保留；本工具更新的是协调服务程序，发布 BGI 客户端安装包仍是单独的操作。

看到“已部署”即表示新镜像和 `/health` 均已确认。切换失败时工具会尝试恢复旧镜像，只有确认旧服务恢复才显示“已回退”。如果连接断开或提示“需检查”，先用下方命令核对现网；旧镜像保留在 `bgi-coordinator-coordinator:deploy-previous`。不要在工具运行时同时执行手工部署或修改 Compose 配置。

在仓库根目录执行以下命令，只检查 SSH、Compose 和服务健康，不上传、不重启：

```powershell
python -X utf8 tools/coordinator-deploy/deploy.py --check
```

如果换电脑，需先配置名为 `bgi-coordinator` 的 SSH 连接及密钥，核对并登记服务器主机指纹。脚本使用现有 SSH 凭据，不保存服务器密码。
