import json, pathlib
W = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB\_workflow\r56-reference-activation-wiring-2026-09-29")
op = json.loads((W/"opening.json").read_text(encoding="utf-8-sig"))
mp = W/"manifest.json"; m = json.loads(mp.read_text(encoding="utf-8-sig"))
task = m["review_control"]["subagents"]["tasks"][0]
task["opening_source_hashes"] = op["source_hashes"]
task["note"] = ("子 Agent 的固定 ref = " + op["head"] + "；上表为开工快照全体 source 的哈希（工具要求与 opening 一致）。"
                "子 Agent 实际只读核查范围为该 ref 上的 MigrationSwitchTransaction.cs / 引用服务可见性 / activation 消费面。")
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("subagent task hashes bound to opening snapshot")
