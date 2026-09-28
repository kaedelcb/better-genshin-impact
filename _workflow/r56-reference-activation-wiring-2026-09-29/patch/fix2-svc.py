import pathlib, hashlib
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB")
# ---------- 服务：激活请求绑定「写入所依据的字节版本」 ----------
p=root/"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs"
s=p.read_text(encoding="utf-8")
def rep(old,new,cnt=1):
    global s
    assert s.count(old)==cnt,(s.count(old),old[:90])
    s=s.replace(old,new,cnt)
rep('''/// <summary>真实 candidate→active 激活请求（D13 三态：只接受 `candidate-ready` 候选）。</summary>
public sealed record MigrationActivationRequest(string Path, string ExpectedBeforeStatus, string TargetStatus);''',
'''/// <summary>
/// 真实 candidate→active 激活请求（D13 三态：只接受 `candidate-ready` 候选）。
/// `ExpectedContentHash` ＝ **本次写入所依据的字节版本**（事务的已确认写集哈希）：端口在写入前必须核对盘上字节哈希，
/// 不符即拒绝——防止「检查之后、写入之前」的锁外改动被连同激活一起合法化（会诊第 2 轮 MUST-3）。
/// </summary>
public sealed record MigrationActivationRequest(string Path, string ExpectedBeforeStatus, string TargetStatus,
    string? ExpectedContentHash = null);''')
rep('''        var hasBom = HasUtf8Bom(bytes);
        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,
                out var reasonText, out var text, out var alreadyTarget))
            return MigrationEffectResult.Rejected(reasonText, 0);''',
'''        if (!string.IsNullOrEmpty(request.ExpectedContentHash)
            && !string.Equals(Sha256Hex(bytes), request.ExpectedContentHash, StringComparison.Ordinal))
            return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);   // 版本绑定（MUST-3）
        var hasBom = HasUtf8Bom(bytes);
        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,
                out var reasonText, out var text, out var alreadyTarget))
            return MigrationEffectResult.Rejected(reasonText, 0);''')
rep('''            if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(bytes))
                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);
            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);''',
'''            var preWrite = File.ReadAllBytes(full);
            if (!preWrite.AsSpan().SequenceEqual(bytes))
                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);
            if (!string.IsNullOrEmpty(request.ExpectedContentHash)
                && !string.Equals(Sha256Hex(preWrite), request.ExpectedContentHash, StringComparison.Ordinal))
                return MigrationEffectResult.Rejected("activation_content_hash_mismatch:" + request.Path, 0);
            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);''')
p.write_text(s,encoding="utf-8")
print("service version-binding done", hashlib.sha256(s.encode()).hexdigest()[:16])
