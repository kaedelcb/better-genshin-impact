from pathlib import Path
import hashlib,json,subprocess,sys
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
load=lambda p:json.loads(p.read_text(encoding='utf-8'))
with s.Session(ROOT,'own-root-01a11380-context-runtime-handoff') as budget:
 out=BASE/'own-context-checkpoint';budget.track(out);out.mkdir(exist_ok=False)
 runtime=BASE/'own-runtime/fifth';result=load(runtime/'result.json');assert result['exit_code']==0 and result['protocol_restored'] and result['product_user_changed']==[]
 assert load(runtime/'apps-tree-terminal.json')['active_processes']==0
 before=runtime/'private/before';after=runtime/'private/after'
 same={folder:[] for folder in ['flows','runs']}
 for folder in same:
  for f in (before/folder).glob('*.json'):
   assert sha(f)==sha(after/folder/f.name),f
   same[folder].append(f.name)
 assert len(same['flows'])==4 and len(same['runs'])==7
 promoted=load(after/'flows/wf-1e7a758a.flow.json');assert promoted.get('activation') is None
 assert len(list((after/'flows').glob('*.json')))==8 and len(list((after/'runs').glob('*.json')))==7
 snapshot=load(BASE/'flow-context/causal/restored/source-hashes.json');assert all(sha(ROOT/n)==h for n,h in snapshot.items())
 value=dict(marker='OWN-ROOT-DEV-MATRIX-20261007-FROM-01a11380',source_thread='01a11380-5e25-7451-a15a-e3812284ea34',head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),candidate='wf-1e7a758a',candidate_before='9da4f8bc',candidate_after=sha(after/'flows/wf-1e7a758a.flow.json'),activation=None,old_byte_identical=same,runs_total=7,flows_total=8,apps_exit=[0,0],runtime=result,independent_review=False,product_complete=False,consultations_new=0)
 s.write(out/'runtime-summary.json',json.dumps(value,ensure_ascii=False,indent=2).encode())
 status=subprocess.run(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT,capture_output=True,check=True);s.write(out/'private/git-status.txt',status.stdout)
 report="""# 流程上下文修复实机检查点与整版接续

标记 OWN-ROOT-DEV-MATRIX-20261007-FROM-01a11380。来源01a11380，前继OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308。原完整产品未完成。源码/因果检查点e6166a46，最终交接HEAD以Git读回为准；main-OldTeaBag-B168，同项目local原工作区。

保存后切换、候选预览/激活同步源码五文件＋五用例，详见flow-context/CHECKPOINT.md。red原四Fail；green83Pass/0Fail/0Skip；negative五关键用例全红，两个源码逐字节恢复；restored同83Pass/0Fail/0Skip、source_drift空、八Job0。原终局代码/历史182Pass/2LocalWaitFail和六有效因果、第四轮实机保持，未改绑。重要发现2保留综合复核责任，不自行closed。

本轮同product五模块c1c11e61afa35a29bc20001f263b1f49a014743ee4097c2da5edbbe8e4969c35；BGI DLL0720fe79…/EXE42aa9e13…及9500User在刷新和第五轮逐SHA不变。第五轮真实BGI25980/助手23440 Session1、自有根。正常退出0/0、Job active0，协议恢复，父/预算终态后交接。runtime_context.py第五轮原始UI调用来源在fifth/private/native-ui-source.jsonl，勿外送敏感原件。

真实UI：原本机流程保存44ac05e9，整表导出full-export.json（4流程）并实际导入4只读候选；原4流程/7run逐SHA同。保存后从独立窗口直接选同名wf-1e7a758a，两窗相同候选、顶部启动/编辑禁用，主预览9da4f8bc/2节点。原流程名称临时输入「· 未保存保护验证」后选候选被拒，两窗原选择/状态保留、原始输入在；原文件SHA同。通过UI把临时输入恢复原值，随后可直接选候选。真实菜单激活wf1e7后activation=null/ceace011，两窗顶条只读标记消失/启动编辑可用，主预览同步新修订及候选文案消失。7run保持，无执行游戏资源。新增另3候选保持只读，8流程均保留。

仍需注意：正常整表导入/激活后存在同名active选择项，当前标签仅Name＋候选/隔离词、不显示稳定身份；旧run-9a416f351b6d有同名误选历史，本轮没有误执行证据。下一安全选择先定向确认是否仍导致用户不能区分目标，必要时最小补稳定身份/完整提示，沿C01实际入口及原重要正确性责任处理，不降级或按小函数重开前审。预览候选的「从选定节点启动」按钮仍视觉可用但起点为空；本轮未点击，该命令空起点直接返回及宿主候选禁执行为静态事实，不能假报此按钮实测通过。恢复原值成功切换后旧错误StatusMessage可残留；若只显示旧反馈且无执行/数据后果，可按用户普通显示问题规则登记后修。不要无限扩为通用设施。

随后集中补全部未验开发侧矩阵、真实资源参数修改保存及引用修订、正常迁移/停止/保存重启/数据保留，稳定完整版本必要独立综合复核与整版/启动步骤。用户定义致命BUG=功能不能用，特殊小范围BUG可原级后修，完整=总计划全部约定功能和正式UI可正常用；用户游戏/账号/兑换码/树脂/关机/队友等等其主动反馈，不代做，不阻断独立开发。参数仅打开的旧证据不等于改后保存。

总计划页首、complete-usable-v1、ui-design-intake、DELIVERY-COVERAGE/FIRST-POLICY及原plan.json ui_research_function_first_20261006保持权威。UI唯一E:\\Program Files\\mistletoe-ui-design，只读v4跨列聚类，不用v6/CODEX旧稿。流程到达节点就执行、重复由路径/循环决定，视觉跨列不复制，无OR/AND或每日配额；单流程与整表互导，C01/C02/C04-C11/C17/C20、公版、八原生、组/JS/宏均须保留。

禁止Codex移动真实用户目录/data_guard prepare/restore/原件改名。13入口AssistantDataDirectory显式NEXUSBGI_DATA_ROOT=E:\\Program Files\\better-genshin-impact-LCB\\_workflow\\final-ui-01a11220\\own-runtime\\assistant-data；不能默认根或迁旧exe配置。第三方JS一字不改。旧r2/external-restored原MSIX116/常规27及全部旧档/失败保持，联合路径不能冒充物理隔离。垫底占位含真实游戏动作。

storage-v1 operation1610612736/retained18790481920/min_free8589934592；原17.5GiB仅原Goal剩余验收复核。Session/process_runner纳管新材料/构建/取证/audit/审查；不换目录/渠道/工作树绕预算，不删历史/唯一成果，无可清scratch，不声称OS硬配额。bundle drift/r61缺report，bf733 blocked/98、extra2/2余额0、旧286=283P/1F/2NotExecuted、助手/BGI全量及ProductionCtor旧失败保持，本轮新增会诊0、无综合pass。稳定候选/具体实机材料齐备再申请固定次数/范围的必要复核，不超额或重置，也不以此停独立修复。

材料外csproj、两Migration源、旧文档/账本/备份与工作保留；只明确归属git commit --only，不push/发布/合并。Computer Use用完整skill/guidance/confirmations/api与node_repl @oai/sky，唯一真实窗口、每动作新观察；关联模态索引不可用可同工具重新观察后截图坐标，不能混PS UIA。文件对话框focused_element可误报搜索，实际截图蓝色文件名选区及写后Value已核；不凭旧焦点盲打。physical Escape停当轮，不reset换通道。

本次交接理由：已完成聚焦上下文源码/因果及同产物真实复验；原总目标仍未完成，且当前上下文出现反复追查/判断效率下降的可定位风险。安全终态、保全和精确提交后，原生暂停自身Goal读回paused，唯一同项目local接班，按来源当时实际模型/档位继承并核新rollout与完整Goal active。全部要求在同一初始消息发送，不让用户搬运，不恢复旧Goal，不开第二写者。
"""
 s.write(out/'HANDOFF.md',report.encode());print(json.dumps(dict(runtime_ok=True,old_flows_same=4,old_runs_same=7,flows=8,runs=7,product_user_changed=[],out=str(out)),ensure_ascii=False),flush=True)
