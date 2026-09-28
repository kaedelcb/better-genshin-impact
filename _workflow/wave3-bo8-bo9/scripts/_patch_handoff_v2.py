import io
path = "_batch21/sb21-4-handoff-2026-09-28.md"
t = io.open(path, encoding="utf-8").read()
pairs = [
 ("- 实现：`DriveAsync` 推进段完成过滤无条件按稳定出现身份生效（BO-8）；真实链尾按计划全序重入仍存活的停驻义务\n"
  "（BO-9，`TryRelocateToLivePark`），入口即持久链尾的记录保持 BO-6 的 Failed／零提交防御语义。",
  "- 实现：`DriveAsync` 推进段完成过滤无条件按稳定出现身份生效（BO-8）；真实链尾按计划全序重建并重入**未履行的恢复义务**\n"
  "（BO-9，`TryRelocateToOutstandingObligation`：仍存活停驻 + 其同轮前插未执行出现），入口即持久链尾的记录保持 BO-6 的 Failed／零提交防御语义。"),
 ("- 证据：反例先行红（开工字节 `red-final/`：BO-8 提交 `[n3,n2,X,n3,Y]`、BO-9 仅 `[(A,1)]`）、最终定向 96/96、\n"
  "助手全量 1565/2/0/1567（基线 1562/2/0/1564）、testId 1564 unchanged/3 added/0 removed/0 changed、\n"
  "12 项反向突变在新字节上 P/F/P、部署目标未被写入。逐项见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。",
  "- 证据：反例先行红（开工字节 `red-final/`：BO-8 提交 `[n3,n2,X,n3,Y]`、BO-9 仅 `[(A,1)]`）、最终定向 97/97、\n"
  "助手全量 1566/2/0/1568（基线 1562/2/0/1564）、testId 1564 unchanged/4 added/0 removed/0 changed、\n"
  "13 项反向突变在新字节上 P/F/P、部署目标未被写入。第 1 轮会诊 1 项 IMPORTANT（链尾漏掉同轮前插 rescue，已修复并新增夹具＋突变）\n"
  "与 1 项建议级（已采纳订正）见 R5.3 §24.127.4。逐项见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。"),
]
for old, new in pairs:
    n = t.count(old)
    assert n == 1, (n, old[:60])
    t = t.replace(old, new)
io.open(path, "w", encoding="utf-8", newline="\n").write(t)
print("patched", path)
