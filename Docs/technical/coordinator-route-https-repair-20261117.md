# 线路管理 HTTPS 部署修复记录（2026-11-17）

## 当前结论

这是已部署服务器的故障修复记录，不是线路产品整版验收。

- 原 HTTP 线路管理入口返回 HTTPS 限制，`www.autobgi.cn` 的 HTTPS 握手失败；服务器已有代理，但没有该域名站点和匹配证书。
- **HTTPS 管理入口已接通并完成服务器端实际登录验证：`https://www.autobgi.cn/admin/routes`。** 原管理员口令保持不变。
- 已取得 `www.autobgi.cn` 的公开可信证书，有效期为 2026-11-17 至 2027-02-15。
- 线路独立持久挂载已生效，原有日志、本体更新挂载保留；切换前后应用镜像未更换，仍为 `b887a46a22bf243075254a257bf990396195b5cd`。
- 切换前两次实时检查均无运行中的锄地任务、30 个助手在线；切换后仍有 30 个助手在线，原 HTTP 8080 的两个 SignalR 通道已完成真实 WebSocket 升级验证。
- **证书自动续期已安装**，每天服务器本地时间 03:17、15:17 检查本任务证书；现有用户 crontab 保留。首次安装被安全权限以 Unauthorized Persistence 拒绝后没有绕过；用户随后在专门问题中明确允许自动续期，才执行安装。Certbot `renew --dry-run --run-deploy-hooks` 已成功，退出码 0，模拟续期和 nginx 配置校验/重载钩子均通过，正式证书未被替换。
- 精确正式 BGI 主程序版本尚未识别，现网没有有效安装包元数据。未用助手 `0.3.5` 或源码默认 `0.67.0-alpha.1` 代填。当前线路包 0 个，未发布生产测试包；版本准入和首次线路发布仍未就绪。

## 已保护的现网材料

部署根：`/opt/bgi-coordinator`。

无口令备份：`/opt/bgi-coordinator/backups/route-https-20261117-01`。

- 两份 Compose 文件在激活前与备份逐字节一致。
- `route-packages-before` 保存原线路库存量；切换后实查其中 1 个原有文件的 SHA256 保持一致。空线路包目录不等于没有存储管理文件。
- `repair-state.json` 保存来源镜像、备份核验、证书申请和激活结果；本次激活成功，未走回退分支。
- 没有备份 `.env`、管理员口令、完整运行容器环境或证书私钥。

证书 lineage：`bgi-coordinator-www-autobgi-cn`，位于现有代理容器的 `/etc/letsencrypt/live/`。没有读取或复制私钥。

申请证书前的历史快照为 34 个助手在线、无运行任务；本次没有将该旧快照当作切换凭据，而是在切换前重新检查游戏房间、控制房和成员的运行标志。缺失任务标志不视为空闲。

## 已执行的限定修复

相关文件在 `tools/coordinator-deploy/`：

- `repair_route_https.py`：仅显式 `--apply` 执行；核备份、镜像、Compose、任务空闲、真实代理来源、存储和端口，遇未知状态拒绝覆盖。已执行成功一次，不应重跑。
- `route-proxy.conf.example`、`route-proxy-location.conf.example`：只新增本服务独立配置，没有替换其他站点的自动生成配置。
- `renew-route-certificate.sh`：仅续期本任务证书，与部署使用相同目录锁。已安装到部署根，用户定时任务已核对存在；未导出原 crontab 内容。

当前网络与存储布局：

1. Docker 后端由公网 `8080:80` 改为 `127.0.0.1:18080:80`。
2. 现有代理提供 `https://www.autobgi.cn`，同时接管原 HTTP 8080，保留助手控制、日志等原地址。明文线路请求仍拒绝。
3. 代理覆盖单值 `X-Forwarded-For`、`X-Forwarded-Proto`，清除其他转发标头；应用仅信任实测的 `172.23.0.1`。
4. 实测发现 Docker userland-proxy 会重写源端口；修复脚本改为对比基线与保持连接期间的新增后端连接，来源不唯一时停止，不硬编码猜测来源。
5. `/opt/bgi-coordinator/route-packages` 持久挂到 `/app/data/route-packages`，切换前核对备份，保留原日志和本体更新挂载。
6. 原管理员口令只在远端进程内存中复用；独立上传令牌已生成并持久保存到服务器 `.env`，没有输出其值。

预留回退顺序：先移除内容仍精确属于本次的新代理配置并重载，释放代理接管的 8080；再恢复原 override 和原容器映射，核原健康入口。回退路径没有在真实生产中故障演练，不能冒称实测通过。

## 实际验证

服务器端使用真实 HTTPS，证书校验未关闭；原口令未回传本机。

- 管理登录及 `/api/admin/routes`：通过，目录包数为 0。
- 已认证 `/admin/routes`：HTTP 200、HTML 类型正确、包含线路管理页面内容。
- CSS、核心 JS、页面 JS、Web Worker：均 HTTP 200，非空内容。
- 管理 Cookie：Secure、HttpOnly 均通过，只输出属性判断，不输出 Cookie。
- HTTP 8080 线路页与策略请求：均拒绝；伪造 `X-Forwarded-Proto: https` 和 loopback 来源头仍不能绕过。
- HTTPS 与原 HTTP 8080：`/health` 正常；`/hub`、`/gateway` 协商正常，真实 WebSocket 握手均返回 101；没有加入房间或启动任务。
- Docker 配置：后端仅 loopback 18080，线路目录为可写持久 bind，原日志和本体更新挂载保留。
- 原有文件：备份中 1 个原文件校验一致。
- 独立上传令牌长度及与管理员口令分离：通过，不输出值。
- 切换后：在线助手成员 30，运行中游戏房间 0；应用镜像与备份身份一致。
- `/api/hoeing-routes/policy` 返回 200；`/current` 明确返回 503“尚未发布首个线路包”。这不是 TLS 失败，也不能记为首包已交付。

另从开发电脑走公网请求 `https://www.autobgi.cn/health` 和 `/admin/routes`，均为 200（匿名管理页请求随正常登录跳转取得 HTML）；未关闭 TLS 校验，证明不只服务器自身能够连接。

当前安装的 Browser preview 不支持附加远程 URL，未取得浏览器截图或在该预览中运行页面交互。不以静态 HTML 或模拟 DOM 冒充浏览器实测；以上页面/API/资源与 WebSocket 是真实服务器网络验证。

## 定向测试与复核边界

修复脚本当前版 **16/16 测试通过**、退出码 0，结果为 `Build/validation-runs/61e0586010514338b9b806cbe211debc/records`。覆盖 dotenv、IPv4/映射 IPv6 代理地址、端口重映射场景、配置防覆盖、代理配置回收、任务状态阻断及跨平台 stdin SSH 传输。

命令：`python -B tools/mistletoe/validate_command.py --root . -- python -B -X pycache_prefix={artifacts}/pycache -m unittest discover -s tools/coordinator-deploy -p test_repair_route_https.py -v`。

此前同名测试的通过记录保留，不将旧记录改绑当前代码。续期脚本通过 `bash -n`；证书模拟续期和实际 nginx 部署钩子已运行成功，未替换正式证书。

此前上线前只读复核 `wf_a13251d2-c26` 因 API 输出超过上限而失败，无有效结论，不能记为通过；未追加该复核。它不是原产品整版指定独立复核。

此前部署就绪检查实现任务 `wf_eaac244d-836` 也因 API 错误失败，没有留下代码。新的有限范围实现正在处理底层部署入口，完成与实际测试前不声称已修好发布工具。真正的发布工作台 GUI 源码尚未定位，不声称已验证其按钮。

## 剩余项

1. 底层部署就绪检查：完成当前实现及定向测试，区分服务健康、线路配置未就绪和首包待发布。
2. 正式 BGI 精确版本和首个真实线路包：必须取真实发布依据，不猜版本、不自动发布生产测试包。

本次没有推送 Git、部署新客户端或改其他站点。当前不是整版功能交付。
