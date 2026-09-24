# 槲寄生计划：首次新任务启动提示词

**当前版本的新任务首轮 422 故障未修复前，这份提示词不能作为可用的新建任务入口**：已在串行链中的任务继续按[接力手册](mistletoe-relay-runbook-2026-09-24.md)方式 A 在同一任务内建立下一批 Goal。应用／模型通道修复并通过“新任务只回复 ok”探针后，再把下面整段粘贴到该项目的新任务。

```text
请启动槲寄生计划的串行自动执行链，持续推进到既定 R0–R6 发布路线全部验收完成。

这是我的明确授权：本任务及其每一个后继任务可在安全批次边界自动创建唯一的下一 Codex 桌面任务，携带交接材料、当前授权、生产门和剩余工作继续执行；不要让我逐个新建任务、粘贴交接或输入“继续”。若后继任务**创建成功但首轮**返回 422 `missing field 'call_id'`（或创建工具报错/回包不明），先用 transferId 和任务列表完成去重并确认没有可运行后继、`ack`、`taken`；确认后以“完整临时文件 + 同目录原子 rename 禁止覆盖”竞争发布 `ownership.json={kind:"cancelled"}`，只有发布成功才解除封存，再按接力手册方式 A 在当前任务内建立下一批 Goal 继续；若输给 `{kind:"taken"}` 或目标损坏则保持只读。工具报错本身不等于“未创建”，不重复盲目建任务、不因此停工。我明确选择直接使用已保存项目的本地目录 E:\Program Files\better-genshin-impact-LCB，不自动另开 worktree、不切分支。首次启动前检查是否已有同计划活动链，避免重复施工。

先读取：
1. E:\Program Files\better-genshin-impact-LCB\AGENTS.md 与其中的 bgi-project-development 技能。
2. E:\Program Files\better-genshin-impact-LCB\Docs\design\mistletoe-session-relay-2026-09-24.md（本次自动接续规则，必须执行）。
3. E:\Program Files\better-genshin-impact-LCB\槲寄生调度器总计划.md 的当前阶段与退出条件。
4. E:\Program Files\better-genshin-impact-LCB\Docs\design\onedragon-r5-handoff-2026-09-21.md 的当前权威状态及执行纪律。
5. E:\Program Files\better-genshin-impact-LCB\Docs\design\onedragon-r5-3-external-start-lifecycle-2026-09-21.md 的 §24.96 与当前批次必要合同。
6. E:\Program Files\better-genshin-impact-LCB\Docs\design\mistletoe-relay-runbook-2026-09-24.md（当前接力方式与故障边界）。
7. C:\Users\Administrator\.codex\hooks\semantic-protocol.md。

不要回读旧聊天，不全量加载历史。先核对真实分支、HEAD、tracked/untracked、代码和验证材料，从当前 R5 剩余依赖选择一个可独立验证的批次，写清结果、约束、完成证据，然后调用 create_goal 建立本批次目标并立即施工。Goal 包含本批验证及必要的安全交棒，不能把整个项目完成混同为当前批次完成。后继任务同样先审计 manifest，再建立自己的 Goal，旧 Goal 不会自动转移。

连续完成审计、反例、实现、既定 GPT 会诊、处置、回归、按文件提交和进度更新。常规会诊 gpt-6-sol/medium，最难或安全敏感 gpt-6-astra/medium；保留全部固定质量门，不以换任务规避失败或额度。已裁决事项不要再次问我，新的必要业务选择集中提交，先完成不依赖裁决的工作。

按任务语义决定继续或切分，不按时间/工具数强制切分；禁止主动压缩、修改压缩阈值或建立每轮心跳。到自然边界生成并核验交接三件套，停止修改被冻结仓库，再通过 create_thread（先 list_projects，目标 environment.type=local）创建唯一后继；若后继任务创建成功但首轮返回 422，或创建工具报错/回包不明，先完成 transferId 去重并确认没有可运行后继、`ack`、`taken`，再以原子 rename 禁止覆盖发布 `ownership.json={kind:"cancelled"}`；只有发布成功才取消派发并回到当前任务按接力手册方式 A 建立下一批 Goal 继续，输给 `taken` 或损坏则保持只读。后继先只读确认接收，等前任回合结束再 audit，并以原子发布 `ownership.json={kind:"taken"}` 成为唯一写者；前任确认接收后结束。严格执行接续规则中的 offer/created/ack/ownership 单写者顺序和不确定调用去重，不能只留下“下一步”就停工。

总目标范围保持总计划现有 R0–R6；不加入明确后置的功能，不重新引入 §24.96 撤销的整机旧备份检测/独立服务。保护用户未提交文件及真实 User、配置、脚本。自动接续授权不替代 R5.8 owner 签署、正式 UI 定稿或生产/部署/真实 User 门的明确授权；不得用测试通过代替实机或伪造总完成。

只有总验收完成、确需我裁决且无独立授权工作可做、或合理排查后的外部阻塞才停止整个链；正常交棒优先自动执行，故障时按方式 A 保持同任务续跑。本次明确授权后继任务继续遵守同样规则，用户后续停止/暂停/范围修订优先传递。最后交付逐项验收、提交与测试/实机证据、剩余项为零的总报告。首次真实跨任务自动接续已实测失败：`create_thread` 创建成功，但后继任务首轮返回 422 `missing field 'call_id'`；在修复并通过探针前不声称自动接力成功。
```
