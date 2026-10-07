from pathlib import Path
import hashlib,json,subprocess,sys
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
with s.Session(ROOT,'own-root-01a11380-context-checkpoint') as budget:
 out=BASE/'flow-context';budget.track(out)
 load=lambda p:json.loads(p.read_text(encoding='utf-8'));sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
 green=load(out/'green/result.json');causal=load(out/'causal/result.json');refresh=load(BASE/'own-runtime/refresh-context/result.json')
 assert green['build']==green['test']==0 and len(green['Passed'])==83 and not green['Failed'] and not green['source_drift']
 assert causal['source_restored'] and causal['phases'][0]['passed']==0 and len(causal['phases'][0]['failed'])==5 and causal['phases'][1]['passed']==83 and not causal['phases'][1]['failed']
 for folder in [out/'red',out/'green',out/'causal/negative',out/'causal/restored']:
  for name in ['build','test']:assert load(folder/(name+'-tree-terminal.json'))['active_processes']==0
 hashes=load(out/'causal/restored/source-hashes.json');assert all(sha(ROOT/n)==h for n,h in hashes.items())
 terminal=load(BASE/'terminal-path/causal/source-restored.json');assert all(sha(ROOT/n)==r['sha256'] for n,r in terminal.items())
 status=subprocess.run(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT,capture_output=True,check=True)
 s.write(out/'private/git-status-before-checkpoint.txt',status.stdout)
 head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip();branch=subprocess.check_output(['git','branch','--show-current'],cwd=ROOT,text=True).strip()
 value=dict(marker='OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308',finding='OWN-ROOT-FLOW-SWITCH-IMPORTANT-2',state='implemented_candidate_runtime_and_independent_review_pending',head_before_commit=head,branch=branch,baseline_passed=83,mutant_failed=5,restored_passed=83,source_restored=True,source_drift=[],review_requests_new=0,review_budget_remaining=0,product_complete=False,assistant_sha=refresh['updated'][0]['sha256'],bgi_sha=refresh['bgi_sha256'],bgi_exe_sha=refresh['bgi_exe_sha256'],product_user_changed=[],user_files=9500,old_terminal_sources_unchanged=True,next='same complete product fifth real UI flow switch/dirty draft/activation, then remaining full developer matrix')
 s.write(out/'checkpoint.json',json.dumps(value,ensure_ascii=False,indent=2).encode())
 report="""# 保存后流程切换与候选上下文检查点

OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308，来源01a11308，执行者01a11380。完整原生Goal本聊天已建并读回active，实际cwd同项目local，实际模型gpt-6.1-sol/xhigh。继续本聊天完成同产物真实复验和余下整版工作；当前不交接、不恢复旧Goal、不重开原opening/历史/累计额度。

采用release-first-v2、complete-usable-v1、storage-v1及自动本地检查点提交。原bf733 blocked/98、extra2/2余额0、原级open/旧全量/LocalWait失败、bundle drift/r61缺报告保持。本阶段新增会诊0，无独立综合pass/receipt。沿原限定修复授权；没有另开整包前审或降级发现。

OWN-ROOT-FLOW-SWITCH-IMPORTANT-2保持重要。源码候选统一共享流程身份、原始编辑值与修订检查；无改动且修订未变的草稿可切换，脏/无效输入、新建或冲突草稿保留；选择失败两窗回到原身份，预览不切到另一份流程。激活后按真实快照修订刷新预览，选择名称绑定条目属性；保留共享Undo和冻结运行修订。五产品文件和一份五用例WPF夹具，写前/后字节、编码和diff范围已核。

原实现red四失败（保存后的同名候选、无改动切换、冲突预览、激活预览）。随后将脏输入独立成第五用例，green Rebuild0、83Pass/0Fail/0Skip；因果negative有意破坏草稿保护/编辑态选择/预览刷新，Rebuild0、五用例全Fail；两源码逐字节恢复，restored Rebuild0、同83Pass/0Fail/0Skip，源码漂移均空。八build/test Job均active0、父终态按运行句柄及预算账读回。TRX/执行请求、真实退出/进程树、源hash与突变恢复原件在red/green/causal。原终局两源码hash仍等于terminal-path/causal/source-restored.json；此前182Pass/2LocalWait失败、六关键突变及第四轮实机证据没有重做或改绑。

同一完整product仍runtime-unified-01a10e1b/product，refresh-context只刷新五助手模块，旧ce4模块在before逐字节保全。BGI DLL/EXE与9500User逐SHA不变。具体助手新SHA见refresh-context/result.json。当前未启动新版，不能把83项或模块刷新当实机或完整交付。

唯一下一项：runtime_context.py fifth，显式NEXUSBGI_DATA_ROOT=own-runtime/assistant-data，通过Computer Use实际保存后同名候选切换、脏稿保留、激活后的标签/预览/动作、双窗共享及正常退出/数据保留。禁止从Codex移动真实用户目录或调用data_guard prepare/restore；第三方JS一字不改；垫底占位含真实游戏动作，不代启动。继续参数改后保存/引用修订、全部未验开发侧入口、正常迁移/停止/重启、必要综合复核与整版版本/启动步骤。用户游戏/账号/兑换码/树脂/关机/队友等等待其主动反馈，不阻断可自行推进工作。

预算operation1610612736/retained18790481920/min_free8589934592保持，材料与执行均经Session/process_runner，不绕预算、不删历史/唯一成果，不声称OS硬配额。材料外csproj、两Migration源、旧文档/账本及其它工作保护。检查点只本地精确commit，不push/发布/合并，不关闭原级义务。
"""
 s.write(out/'CHECKPOINT.md',report.encode());print(json.dumps(value,ensure_ascii=False),flush=True)
