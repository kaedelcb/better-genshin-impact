import pathlib, ast
region2=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/region2.txt").read_text(encoding="utf-8")
old = region2[:region2.rindex("AtomicWrite(full, EncodeText(text!, hasBom), hasBom);")+len("AtomicWrite(full, EncodeText(text!, hasBom), hasBom);")]
new = """        var hasBom = HasUtf8Bom(bytes);
        if (!TrySetActivationStatus(DecodeText(bytes), request.ExpectedBeforeStatus, request.TargetStatus,
                out var reasonText, out var text, out var alreadyTarget))
            return MigrationEffectResult.Rejected(reasonText, 0);
        if (alreadyTarget) return MigrationEffectResult.Ok(0);         // 已是目标状态：真实生效无需二次写入
        try
        {
            var preWrite = File.ReadAllBytes(full);
            if (!preWrite.AsSpan().SequenceEqual(bytes))
                return MigrationEffectResult.Unknown("activation_target_changed_after_read:" + request.Path, 0);
            AtomicWrite(full, EncodeText(text!, hasBom), hasBom);   // MUTANT: 版本绑定（两处）整体被删除"""
p=pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s=p.read_text(encoding="utf-8")
i=s.index('"id": \'M31-no-activation-content-binding\'')
start=s.rindex('  {', 0, i); end=s.index('},\n', i)+3
entry = ('  {"id": %r, "desc": %r,\n   "old": %r,\n   "new": %r,\n   "test": %r,\n   "src": %r},\n' % (
  "M31-no-activation-content-binding", "激活不再绑定写入所依据的字节版本（两处哈希核对整体删除）",
  old, new,
  "R56ReferenceActivationWiringTests_Part3.Activation_VersionBinding_RejectsDriftAfterPrecheck",
  "MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs"))
s=s[:start]+entry+s[end:]
p.write_text(s,encoding="utf-8"); ast.parse(s); print("M31 redefined as whole-block weakening")
