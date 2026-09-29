# 开工审计与会诊台账

- Goal 已建立；本批 review-process-v3，只施工流程工具与规则，不改 R5.6 产品源码/证据/Goal。
- 权威工作区：独立 managed worktree mistletoe-mainline-gate，分支 codex/review-workflow-v3，基于 1676b54139e04941a4cd2c300764944e6acdd20e。
- 旧设施测试：83 项通过（未实现 v3 时的回归基线）。
- 本批累计会诊上限8；所有以下请求均独立本地 gpt-6-astra / medium / read-only。

|累计|结果|证据|
|1|只读shell被环境拒绝，未取得实质报告，计次|bootstrap-plan/|
|2|新只读MCP中文编码失败，取消本批子进程，计次|bootstrap-plan2/|
|3|方案7项MUST/IMPORTANT，已集中修订|bootstrap-plan3/|
|4|其中6项方案通过，R3-4执行来源仍阻断|bootstrap-plan4/|
|5|对ExecutionEvidence合同复核中|bootstrap-plan5/|

R3原始快照已按 request.json 哈希恢复存档于 bootstrap-source3-archive；bootstrap-source 后续用于R4修订，不能冒充R3原始快照。R5使用独立 bootstrap-source5，不覆盖。正式工具使用唯一UUID不可覆盖快照，消除此类手工路径复用问题。

本地CLI的读取通道：保留 read-only 沙箱，额外提供仅可 list/read/search 的固定文件MCP，不提供任意shell执行能力；stdio显式UTF-8。使用专用ChatGPT登录目录与环境白名单，不继承主模型的relay配置。未修改用户全局配置或hooks。

## 实施与验证进度

- R6 独立本地 Astra/medium 对完整整合方案给出 PASS：原七项在方案层面闭合，允许实施，不等于实现验收。
- 已实现 review_process/review_support/snapshot_reader/execution_evidence，接入原workflow新code begin及review/closeout；旧opening按明确清单兼容，adopt后不可回退。
- 全套113项测试通过。真实新执行来源：executions/cbf287881d8840adb28e626742df2406/receipt.json，包含命令、输入、日志与实际TRX。新测试30项，含5处关键门禁反向突变；原83项回归保留（旧fixture明确模拟历史opening）。
- 第7次独立实现审查正在进行：final-review7/；同时验证导航外依赖发现，隔离演练夹具不属于产品实现。
- 本批已用7/8；第1次环境只读shell拒绝，第2次UTF-8传输失败均计入。未经owner有限授权不得超过8。
- 已确认R5.6原会话idle/interrupted，main HEAD仍为开工提交，未向其发送指令。主线既有规则/设施6文件已备份并记录哈希于main-before-integration；尚未覆盖主线。

## R7 后集中修复

R7 已结束，6项原级发现，见 final-review7/report.json。候选修复/新模型政策见 r7-disposition.md。125项完整回归通过后补充了派发transport测试及完整生命周期A/B材料借用反例，均定向通过；最终统一执行来源待下方新增记录。已用7/8，未向R5.6发消息，未导入主线。

## R8 运行链路与外部阻塞

第8次综合复核以新增 process_runner 真实发出，完整事件/进程树证据保存；终端 usage limit，无 report，按规则计8/8。R8发送前版本 bundle 已复核通过，发送后再次跑当前126项全绿。流程链路证据与额度恢复后的一次有界重试材料见 flow-verification.md。产品主线与R5.6未动。

## 当前可用证据范围（2026-09-29）

- 流程机械链路、冻结版本一致性、测试与独立请求运行通道已经核验，见 flow-verification.md。
- 唯一缺失仍是 GPT 额度耗尽导致的第8次综合报告；该请求已计 8/8，未有 report.json。
- 本批保持 active，不标完成；主线和暂停 R5.6 未接入。额度恢复后使用冻结的 final_review8.py 一次重试。

## 2026-09-30 用户修订与追加授权

统一模型 gpt-6.1-sol，默认 medium，复杂/高风险/未排除风险用 high。原 Goal 修订见 goal-amendment-20260930.md；本轮用户明确追加最多2次，详见 additional-review-authorization-20260930.json，累计第9/10次，不重置原8次。模型迁移和Git换行继承127项回归通过，最新执行来源正在生成。
