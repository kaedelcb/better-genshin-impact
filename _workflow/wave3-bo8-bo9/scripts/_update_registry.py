import io, json
# 1) registry markdown
p = "Docs/design/mistletoe-parallel-deliveries.md"
t = io.open(p, encoding="utf-8").read()
old = ("〔2026-09-28 更新：BO-8 R29 与 BO-9 R34 F5 已由主线子批 `wave3-bo8-bo9-2026-09-28` 施工并按原级登记为 closed；"
       "BO-6/7-D1 已在该批内随 `WorkflowRunner.cs` 哈希重绑定修正；见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。〕")
new = ("〔2026-09-28 更新：BO-8 R29 与 BO-9 R34 F5 已由主线子批 `wave3-bo8-bo9-2026-09-28` 施工，并分别于该批第 1／3 轮会诊"
       "按原级登记为 closed（第 1 轮 BO-8 closed；第 3 轮 BO-9 closed；BO-9 经第 1 轮 IMPORTANT-1 与第 2 轮 IMPORTANT-2 两轮"
       "原级修复后闭合，收尾原文「本批是否仍有未闭合的 MUST/IMPORTANT：否」，本子批会诊计 3/8）；BO-6/7-D1 已在该批内随 "
       "`WorkflowRunner.cs` 哈希重绑定修正；新增建议级残项 **BO-9-D1**（4 处注释陈旧）留待下一次重绑定该文件哈希的批次订正。"
       "见 R5.3 §24.127 与 `_workflow/wave3-bo8-bo9/`。〕")
assert t.count(old) == 1
io.open(p, "w", encoding="utf-8", newline="\n").write(t.replace(old, new))

# 2) registry json
p = "Docs/design/mistletoe-parallel-deliveries.json"
d = json.load(io.open(p, encoding="utf-8"))
e = [x for x in d["deliveries"] if x.get("id") == "wave3-bo6-bo7-2026-09-28"][0]
e["blockers"] = [
    "BO-8 R29 与 BO-9 R34 F5 已由主线子批 wave3-bo8-bo9-2026-09-28 按原级 closed（BO-8 第 1 轮、BO-9 第 3 轮会诊裁定；BO-9 经两轮原级修复），本条目已无待消费内容。",
    "新增建议级残项 BO-9-D1：WorkflowRunner.cs 4 处注释陈旧（旧方法名 TryRelocateToLivePark ×3、return candidate 行尾措辞 ×1），留待下一次重新绑定该文件哈希的批次订正（与 BO-6/7-D1 同族口径）。",
    "R5.6、R6.1、R6 diff guard 仍未提前集成（各自目标批次处理）；BGI 产品入口、真实 User、R5.8、E3/E4/E5、热键面与生产进程门继续关闭。",
]
e["later_consumption"] = {
    "batch": "wave3-bo8-bo9-2026-09-28",
    "date": "2026-09-28",
    "note": ("BO-8 R29 与 BO-9 R34 F5 属台账 BO-11 冻结残项，与并行成果无关；BO-9 经第 1／2／3 轮会诊（本子批 3/8）后按原级 closed，"
             "BO-6/7-D1 由本批修正，另登记建议级残项 BO-9-D1。本条目所列交付物（BO-6/BO-7 源码/夹具/文档）在接收批已逐字节接收，未再变更。"),
}
d["updated_at"] = "2026-09-28T21:40:00+08:00"
io.open(p, "w", encoding="utf-8", newline="\n").write(json.dumps(d, ensure_ascii=False, indent=2) + "\n")
print("registry updated")
