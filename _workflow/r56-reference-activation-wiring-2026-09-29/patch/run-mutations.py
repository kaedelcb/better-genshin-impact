import hashlib, json, pathlib, subprocess, sys, re

ROOT = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
WT = ROOT / "MultiplayerHoeingAssistant" / "Services" / "TaskCenter" / "MigrationSwitchTransaction.cs"
SV = ROOT / "MultiplayerHoeingAssistant" / "Services" / "TaskCenter" / "MigrationReferenceActivation.cs"
OUT = ROOT / "_workflow" / "r56-reference-activation-wiring-2026-09-29" / "mutations"
OUT.mkdir(parents=True, exist_ok=True)

MUTATIONS = [
  ("M1-no-readback-confirmation", WT,
   "不读回确认就推进阶段（真实副作用读回被跳过 = 假成功）",
   """                if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                    return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);""",
   """                // MUTANT: 读回确认被跳过""",
   "R56ReferenceActivationWiringTests.SelfReportedSuccessWithoutRealWrite_BlocksOnReadback"),

  ("M2-no-cross-check-with-change-registry", WT,
   "去掉写集与变更登记的交叉核对（漏项/多项不再拒绝）",
   """            if (!seen.Contains(PathKey(record.Path)))
                return "reference_writeset_mismatch:registered_not_covered:" + record.Path;   // 漏项""",
   """            // MUTANT""",
   "R56ReferenceActivationWiringTests.WritesetMismatch_RegisteredNotCovered_Blocks"),

  ("M3-no-outside-write-detection", WT,
   "去掉「写集外不得改动」检测",
   """                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);""",
   """                // MUTANT""",
   "R56ReferenceActivationWiringTests.WriteOutsideDeclaredWriteset_Blocks"),

  ("M4-no-activation-target-membership", WT,
   "去掉「激活目标必须在已确认写集内」校验",
   """        if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(request.Path)))
            return "activation_target_not_in_writeset:" + request.Path;                       // REF-F4""",
   """        // MUTANT""",
   "R56ReferenceActivationWiringTests.ActivationTargetOutsideWriteset_Blocks"),

  ("M5-no-commit-recheck", WT,
   "去掉提交前的写集/激活读回复核（提交不再核对盘上漂移）",
   """                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);
                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);""",
   """                // MUTANT: 提交前的写集/激活读回复核整体被跳过（两层复核对同一漂移各自独立判别，
                // 单独去掉任一层会被另一层拦下，故此处按「整块削弱」定义突变以取得判别力）""",
   "R56ReferenceActivationWiringTests.Commit_RefusesWhenWrittenFileDriftsAfterActivation"),

  ("M6-no-stage-mark-gate", WT,
   "去掉「注入真实端口后阶段标记入口必须拒绝」的门禁",
   """        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)
                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);""",
   """        => Advance(MigrationStage.ReferenceUpdating);   // MUTANT""",
   "R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired"),

  ("M7-no-activation-writeset-hash-sync", WT,
   "激活后不同步更新写集哈希（写集与盘上状态脱钩）",
   """            foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(request.Path)).ToList())
                m.ReferenceWriteSet[key] = hash;""",
   """            // MUTANT""",
   "R56ReferenceActivationWiringTests.Rollback_RevertsActivationAndBytes_KeepsForeignAdditions"),

  ("M8-no-reference-gate-for-activation", WT,
   "去掉「激活须先有已确认真实引用更新」的阶段前置",
   """            if (m.Stage != MigrationStage.ReferenceUpdating)
                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);""",
   """            // MUTANT""",
   "R56ReferenceActivationWiringTests.Activation_RealWrite_RequiresConfirmedReferenceUpdate"),
]

def run(args, log):
    p = subprocess.run(args, cwd=str(ROOT), capture_output=True, text=True, encoding="utf-8", errors="replace")
    log.write("$ " + " ".join(args) + "\n" + (p.stdout or "") + (p.stderr or "") + "\n[exit=%d]\n\n" % p.returncode)
    log.flush()
    return p.returncode, (p.stdout or "") + (p.stderr or "")

def trx_outcomes(path, test_name):
    """Return {testName: outcome} for every result whose name contains the fixture test name (theory cases included)."""
    import xml.etree.ElementTree as ET
    ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
    out, msgs = {}, {}
    if not path.exists(): return out, msgs
    for r in ET.parse(str(path)).getroot().iter(ns + "UnitTestResult"):
        name = r.get("testName", "")
        if test_name in name:
            out[name] = r.get("outcome")
            msg = r.find(".//" + ns + "Message")
            msgs[name] = (msg.text or "") if msg is not None else ""
    return out, msgs

def summarize(outcomes):
    if not outcomes: return "NOT_FOUND"
    if all(v == "Passed" for v in outcomes.values()): return "Passed"
    if any(v == "Failed" for v in outcomes.values()): return "Failed"
    return "|".join(sorted(set(outcomes.values())))

results = []
for mid, target, desc, old, new, test in MUTATIONS:
    md = OUT / mid
    md.mkdir(parents=True, exist_ok=True)
    src = target.read_text(encoding="utf-8")
    original_sha = hashlib.sha256(src.encode()).hexdigest()
    assert src.count(old) == 1, (mid, src.count(old))
    with (md / "build.log").open("w", encoding="utf-8") as bl:
        rc, out = run(["dotnet","build","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                       "-p:DeployToBgiTools=false","-v","q","--nologo"], bl)
    assert rc == 0, (mid, "baseline build failed")

    trx = md / "baseline.trx"
    rc, out = run(["dotnet","test","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                   "-p:DeployToBgiTools=false","--no-build","--filter","FullyQualifiedName~"+test,
                   "--logger","trx;LogFileName=baseline.trx","--results-directory",str(md)], (md/"baseline.log").open("w",encoding="utf-8"))
    base_outcome, _ = trx_outcomes(trx, test)
    base_outcome = summarize(base_outcome)

    mutant = src.replace(old, new, 1)
    target.write_text(mutant, encoding="utf-8")
    mutant_sha = hashlib.sha256(target.read_text(encoding="utf-8").encode()).hexdigest()
    with (md / "mutant-build.log").open("w", encoding="utf-8") as bl:
        rc, out = run(["dotnet","build","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                       "-p:DeployToBgiTools=false","-v","q","--nologo"], bl)
    mutant_build_exit = rc
    mutant_outcome, mutant_msg = (None, "build_failed")
    if rc == 0:
        rc2, out2 = run(["dotnet","test","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                       "-p:DeployToBgiTools=false","--no-build","--filter","FullyQualifiedName~"+test,
                       "--logger","trx;LogFileName=mutant.trx","--results-directory",str(md)], (md/"mutant.log").open("w",encoding="utf-8"))
        raw, msgs = trx_outcomes(md/"mutant.trx", test)
        mutant_outcome = summarize(raw)
        mutant_msg = " | ".join(msgs.values())

    target.write_text(src, encoding="utf-8")
    restored_sha = hashlib.sha256(target.read_text(encoding="utf-8").encode()).hexdigest()
    with (md / "restored-build.log").open("w", encoding="utf-8") as bl:
        rc, out = run(["dotnet","build","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                       "-p:DeployToBgiTools=false","-v","q","--nologo"], bl)
    restored_build_exit = rc
    restored_outcome = "NOT_RUN"
    if rc == 0:
        run(["dotnet","test","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
             "-p:DeployToBgiTools=false","--no-build","--filter","FullyQualifiedName~"+test,
             "--logger","trx;LogFileName=restored.trx","--results-directory",str(md)], (md/"restored.log").open("w",encoding="utf-8"))
        raw, _ = trx_outcomes(md/"restored.trx", test)
        restored_outcome = summarize(raw)

    entry = {"id": mid, "source": str(target.relative_to(ROOT)).replace("\\","/"), "description": desc,
             "original_sha256": original_sha, "mutant_sha256": mutant_sha, "restored_sha256": restored_sha,
             "restored_exact": original_sha == restored_sha,
             "baseline_outcome": base_outcome, "mutant_build_exit": mutant_build_exit,
             "mutant_outcome": mutant_outcome, "mutant_message": mutant_msg[:300],
             "restored_outcome": restored_outcome}
    (md / "result.json").write_text(json.dumps(entry, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    results.append(entry)
    print(mid, base_outcome, mutant_build_exit, mutant_outcome, restored_outcome, "restored_exact=", entry["restored_exact"], flush=True)

(OUT / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
ok = all(r["baseline_outcome"] == "Passed" and r["mutant_outcome"] == "Failed" and r["restored_outcome"] == "Passed"
         and r["restored_exact"] for r in results)
print("ALL_MUTATIONS_VALID=", ok)
