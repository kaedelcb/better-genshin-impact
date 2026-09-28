# 反向突变台账（28 项，供审查者核对判别力）

每项＝对最终源码的**单点削弱**；判定要求 baseline Passed / mutant Failed（构建 exit 0、测试 exit>0 且命中具名断言）/ restored Passed，
且恢复后源码**逐字节**等于原始哈希。逐项目录含 `build.log`、`baseline.log|trx`、`mutant-build.log`、`mutant.log|trx`、`restored-build.log`、`restored.log|trx`、`record.json`。

## 逐项：补丁（before → after）与判定

### M1-no-readback-confirmation
- 描述：不读回确认就推进阶段（真实副作用读回被跳过 = 假成功）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `6d767ff40954f40b464146623bea7d0a721313bbfdbbc29997ac0b373a4570bc`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.SelfReportedSuccessWithoutRealWrite_BlocksOnReadback`（testId `9120aa3d-1436-a8f6-fa8a-da29cec5c17f`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `SelfReportedSuccessWithoutRealWrite_BlocksOnReadback`

```text
--- MUTANT 补丁（before → after）---
替换前：
                    if (!_effects.TryReadReferenceState(_configRoot, target, out var detail))
                        return MarkBlocked("reference_readback_failed:" + target.Path + ":" + detail);
替换后：
                    // MUTANT: 读回确认被跳过
```

### M2-no-cross-check-with-change-registry
- 描述：去掉写集与变更登记的交叉核对（漏项不再拒绝）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `db08f0eff7df4d6a1e521692893885ba0409f04861e39d0993438428f2856251`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.WritesetMismatch_RegisteredNotCovered_Blocks`（testId `bb355b0d-c65c-d267-0304-1c78222244af`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `WritesetMismatch_RegisteredNotCovered_Blocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (!seen.Contains(PathKey(record.Path)))
                return "reference_writeset_mismatch:registered_not_covered:" + record.Path;   // 漏项
替换后：
            // MUTANT
```

### M3-no-outside-write-detection
- 描述：去掉「写集外不得改动」检测
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `80b7ae4591a9755991b5724711ec94b8bfc51e7c9ad30b50c91855e23404a472`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.WriteOutsideDeclaredWriteset_Blocks(deleteInstead: False)`（testId `6608a8e3-0b21-3f57-a0f7-a2d88397e742`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `WriteOutsideDeclaredWriteset_Blocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
                if (!TryHashConfigFile(baseline.Key, out var currentHash, out var unexpectedProblem))
                {
                    if (unexpectedProblem == "file_missing") return MarkBlocked("unexpected_outside_write:deleted:" + baseline.Key);
                    return MarkBlocked("unexpected_outside_write:" + unexpectedProblem + ":" + baseline.Key);
                }
                if (!string.Equals(currentHash, baseline.Value, StringComparison.Ordinal))
                    return MarkBlocked("unexpected_write_outside_writeset:" + baseline.Key);
替换后：
                // MUTANT: 写集外改动检测（改写支 + 删除支）整体被跳过
```

### M4-no-activation-target-membership
- 描述：去掉「激活目标必须在已确认写集内」校验
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `7716b7fe642995940cc92afedae6ad87feff48cd535ae7f0c9688ecf8e82e9ea`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ActivationTargetOutsideWriteset_Blocks`（testId `3536a152-f6d5-71c7-091e-e34429b764b9`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `ActivationTargetOutsideWriteset_Blocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
        if (!m.ReferenceWriteSet.Any(p => PathKey(p.Key) == PathKey(request.Path)))
            return "activation_target_not_in_writeset:" + request.Path;                       // REF-F4
替换后：
        // MUTANT
```

### M5-no-commit-recheck
- 描述：去掉提交前的写集/激活读回复核
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `ab531386388bfa34537ddd3a9b7f86e57fe71d3ebb0a6022894ed12b1821744a`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.Commit_RefusesWhenWrittenFileDriftsAfterActivation`（testId `0c64790c-23ff-4198-f8b1-505ea6864375`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `Commit_RefusesWhenWrittenFileDriftsAfterActivation`

```text
--- MUTANT 补丁（before → after）---
替换前：
                var rechecked = RecheckReferenceWriteSet(m);
                if (!rechecked.Success) return MigrationResult.Fail("commit_recheck_failed:" + rechecked.Reason, m.Stage);
                var activationRecheck = RecheckActivationRecord(m);
                if (!activationRecheck.Success) return MigrationResult.Fail("commit_recheck_failed:" + activationRecheck.Reason, m.Stage);
替换后：
                // MUTANT: 提交前的写集/激活读回复核整体被跳过
```

### M6-no-stage-mark-gate
- 描述：去掉「注入真实端口后阶段标记入口必须拒绝」的门禁
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `db073f9138cbdb39a8680db69306ad1acd693ba993db03085aae150a0a3d48b0`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired`（testId `4e66362b-8fbd-926e-95d1-fc6403406da0`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `LegacyStageMarks_ReportSuccessWithoutAnyWrite_AndAreRejectedOnceEffectsAreWired`

```text
--- MUTANT 补丁（before → after）---
替换前：
        => _effects is null ? Advance(MigrationStage.ReferenceUpdating)
                            : MigrationResult.Fail("real_side_effects_required", LoadManifest()?.Stage ?? MigrationStage.None);
替换后：
        => Advance(MigrationStage.ReferenceUpdating);   // MUTANT
```

### M7-no-activation-writeset-hash-sync
- 描述：激活后不同步更新写集哈希（写集与盘上状态脱钩）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `1feb5ce4210899c029b33e97ad8eb1b94f3db1e5109a2d9cdad479462112bc2b`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.CommittedRealTransaction_AuthorizesProductionExactlyOnce`（testId `dc0b9da1-85c1-362f-ba3d-726bbc7ca289`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.True() Failure` + 断言栈 `CommittedRealTransaction_AuthorizesProductionExactlyOnce`

```text
--- MUTANT 补丁（before → after）---
替换前：
            foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(request.Path)).ToList())
                m.ReferenceWriteSet[key] = hash;
替换后：
            // MUTANT
```

### M9-fail-open-on-non-success
- 描述：非成功结果（拒绝/未知/取消）被当作成功继续（fail-open）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `107dfce27d9f765a44b3a3773187fabc2fcf6e1a3f25bdb6e78f7c168e00dd74`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite`（testId `5370c416-3263-2816-0923-f3ca38400567`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.StartsWith() Failure: String start does not match` + 断言栈 `ReferenceUpdate_Rejected_PersistsBlockedWithReasonAndNoWrite`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (result.Outcome != MigrationEffectOutcome.Succeeded)
            {
                if (result.Outcome == MigrationEffectOutcome.Cancelled && result.CompletedWrites == 0)
                    return MigrationResult.Fail("reference_update_cancelled_before_effects:" + result.Reason, m.Stage);
                return MarkBlocked("reference_update_" + result.Outcome.ToString().ToLowerInvariant() + ":"
                    + result.Reason + ";writes=" + result.CompletedWrites);
            }
替换后：
            // MUTANT: 失败/未知/取消一律继续
```

### M10-no-production-commit-gate
- 描述：未提交也可生产执行（生产闸门被移除）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `0c5aa78f9bcdc66c936cfb6a13047411c6d9243ca2b17405e8dad56d9d60f623`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ActivatedButUncommitted_ProductionStillRunsNothing`（testId `34602a8b-5ceb-3ec8-3e54-b72f31f37323`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `ActivatedButUncommitted_ProductionStillRunsNothing`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (m.Stage != MigrationStage.Committed) return MigrationResult.Fail("not_committed:" + m.Stage, m.Stage);
替换后：
            // MUTANT: 未提交也可生产执行
```

### M11-no-lock-guard-on-real-effects
- 描述：真实副作用入口不再要求持有独占锁
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `bee62eda199ea9886513e680af80546575a3ce540b114b98bfac4a76b2f60099`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.WithoutExclusiveLock_RealSideEffectsAreRejected`（testId `d17031e9-76c3-0d5d-54f0-10ec27c99423`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `WithoutExclusiveLock_RealSideEffectsAreRejected`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);
替换后：
            var m = LoadValidated();
            if (m is null) return MigrationResult.Fail("manifest_missing_or_invalid", MigrationStage.None);

            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);
```

### M12-stage-persisted-before-effect
- 描述：副作用之前就持久化 ReferenceUpdating 阶段
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `57675810b6cfecb069617a077f811c5b92edf9e801e1e4bb818cb01e02576c11`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes`（testId `5d121dc3-6ebf-526f-284c-f33ad5c5d66d`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Contains() Failure: Sub-string not found` + 断言栈 `CrashAfterRealWriteBeforeStagePersist_RecoversToOldBytes`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);
替换后：
            if (ValidateReferencePlan(m, plan) is { } planProblem) return MarkBlocked(planProblem);
            m.Stage = MigrationStage.ReferenceUpdating;   // MUTANT: 阶段先于副作用持久化
            WriteManifest(m);
            return MigrationResult.Ok(m.Stage);
```

### M13-no-activation-readback
- 描述：激活后不读回状态就推进阶段
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `361c8ab88e259924a5a8a1e03bdfe53f8a61eb2d520bcc46a4061b450b3bd315`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ActivationReadbackMismatch_Blocks`（testId `d79a5bcb-ff16-67dd-0cd7-7ec7703ff0ff`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `ActivationReadbackMismatch_Blocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
                if (!_effects.TryReadActivationStatus(_configRoot, request.Path, out var status, out var detail)
                    || !string.Equals(status, request.TargetStatus, StringComparison.Ordinal))
                    return MarkBlocked("activation_readback_failed:" + request.Path + ":" + detail);
替换后：
                    // MUTANT: 激活状态读回被跳过
```

### M14-no-real-evidence-invariants
- 描述：结构校验不再要求真实写集/激活证据
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `85a0c7fe426dd4eb5e4ebf16061e0072746a8e9d78c39e838e648ee101e22042`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected`（testId `7bc93f93-430e-82c2-804a-4c05389c58f9`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Null() Failure: Value is not null` + 断言栈 `ManifestTamper_WithoutRealEvidence_IsRejected`

```text
--- MUTANT 补丁（before → after）---
替换前：
        if (m.RealEffectsRequired)
        {
            if (m.Stage is MigrationStage.ReferenceUpdating or MigrationStage.Activated or MigrationStage.Committed
                && m.ReferenceWriteSet.Count == 0) return false;
            if (m.Stage is MigrationStage.Activated or MigrationStage.Committed && m.ActivationRecord is null) return false;
        }
替换后：
        // MUTANT: 真实证据不变量被删除
```

### M15-no-reference-idempotent-recheck
- 描述：重复调用不再幂等复核，而是重新触发副作用
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `b1153cd0fce08a3ee9fabf4a41df9445f681714df5419ab3a00e7a4469544d1f`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting`（testId `00ca64e4-2c51-3f2e-22d1-089054e8092e`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`illegal_advance:ReferenceUpdating->ReferenceUpdating` + 断言栈 `RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (m.Stage == MigrationStage.ReferenceUpdating)
                return RecheckReferenceWriteSet(m);                       // **幂等**：只重读盘复核，不再次写入
替换后：
            // MUTANT: 幂等复核被跳过
```

### M16-no-activation-idempotent-recheck
- 描述：已激活后的重复调用不再幂等复核
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `9765c82d7bc0d96508aa32979093a7f52da2d1d80eb3d5d758fb7a8b6623ecb4`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting`（testId `00ca64e4-2c51-3f2e-22d1-089054e8092e`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`activation_requires_confirmed_reference_update:Activated` + 断言栈 `RepeatedCalls_AreIdempotent_AndRecheckInsteadOfRewriting`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (m.Stage == MigrationStage.Activated) return RecheckActivationRecord(m);   // **幂等**
替换后：
            // MUTANT: 激活幂等复核被跳过
```

### M17-writeset-evidence-not-persisted
- 描述：真实写入后不持久化写集证据
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `c7b3aa7c08ba503cfd7f61e4520d7351b4170a8d897ea8ca9f5bab24ddf4e314`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback`（testId `0758b932-6dc7-e2bb-ba01-64525d386dc5`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`System.NullReferenceException : Object reference not set to an instance of an ob` + 断言栈 `ReferenceUpdate_RealWrite_AdvancesOnlyAfterReadback`

```text
--- MUTANT 补丁（before → after）---
替换前：
            m.ReferenceWriteSet = writeSet;
            m.Stage = MigrationStage.ReferenceUpdating;
替换后：
            // MUTANT: 写集证据不被持久化
            m.Stage = MigrationStage.ReferenceUpdating;
```

### M18-no-activation-undo
- 描述：回滚不再撤销真实激活（只还原字节）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `7783bdab15877ba1be65cc083e226bdc09b8fb33adc30eee10d2012916e6e39c`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.Rollback_InvokesRealActivationUndo`（testId `9595709b-d3d9-d6d3-b138-374e7f2e88ef`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Values differ` + 断言栈 `Rollback_InvokesRealActivationUndo`

```text
--- MUTANT 补丁（before → after）---
替换前：
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
替换后：
                // MUTANT: 撤销激活被跳过
```

### M19-no-activation-pre-drift-check
- 描述：激活前不再核对已确认写集的字节版本
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `1108ea47bee9dcb087986f4ccb714fff9eb81cda0a2fc6f5b73e976ce142447b`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.Activation_AfterDrift_IsNotAbsorbed`（testId `407278ae-9e31-c8fc-e655-1dc876365d35`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `Activation_AfterDrift_IsNotAbsorbed`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (!string.Equals(preHash, confirmedHash, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_drifted:" + request.Path);
替换后：
            // MUTANT: 漂移核对被跳过
```

### M20-no-new-file-detection
- 描述：不再检测写集外**新增**文件
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `bfff8ce4d229f2e9daf4ce274b9303967cbdd7f0e17aac54d24484b68d30260e`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.NewFileOutsideWriteset_Blocks`（testId `e72e8b5a-bb74-e3b2-db18-b73e22e6549e`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `NewFileOutsideWriteset_Blocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (UnexpectedFileReason(m, plan!.Targets) is { } unexpectedFile) return MarkBlocked(unexpectedFile);
替换后：
            // MUTANT: 写集外新增文件检测被跳过
```

### M21-no-full-baseline-verification-on-rollback
- 描述：回滚后只核对成功写集，不核对完整基线字节
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `d94056d3138dc1161b3abf0564ef3cb02a1f48fcf1a2fa6da3787ca96565a33d`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet`（testId `0ac6ce90-1a90-0a40-ff2f-374f287a5d6c`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `Rollback_VerifiesFullBaselineBytes_NotOnlyWriteSet`

```text
--- MUTANT 补丁（before → after）---
替换前：
                foreach (var baseline in m.FileHashes)
                {
                    if (!TryHashConfigFile(baseline.Key, out var restoredHash, out var restoreProblem))
                        return MarkBlocked("rollback_restore_" + restoreProblem + ":" + baseline.Key);
                    if (!string.Equals(restoredHash, baseline.Value, StringComparison.Ordinal))
                        return MarkBlocked("rollback_restore_bytes_differ:" + baseline.Key);
                }
替换后：
                // MUTANT: 完整基线字节核对被跳过
```

### M23-commit-gate-not-bound-to-instance
- 描述：提交门槛不再绑定「本实例接入真实副作用」
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `d0fec2e97ba973ba33440c4786d1131ae372fee9fa38831eeab1c2862f487f2b`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected`（testId `7bc93f93-430e-82c2-804a-4c05389c58f9`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `ManifestTamper_WithoutRealEvidence_IsRejected`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if ((m.RealEffectsRequired || _effects is not null)
                && (m.ReferenceWriteSet.Count == 0 || m.ActivationRecord is null))
                return MigrationResult.Fail("real_evidence_required_for_production", m.Stage);
替换后：
            // MUTANT: 授权不再要求真实证据（只依赖可改写并重算摘要的持久化标记）
```

### M24-no-change-registry-freeze
- 描述：确认引用更新后仍允许改变更登记
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `79857a4610ef1840cbe7352771c5537aae9f9ce798017c266a726d0774719aaa`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate`（testId `2137126c-7151-96ff-3865-726893babc70`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `ChangeRegistryIsFrozenAfterConfirmedReferenceUpdate`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (m.ReferenceWriteSet.Count > 0)
                return MigrationResult.Fail("change_registry_frozen_after_reference_update", m.Stage);
替换后：
            // MUTANT: 登记冻结被跳过
```

### M25-no-port-exception-containment
- 描述：副作用端口抛异常不再收敛为 Blocked（异常外泄、可重复执行）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `1fc35bf0902c7d248841a39d14c7fd2b7a0e191b95e6be1d78e6b7a64d807189`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.EffectPortExceptions_BecomeBlockedWithoutRepeat`（testId `4a42ae03-38a7-f449-0c96-d3f702762999`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`System.InvalidOperationException : scripted apply failure` + 断言栈 `EffectPortExceptions_BecomeBlockedWithoutRepeat`

```text
--- MUTANT 补丁（before → after）---
替换前：
            catch (Exception ex)      // 副作用可能已发生 ⇒ fail-closed，绝不重复执行（会诊 IMPORTANT-8）
            {
                return MarkBlocked("reference_update_exception_unknown:" + ex.GetType().Name);
            }
替换后：
            catch (Exception) { throw; }   // MUTANT: 异常不再收敛为 Blocked
```

### M26-no-activation-status-precheck
- 描述：激活前不再观测盘上现值与声明的 before 状态是否一致
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `0eb020a6eb32b6e5ad8cbfd01c8f9ec38bd602e6cdae8a5071332795a86b5b38`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions`（testId `30f5595d-3f84-02c1-37ee-76cbfaa50bdc`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Values differ` + 断言栈 `Activation_RejectsAlreadyAppliedAndForeignTransitions`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (string.Equals(observed, request.TargetStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_already_applied:" + request.Path);
            if (!string.Equals(observed, request.ExpectedBeforeStatus, StringComparison.Ordinal))
                return MarkBlocked("activation_precondition_status_mismatch:" + request.Path + ":" + observed);
替换后：
            // MUTANT: 前置状态观测核对被跳过
```

### M27-no-d13-transition-restriction
- 描述：激活不再限定权威的 candidate-ready→active 转换
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `b5ce4d144b6a41106d0b4279fbf1bb78004069046b04464114b017bf96cd6391`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.Activation_RejectsAlreadyAppliedAndForeignTransitions`（testId `30f5595d-3f84-02c1-37ee-76cbfaa50bdc`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Contains() Failure: Sub-string not found` + 断言栈 `Activation_RejectsAlreadyAppliedAndForeignTransitions`

```text
--- MUTANT 补丁（before → after）---
替换前：
        if (!string.Equals(request.ExpectedBeforeStatus, CandidateReadyStatus, StringComparison.Ordinal)
            || !string.Equals(request.TargetStatus, ActiveStatus, StringComparison.Ordinal))
            return "unsupported_activation_transition:" + request.ExpectedBeforeStatus + "->" + request.TargetStatus;
替换后：
        // MUTANT: 转换白名单被删除
```

### M28-added-target-overwrite-allowed
- 描述：新增目标允许覆盖同名既有文件（覆盖他方文件＝静默数据破坏）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs`；original SHA-256 `c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4`；mutant `7d231ff371fa567bb599e9a17030d4c4a128ba800657c08c06f2410789f1f7fd`；restored `c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part2.AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback`（testId `4a7e697f-3c47-bd13-5759-79b2405e3218`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `AddedTargetNeverCreatedByTransaction_IsNotDeletedOnRollback`

```text
--- MUTANT 补丁（before → after）---
替换前：
                    if (File.Exists(full))
                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        // **新文件不得覆盖**：竞争窗口内被他人创建 ⇒ 本次写入失败（不静默覆盖他人文件）
                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);
替换后：
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: true);   // MUTANT
```

### M8-no-activation-stage-gate
- 描述：去掉「激活须先有已确认真实引用更新」的阶段前置（与 M6 的旧阶段标记门禁不同点）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `337577bda5a4d2442fb3691a8b5b33c1778112d989293b9d794802afcf55caf7`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.Activation_RealWrite_RequiresConfirmedReferenceUpdate`（testId `354f034a-261c-1a1c-f4a1-d0e65537878b`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `Activation_RealWrite_RequiresConfirmedReferenceUpdate`

```text
--- MUTANT 补丁（before → after）---
替换前：
            if (m.Stage != MigrationStage.ReferenceUpdating)
                return MigrationResult.Fail("activation_requires_confirmed_reference_update:" + m.Stage, m.Stage);
替换后：
            // MUTANT: 激活的阶段前置被删除
```

### M29-no-ownership-precheck-before-write
- 描述：回滚前不再预检归属（撤销激活先写入，可能改写他方文件）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `e800719430aad98c4e5e9af352c58a7b1e5141921a71bfdf02682e82028c10a9`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part3.Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten`（testId `fb7e6ff8-c357-8912-ec82-30545d47e58b`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `Rollback_ForeignReplacementOfAddedActivationTarget_IsNotRewritten`

```text
--- MUTANT 补丁（before → after）---
替换前：
                if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
替换后：
                // MUTANT: 归属预检被跳过（先写后查）
```

### M30-no-addition-ownership-enforcement
- 描述：归属预检与删除时的归属过滤整体被去除（他方「新增」文件会被删除）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `0df17a897112217dbb5ccb51906b59e89c193083575e822a935876809fc9fe25`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part3.ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks`（testId `f2436c03-f0c6-7bc9-9210-c5df8a3682a0`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `ForeignAddedFileWithoutActivation_IsPreservedAndRollbackBlocks`

```text
--- MUTANT 补丁（before → after）---
替换前：
                var ownerBound = m.RealEffectsRequired || _effects is not null;   // 判据绑定实例（MUST-2）
                HashSet<string>? ownershipVerified = null;   // null ⇒ 未接入真实副作用端口的旧路径：按登记删除（既有合同）
                if (ownerBound)
                {
                    if (OwnershipConflictReason(m) is { } ownershipConflict) return MarkBlocked(ownershipConflict);
                    ownershipVerified = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var added in m.ChangedFiles.Where(c => c.Kind == ChangeKind.Added))
                        ownershipVerified.Add(PathKey(added.Path));
                }
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot, ownershipVerified) > 0)
替换后：
                var ownerBound = m.RealEffectsRequired || _effects is not null;
                if (m.ActivationRecord is { } activation)
                {
                    var undone = UndoActivation(m, activation);
                    if (!undone.Success) return undone;
                }
                RestoreFromSnapshot(m, _configRoot);
                if (DeleteRecordedAdditions(m, _configRoot, null) > 0)   // MUTANT: 归属保护整体被移除
```

### M31-no-activation-content-binding
- 描述：激活不再绑定写入所依据的字节版本（两处哈希核对整体删除）
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs`；original SHA-256 `c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4`；mutant `7aa7e0aa019b139a407446bfe21d6e8b4a65435ba6ea7e945f166dbcbc624277`；restored `c420ba8d9e8f3a06cb1cc61f43dc1e45064c72d2eb52711695b551ed21da12a4`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part3.Activation_VersionBinding_RejectsDriftAfterPrecheck`（testId `dfe239eb-e844-b8dc-4ee5-10416277eb87`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Contains() Failure: Sub-string not found` + 断言栈 `Activation_VersionBinding_RejectsDriftAfterPrecheck`

```text
--- MUTANT 补丁（before → after）---
替换前：
        if (!string.IsNullOrEmpty(request.ExpectedContentHash)
            && !string.Equals(Sha256Hex(bytes), request.ExpectedContentHash, StringComparison.Ordinal))
            return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);   // 版本绑定（MUST-3）
        var hasBom = HasUtf8Bom(bytes);
        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,
                out var reasonText, out var text, out var alreadyTarget))
            return MigrationEffectResult.Rejected(reasonText, 0);
        if (alreadyTarget) return MigrationEffectResult.Ok(0);         // 已是目标状态：真实生效无需二次写入
        try
        {
            var preWrite = File.ReadAllBytes(full);
            if (!preWrite.AsSpan().SequenceEqual(bytes))
                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);
            if (!string.IsNullOrEmpty(request.ExpectedContentHash)
                && !string.Equals(Sha256Hex(preWrite), request.ExpectedContentHash, StringComparison.Ordinal))
                return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);
            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);
替换后：
        var hasBom = HasUtf8Bom(bytes);
        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,
                out var reasonText, out var text, out var alreadyTarget))
            return MigrationEffectResult.Rejected(reasonText, 0);
        if (alreadyTarget) return MigrationEffectResult.Ok(0);         // 已是目标状态：真实生效无需二次写入
        try
        {
            var preWrite = File.ReadAllBytes(full);
            if (!preWrite.AsSpan().SequenceEqual(bytes))
                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);
            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);   // MUTANT: 版本绑定（两处）整体被删除
```

### M32-no-missing-baseline-half
- 描述：文件集合核对只查多余、不查缺失
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `718cd3c4acef151788fcf38192b1ac7dcf351a257b849bae172a31903baa8043`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part3.Commit_RejectsMissingBaselineFile`（testId `020506d4-fba1-6352-53d6-7285c481f1b0`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.False() Failure` + 断言栈 `Commit_RejectsMissingBaselineFile`

```text
--- MUTANT 补丁（before → after）---
替换前：
        foreach (var baseline in m.FileHashes.Keys)
            if (!present.Contains(PathKey(baseline))) return "missing_baseline_file:" + baseline;   // 相等检查的另一半
替换后：
        // MUTANT: 缺失一半被删除
```

### M33-no-reentrancy-guard-on-rollback
- 描述：回滚入口不再拒绝端口回调内的同实例重入
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `fea54002d8cb4a895420fee49861c2c14325e596f62ed5ffb3eb8636e8df7449`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests_Part3.ReentrantMutationFromEffectCallback_IsRejected`（testId `93347cf9-00ca-e823-7ad7-628c19462f04`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Equal() Failure: Strings differ` + 断言栈 `ReentrantMutationFromEffectCallback_IsRejected`

```text
--- MUTANT 补丁（before → after）---
替换前：
public MigrationResult Rollback()
    {
        lock (_sync)
        {
            if (_effectCallInProgress) return MigrationResult.Fail("reentrant_mutation_rejected", LoadManifest()?.Stage ?? MigrationStage.None);
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);
替换后：
public MigrationResult Rollback()
    {
        lock (_sync)
        {
            if (!HoldsExclusiveLock) return MigrationResult.Fail("lock_not_held", LoadManifest()?.Stage ?? MigrationStage.None);   // MUTANT: 重入守卫被删除
```

### M34-no-duplicate-change-identity-check
- 描述：变更登记身份唯一性不再校验
- 源文件：`MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs`；original SHA-256 `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`；mutant `7ff6aadef427f829bd8b050bce2a7041901e734fc7c1a23d541e92d3c3a3fe93`；restored `56c795554dbb95a8d94dc540506a860994c53290ba1d5f26811a9a0ae9290fc8`（逐字节相等＝True）
- 目标断言：`MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.R56ReferenceActivationWiringTests.ManifestTamper_WithoutRealEvidence_IsRejected`（testId `7bc93f93-430e-82c2-804a-4c05389c58f9`）
- 判定：baseline **Passed** / mutant **Failed**（构建 exit 0、测试 exit 1）/ restored **Passed**
- 命中标记：`Assert.Null() Failure: Value is not null` + 断言栈 `ManifestTamper_WithoutRealEvidence_IsRejected`

```text
--- MUTANT 补丁（before → after）---
替换前：
        if (!changeIdentities.Add(PathKey(change.Path))) return "evidence_relation:duplicate_change_identity:" + change.Path;
替换后：
        changeIdentities.Add(PathKey(change.Path));   // MUTANT: 重复身份不再拒绝
```

