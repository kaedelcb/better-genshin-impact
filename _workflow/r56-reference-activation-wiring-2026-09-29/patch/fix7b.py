import pathlib
p=pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s=p.read_text(encoding="utf-8")
def rep(old,new,cnt=1):
    global s
    assert s.count(old)==cnt,(s.count(old),old[:110])
    s=s.replace(old,new,cnt)
rep('''        public bool ThrowOnApply;
        public bool ThrowOnActivate;''',
'''        public bool ThrowOnApply;
        public bool ThrowOnActivate;
        /// <summary>状态读取回调（读取前调用；用于构造「检查后、写入前」的锁外扰动）。</summary>
        public Action<string>? OnStatusRead;
        /// <summary>副作用回调（写入之前调用；用于构造端口回调内的同实例重入）。</summary>
        public Action<MigrationSwitchTransaction>? OnApply;
        /// <summary>供 `OnApply` 使用的同实例引用（构造重入场景）。</summary>
        public MigrationSwitchTransaction? ReentrancyTarget { get; set; }''')
rep('''            ApplyCalls++;
            Trace.Add("apply:" + plan.Targets.Count);
            if (ThrowOnApply) throw new InvalidOperationException("scripted apply failure");''',
'''            ApplyCalls++;
            Trace.Add("apply:" + plan.Targets.Count);
            OnApply?.Invoke(ReentrancyTarget!);
            if (ThrowOnApply) throw new InvalidOperationException("scripted apply failure");''')
rep('''        public bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail)
            => _real.TryReadActivationStatus(configRoot, relPath, out status, out detail);''',
'''        public bool TryReadActivationStatus(string configRoot, string relPath, out string status, out string detail)
        {
            OnStatusRead?.Invoke(configRoot);
            return _real.TryReadActivationStatus(configRoot, relPath, out status, out detail);
        }''')
rep('''        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyCompletedWrites = 1,
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);''',
'''        var effects = new ScriptedEffectService
        {
            ApplyOutcome = MigrationEffectOutcome.Cancelled,
            ApplyCompletedWrites = 1,
            ApplyTargetsLimit = 1,                     // **真实部分写入**（会诊第 2 轮 IMPORTANT-8：此前未实际写入）
        };
        using var tx = BeginWithEffects(effects, changes: [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);''')
rep('''        // (g) 激活记录哈希与写集哈希不一致 ⇒ 结构关系不变量拒绝''',
'''        // (f2) 变更登记出现**重复身份** ⇒ 结构关系不变量拒绝（会诊第 2 轮 MUST-4 残余）
        var duplicateChange = Fresh(pristine);
        duplicateChange.ChangedFiles.Add(new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified });
        duplicateChange.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(duplicateChange);
        WriteManifestJson(tx, duplicateChange);
        Assert.Null(tx.LoadValidated());

        // (g) 激活记录哈希与写集哈希不一致 ⇒ 结构关系不变量拒绝''')
p.write_text(s,encoding="utf-8")
print("hooks + fixture fixes applied")
