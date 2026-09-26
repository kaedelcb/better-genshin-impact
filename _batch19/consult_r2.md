以下为只读会诊结论。边界声明：我不能运行命令/测试；`_batch19/`、两份 kimi 台账、`git show --stat`、提交后 `git status`、守卫复跑输出均未在材料内，相关真实性记为**未验证**。

【分级】必改  
【位置】material-manifest.json:8-16（`scope_patterns: []`、`batch_files: []`、`out_of_batch_changes`）  
【发现】机械清单把本批全部实质交付面都判成材料外：`batch_files` 为空，而 `git-diff-stat.txt` 的 5 个变更文件（`Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`、`Docs/design/onedragon-r5-handoff-2026-09-21.md`、`Docs/design/unified-job-registry-master-plan.md`、`Test/.../ClaimSurfaceManifest.txt`）全部落在 `out_of_batch_changes`。按 R6，材料外变更不得当作本批成果；但本批目标与 F1/F2/F3 处置又明确建立在这些 diff 上。  
【若成立的后果】批次 19 的“成果归属”在机械闸门层为空：会诊若判绿，等于把 out-of-batch diff 当本批成果；提交若按显式路径收口，manifest 又不能证明这些路径属于 batch19，后续审计可反驳“本批交付无范围凭据”。  
【反例尝试记录】我尝试在 manifest 内找任一可覆盖 `Docs/design/*` 或 `ClaimSurfaceManifest.txt` 的 `scope_patterns`/`batch_files` 条目：无；尝试用已暂存 diff 归属本批：`git-diff-staged.patch` 为空；尝试避开这 5 个文件构造本批成果：剩余只有 `_batch19/` 未跟踪目录与 91 项材料外残留，不能承载 owner 裁决登记。故此处不能写“无影响”，只能判材料归属必改。

【分级】重要  
【位置】git-status.txt:1-5；本批任务书“F1 处置”段；`Docs/design/onedragon-r5-handoff-2026-09-21.md:32`  
【发现】F1 的“处置闭合”按批次 18 收口时序先例**可以成立**，但 F1 本体（两禁入文档与 91 项材料外残留不得进入不可撤销提交）在当前材料中仍未闭合：材料只给出 unstaged diff 与空 staged diff，未给出提交动作、提交后 `git show --stat` 路径清单、提交后 `git status --porcelain`，也未给出 `_batch19/` 内落册证据。当前工作区仍存在两个禁入/边界文档的未提交修改（`mistletoe-session-relay-2026-09-24.md`、`unified-job-registry-master-plan.md`）以及大量 `.bak/.stale/TestResults/log/dll` 未跟踪残留；一旦使用 `git add -A`/`git commit -a` 或非显式路径，先例防护即失效。  
【若成立的后果】禁入文档或构建/审计残留进入提交后属于不可撤销历史污染；后续“仅含 4 项路径”的口头承诺无法机械追溯，收口时序先例被反例击穿。  
【反例尝试记录】我构造了两类反例：①`git add -A && git commit` 会把 5 个 tracked 修改与可加入的未跟踪残留一起纳入，明显越界；②显式 `git add <R5.3> <handoff> <ClaimSurfaceManifest.txt> _batch19/` 后再 `git show --stat` 可排除两禁入文档，但该正确路径的证据不在材料内。故风险未被证伪，记为未验证/未闭合，不采用全称否定。

【分级】建议  
【位置】`Docs/design/onedragon-r5-handoff-2026-09-21.md:29-34`；`Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt:223,281,528`  
【发现】F2 的行移位本身在 diff 中可见：批次 19 块已位于批次 18 块之后、ev5 块之前，块文未见因移位产生的新截断；我没有在移位本身中发现新的必改/重要问题。但任务书称“清单哈希不变 8848E2B1”与当前 `ClaimSurfaceManifest.txt` diff 的 +3 行需要口径分层：若指“仅移动 handoff 批次 19 行、内容不变”的瞬间，声明面集合可不变；若指本批最终落册，则 §24.119 已自述新增 3 行并再生清单，最终哈希应以提交时清单本体为准。  
【若成立的后果】若把“移位后哈希不变”误读为“本批最终清单也不变”，会与 §24.119 “新增 3 行/再生/终值以提交时为准”的口径相碰，造成审计读数歧义。  
【反例尝试记录】我尝试把 8848E2B1 同时解释为“移位后瞬时值”和“最终提交值”：前者可与内容不变相容；后者与 manifest +3 行不相容。因最终清单哈希/守卫输出未随材料给出，终值未验证。

F3 核对：保留“kimi 会诊闸门绿/机械闸门 exit 0”断言属于与批次 18 同款的收口时序处置；其真实性仍依赖“gate exit 0 先于 commit，非 0 则不提交并先修订行”的机械证据。当前材料未含该证据，记为未验证；但因 F1 已按重要项要求补提交后证据，同一证据包可同时覆盖 F3 成真条件，故不单独列为未闭合重要项。

【本轮裁定】仍有 2 项未闭合  
【未闭合清单】必改 B1＝manifest/scope 把本批实质交付全部判为 out-of-batch，须更正归属或登记凭据；重要 F1-本体＝禁入文档/材料外残留排除仍待“显式 4 路径提交＋提交后 `git show --stat`/`git status`＋`_batch19/` 证据”闭合。