import io, json, os
base = r"_workflow/wave3-bo8-bo9"
os.makedirs(os.path.join("_workflow", "wave3-bo8-bo9", "consultation"), exist_ok=True)
req = """# 会诊请求 v1（主线施工批 `wave3-bo8-bo9-2026-09-28`，2026-09-28）

固定模型/强度：`gpt-6-astra` / `medium`。渠道：既有 GPT 会诊工具（read-only，自动附本批工作区差异）。

## 本批目标（请审查）
在茶包主线把 R5 Wave3 剩余两项原级未闭合 **IMPORTANT** 施工到真实 Runner：**BO-8 R29**（恢复点之后推进段无完成过滤）
与 **BO-9 R34 F5**（多有效停驻跨轮次推进）；并在同一份 `WorkflowRunner.cs` 哈希重绑定内按登记口径修正
**BO-6/7-D1**（建议级注释陈旧）。BO-8 与 BO-9 同属 `DriveAsync` 推进层，在同一子批内按依赖顺序收敛。

## 送审材料（本目录同批）
- `context.md`（objective）／`findings.md`（findings）／`budget.md`（budget）；
- 产品与夹具全文：`MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs`、
  `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs`；
- 生成清单摘录：`ClaimSurfaceManifest.txt` 本批新增行；状态登记摘录：R5.3 §24.127 全文；
- 机读证据：`final-v3/targeted-final.trx`（定向 96/96）、`final-v3/assistant-full-final.trx`（1565/2/0/1567）、
  `baseline/targeted-baseline.trx`（93/93）、`baseline/assistant-full-baseline.trx`（1562/2/0/1564）、
  `final-v3/testid-comparison.json`（1564 unchanged／3 added／0 removed／0 changed）、
  `mutation-records-final.json`（12 项 baseline Passed／mutant Failed／restored Passed）、
  `red-final/bo8-bo9-red-final.trx` 与 `red-final/prefix-source.cs`（反例先行红，开工字节 + 最终夹具）、
  `claims/*`（声明面再生＋清除变量复跑）、`deploy-target-before.txt`／`deploy-target-final.txt`；
- 工作区差异（由会诊工具自动附加）：全量 `git status --porcelain` 与本批 scoped unstaged／staged diff。

## 本轮请回答（逐项给结论与依据）
1. **BO-8 R29 是否可按原级（IMPORTANT）登记为 closed？** 特别请核对：把"推进段完成过滤"改为**无条件生效**是否引入
   "该执行而未执行"的新风险——例如循环回绕（`plan.Next` 到下一轮）、修订语义下应由 candidate 承载的出现、
   前置／闸门（skip/reject）／失败／unknown 路径、以及 `waitLocally` 停驻标记是否会被误当"已完成"而不再重驱；
   判据只用稳定出现身份 `(NodeId, Occurrence, LoopIteration)` 是否充分。
2. **BO-9 R34 F5 是否可按原级（IMPORTANT）登记为 closed？** 特别请核对链尾重入是否可能：
   (a) 不终止（重入循环）；(b) 重开 BO-6 明示的"持久 `TailReached` ＋未清偿停驻 ⇒ Failed 且零提交"防御语义；
   (c) 漏驱动、或经重入跳跃而二次提交已完成出现；(d) 与轮次起点等待 `AwaitLoopRoundStartAsync`／
   `LastScheduledRoundWait` 语义冲突（重入到高轮次出现时）。
3. **BO-6/7-D1 的修正是否只改注释、不改行为**（逐处核对 `:1400–1403`、`:1486`、`:1535–1537` 及同步的文档注释），
   并判断 12 项反向突变（8 项在新字节上 rebased ＋ 4 项新增）是否足以支撑这三项的新旧断言判别力。
4. **是否存在新的 MUST／IMPORTANT？** 若有，请给出可观察后果、反例构造思路与最小修复方向。
5. 收尾请明确一句："**本批是否仍有未闭合的 MUST/IMPORTANT：是／否**"。

## 边界（不变）
只审本批材料：BO-8、BO-9 与 BO-6/7-D1。不审 R5.6／R6.1／R6 diff guard，不审未消费的并行成果；
未做实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5、热键面验证，外部执行边界为 fake，生产门保持关闭。
本轮结论将原文登记进 `consultation/review-outcome-v1.md`；R5.3 §24.127.4 在该轮返回后按原文补写，
本节以"§24.127.4 引用本轮结论"为登记口径以避免版本漂移。
"""
io.open(os.path.join(base, "consultation", "review-request-v1.md"), "w", encoding="utf-8", newline="\r\n").write(req)

import hashlib
files = [
    base + "/consultation/review-request-v1.md",
    base + "/context.md",
    base + "/findings.md",
    base + "/budget.md",
    "MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs",
    "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs",
    "Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt",
    "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md",
    base + "/mutation-records-final.json",
    base + "/final-v3/testid-comparison.json",
    base + "/final-v3/targeted-final.trx",
    base + "/final-v3/assistant-full-final.trx",
    base + "/baseline/targeted-baseline.trx",
    base + "/baseline/assistant-full-baseline.trx",
    base + "/red-final/bo8-bo9-red-final.trx",
    base + "/red-final/prefix-source.cs",
    base + "/red-final/red-final-exits.json",
    base + "/claims/manifest-diff-summary.json",
    base + "/claims/manifest-before.sha256",
    base + "/claims/manifest-after-regen.sha256",
    base + "/claims/manifest-after-noenv.sha256",
    base + "/claims/claim-regen.trx",
    base + "/claims/claim-noenv.trx",
    base + "/deploy-target-before.txt",
    base + "/deploy-target-final.txt",
]
sizes = {f: os.path.getsize(f) for f in files}
total = sum(sizes.values())
def toks(path):
    data = open(path, "rb").read().decode("utf-8", errors="replace")
    cjk = sum(1 for c in data if "\u4e00" <= c <= "\u9fff")
    return cjk / 1.45 + (len(data) - cjk) / 3.6
est = int(sum(toks(f) for f in files))
pre = {
    "kind": "consultation capacity pre-check (record only; no request dispatched as part of this step)",
    "channel": "existing GPT consultation tool (read-only, mode=review; workspace diff auto-attached)",
    "model": "gpt-6-astra",
    "effort": "medium",
    "fixed_snapshot": {"branch": "main-OldTeaBag-B168",
                       "head": "f47b57b1cba90e78624ae6b6d2236aafd402f0e1",
                       "note": "uncommitted construction state; local audit snapshot _workflow/wave3-bo8-bo9/review-v1"},
    "allowed_files": files,
    "file_bytes": sizes,
    "total_bytes": total,
    "estimated_input_tokens": est,
    "estimate_method": "tokens ~= cjk_chars/1.45 + non_cjk_chars/3.6 per decoded file; order-of-magnitude, conservative for the code-heavy mix",
    "assumed_model_window": 272000,
    "assumed_effective_window_95pct": 258400,
    "estimated_fraction_of_effective_window": round(est / 258400, 3),
    "attachment_transport_limits": "30 files / 512 KiB per file / 2 MiB total",
    "attachment_transport_status": "within limits ({} files of {} allowed; max single file {} B of 524288; total {} B of 2097152)".format(
        len(files), 30, max(sizes.values()), total),
    "excluded_material": [
        {"path": base + "/final-v3/testid-comparison.json", "reason": "included (small)"},
        {"path": base + "/mutations-final/**", "reason": "12 个突变目录含 36 份 TRX 与 36 份构建日志，其可审内容由 mutation-records-final.json（三阶段退出码、目标 testId/名称、断言行、源码哈希与 TRX 路径）承载；逐份 TRX 保留在磁盘供按需核对。"},
        {"path": "ClaimsSurfaceManifest.txt 全文（609 行 / ~220 KB）", "reason": "生成产物；以本批新增 4 条代表性摘录 + manifest-diff-summary.json + 两份守卫 TRX 承载，全文哈希绑定在证据 source_sha256 中。"},
    ],
    "fallback_assessment": "Estimated input is about {}% of the assumed effective window, so the local read-only CLI fallback condition (>= one third) is judged {} on this estimate; the existing GPT consultation tool is used first.".format(
        round(est / 258400 * 100), "MET" if est / 258400 >= 1 / 3 else "NOT met"),
}
io.open(os.path.join(base, "consultation", "preflight-v1.json"), "w", encoding="utf-8", newline="\n").write(
    json.dumps(pre, ensure_ascii=False, indent=2) + "\n")
print(json.dumps({k: v for k, v in pre.items() if k in ("total_bytes", "estimated_input_tokens", "estimated_fraction_of_effective_window", "attachment_transport_status", "fallback_assessment")}, ensure_ascii=False, indent=1))
