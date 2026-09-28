import io, sys, hashlib, pathlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs")
src = p.read_text(encoding="utf-8")
orig = src
def rep(old, new, count=1):
    global src
    assert src.count(old) == count, (src.count(old), old[:80])
    src = src.replace(old, new, count)

# ---- 1) manifest fields ----
rep('''    [JsonPropertyName("manifestIntegrity")] public string ManifestIntegrity { get; set; } = "";
}''',
'''    [JsonPropertyName("manifestIntegrity")] public string ManifestIntegrity { get; set; } = "";
    /// <summary>
    /// **真实引用写入的读回证据**（本事务写集声明；`path → 写入后盘上字节 SHA256`；大小写不敏感身份）。
    /// 只有**逐项读回确认**后才写入；空表示尚未完成真实引用更新。
    /// </summary>
    [JsonPropertyName("referenceWriteSet")] public Dictionary<string, string> ReferenceWriteSet { get; set; } = new(StringComparer.Ordinal);
    /// <summary>
    /// **真实激活的读回证据**：目标路径、前后状态、写入后字节哈希；null 表示尚未完成真实激活。
    /// </summary>
    [JsonPropertyName("activationRecord")] public MigrationActivationRecord? ActivationRecord { get; set; }
}

/// <summary>真实激活读回证据（D13：`candidate → active`）。</summary>
public sealed class MigrationActivationRecord
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("beforeStatus")] public string BeforeStatus { get; set; } = "";
    [JsonPropertyName("afterStatus")] public string AfterStatus { get; set; } = "";
    [JsonPropertyName("afterHash")] public string AfterHash { get; set; } = "";
}''')

# ---- 2) ctor param ----
rep('''    private readonly Action<string>? _fileRestoredHook;''',
'''    private readonly Action<string>? _fileRestoredHook;
    private readonly IMigrationEffectService? _effects;''')
rep('''    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null,
        Action<string>? fileRestoredHook = null)''',
'''    public MigrationSwitchTransaction(string configRoot, string transactionRoot, Func<DateTimeOffset>? utcNow = null,
        Func<IDisposable>? quiesce = null, bool requireQuiescence = true, Action<MigrationStage>? stageHook = null,
        Action<string>? fileRestoredHook = null, IMigrationEffectService? effectService = null)''')
rep('''        _fileRestoredHook = fileRestoredHook;   // 夹具接缝：逐文件恢复后回调（生产=null）''',
'''        _fileRestoredHook = fileRestoredHook;   // 夹具接缝：逐文件恢复后回调（生产=null）
        _effects = effectService;               // **真实副作用端口**：null ⇒ 本实例只能演练阶段推进（旧语义仅保留给只读夹具）''')
p.write_text(src, encoding="utf-8")
print("stage1 ok, sha256=", hashlib.sha256(src.encode()).hexdigest())
