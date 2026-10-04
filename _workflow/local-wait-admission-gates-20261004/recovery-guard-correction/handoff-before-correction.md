# 当前施工现场与唯一接续项

本 Goal 是原计划全部约定功能最终交付，仍未完成。当前工作区仍为 `E:/Program Files/better-genshin-impact-LCB`，分支 `main-OldTeaBag-B168`；最新 HEAD 和工作区以交接观察文件为准。来源聊天 `01a10447-3c8c-7493-9109-aa0c084e43dc`。原 opening、注册、报告、失败、计数及材料外改动保留。

## 当前实现和验证等级

- `dc25a5e38`：G10 首批明细字段候选；只覆盖部分链路，不能记为 G10 闭合。
- `436028e3a`：发送侧游标早期检查候选；反例证明早窗零发送，但独立审查发现准备 CAS 后续窗口和消费代次仍未解决。
- 当前 G10 补修贯通准备拒绝/准备异常、拒绝镜像、续用/对象重建回读，新增发送关联的未知诊断（仅诊断，不是受理、终态或释放依据）。三项突变指定断言失败，恢复字节一致，恢复 Rebuild/定向测试通过。证据为 `g10-completion/candidate-observation.json`、`mutation-observation.json`、原始 TRX/日志；普通过程观察不冒充认证 receipt。
- 实质独立源码审查原文为 `g10-completion/independent-audit-original.md`，对应原生 final event、rollout/模型证据另存同目录。真实模型 `gpt-6.1-sol/high`。报告 `blocked`，八项原级义务全部 `important / implementation / retained_open`；审查期间 G10 漂移明确记录，不能作为当前补修实现 pass。
- 旧实机文件证明的仅为应用进程启动、强制结束、重启和日志追加；没有真实工作流/八类单项/完整停止链的验收。原接班消息“控制链实机 PASS”的宽泛表述不能代替实际文件中的局限。源码 capability=true 也不能代替八类任务实际执行。

## 唯一下一项：完整发送许可与恢复证据链（G4/G7，共享协议合批）

1. G4：把冻结游标及稳定逻辑消费身份纳入准备 CAS 和许可消费边界。当前 RecordRevision 是记录版本，不能当逻辑消费代次。补“检查通过后、冻结 CAS 前”“许可消费时游标推进”“同游标仅 Note 改动后再次准入”“跨迁区/重启重放”反例。不能只加第二次 Load 后宣称消除窗口。
2. G7：复用真实 BGI 查询、原提交/epoch/run/node/occurrence/loop/attempt/task/configRevision/实际载荷证据。当前查询缺完整载荷指纹；必要的加法查询投影属于实现原功能，不受旧 plan 自设“不改 BGI 协议”限制，但须证明旧端兼容和能力拒绝。先保留未知，不能凭裸 jobId、当前流程定义或本地自述补造受理。
3. 将合法证据与原完整 send identity 同 CAS 持久化并读回，再走现有严格 `TakeoverPersist`/`SettleReconciledAsync`。CurrentSubmission 之外的 SubmissionHistory/NodeOutcomes 必须保留；历史不可变与封印守卫不可放宽。对缺历史身份可追加证据绑定恢复关联，不改旧原件。多 sendSeq 无法唯一关联时保留冲突。
4. 复用已有 `ParentRequestIdentity` 原子父子绑定；W3 不是从零加父字段。补受理时类型化精确来源（面板/移交），保留删除、歧义、旧数据不明来源的保守拒绝。
5. 已有普通 Fact 33 节点夹具，不按旧 partial 写成“无夹具”。补逐次容量原因码、原发送身份逐节点迁区、恢复/四类实际入口和同步 gate 等待链证据。G2(e)、G4a、G8及交错⑤⑥保持原级 open。
6. 上述共享链稳定后，统一 Rebuild、规定回归/实际失败身份对照、关键 P/F/P、来源核验、独立综合实现后审与成批闭环。原生产开门和 R5.8/真实入口、实机要求均未满足；不能只传 `_successorAdmissionWired=true`。

## 权限和工序纠正

`owner-policy.json` 曾将另一个 Goal 的无限请求权限复制为 true；当前没有这种授权依据，已保存原件 `owner-policy-before-correction.json` 并将当前解释改为 false。原冻结 request 的字节不改，它不授予无限额度。

当前聊天已实际派发：原生方案审查失败三次（409 路由，无报告）、CLI 一次（模型不支持，无报告）、当前原生实质源码审查一次（成功，blocked），合计五次，失败照计。首个 spawn 的非法任务名称在本地被拒不计。历史 G 系列及原 control 包请求/发现必须继承；旧 adopt 的空 history 和“pre-tool无请求”说明不是历史计数归零依据。后续派发前核对原账，不滚动扩额度。当前原生渠道已取得实际报告，不能继续把旧路由故障当确认当前不可用。

当前 plan 属旧未获 pass 草案；保留 opening/注册/旧 request，不倒签前审。依据 `DELIVERY-FIRST-POLICY.md`，明确定向技术修复可继续，原级 blocked/open 不改变。正式后审及全功能验收继续必需。

权威入口：`_workflow/usable-delivery-20261003/DELIVERY-COVERAGE.md`、总计划及 2026-09-17 公版比较/兼容审计；`Docs/design/mistletoe-review-process.md`、`mistletoe-workflow-facilities.md`、并行成果 md/json、`tools/mistletoe/README.md`、工作包合批 Skill 引用。当前快照不能按旧数字认定已满足完整交付。

保护所有 User/配置/宏/第三方 JS/截图/.kiro/旧 D:/DOWN、既有未提交 R56 及工具材料。Rebuild 必须带 `DeployToBgiTools=false`；当前专属产品目录 `g10-completion/products` 可复用，不复制历史产物或整树。阶段提交仅候选，不 push/部署/发布。自动接班按 `handoff-inherit-model-20261004-v1`，继承来源当前 rollout 实际模型和推理档位，读回新 Goal 和模型成立前禁止接班写入。
