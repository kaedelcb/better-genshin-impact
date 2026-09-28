import io
p = "Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md"
t = io.open(p, encoding="utf-8").read()
old = "。部轮次等待组合（有循环时的全部 "
new = "。高轮次等待组合（有循环定义时 "
assert t.count(old) == 1
t = t.replace(old, new)
old2 = "`LastScheduledRoundWait` 交错）未被本批夹具覆盖。"
new2 = "`LastScheduledRoundWait` 的全部交错）未被本批夹具覆盖；本批夹具均为无 scheduled loop 的计划。"
assert t.count(old2) == 1
t = t.replace(old2, new2)
io.open(p, "w", encoding="utf-8", newline="\n").write(t)
print("wording fixed")
