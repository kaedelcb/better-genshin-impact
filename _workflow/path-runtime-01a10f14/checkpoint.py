from pathlib import Path
import sys,json,hashlib,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
paths=['MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowModels.cs','MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowPath.cs','MultiplayerHoeingAssistant/Services/BgiLogTailService.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowNodeSchedule.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlan.Path.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlanner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowObservation.cs','MultiplayerHoeingAssistant/ViewModels/MistletoeViewModel.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/ViewModels/WorkflowPathEditVm.cs','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalScheduleTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalScheduleRenderTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalObservationTests.cs']
result=json.loads((base/'causal-repeat/result.json').read_text(encoding='utf-8-sig'))
assert not result['source_drift'] and result['baseline_exit']==0 and result['negative_exit']==1 and result['restored_exit']==0
hashes=json.loads((base/'causal-repeat/source-hashes.json').read_text(encoding='utf-8-sig'))
assert all(hashlib.sha256((root/p).read_bytes()).hexdigest()==h for p,h in hashes.items())
with s.Session(root,'path-runtime-01a10f14-source-checkpoint') as budget:
 budget.track(base)
 facts=[]
 for path in paths:
  data=(root/path).read_bytes();facts.append(dict(path=path,bytes=len(data),lines=data.count(b'\n'),sha256=hashlib.sha256(data).hexdigest(),bom=data.startswith(b'\xef\xbb\xbf'),crlf=b'\r\n' in data))
 s.write(base/'checkpoint-source.json',json.dumps(facts,ensure_ascii=False,indent=2).encode())
 affected=json.loads((base/'causal-repeat/affected-final/result.json').read_text(encoding='utf-8-sig'))
 counts={outcome:sum(t['outcome']==outcome for t in affected['tests']) for outcome in ['Passed','Failed','NotExecuted']}
 failure=[t for t in affected['tests'] if t['outcome']=='Failed']
 assert len(failure)==1 and failure[0]['name'].endswith('.ProductionCtor_DoesNotEnableSuccessorPathGate')
 trees=[json.loads(p.read_text()) for p in (base/'causal-repeat').glob('*/*tree-terminal.json')]
 assert len(trees)==7 and all(t['active_processes']==0 for t in trees)
 evidence=dict(marker='PATH-RUNTIME-FINISH-20261006-FROM-01a10ed6',opening='2e6aa2b13f8f517e3c9bfd5ad6c405dd2cfed8b9',head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root).decode().strip(),candidate=True,source_hashes_match=True,affected=counts,known_failure=failure,causal=result,owned_build_test_trees=trees,independent_review=False,review_requests_sent=0,old_budget='2/2 consumed; unchanged',product_updated=False,ui_data_prepared=False,game_executed=False,goal_complete=False)
 s.write(base/'checkpoint-evidence.json',json.dumps(evidence,ensure_ascii=False,indent=2).encode())
 text='''# 节点时间、观察生命周期及正式界面候选检查点

采用 mistletoe-release-first-20261005-v2 与 mistletoe-storage-limits-20261005-v1。原报告/opening/原级open/预算保留，本组会诊请求0。完整产品Goal未完成。

本组候选：节点持久时刻进入等待来源核验与后继仲裁；首次跨午夜与恢复绑定；顺序型及有效固定/灵活窗口的合法重复不加一天一次限制；按时循环新轮次开始日落盘。日志观察器复用既有tail，叶子前arm，终态先冻结事实再撤订阅，再由判断节点消费独立载荷；Unknown不当未命中，等待不挂，停止强制收场。目录/检查器双模式、判断/结束拖入与添加引导、跨列相交集合、每条到达车道的线路、真实当前与下一候选标识、资源编辑/引用修订入口均已接。

证据：causal-repeat为当前字节；Rebuild0，指定6Passed→6Failed→6Passed；两个源文件逐字节恢复，所有自有Job树0。扩大回归286中283Passed/1Failed/2NotExecuted。失败为ProductionCtor_DoesNotEnableSuccessorPathGate：接班前源代码即启用该门，断言未改，不能报全绿。针对正式功能的148用例全部通过，WPF为真实Window/组件测试，尚非完整软件验收。

旧same-day-red确实在日期断言3/3红；其他早期r1/r2夹具schema/扁平参数/缺名失败原件保持，不改写。旧causal是修复重复日期前版本的PFP，不能代替当前causal-repeat。

唯一下一项：更新同一完整product的助手模块，完成实际启动、正式页面、未来等待停止、保存重启及数据保留，交付版本/步骤。runtime-refresh.py只替换五个模块并保全原模块，BGI/共享库1351输入同原产物来源，可复用；ui-data.py仅准备自有MSIX测试命名空间，旧116文件及NexusBGI-tested-01a10e1b/安装记录保留、身份不冒充真实旧根。两个脚本尚未运行；任何新操作先核真实进程、数据和完整安全终态。不可盲重跑已存在证据目录。

游戏/账户/兑换码/树脂/关机/队友动作未执行，待用户主动反馈；不启动第二套游戏任务。材料外csproj、MigrationReferenceActivation/MigrationSwitchTransaction、旧文档/账本与大量_workflow材料保护。仅明确18源码/测试及精简本批材料本地提交，禁止整库add/恢复/清理，不push/合并/部署。
'''
 s.write(base/'CHECKPOINT.md',text.encode());print('checkpoint',len(facts),'files; affected',counts,flush=True)
