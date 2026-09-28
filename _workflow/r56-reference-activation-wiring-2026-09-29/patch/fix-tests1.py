import pathlib, hashlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:100])
    s = s.replace(old, new, cnt)

# ScriptedEffectService：记录激活请求 + 可抛出异常（IMPORTANT-9 / IMPORTANT-8）
rep('''        public int ApplyCalls;
        public int ActivateCalls;
        public readonly List<string> Trace = new();''',
'''        public int ApplyCalls;
        public int ActivateCalls;
        public readonly List<string> Trace = new();
        /// <summary>端口收到的激活请求（会诊 IMPORTANT-9：用于证明回滚确实调用了撤销路径）。</summary>
        public readonly List<MigrationActivationRequest> ActivationRequests = new();
        /// <summary>副作用端口抛异常（会诊 IMPORTANT-8：证明异常被收敛为 Blocked 且不重复执行）。</summary>
        public bool ThrowOnApply;
        public bool ThrowOnActivate;''')
rep('''        public MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan)
        {
            ApplyCalls++;
            Trace.Add("apply:" + plan.Targets.Count);''',
'''        public MigrationEffectResult ApplyReferenceUpdate(string configRoot, MigrationReferenceUpdatePlan plan)
        {
            ApplyCalls++;
            Trace.Add("apply:" + plan.Targets.Count);
            if (ThrowOnApply) throw new InvalidOperationException("scripted apply failure");''')
rep('''        public MigrationEffectResult Activate(string configRoot, MigrationActivationRequest request)
        {
            ActivateCalls++;''',
'''        public MigrationEffectResult Activate(string configRoot, MigrationActivationRequest request)
        {
            ActivateCalls++;
            ActivationRequests.Add(request);
            if (ThrowOnActivate) throw new InvalidOperationException("scripted activate failure");''')

# 篡改测试：每个分支都从**盘上原样 manifest 的独立副本**出发（IMPORTANT-9）
rep('''        // (a) 清空写集并**重算摘要**（伪造者知道摘要算法）⇒ 仍判无效
        var cleared = tx.LoadManifest()!;
        cleared.ReferenceWriteSet = new Dictionary<string, string>(StringComparer.Ordinal);
        cleared.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(cleared);
        WriteManifestJson(tx, cleared);
        Assert.Null(tx.LoadValidated());
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);

        // (b) 清空激活记录并重算摘要 ⇒ 仍判无效
        var noActivation = tx.LoadManifest()!;
        noActivation.ActivationRecord = null;
        noActivation.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(noActivation);
        WriteManifestJson(tx, noActivation);
        Assert.Null(tx.LoadValidated());

        // (c) 仅改阶段、不重算摘要 ⇒ 摘要不符
        var stageOnly = tx.LoadManifest()!;
        stageOnly.Stage = MigrationStage.Activated;
        WriteManifestJson(tx, stageOnly);
        Assert.Null(tx.LoadValidated());''',
'''        var pristine = File.ReadAllBytes(tx.ManifestPath);      // 每个分支从**同一合法 manifest 的独立副本**出发

        // (a) 清空写集并**重算摘要**（伪造者知道摘要算法）⇒ 结构关系不变量判无效
        var cleared = Fresh(pristine);
        cleared.ReferenceWriteSet = new Dictionary<string, string>(StringComparer.Ordinal);
        cleared.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(cleared);
        WriteManifestJson(tx, cleared);
        Assert.Null(tx.LoadValidated());
        Assert.Equal("manifest_missing_or_invalid", tx.AuthorizeProductionExecution().Reason);

        // (b) 清空激活记录并重算摘要 ⇒ 仍判无效
        var noActivation = Fresh(pristine);
        noActivation.ActivationRecord = null;
        noActivation.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(noActivation);
        WriteManifestJson(tx, noActivation);
        Assert.Null(tx.LoadValidated());

        // (c) 仅改阶段、不重算摘要 ⇒ 摘要不符
        var stageOnly = Fresh(pristine);
        stageOnly.Stage = MigrationStage.Activated;
        WriteManifestJson(tx, stageOnly);
        Assert.Null(tx.LoadValidated());

        // (d) **把 realEffectsRequired 降级为 false 并清空全部真实证据、重算摘要**
        //     ⇒ 结构校验可能放行（真实/夹具模式无法自证），但**接入真实端口的实例**必须仍然拒绝生产执行（MUST-4）
        File.WriteAllBytes(tx.ManifestPath, pristine);
        var downgraded = Fresh(pristine);
        downgraded.RealEffectsRequired = false;
        downgraded.ReferenceWriteSet = new Dictionary<string, string>(StringComparer.Ordinal);
        downgraded.ActivationRecord = null;
        downgraded.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(downgraded);
        WriteManifestJson(tx, downgraded);
        var authorize = tx.AuthorizeProductionExecution();
        Assert.False(authorize.Success);
        Assert.Equal("real_evidence_required_for_production", authorize.Reason);

        // (e) 同 (d) 但保留标记、只删激活记录 ⇒ 结构校验即拒
        var flagKept = Fresh(pristine);
        flagKept.ActivationRecord = null;
        flagKept.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(flagKept);
        WriteManifestJson(tx, flagKept);
        Assert.Null(tx.LoadValidated());

        // (f) 写集与变更登记不再精确对应（多出一项写集证据）⇒ 结构关系不变量拒绝
        File.WriteAllBytes(tx.ManifestPath, pristine);
        var extraEntry = Fresh(pristine);
        extraEntry.ReferenceWriteSet["other/extra.json"] = extraEntry.ReferenceWriteSet[FlowPath];
        extraEntry.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(extraEntry);
        WriteManifestJson(tx, extraEntry);
        Assert.Null(tx.LoadValidated());

        // (g) 激活记录哈希与写集哈希不一致 ⇒ 结构关系不变量拒绝
        var hashMismatch = Fresh(pristine);
        hashMismatch.ActivationRecord!.AfterHash = new string('a', 64);
        hashMismatch.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(hashMismatch);
        WriteManifestJson(tx, hashMismatch);
        Assert.Null(tx.LoadValidated());''')

rep('''    private static void WriteManifestJson(MigrationSwitchTransaction tx, MigrationManifest manifest)''',
'''    private static MigrationManifest Fresh(byte[] pristine)
        => System.Text.Json.JsonSerializer.Deserialize<MigrationManifest>(Encoding.UTF8.GetString(pristine))!;

    private static void WriteManifestJson(MigrationSwitchTransaction tx, MigrationManifest manifest)''')
p.write_text(s, encoding="utf-8")
print("fixture part-1 patched", hashlib.sha256(s.encode()).hexdigest()[:16])
