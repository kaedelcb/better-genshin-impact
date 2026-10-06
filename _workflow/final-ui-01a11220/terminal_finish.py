from pathlib import Path
import json,hashlib,subprocess,sys
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent
OUT=BASE/'own-terminal-checkpoint';DATA=BASE/'own-runtime/assistant-data'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
with s.Session(ROOT,'own-root-01a11308-terminal-actual-checkpoint') as budget:
 budget.track(OUT);OUT.mkdir(exist_ok=False)
 def write(p,v):s.write(p,json.dumps(v,ensure_ascii=False,indent=2).encode())
 fourth=load(BASE/'own-runtime/fourth/result.json')
 assert fourth['exit_code']==0 and fourth['product_user_changed']==[] and fourth['protocol_restored']
 assert load(BASE/'own-runtime/fourth/apps-tree-terminal.json')['active_processes']==0
 assert load(BASE/'own-runtime/fourth/apps-process-result.json')['exit_code']==0
 runs={p.stem.replace('.run',''):load(p) for p in (DATA/'runs').glob('*.run.json')}
 arbitration=load(DATA/'arbitration/arbitration-lease.json');assert arbitration['lease'] is None
 operations=arbitration['handoff']['operations']+arbitration['handoff'].get('archivedOperations',[])
 facts=[]
 for key in ['run-7ac18b5047e9','run-c1c516e6be73','run-e1b8d4e6b5f8','run-53aec3a681db']:
  r=runs[key];op=[o for o in operations if o.get('runBinding')==key]
  assert r['state']==4 and not r['stopRequested'] and not r['submissionHistory'] and r['currentSubmission'] is None and not r['completionHistory']
  assert r['terminalRelease'] and len(op)==1 and op[0]['requestState']==6 and op[0]['terminalReleaseEvidence']=='runstore-seal:'+r['terminalRelease']['Id']
  facts.append(dict(run_id=key,workflow_id=r['workflowId'],revision=r['workflowRevision'],state=4,outcomes=[o['result'] for o in r['nodeOutcomes']],request_identity=op[0]['requestIdentity'],submission_identity=op[0]['submissionIdentity'],target_epoch=op[0]['targetEpoch'],request_state=6,seal=r['terminalRelease']['Id'],submissions=0,completion=0))
 old=load(BASE/'own-root-checkpoint/private/runs/run-7ac18b5047e9.run.json')
 assert old['nodeOutcomes']==runs['run-7ac18b5047e9']['nodeOutcomes']
 preserved=[]
 for key in ['run-cdd4a73f7a8d','run-9a416f351b6d','run-c897edad1896']:
  path=DATA/'runs'/(key+'.run.json');prior=BASE/'own-root-checkpoint/private/runs'/path.name
  assert path.read_bytes()==prior.read_bytes();preserved.append(key)
 candidate=load(DATA/'flows/wf-242498c6.flow.json');assert candidate.get('activation') is None
 assert [n['nodeId'] for n in candidate['nodes']]==['n-6fe663b0','n-090b94d1','n-c747b01d']
 control=load(DATA/'flows/wf-own-control-01a112a0.flow.json');assert control['nodes'][0]['path']['condition']['value'] is True
 for folder in ['runs','flows']:
  for p in (DATA/folder).glob('*.json'):s.write(OUT/'private'/folder/p.name,p.read_bytes())
 s.write(OUT/'private/arbitration-lease.json',(DATA/'arbitration/arbitration-lease.json').read_bytes())
 write(OUT/'runtime-summary.json',dict(marker='OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308',source_thread='01a11308-0978-72b3-9a17-4933e199439a',facts=facts,old_stopped_runs_sha_same=preserved,old_original_node_outcomes_same=True,candidate_activated_no_run=True,total_runs=7,restored_control_value=True,control_revision='44ac05e9',control_normalized_schedule_span=1,apps_exit=[0,0],fourth=fourth,lease=None,independent_review=False,product_complete=False,consultations_new=0))
 status=subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain'],cwd=ROOT)
 s.write(OUT/'git-status.txt',status)
 head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
 write(OUT/'source-identity.json',dict(head=head,model='gpt-6.1-sol',effort='medium',assistant_sha=sha(ROOT/'_workflow/runtime-unified-01a10e1b/product/Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll'),bgi_dll_sha=sha(ROOT/'_workflow/runtime-unified-01a10e1b/product/BetterGI.dll'),bgi_exe_sha=sha(ROOT/'_workflow/runtime-unified-01a10e1b/product/BetterGI.exe')))
 report='''# 仲裁终局实际复验与流程切换接续

唯一标记 OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308，来源01a11308-0978-72b3-9a17-4933e199439a。完整总产品未完成。当前源码候选44b978cb9159db9613bddf96afbf1afe1a25802c；后续本交接提交HEAD以Git读回为准。原Goal/opening、bf733 blocked/98、重要责任、extra2/2余额0及历史失败不重置。本轮新增会诊0，无综合pass。

## 本阶段事实

RunSettled旧白名单遗漏branchYes/branchNo，Succeeded无封印，原流程操作Accepted。限定补丁只接受无原始外部身份及同次提交的本地分支；复用原映射/封印/终局机制，扩展终态Stop核对Succeeded/Failed/Cancelled，原结果不改，不重新驱动；历史入口新增核对结束状态。所有退出、未知、映射和生产门保持。详见own-runtime/refresh-terminal/CHECKPOINT.md及terminal-path原始来源。

red2Fail；首次green178Pass/5Fail含新增夹具缺停止权威端口3Fail，原件保持。修正后causal baseline/restored均182Pass/2Fail，六个新用例全部Pass；negative六新用例均Fail，加原二项合计8Fail。三轮Rebuild0/六Job0/父exit0/源码漂移空，两个源码逐字节恢复。原unsupported/corrupt两LocalWait失败在原逻辑反向突变中同样失败，不改断言、不报全量绿。当前源码不是mutant。候选本地提交44b978c共70明确文件，源码6、脚本及原始失败/执行材料，其它暂存未吞入；提交后暂存空、源哈希无漂移、材料外csproj及两Migration源保持。

同一完整product仍runtime-unified-01a10e1b/product，五助手模块精确刷新ce4c6605caaa7404b70d3020158ae6a9a8dc4502fe49ab0efa0eaf75ef028b4b，旧508fb模块在refresh-terminal/before。BGI DLL0720fe79e71f1069c51378cc8a0e54e621dd03ea900fca052915c77f9a9147c7、EXE42aa9e13aa823a2796fd96b666360dff29890ed1f4d73798f837fa740ac70795分别核；1351输入和9500User SHA同。

第四轮BGI10428/助手25316，Session1/精确product路径/显式own-runtime/assistant-data。实际历史按钮核对原run-7ac18b5047e9：原操作cc8fd9167daf4fe8b7a3cc03cc721ff1的5→6，seal17336632302a40408d088847b6defd3e；state4、stopfalse、原NodeOutcomes/原发送身份/原epoch24428不改，0提交/0收尾、run数仍4。正常是分支run-c1c516e6be73、run-e1b8d4e6b5f8两次各branchYes+succeeded，自动原操作6/封印，0提交/收尾。真实检查器改常量false并保存48726c75，run-53aec3a681db只branchNo直接结束，自动6/封印、0提交/收尾；经UI恢复true保存44ac05e9，原节点/路径保持，条件节点规范化scheduleSpan=1，故不是原flow字节同。前三个旧Cancelled run逐SHA同。三个新run保持各自冻结修订。

实际整表导入候选wf-242498c6在关闭已保存临时草稿后成功激活，activation:null=正常可编辑，原三节点及11D4资源修订保持，run总数7不变；未启动该资源。垫底占位含真实合成树脂/秘境等游戏动作，不代执行。助手确认正常退出、BGI正常关闭，两exit0/0、Job active0、父执行终态、协议恢复、product_user_changed=[]。无自有应用/构建/测试/突变在途，预算终态后再交接。private存原始自有样本，保全、不随意外送，不编辑账本为成功。

## 唯一下一项：UI流程切换与候选状态

OWN-ROOT-FLOW-SWITCH-IMPORTANT-2：先保留重要正确性/正常入口责任，根因与用户后果定向证实，不自行降级。真实保存后SaveEditing立即重开Host.Editing；GuardNoOpenDraft只看Editing非空，FlowChanged候选Preview后又按旧Draft.WorkflowId回选。实际选候选却仍顶部旧本机流程/启动可用，下面候选预览已经出现，激活灰；关闭已保存临时草稿再选候选才正常禁启动/编辑、激活可用。候选和旧流程同名时存在用户意图/实际入口错配风险，未假报误执行发生。另激活后CanStart/CanEdit已真、activation已无candidate标记，但顶部（只读候选）及旧预览c48e状态不更新，原静态文案称此处不提供激活。预览冻结修订与当前选择/编辑上下文要明确保持一致，不能为了切换丢掉未保存或修订冲突草稿。

查ScheduleListView.xaml.cs Observe/HostChanged/FlowChanged/SaveEditing/ChoiceLabel与XAMLSelectedItem converter；TaskCenterPanelViewModel GuardNoOpenDraft/Preview/Edit/Save/Discard/Refresh；EditParts激活/Refresh和ViewModel通知。以完整生产WPF入口红反例集中修复：保存后安全切换、脏/冲突草稿不静默丢失、候选只读禁启动编辑/菜单正确身份、激活后当前选择与预览/标签刷新、双窗共享状态/Undo保持。当前片段已可定位，原修复许可沿用，不另开小函数整包前审；稳定候选一次Rebuild、有效因果及相称回归，再同一product实际复验。核心仲裁补丁不要重做/削门。

随后继续全部约定全功能/UI及实际开发侧矩阵、真实资源参数改后保存/引用修订、正常迁移/数据保持、必要独立综合复核及整版版本/启动步骤。预算0，不超额/重置；稳定候选和具体实机材料齐备时才申请固定次数/范围的必要复核授权。游戏/账号/兑换码/树脂/关机/队友等等用户主动反馈，不代做，不第二套游戏测试，不阻断其它开发。

## 保护与权威入口

任何启动都NEXUSBGI_DATA_ROOT=E:\\Program Files\\better-genshin-impact-LCB\\_workflow\\final-ui-01a11220\\own-runtime\\assistant-data，禁止默认根；禁止Codex移动真实目录或data_guard prepare/restore。旧外部恢复r2/external-restored原MSIX116/常规27及原件/测试档保持，不拿联合路径冒充物理隔离。第三方JS一字不改，源码/产物/组件/真实验收等级分开。

先读AGENTS、bgi-project-development、本交接、源identity/runtime-summary/private、OWN-ROOT-HANDOFF/原CHECKPOINT；总计划页首、complete-usable-v1、ui-design-intake、DELIVERY-COVERAGE/FIRST-POLICY、plan.json ui_research_function_first_20261006、path-runtime与RUNTIME-MATRIX/causal-repeat。唯一UI预研E:\\Program Files\\mistletoe-ui-design只读，README/总册v2/redesign附录A/app/lib/sched/components/Home/10截图；layout v4跨列聚类，v6/旧CODEX稿不用。车道是路径，流程到达就执行、重复由路径/循环决定，视觉跨列不复制/不设ORAND或每日配额；单流程和整表互导须支持。C01/C02/C04-C11/C17/C20、公版、八原生、组JS宏保持，退役不复活。

storage-v1现operation1610612736/retained18790481920/min_free8589934592，原用户17.5GiB授权third/storage-authorization-17.5gib.json只覆盖原Goal剩余验收复核；新材料/构建/审查/audit经Session/process_runner，不绕目录/渠道/工作树，不声称OS硬配额，无可清scratch。设施/storage/v3/README/并行md-json及deliveries只读发现；bundle drift与r61缺报告保持，不扩工具。单次core.longpaths=true只读status已确认删除0，不按旧长路径告警恢复/删文件。

Computer Use完整skill/guidance/confirmations/api与node_repl @oai/sky，选唯一实际返回窗口，每动作新观察，不混PowerShell UIA。关联popup索引不可用/控件offscreen时同工具刷新后截图坐标/外层滚动恢复；本轮set_value滚动条0x80131509后仅重观察及正常滚动，正常退出capture窗口gone据进程/Job确认。physical Escape即停当轮，既有恢复授权不扩大为永久绕过。

真实施工必要交接按全局0：安全终态/本地精确commit、原生旧Goal paused读回、同项目local唯一接班，继承来源当时实际model/effort并核新rollout/完整Goal active，不恢复旧Goal、不重开opening/历史/额度、不把此阶段当产品交付。
'''
 s.write(OUT/'HANDOFF.md',report.encode())
 prompt='''/goal 按正式UI预研与“流程到达节点就执行、重复由路径/循环决定”完成BetterGI茶包版＋槲寄生助手全部约定功能、正式WPF界面和真实接线；保护真实User/第三方JS/历史证据、不从Codex移动真实用户目录，接续已通过的本机仲裁终局修复与同产物实际复验，先集中修复保存后流程切换/候选上下文及激活状态同步，再补全全部未验开发侧实际入口、参数保存/引用修订、正常迁移/停止/保存重启/数据保留、必要独立综合复核和整版版本/启动步骤；原级open/历史/累计预算保持，不将局部Succeeded或组件通过当完整交付。

唯一标记 OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308。来源聊天01a11308-0978-72b3-9a17-4933e199439a；前继OWN-ROOT-TERMINAL-20261007-FROM-01a112a0。先建本批Goal（结果＋约束＋完成判据），再开工：先查询本线程原生Goal，无则只建一次覆盖本消息全部审计、实施/修复、验证与完整产品交付并读回active；有则核完整范围。/goal文字不是已建立证明。本聊天唯一同项目local接班，实际cwd E:\\Program Files\\better-genshin-impact-LCB，main-OldTeaBag-B168，沿用原工作区、不换worktree/克隆。原总产品未完成，不恢复旧Goal，不重开opening/包/历史/累计额度。来源先安全终态、本地提交、原生paused读回后创建，本消息一次完整发送。

采用handoff-auto-create-20261004-v1及handoff-inherit-model-20261004-v1，明确继承来源实际gpt-6.1-sol/medium；来源最新turn_context2026-10-06T21:04:21.024Z model/effort与settings一致，原生路径E:\\CodexData\\home\\sessions\\2026\\10\\07\\rollout-2026-10-07T05-04-16-01a11308-0978-72b3-9a17-4933e199439a.jsonl。先只读核本聊天实际rollout一致，未知/冲突不写、不静默换模型/降档、不重复创建。第一条反馈marker、实际cwd、完整原生Goal active和自身实际model/effort证据供来源核验。

开工先读AGENTS、.agents/skills/bgi-project-development/SKILL.md、_workflow/final-ui-01a11220/own-terminal-checkpoint/HANDOFF.md、runtime-summary.json/source-identity.json/private/git-status.txt，及own-runtime/fourth的真实结果/进程终态、refresh-terminal/CHECKPOINT/result、terminal-path/red/green/causal全部真实TRX/源hash/Job终态/恢复记录；再沿OWN-ROOT-HANDOFF、前继CHECKPOINT/HANDOFF/RUNTIME-MATRIX/path-runtime/causal-repeat。最新HEAD/分支/工作区现场读回，不把候选44b978c当最后交接HEAD。当前无自有BGI/助手/python/dotnet、构建/测试/突变/恢复/预算写者在途；全部预算终态由来源核验后交接。大量材料外csproj、两Migration源、旧文档/账本/备份及_workflow保护，core.longpaths=true只读status已确认删除0，不按旧长路径告警恢复/清理。

已完成聚焦事实：RunSettled漏branchYes/No致本机Succeeded无seal、Accepted留Active。最小补丁接受严格无发送事实本地分支；终态Stop通过原映射/封印机制核对Succeeded/Failed/Cancelled，历史核对按钮接线。red2F，首次green178P/5F含新夹具缺停止权威3F，原件保持；修正后causal182P/2F→六新用例全红＋原二F共8F→182P/2F，三Rebuild0/六Job0/父0/源漂移空/两源逐字节恢复。原unsupported/corrupt两LocalWait失败在旧逻辑突变中同样失败，断言不改，不报全量绿。当前不是mutant。

同一完整product仍_workflow/runtime-unified-01a10e1b/product，五模块刷新助手DLL ce4c6605caaa7404b70d3020158ae6a9a8dc4502fe49ab0efa0eaf75ef028b4b；旧508fb模块保全在refresh-terminal/before。BGI DLL0720fe79e71f1069c51378cc8a0e54e621dd03ea900fca052915c77f9a9147c7和EXE42aa9e13aa823a2796fd96b666360dff29890ed1f4d73798f837fa740ac70795独立核；1351输入/9500User同。第四轮BGI10428/助手25316 Session1该产物已正常exit0/0，Job active0、父0、协议恢复、product_user_changed=[]。

实际旧run7ac原cc8fd操作5→6/173366seal，Succeeded/stopfalse/原结果/原发送身份/原epoch24428保持，0提交收尾、run数4；新c1c/e1b两是路径及53ae否直接结束全Succeeded、自动原操作6/封印、0BGI提交/0收尾。UI false保存48726c75→恢复true44ac05e9，节点/路径保持，条件规范化scheduleSpan1，不假报原flow字节同。旧三个Cancelled run逐SHA同。整表候选wf242498在关闭已保存临时草稿后真实激活，activation:null正常可编辑、三节点及11D4资源修订保持，run总7不变。未启动游戏资源。原样本private保留，不编辑运行/仲裁文件来制造成功。OWN-ROOT-FAST-END-IMPORTANT-1已有源、因果、同产物本机证据，仍保留必要独立综合复核责任，不自行改原报告/原级closed。

唯一下一项OWN-ROOT-FLOW-SWITCH-IMPORTANT-2：保留重要正确性/正常入口责任，定向证实用户后果，不自行降级。SaveEditing重开Editing，GuardNoOpenDraft只看非空，FlowChanged候选Preview后按旧Draft.ID回选；实际选择候选却仍顶部旧本机流程/启动可用、下方候选预览已出现/激活灰，关闭已保存临时草稿后才可选候选并禁启动编辑/激活。候选与旧流程同名存在意图/实际入口错配风险，未假报误执行发生。激活后实际CanStart/Edit真且文件已正常，但顶部只读候选/旧预览c48e与旧文案不更新。集中检查ScheduleListView Observe/HostChanged/FlowChanged/SaveEditing/ChoiceLabel/XAML binding、VM GuardNoOpenDraft/Preview/Edit/Save/Discard/Refresh、EditParts激活和通知。生产WPF入口红反例→最小一致修复：保存后安全切换，脏/冲突草稿不静默丢，候选只读/真实当前选择与菜单身份、激活后预览/标签同步、双窗共享Undo保持；一次Rebuild/有效因果/相称回归→同产物实际复验。沿原限定修复许可，不按小函数重开整包前审，不重做已有效仲裁/拖动/独立根修复，不削门。

全部目标继续：用户定义致命BUG=功能没法用，特殊小范围BUG可原级后修，完整=总计划全部约定功能和正式UI正常可用；读总计划页首、Docs/design/mistletoe-complete-usable-delivery-20261006.md、ui-design-intake、DELIVERY-COVERAGE/FIRST-POLICY、原plan.json ui_research_function_first_20261006，采用release-first-v2/complete-usable-v1。UI唯一E:\\Program Files\\mistletoe-ui-design只读，README/总册v2/redesign附录A/app/lib/sched/components/Home/10截图，layout v4跨列聚类，v6/CODEX旧稿不用。车道是路径，到达节点就执行，重复由路径/循环决定，视觉跨列不复制，无ORAND/每日配额，不再询问已定语义；单流程和整表互导必须支持。C01/C02/C04-C11/C17/C20、公版、八原生、组/JS/宏保持。补所有仍欠全功能开发側矩阵、真实资源参数改后保存/引用修订、正常迁移/数据/停止重启、必要综合复核及整版产物/启动步骤，不交演示缩水版。

数据边界：禁止再从Codex移动真实用户目录/data_guard prepare或restore/原件改名。r2/external-restored已证原MSIX116/常规27逐SHA物理恢复，原件/旧档/失败保持；联合路径/FolderPath不能替代物理隔离。13入口AssistantDataDirectory统一显式NEXUSBGI_DATA_ROOT，任何验收启动必须E:\\Program Files\\better-genshin-impact-LCB\\_workflow\\final-ui-01a11220\\own-runtime\\assistant-data，不默认根、不迁入旧exe配置。第三方JS一字不改，不向main.js注入。游戏/UID/账号切换/兑换码/树脂/关机/队友动作等用户主动反馈，不代做，不第二套游戏测试，不阻断独立开发。垫底占位含真实游戏动作，不因名字假定空任务。

预算：storage-v1 operation1610612736/retained18790481920/min_free8589934592，用户原17.5GiB仅原Goal剩余验收复核，原third/storage-authorization-17.5gib.json与17GiB旧授权保持。新材料/构建/取证/audit/审查经Session/process_runner，不绕目录/渠道/工作树、不声称OS硬配额、不删除历史User/唯一成果，无可清scratch。先读设施/storage/v3/README并核bundle、并行md-json和deliveries只读发现，自行核消费。bf733 blocked/98、extra2/2余额0、旧286=283P/1F/2NotExecuted、助手/BGI全量失败/ProductionCtor旧断言、bundle drift/r61缺report保持。本来源新增会诊0，无综合pass/receipt。必要复核授权在稳定候选/具体实机材料齐备后申请固定次数/范围，不超额/重置，也不以此停可独立修复。

Computer Use完整skill/guidance/confirmations/api、node_repl import @oai/sky，只取唯一真实返回窗口，每动作新观察，不混PS UIA。offscreen或关联模态索引不可用可同工具刷新后截图坐标/滚动恢复；physical Escape停当轮，不reset换通道，既有恢复意图不扩大永久授权。逻辑/WPF批量自动、真实鼠标/跨软件用Computer Use。编辑前核编码/大小/hash，后diff/stat；正常Rebuild DeployToBgiTools=false核部署，不杀其它用户Session程序。安全终态按明确归属git commit --only精确文件，新增精确add，不push/发布/合并。每次必要施工交接安全终态/精确保全、本地提交、原生paused读回、同项目local唯一接班、继承各自来源当时真实model/effort并核新rollout/完整Goal active，原总目标未完成不complete。全部要求在本初始消息完整发送，不另发必需补充。
'''
 s.write(OUT/'NEXT-PROMPT.txt',prompt.encode())
 print('terminal actual checkpoint captured; protected data unchanged; product incomplete',flush=True)
