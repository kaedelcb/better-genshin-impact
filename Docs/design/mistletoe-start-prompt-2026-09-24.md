# 槲寄生计划：首次新任务启动提示词

将下面整段粘贴到该项目的新任务。首次粘贴启动后，正常批次接续不再需要手动触发。

```text
请启动槲寄生计划的串行自动执行链，持续推进到既定 R0–R6 发布路线全部验收完成。

这是我的明确授权：本任务及其每一个后继任务可在安全批次边界自动创建唯一的下一 Codex 桌面任务，携带交接材料、当前授权、生产门和剩余工作继续执行；不要让我逐个新建任务、粘贴交接或输入“继续”。我明确选择直接使用已保存项目的本地目录 E:\Program Files\better-genshin-impact-LCB，不自动另开 worktree、不切分支。首次启动前检查是否已有同计划活动链，避免重复施工。

先读取：
1. E:\Program Files\better-genshin-impact-LCB\AGENTS.md 与其中的 bgi-project-development 技能。
2. E:\Program Files\better-genshin-impact-LCB\Docs\design\mistletoe-session-relay-2026-09-24.md（本次自动接续规则，必须执行）。
3. E:\Program Files\better-genshin-impact-LCB\槲寄生调度器总计划.md 的当前阶段与退出条件。
4. E:\Program Files\better-genshin-impact-LCB\Docs\design\onedragon-r5-handoff-2026-09-21.md 的当前权威状态及执行纪律。
5. E:\Program Files\better-genshin-impact-LCB\Docs\design\onedragon-r5-3-external-start-lifecycle-2026-09-21.md 的 §24.96 与当前批次必要合同。
6. C:\Users\Administrator\.codex\hooks\semantic-protocol.md。

不要回读旧聊天，不全量加载历史。先核对真实分支、HEAD、tracked/untracked、代码和验证材料，从当前 R5 剩余依赖选择一个可独立验证的批次，写清结果、约束、完成证据，然后调用 create_goal 建立本批次目标并立即施工。Goal 包含本批验证及必要的安全交棒，不能把整个项目完成混同为当前批次完成。后继任务同样先审计 manifest，再建立自己的 Goal，旧 Goal 不会自动转移。

连续完成审计、反例、实现、既定 GPT 会诊、处置、回归、按文件提交和进度更新。常规会诊 gpt-6-sol/medium，最难或安全敏感 gpt-6-astra/medium；保留全部固定质量门，不以换任务规避失败或额度。已裁决事项不要再次问我，新的必要业务选择集中提交，先完成不依赖裁决的工作。

按任务语义决定继续或切分，不按时间/工具数强制切分；禁止主动压缩、修改压缩阈值或建立每轮心跳。到自然边界生成并核验交接三件套，停止修改被冻结仓库，再通过 create_thread（先 list_projects，目标 environment.type=local）创建唯一后继。后继先只读确认接收，等前任回合结束再 audit 接管；前任确认接收后结束。严格执行接续规则中的 offer/created/ack/taken 单写者顺序和不确定调用去重，不能只留下“下一步”就停工。

总目标范围保持总计划现有 R0–R6；不加入明确后置的功能，不重新引入 §24.96 撤销的整机旧备份检测/独立服务。保护用户未提交文件及真实 User、配置、脚本。自动接续授权不替代 R5.8 owner 签署、正式 UI 定稿或生产/部署/真实 User 门的明确授权；不得用测试通过代替实机或伪造总完成。

只有总验收完成、确需我裁决且无独立授权工作可做、或合理排查后的外部阻塞才停止整个链；正常交棒由你自动执行。本次明确授权后继任务继续遵守同样规则，用户后续停止/暂停/范围修订优先传递。最后交付逐项验收、提交与测试/实机证据、剩余项为零的总报告。首次真实自动接续尚待验证，运行中保存证据，不提前声称成功。
```
