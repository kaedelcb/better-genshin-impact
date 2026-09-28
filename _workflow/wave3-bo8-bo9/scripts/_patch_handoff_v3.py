import io
p = "_batch21/sb21-4-handoff-2026-09-28.md"
t = io.open(p, encoding="utf-8").read()
pairs = [
 ("（BO-9，`TryRelocateToLivePark`）", "（BO-9，`TryRelocateToOutstandingObligation`：仍存活停驻 + 其同轮前插未执行出现）"),
 ("最终定向 96/96、助手全量 1565/2/0/1567", "最终定向 97/97、助手全量 1566/2/0/1568"),
 ("testId 1564 unchanged/3 added/0 removed/0 changed", "testId 1564 unchanged/4 added/0 removed/0 changed"),
 ("12 项反向突变在新字节上 P/F/P、部署目标未被写入。",
  "13 项反向突变在新字节上 P/F/P、部署目标未被写入。第 1 轮会诊 1 项 IMPORTANT（链尾漏掉同轮前插 rescue，已修复并新增夹具＋突变）与 1 项建议级（已采纳订正）见 R5.3 §24.127.4。"),
]
for old, new in pairs:
    n = t.count(old)
    assert n == 1, (n, old[:60])
    t = t.replace(old, new)
io.open(p, "w", encoding="utf-8", newline="\n").write(t)
print("patched handoff")
