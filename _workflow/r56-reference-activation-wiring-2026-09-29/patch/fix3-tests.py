import pathlib, re
p=pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s=p.read_text(encoding="utf-8")
def rep(old,new,cnt=1):
    global s
    assert s.count(old)==cnt,(s.count(old),old[:110])
    s=s.replace(old,new,cnt)

# 统一：激活请求携带「已确认写集」的字节版本（会诊第 2 轮 MUST-3）
rep('''    private static MigrationActivationRequest Activation(string path = FlowPath)
        => new(path, "candidate-ready", "active");

    private static bool BytesEqual(string path, byte[] expected)
        => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);''',
'''    /// <summary>激活请求（携带**已确认写集**的字节版本；写集缺失时用占位哈希，仅用于「不入端口即可拒绝」的反例）。</summary>
    private static MigrationActivationRequest Activation(MigrationSwitchTransaction tx, string path = FlowPath)
    {
        var manifest = tx.LoadManifest();
        string hash = new('0', 64);
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    private static bool BytesEqual(string path, byte[] expected)
        => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);''')
# Part2 的同名辅助（覆盖同名方法）
rep('''    private static MigrationActivationRequest Activation(string path = FlowPath)
        => new(path, "candidate-ready", "active");

    private static R56ReferenceActivationWiringTests.ScriptedEffectService Effects()
        => new();''',
'''    private static MigrationActivationRequest Activation(MigrationSwitchTransaction tx, string path = FlowPath)
    {
        var manifest = tx.LoadManifest();
        string hash = new('0', 64);
        if (manifest is not null)
            foreach (var entry in manifest.ReferenceWriteSet)
                if (string.Equals(entry.Key, path, StringComparison.OrdinalIgnoreCase)) hash = entry.Value;
        return new MigrationActivationRequest(path, "candidate-ready", "active", hash);
    }

    private static R56ReferenceActivationWiringTests.ScriptedEffectService Effects()
        => new();''')
# 调用点批量替换
s = re.sub(r'Activation\(\)', 'Activation(tx)', s)
s = re.sub(r'Activation\(tx2\)', 'Activation(tx2)', s)
s = re.sub(r'Activation\(SecondPath\)', 'Activation(tx, SecondPath)', s)
# Part2 中出现在 tx2 语境的两处
s = s.replace('tx2.ActivateCandidate(Activation(tx))', 'tx2.ActivateCandidate(Activation(tx2))')
s = s.replace('tx.ActivateCandidate(new MigrationActivationRequest(FlowPath, "active", "inactive"))',
              'tx.ActivateCandidate(new MigrationActivationRequest(FlowPath, "active", "inactive", new string(\'0\', 64)))')
p.write_text(s,encoding="utf-8")
print("activation hash wired; remaining bare calls:", s.count("Activation()"))
