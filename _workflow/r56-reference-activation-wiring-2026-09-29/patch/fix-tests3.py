import pathlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:110])
    s = s.replace(old, new, cnt)

rep('''    private MigrationSwitchTransaction BeginIndependent(string name, IMigrationEffectService effects,
        IEnumerable<ChangeRecord> changes, string txId = "t2")
    {
        var cfg = Path.Combine(_root, "cfg-" + name);
        var txRoot = Path.Combine(_root, "tx-" + name);
        Directory.CreateDirectory(cfg);''',
'''    private MigrationSwitchTransaction BeginIndependent(string name, IMigrationEffectService effects,
        IEnumerable<ChangeRecord> changes, string status = "candidate-ready", string txId = "t2")
    {
        var cfg = Path.Combine(_root, "cfg-" + name);
        var txRoot = Path.Combine(_root, "tx-" + name);
        Directory.CreateDirectory(cfg);
        var seeded = Path.Combine(cfg, "flows");
        Directory.CreateDirectory(seeded);
        File.WriteAllText(Path.Combine(seeded, "plan.flow.json"), FlowJson("计划", status, "配置A"), new UTF8Encoding(false));''')

rep('''        using var tx2 = BeginIndependent("already", effects2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);''',
'''        using var tx2 = BeginIndependent("already", effects2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], status: "active");''')

rep('''        using var tx2 = BeginIndependent("activate", throwing2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }]);''',
'''        using var tx2 = BeginIndependent("activate", throwing2, [new ChangeRecord { Path = FlowPath, Kind = ChangeKind.Modified }], status: "candidate-ready");''')
p.write_text(s, encoding="utf-8")
print("seeding fixed")
