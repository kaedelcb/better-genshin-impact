import io
def patch(path, pairs):
    t = io.open(path, encoding="utf-8").read()
    for old, new in pairs:
        n = t.count(old)
        assert n == 1, (path, n, old[:60])
        t = t.replace(old, new)
    io.open(path, "w", encoding="utf-8", newline="\n").write(t)
    print("patched", path)

patch("_batch21/b21_plan.md", [
 ("最终定向 96/96、助手全量 1565/2/0/1567（基线 1562/2/0/1564）、",
  "最终定向 97/97、助手全量 1566/2/0/1568（基线 1562/2/0/1564）、"),
 ("testId 1564 unchanged/3 added/0 removed/0 changed；12 项反向突变在新字节上重做（8 项 rebased + 4 项新增）全部 P/F/P 且命中具名断言；",
  "testId 1564 unchanged/4 added/0 removed/0 changed；13 项反向突变在新字节上重做（8 项 rebased + 5 项新增）全部 P/F/P 且命中具名断言；"),
 ("真实链尾按计划全序重入仍存活的停驻义务（入口即持久链尾的记录不由该路径重开）。",
  "真实链尾按计划全序重建并重入**未履行的恢复义务**（仍存活停驻 + 其同轮前插未执行出现；入口即持久链尾的记录不由该路径重开）。"
  "本子批会诊 2/8：第 1 轮 1 项 IMPORTANT（链尾只重入停驻点会丢掉同轮前插 rescue ⇒ 假成功）已修复并新增夹具＋突变钉死，"
  "1 项建议级（D1 残余注释）已采纳订正；第 2 轮验证结论见 R5.3 §24.127.4。"),
])

patch("槲寄生调度器总计划.md", [
 ("修复后定向 96/96、助手全量 1565/2/0/1567",
  "修复后定向 97/97、助手全量 1566/2/0/1568"),
 ("testId 1564 unchanged/3 added/0 removed/0 changed；12 项反向突变在新字节上重做并命中具名断言；",
  "testId 1564 unchanged/4 added/0 removed/0 changed；13 项反向突变在新字节上重做并命中具名断言；"),
 ("真实链尾按计划全序重入仍存活的停驻义务，入口即持久链尾的记录仍按 BO-6 防御语义 Failed 且零提交。",
  "真实链尾按计划全序重建并重入未履行恢复义务（仍存活停驻 + 其同轮前插未执行出现），入口即持久链尾的记录仍按 BO-6 防御语义 Failed 且零提交。"
  "本子批会诊 2/8：第 1 轮 1 项 IMPORTANT（链尾漏掉同轮前插 rescue）已修复并新增夹具＋突变钉死，1 项建议级已采纳订正。"),
])

patch("_batch21/sb21-4-handoff-2026-09-28.md", [
 ("- 实现：`DriveAsync` 推进段完成过滤无条件按稳定出现身份生效（BO-8）；真实链尾按计划全序重入仍存活的停驻义务\r\n"
  "（BO-9，`TryRelocateToLivePark`），入口即持久链尾的记录保持 BO-6 的 Failed／零提交防御语义。",
  "- 实现：`DriveAsync` 推进段完成过滤无条件按稳定出现身份生效（BO-8）；真实链尾按计划全序重建并重入**未履行的恢复义务**\r\n"
  "（BO-9，`TryRelocateToOutstandingObligation`：仍存活停驻 + 其同轮前插未执行出现），入口即持久链尾的记录保持 BO-6 的 Failed／零提交防御语义。"),
 ("- 证据：反例先行红（开工字节 `red-final/`：BO-8 提交 `[n3,n2,X,n3,Y]`、BO-9 仅 `[(A,1)]`）、最终定向 96/96、\r\n"
  "助手全量 1565/2/0/1567（基线 1562/2/0/1564）、testId 1564 unchanged/3 added/0 removed/0 changed、\r\n"
  "12 项反向突变在新字节上 P/F/P、部署目标未被写入。逐项见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。",
  "- 证据：反例先行红（开工字节 `red-final/`：BO-8 提交 `[n3,n2,X,n3,Y]`、BO-9 仅 `[(A,1)]`）、最终定向 97/97、\r\n"
  "助手全量 1566/2/0/1568（基线 1562/2/0/1564）、testId 1564 unchanged/4 added/0 removed/0 changed、\r\n"
  "13 项反向突变在新字节上 P/F/P、部署目标未被写入。第 1 轮会诊 1 项 IMPORTANT（链尾漏掉同轮前插 rescue，已修复并新增夹具＋突变）\r\n"
  "与 1 项建议级（已采纳订正）见 R5.3 §24.127.4。逐项见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。"),
])
