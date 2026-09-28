import hashlib, pathlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs")
src = p.read_text(encoding="utf-8")
def rep(old, new, count=1):
    global src
    assert src.count(old) == count, (src.count(old), old[:90])
    src = src.replace(old, new, count)

rep('''        detail = "";
        if (target.Kind != ChangeKind.Modified) { detail = "not_a_rename_target"; return false; }
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, target.Path, out var full, out detail)) return false;
        if (!File.Exists(full)) { detail = "target_missing"; return false; }
        try
        {''',
'''        detail = "";
        var root = Path.GetFullPath(configRoot);
        if (!TryResolve(root, target.Path, out var full, out detail)) return false;
        if (!File.Exists(full)) { detail = "target_missing"; return false; }
        if (target.Kind == ChangeKind.Added)
        {
            // 新增目标的**写回确认**＝内容逐字节等于声明内容（不适用「引用改写」语义）
            if (target.NewContent is null) { detail = "added_target_without_content"; return false; }
            try
            {
                var expected = Utf8NoBom.GetBytes(target.NewContent);
                if (!File.ReadAllBytes(full).AsSpan().SequenceEqual(expected)) { detail = "added_content_mismatch"; return false; }
                detail = "added_content_confirmed";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                detail = "read_io_failed:" + ex.GetType().Name;
                return false;
            }
        }
        if (target.Kind != ChangeKind.Modified) { detail = "not_a_rename_target"; return false; }
        try
        {''')
p.write_text(src, encoding="utf-8")
print("stage4 ok sha256=", hashlib.sha256(src.encode()).hexdigest())
