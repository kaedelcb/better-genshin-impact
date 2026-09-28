import pathlib
p=pathlib.Path("Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md")
s=p.read_text(encoding="utf-8")
assert "§24.129.8" not in s
sec = """

### §24.129.8 会诊（第 2 轮，验证轮）与逐项处置（子批计数 2/8）

- **渠道/模型/强度**：既有 GPT 只读会诊工具；`gpt-6-astra`／medium；attempts=1（预检见 `consultation/preflight-v2.json`，白名单 10 文件 + 自动附加 diff）。
- **结论**：第 1 轮 9 项中 **已闭环 4 项**（MUST-3 原反例、MUST-5、IMPORTANT-6、IMPORTANT-7 原反例），**仍未闭环 5 项**（MUST-1 版本绑定、MUST-2 归属保护、MUST-4 关系不变量残余、IMPORTANT-8 异常边界、IMPORTANT-9 夹具/突变台账），并新报 **4 项 MUST + 4 项 IMPORTANT**。报告原文见 `consultation/review-round2-report.md`。
- **逐项处置（全部原级采纳，同一修复批；均新增夹具并绑定有效突变）**：

| # | 原级 | 会诊要点 | 处置（修复 + 反例 + 突变） |
|---|---|---|---|
| 1 | MUST | 新增文件作激活目标时，撤销激活**先写后查**，正常回滚必然归属冲突、且可能改写他方文件 | 归属预检**先于任何写入**（先判不存在、再判证据），并在撤销/恢复之后按**预核归属集合**清理新增（`Rollback_AddedActivationTarget_ConvergesAndRemovesIt`、`Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten`／M29、M30） |
| 2 | MUST | 回滚归属保护仍可通过**降级 manifest 标记**绕过 | 归属判据同样绑定本实例（`realEffectsRequired \\|\\| _effects is not null`）（`DowngradedManifestFlag_DoesNotBypassRollbackOwnership`；**判别力未由突变证明**，见下方残项） |
| 3 | MUST | 激活未绑定**写入所依据的版本**（前置检查与端口写入之间仍可漂移） | 激活请求新增 `ExpectedContentHash`，端口在写入前核对盘上字节哈希（两处），不符即 `activation_content_hash_mismatch` 置 Blocked（`Activation_VersionBinding_RejectsDriftAfterPrecheck`／M31） |
| 4 | MUST | 提交面文件集合核对**不是相等检查**（缺文件漏检） | 补齐「基线文件必须全部存在」一半 ⇒ `missing_baseline_file` 拒绝提交（`Commit_RejectsMissingBaselineFile`／M32） |
| 5 | MUST | 变更登记**重复身份**可由 `HashSet` 折叠绕过 | 结构关系不变量新增身份唯一性检查（`evidence_relation:duplicate_change_identity`）（`ManifestTamper_…` 新增分支／M34） |
| 6 | IMPORTANT | 只快照+登记 Added、尚未写入即中止的事务被**永久阻断** | 归属预检**先判不存在**（不存在无需证据），中止事务可安全回滚（`AbortedTransactionWithOnlyRecordedAddition_RollsBackSafely`／M30） |
| 7 | IMPORTANT | 异常边界仍遗漏复核与回滚读回路径 | `RecheckActivationRecord`、撤销后读回、`TryHashConfigFile` 的安全检查均纳入异常边界（收敛为拒绝/Blocked）（`EffectPortExceptions_…` 覆盖正向路径；M25） |
| 8 | IMPORTANT | 同实例 monitor 可重入 ⇒ 端口回调内重入可覆盖已完成回滚 | 新增 `_effectCallInProgress` **重入守卫**（覆盖全部变更入口）并在副作用返回后核对阶段未被改变（`ReentrantMutationFromEffectCallback_IsRejected`／M33） |
| 9 | IMPORTANT | 夹具与突变台账仍不一致（取消后未真实写入、M8 与 M6 重复、M22 判别力失真、矩阵残留说明、findings 记旧计数） | 「副作用后取消」改为**真实部分写入**；M8 重定义为「激活阶段前置门」并与 M6 去重；新增 M30（整块去除归属保护）以证明「他方文件被删除」可被检出；矩阵清除陈旧说明；`findings.md` 与本节统一为 33 项突变口径（`ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks` 等） |

- **计数**：本轮后子批累计 **2/8**（无失败/超时请求）。仍有未闭合 MUST/IMPORTANT ⇒ 继续复会诊（第 3 轮）。
- **本批残项（如实）**：`REF-C4`（同实例并发提交/回滚）与「降级 manifest 标记不得绕过归属保护」两条，**未取得可杀死对应夹具的单点突变**（尝试记录见 §24.129.8 表格第 2 行与矩阵 `REF-C4.reason`）：前者在两种获胜顺序下不变量均成立，后者以「判据只依赖标记」构造的探测突变仍使夹具通过。二者保留**夹具级反例**但**不主张突变判别力**，不计入「关键断言均已突变验证」的陈述。
- **边界**：会诊只审阅所附材料与 diff；B–F、生产接线与 owner 检查点不作为本批缺陷，仍关闭。""".rstrip()+"\n"
p.write_text(s.rstrip("\n")+sec, encoding="utf-8")
print("§24.129.8 appended")
