import json, pathlib
W = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB\_workflow\r56-reference-activation-wiring-2026-09-29")
mp = W/"manifest.json"; m = json.loads(mp.read_text(encoding="utf-8-sig"))
rc = m["review_control"]
rc["existing_results"] = {"decision":"none",
  "reason":("逐项核对现有交付后，本批没有可复用的同版本结果：R56/R58 组件夹具（53+14 条）与 activation-prep 验收矩阵"
            "只覆盖「阶段标记语义」与准备型材料，而本批要推翻的正是「阶段标记可代表真实副作用」这一旧语义；"
            "已 integrated 的 r56-migration-audit / r56-activation-prep 不重复接收。基线 TRX 以 binding=historical 仅作差集对照，"
            "不作当前回归证明。")}
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("reuse decision fixed")
