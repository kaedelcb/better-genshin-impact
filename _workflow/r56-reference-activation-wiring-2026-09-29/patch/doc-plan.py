import pathlib
# 1) 总计划：追加进度日志条目（与既有 2026-09-28 条目同形）
p = pathlib.Path("槲寄生调度器总计划.md")
s = p.read_text(encoding="utf-8")
assert "2026-09-29：R5.6 A 项主线施工" not in s
anchor = "## 2026-09-28：R5.6 主线迁移集成批（并行成果接收与集成验证）"
assert s.count(anchor) == 1
entry = """## 2026-09-29：R5.6 A 项主线施工（隔离配置根的真实引用写入与 candidate→active 激活接线）

- **性质与范围**：施工批。只做 R5.6 验收矩阵 **A 项**（真实引用更新 + `candidate→active` 激活 + 事务编排），全程**隔离配置根**；**不**做真实 `User` 目录切换、**不**开生产入口、**不**启用 E3/E4/E5／热键／R5.8 签署。
- **反例先行**：先证明 `MarkReferenceUpdateCompleted`／`MarkActivated` 仅推进阶段——未注入真实副作用端口时**零写入**即可推进到 `Activated` 并提交成功（配置根字节始终未变，假成功）；注入端口后同一入口被拒（`real_side_effects_required`）。
- **实现**：新增真实副作用端口与实现（`MigrationReferenceActivation.cs`：引用重命名／新增文件／激活读改写，临时文件+同目录替换、保留 BOM、写前复检、解析失败隔离跳过、四类结果与已落盘数如实回报）；事务新增 `ApplyReferenceUpdate`／`ActivateCandidate`（写集声明校验 → 真实副作用 → 语义+字节读回 → **才**推进阶段并持久化读回证据；拒绝/未知/副作用后取消 fail-closed 置 `Blocked`；副作用前取消保持阶段；已到阶段幂等复核；提交前复核写集与激活记录；回滚先撤销激活再恢复旧字节并要求旧态一致）；manifest 增 `realEffectsRequired`／`referenceWriteSet`／`activationRecord` 并纳入完整性与结构不变量。
- **验证**：定向 **99/99**（同条件基线 70/70）；助手全量 **1599 通过 / 2 跳过 / 0 失败 / 1601**（同条件开工基线 **1570/2/0/1572**；testId added=29／removed=0／changed=0）；**17 项反向突变**全部 baseline Passed／mutant Failed／restored Passed 且源码逐字节恢复；23 行状态/并发/故障矩阵全部 covered 并各绑定有效突变；部署目标未留下可观察变化（最新写入时间早于本批）。
- **owner 检查点（1 项）**：BGI 侧 W4 `OneDragonConfigReferenceService` 为 BGI 程序集内 `internal`，助手工程对其**零引用** ⇒ 生产引用写方的归属（BGI 直调／共享库／IPC 委派）**无权威裁决**，生产接线保持关闭，待 owner 定夺。
- **门禁**：**B–F 六项启用前置与真实入口/实机/真实 User 门继续未验收**；本批**不**等于 R5.6 完成，也不等于 R5 完成。详见 R5.3 §24.129 与 `_workflow/r56-reference-activation-wiring-2026-09-29/`。

"""
s = s.replace(anchor, entry + anchor, 1)
p.write_text(s, encoding="utf-8")
print("plan entry inserted; bytes=", len(p.read_bytes()))

# 2) 并行索引：登记 A 项进展与下一依赖（不改已登记来源的集成状态）
q = pathlib.Path("Docs/design/mistletoe-parallel-deliveries.md")
t = q.read_text(encoding="utf-8")
assert "§24.129" not in t
anchor2 = "## 无需 owner 提醒的执行闭环"
assert t.count(anchor2) == 1
add = """### R5.6 A 项进展（主线施工，2026-09-29）

`r56-migration-audit` 与 `r56-activation-prep` 的集成状态**不变**（均为 `integrated`），其 A–F 六项前置的**状态更新**如下：

- **A（真实引用更新 + candidate→active 激活）**：已由主线子批 `r56-reference-activation-wiring-2026-09-29` 在**隔离配置根**内接线并验证（定向 99/99、助手全量 1599/2/0/1601、17 项反向突变、23 行矩阵覆盖）。**仍未验收**：真实 `User` 目录、真实入口与生产消费。
- **B–F**：**未验收**，状态不变（助手元数据恢复范围、生产检查点、真实静止窗口、真实入口回执、目标机路径身份）。
- **owner 检查点（新增 1 项）**：生产引用写方归属未裁决（BGI 侧 W4 为 `internal`、助手程序集零引用）。详见 R5.3 §24.129.5。
- **下一依赖**：owner 裁决写方归属 → 再定 B/C/D 的接线顺序；真实 User 切换与 R5.8 签署仍在 owner 侧。

"""
t = t.replace(anchor2, add + anchor2, 1)
q.write_text(t, encoding="utf-8")
print("index note inserted; bytes=", len(q.read_bytes()))
