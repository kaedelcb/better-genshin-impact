import io, json
p = "_workflow/wave3-bo8-bo9/manifest.json"
m = json.load(io.open(p, encoding="utf-8"))
for e in m["evidence"]:
    if e["id"] == "claims-before-hash":
        e["purpose"] = "末次再生前清单哈希（617 行）"
        e["conditions"] = "daa2639f36ba35ae9370a759fe84e11afe49cb8d210d3892fa03e246520fb684"
    if e["id"] == "claims-after-hash":
        e["purpose"] = "末次再生后并清除变量后清单哈希（618 行）"
        e["conditions"] = "1f2b674365c9f150a0c4e898a54e586177283e07fa5bfb83bd2f4cbb786a5ccc"
    if e["id"] == "claims-diff-summary":
        e["purpose"] = "清单差异摘要：+1 行／-0 行（BO-9-D1 登记句）"
io.open(p, "w", encoding="utf-8").write(json.dumps(m, ensure_ascii=False, indent=2) + "\n")
print("evidence paths/hashes fixed")
