import pathlib, hashlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:90])
    s = s.replace(old, new, cnt)

# ---- 1) AtomicWrite 支持「新文件不得覆盖」；Added 用 overwrite:false ----
rep('''                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false);
                    }''',
'''                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        // **新文件不得覆盖**：竞争窗口内被他人创建 ⇒ 本次写入失败（不静默覆盖他人文件）
                        AtomicWrite(full, Utf8NoBom.GetBytes(target_.NewContent), hasBom: false, overwrite: false);
                    }
                    catch (IOException) when (File.Exists(full))
                    {
                        return MigrationEffectResult.Rejected("added_target_already_exists:" + target_.Path, writes);
                    }''')

rep('''    private static void AtomicWrite(string full, byte[] bytes, bool hasBom)
    {
        var dir = Path.GetDirectoryName(full)!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, full, overwrite: true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }''',
'''    private static void AtomicWrite(string full, byte[] bytes, bool hasBom, bool overwrite = true)
    {
        var dir = Path.GetDirectoryName(full)!;
        var tmp = Path.Combine(dir, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        try { File.Move(tmp, full, overwrite: overwrite); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }''')

# ---- 2) 文档形状异常（非对象节点/非对象 ref）稳定转结构化拒绝，不抛异常 ----
rep('''    /// <summary>真实引用重命名：遍历 `nodes[].ref.config`（助手侧流程文件的资源引用面）。</summary>
    private static int RenameReferences(JsonObject doc, string from, string to)
    {
        var renamed = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node?["ref"] is not JsonObject reference) continue;
            if (reference["config"] is not JsonValue value) continue;
            if (!value.TryGetValue<string>(out var current) || !string.Equals(current, from, StringComparison.Ordinal)) continue;
            reference["config"] = to;
            renamed++;
        }
        return renamed;
    }

    private static int CountMatchingReferences(JsonObject doc, string name)
    {
        var count = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node?["ref"] is not JsonObject reference) continue;
            if (reference["config"] is not JsonValue value) continue;
            if (value.TryGetValue<string>(out var current) && string.Equals(current, name, StringComparison.Ordinal)) count++;
        }
        return count;
    }''',
'''    /// <summary>
    /// 真实引用重命名：遍历 `nodes[].ref.config`（助手侧流程文件的资源引用面）。
    /// **形状异常即拒绝**（标量节点、非对象 `ref` 等）：助手侧 `WorkflowStore` 对 `nodes` 非数组/元素非对象按隔离处理，
    /// 本服务不得把无法安全解析的文档改写后写回（会改变工件形状）；返回 null 表示文档形状不可用。
    /// </summary>
    private static int? RenameReferences(JsonObject doc, string from, string to)
    {
        var renamed = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject) return null;         // 标量/数组元素 ⇒ 形状不可用
            if (!nodeObject.TryGetPropertyValue("ref", out var refNode) || refNode is null) continue;
            if (refNode is not JsonObject reference) return null;       // ref 非对象 ⇒ 形状不可用
            if (!reference.TryGetPropertyValue("config", out var configNode) || configNode is null) continue;
            if (configNode is not JsonValue value) return null;
            if (!value.TryGetValue<string>(out var current) || !string.Equals(current, from, StringComparison.Ordinal)) continue;
            reference["config"] = to;
            renamed++;
        }
        return renamed;
    }

    private static int? CountMatchingReferences(JsonObject doc, string name)
    {
        var count = 0;
        if (doc["nodes"] is not JsonArray nodes) return 0;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject) return null;
            if (!nodeObject.TryGetPropertyValue("ref", out var refNode) || refNode is null) continue;
            if (refNode is not JsonObject reference) return null;
            if (!reference.TryGetPropertyValue("config", out var configNode) || configNode is null) continue;
            if (configNode is not JsonValue value) return null;
            if (value.TryGetValue<string>(out var current) && string.Equals(current, name, StringComparison.Ordinal)) count++;
        }
        return count;
    }''')

# ---- 3) TryRenameReferences 传播形状异常 ----
rep('''    private static bool TryRenameReferences(string text, string from, string to, out int renamed, out string? output)
    {
        renamed = 0;
        output = null;
        if (!TryParseDocument(text, out var doc, out _)) return false;
        renamed = RenameReferences(doc!, from, to);
        output = doc!.ToJsonString(WriteOptions);
        return true;
    }''',
'''    private static bool TryRenameReferences(string text, string from, string to, out int renamed, out string? output)
    {
        renamed = 0;
        output = null;
        if (!TryParseDocument(text, out var doc, out _)) return false;
        var count = RenameReferences(doc!, from, to);
        if (count is null) return false;                 // 形状不可用 ⇒ 隔离跳过（不写回、不改变工件形状）
        renamed = count.Value;
        output = doc!.ToJsonString(WriteOptions);
        return true;
    }''')

# ---- 4) 语义读回用可空计数 ----
rep('''            var stale = CountMatchingReferences(doc!, target.RenameFrom!);
            if (stale > 0) { detail = "stale_reference_remaining:" + stale; return false; }
            var updated = CountMatchingReferences(doc!, target.RenameTo!);
            if (updated == 0) { detail = "renamed_reference_absent"; return false; }
            detail = "renamed_reference=" + updated;''',
'''            var stale = CountMatchingReferences(doc!, target.RenameFrom!);
            if (stale is null) { detail = "document_shape_unusable"; return false; }
            if (stale > 0) { detail = "stale_reference_remaining:" + stale; return false; }
            var updated = CountMatchingReferences(doc!, target.RenameTo!);
            if (updated is null) { detail = "document_shape_unusable"; return false; }
            if (updated == 0) { detail = "renamed_reference_absent"; return false; }
            detail = "renamed_reference=" + updated;''')

# ---- 5) Modified 分支：文档形状/transform 异常稳定转拒绝 ----
rep('''                    var hasBom = HasUtf8Bom(bytes);
                    if (!TryRenameReferences(DecodeText(bytes), target_.RenameFrom!, target_.RenameTo!, out var renamed, out var text))
                        return MigrationEffectResult.Rejected("reference_document_unusable:" + target_.Path, writes);   // 坏文件隔离：不覆盖''',
'''                    var hasBom = HasUtf8Bom(bytes);
                    bool renamedOk;
                    int renamed;
                    string? text;
                    try
                    {
                        renamedOk = TryRenameReferences(DecodeText(bytes), target_.RenameFrom!, target_.RenameTo!, out renamed, out text);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
                    {
                        return MigrationEffectResult.Rejected("reference_document_unusable:" + target_.Path + ":" + ex.GetType().Name, writes);
                    }
                    if (!renamedOk)
                        return MigrationEffectResult.Rejected("reference_document_unusable:" + target_.Path, writes);   // 坏文件隔离：不覆盖''')
p.write_text(s, encoding="utf-8")
print("service patched", hashlib.sha256(s.encode()).hexdigest()[:16])
