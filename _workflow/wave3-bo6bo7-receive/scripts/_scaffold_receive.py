import json, io, os
d = '_workflow/wave3-bo6bo7-receive'
os.makedirs(d, exist_ok=True)

BATCH = 'wave3-bo6bo7-receive'
SRC = [
 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt',
]

rows = [
 {"id":"S1","dimension":"state","critical":True,"status":"planned",
  "scenario":"恢复记录含持久 waitLocally 历史、且保存游标在新 plan 仍可定位时发起显式 Resume（含删除后同身份插回锚前的舞步）。",
  "expected":"在覆盖修订号前重算恢复点，按 (LoopIteration,SequenceIndex) 计划全序选择最早仍有效停驻；提交序列恰为 P1,Q,R,stop，lead/A/B 各恰一次，不产生虚假成功或终态收尾。"},
 {"id":"S2","dimension":"state","critical":True,"status":"planned",
  "scenario":"恢复推进段跨越修订重排后落在恢复点之后的已完成 occurrence（同轮次或跨轮次旧轮身份）。",
  "expected":"按稳定身份 NodeId+Occurrence+LoopIteration 跳过本 run 已完成出现；已完成节点不被二次提交，新轮同名出现仍可执行。"},
 {"id":"S3","dimension":"state","critical":True,"status":"planned",
  "scenario":"持久 TailReached 与仍可定位、未完成的停驻义务并存时到达聚合判定。",
  "expected":"聚合结果 Failed 并持久化，保留停驻义务与 TailReached，不创建 PendingCompletion、不执行终态动作。"},
 {"id":"S5","dimension":"state","critical":True,"status":"planned",
  "scenario":"candidate 与 rescue 分处不同 loop 轮次（candidate 较早 与 rescue 较早 两种方向）。",
  "expected":"按 (LoopIteration,SequenceIndex) 字典序选择全序较早者，较晚者由线性推进自然到达，两个义务都不丢。"},
 {"id":"S4","dimension":"state","critical":False,"status":"planned",
  "scenario":"集成后声明面（R5.3 状态词/门禁词/证据等级行）与源码的一致性检查。",
  "expected":"需要再生时 CLAIM_SURFACE_REGENERATE=1 再生通过，清除变量后守卫通过，清单 SHA 在两次运行间一致。"},
 {"id":"C1","dimension":"concurrency","critical":True,"status":"planned",
  "scenario":"恢复推进的重定位写与 Failed 聚合写相对返回/重载的可观察顺序（同一 run 单写者）。",
  "expected":"每次重定位后更新运行记录；Failed 在返回前持久化；重新 Load 可观察到同一状态（不出现内存 Failed、盘上 Running）。"},
 {"id":"C2","dimension":"concurrency","critical":False,"status":"planned",
  "scenario":"多有效停驻跨轮次推进、以及多实例/跨进程对同一运行的并发写。",
  "expected":"本接收批不作结论。","reason":"占位，送审前改为 not_applicable"},
 {"id":"F1","dimension":"fault","critical":True,"status":"planned",
  "scenario":"停驻/完成身份在修订重排后被删除或同身份重新插入，SequenceIndex 发生变化。",
  "expected":"完成判定只用 NodeId+Occurrence+LoopIteration，不因 SequenceIndex 变化误判完成或漏判已完成。"},
 {"id":"F2","dimension":"fault","critical":True,"status":"planned",
  "scenario":"恢复点无法构造，或停驻身份在当前 plan 不可定位（异常/退化路径）。",
  "expected":"fail-closed：不得静默按链尾成功或吞掉可定位的未完成停驻义务；聚合保护将其转 Failed。"},
]
matrix = {"schema_version":1,"batch":BATCH,"rows":rows}
io.open(os.path.join(d,'risk-matrix.json'),'w',encoding='utf-8',newline='\n').write(json.dumps(matrix,ensure_ascii=False,indent=2)+"\n")

manifest = {
 "schema_version":2,"batch":BATCH,"mode":"code",
 "sources":SRC,
 "risk_matrix":d+"/risk-matrix.json",
 "opening_snapshot":d+"/opening.json",
 "review_control":{
   "existing_results":{"decision":"none","reason":"来源独立交付的 93/93、1562/2/0/1564 与 8 项突变均绑定隔离 worktree 的字节，不是本主线集成版本的运行结果；本批必须在集成后的主线 HEAD 重跑定向、全量与突变，不能复用为当前回归证明。"},
   "review_round":1,
   "prior_findings":[],
   "criticality_reason":"",
   "subagents":{"decision":"not_used","reason":"本接收批的验证主体是把来源修复导入后由唯一写者重新执行定向/全量回归与 8 项反向突变（Runner 为共享状态链，设施计划禁止多子 Agent 并行改代码或操作测试产物）。固定开工 ref 上的只读子 Agent 只能重derive 两名独立审查者已在同一字节上给出结论的材料（会诊报告、突变 TRX、testId 差集），无新增独立信息，故不派遣。"}
 },
 "evidence":[],
 "tests":[],
 "mutation_scope":"",
 "mutations":[],
 "outside_changes":"",
 "packet_limit_bytes":524288,
 "packet":[]
}
io.open(os.path.join(d,'manifest.json'),'w',encoding='utf-8',newline='\n').write(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n")
print("scaffolding written")
