import hashlib, pathlib
def edit(path, pairs):
    p = pathlib.Path(path); s = p.read_text(encoding="utf-8")
    for old, new, cnt in pairs:
        assert s.count(old) == cnt, (path, s.count(old), old[:70])
        s = s.replace(old, new, cnt)
    p.write_text(s, encoding="utf-8")
    return hashlib.sha256(s.encode()).hexdigest()[:16]

# 1) 宽松编码器：真实写入保持可读 UTF-8（不被转义为 \\uXXXX），便于人工比对与评审
h1 = edit("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs", [
 ('''    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);''',
  '''    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>真实写入用序列化选项：保留人类可读的非 ASCII（不被转义为 `\\uXXXX`），便于写入后人工比对与评审。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };''', 1),
 ('''        output = doc!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return true;''',
  '''        output = doc!.ToJsonString(WriteOptions);
        return true;''', 1),
 ('''        output = doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return true;''',
  '''        output = doc.ToJsonString(WriteOptions);
        return true;''', 1),
])

# 2) 激活后同步更新写集内该文件的期望哈希（激活也属于本事务对该文件的真实写入）
h2 = edit("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs", [
 ('''            m.ActivationRecord = new MigrationActivationRecord
            {
                Path = request.Path,
                BeforeStatus = request.ExpectedBeforeStatus,
                AfterStatus = request.TargetStatus,
                AfterHash = hash,
            };''',
  '''            m.ActivationRecord = new MigrationActivationRecord
            {
                Path = request.Path,
                BeforeStatus = request.ExpectedBeforeStatus,
                AfterStatus = request.TargetStatus,
                AfterHash = hash,
            };
            // 写集记录的是「本事务写过的文件的**当前期望盘上状态**」：激活同样改写了该文件，故须同步更新其哈希，
            // 否则提交前的写集复核会把本事务自己的激活写入误判为漂移。
            foreach (var key in m.ReferenceWriteSet.Keys.Where(k => PathKey(k) == PathKey(request.Path)).ToList())
                m.ReferenceWriteSet[key] = hash;''', 1),
])
print("service", h1, "transaction", h2)
