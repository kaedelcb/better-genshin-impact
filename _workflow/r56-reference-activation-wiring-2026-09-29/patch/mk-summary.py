import json, pathlib
root=pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB"); W=root/"_workflow/r56-reference-activation-wiring-2026-09-29"
recs=json.loads((W/"mutations/records.json").read_text(encoding="utf-8"))
L=["# 反向突变台账（28 项，供审查者核对判别力）","",
   "每项＝对最终源码的**单点削弱**；判定要求 baseline Passed / mutant Failed（构建 exit 0、测试 exit>0 且命中具名断言）/ restored Passed，",
   "且恢复后源码**逐字节**等于原始哈希。逐项目录含 `build.log`、`baseline.log|trx`、`mutant-build.log`、`mutant.log|trx`、`restored-build.log`、`restored.log|trx`、`record.json`。",
   "","## 逐项：补丁（before → after）与判定",""]
for r in recs:
    L.append(f"### {r['id']}")
    L.append(f"- 描述：{r['description']}")
    L.append(f"- 源文件：`{r['source']}`；original SHA-256 `{r['original_sha256']}`；mutant `{r['mutant_sha256']}`；restored `{r['restored_sha256']}`（逐字节相等＝{r['original_sha256']==r['restored_sha256']}）")
    L.append(f"- 目标断言：`{r['target_name']}`（testId `{r['target_test_id']}`）")
    L.append(f"- 判定：baseline **{r['outcomes']['baseline']}** / mutant **{r['outcomes']['mutant']}**（构建 exit {r['mutant_build_exit']}、测试 exit {r['mutant_exit']}）/ restored **{r['outcomes']['restored']}**")
    L.append(f"- 命中标记：`{r['failure_contains']}` + 断言栈 `{r['assertion_contains']}`")
    L.append("")
    L.append("```text")
    L.append("--- MUTANT 补丁（before → after）---")
    L.append(r.get("mutant_patch", "").strip() or "(补丁原文见同目录 record.json 的 old/new 字段)")
    L.append("```")
    L.append("")
(W/"mutations/summary.md").write_text("\n".join(L)+"\n", encoding="utf-8")
print("summary written:", (W/"mutations/summary.md").stat().st_size)
