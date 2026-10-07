from pathlib import Path
import hashlib,json,os,sys
ROOT=Path(__file__).resolve().parents[2]
BASE=ROOT/'_workflow/final-ui-01a11220/own-runtime/startup-center-01a11808'
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
DATA=ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
sha=lambda b:hashlib.sha256(b).hexdigest()
load=lambda p:json.loads(p.read_bytes())
def tool_texts(path):
    rows=[]
    for line in path.read_bytes().splitlines():
        row=json.loads(line);payload=row.get('payload',{})
        if payload.get('type')!='custom_tool_call_output':continue
        output=payload.get('output',[])
        if not isinstance(output,list):continue
        text='\n'.join(item['text'] for item in output if item.get('type')=='input_text')
        rows.append(dict(timestamp=row.get('timestamp'),call_id=payload.get('call_id'),text=text,raw_row_sha256=sha(line)))
    return rows
with s.Session(ROOT,'startup-center-finite-actual-source-closeout-01a11808') as budget:
    old=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(old));assert set(old)==set(budget.old_roots)
    budget.track(BASE);budget.track(PRODUCT/'VERSION.json')
    guide=ROOT/'Docs/technical/mistletoe-startup-migration-recovery.md';budget.track(guide)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
    result=load(BASE/'result.json');assert result['normal_app_exits']==[0,0] and result['startup_config_cold_preserved'] and result['original_startup_config_restored']
    assert not result['user_changed'] and not result['source_drift'] and result['taskcenter_run_files_unchanged']
    first=load(BASE/'first-tree-run-cancel-save/startup-after.json');second=load(BASE/'cold-reload-tree/startup-after.json');assert first==second
    assert (DATA/'startup-flow.json').read_bytes()==(BASE/'private/startup-original.json').read_bytes()
    texts=tool_texts(BASE/'first-tree-run-cancel-save/private/native-ui-source.jsonl')
    cold=tool_texts(BASE/'cold-reload-tree/private/native-ui-source.jsonl')
    def witness(rows,label,required):
        matches=[row for row in rows if all(token in row['text'] for token in required)]
        assert matches,(label,required)
        row=matches[0]
        return dict(label=label,timestamp=row['timestamp'],call_id=row['call_id'],raw_row_sha256=row['raw_row_sha256'],required_actual_text=required)
    witnesses=[
        witness(texts,'manual complete',['上次执行完成（06:01:13，手动触发）']),
        witness(texts,'real cancellation',['82 文本 已取消']),
        witness(texts,'diagram actual node rendering',['0 窗口 槲寄生 · 启动流程图','等待 60 秒','等待 10 秒','结束流程']),
        witness(texts,'diagram return to unselected parameters',['565 文本 等待秒数','566 编辑 (settable, string) Value: 10']),
        witness(cold,'cold UI tree readback',['尚未执行过','等待 60 秒','等待 10 秒']),
    ]
    for phase in ['first-tree-run-cancel-save','cold-reload-tree']:
        assert load(BASE/phase/'app-tree-terminal.json')['active_processes']==0
        assert load(BASE/phase/'result.json')['raw_ui_sha256']==sha((BASE/phase/'private/native-ui-source.jsonl').read_bytes())
    config=first['steps'][0]
    observation=dict(kind='limited native Startup Center acceptance; not game/resource or whole-product acceptance',
        condition_id=config['id'],true_wait_id=config['trueSteps'][0]['id'],false_wait_id=config['falseSteps'][0]['id'],end_id=first['steps'][1]['id'],
        manual_start='2026-10-08 06:01:03 +08:00',manual_completion='2026-10-08 06:01:13 +08:00',selected_wait_seconds=10,
        selected_true_branch_completed=True,unselected_false_branch_dimmed=True,main_end_completed=True,
        cancellation_start='2026-10-08 06:04:10 +08:00',cancellation_observed='2026-10-08 06:04:32 +08:00',configured_cancellation_wait_seconds=60,
        global_cancelled=True,cancel_button_disabled_afterwards=True,second_run_end_not_reached=True,diagram_rendered=True,diagram_fit_window=True,diagram_return_to_parameters=True,
        cold_wait_parameters=[60,10],cold_tree_ids_preserved=True,automatic_startup_enabled=False,automatic_execution_not_observed=True,
        ordinary_display_issue=dict(id='STARTUP-CANCELLED-PATH-DISPLAY-1',condition='cancel native wait after path visualization is active',observed='global state Cancelled while old wait card/diagram retains Executing badge',
            impact='stale runtime path display; manual cancellation returned, downstream end not reached',deferred_after_delivery=True,not_silently_closed=True),
        source_modified=False,new_reviews=0,review_remaining=0,not_taskcenter_resource_skip=True,game_executed=False,product_complete=False,tool_output_witnesses=witnesses)
    write(BASE/'observations.json',observation)
    actual='''# 启动中心本机条件树实际补验

完整Goal接续，原生active读回、实际model/effort与settings仍gpt-6.1-sol/ultra；main-OldTeaBag-B168，开工HEAD86e8f588a。采用完整交付/交付优先/存储限制；现有矩阵仅声明启动中心已有UI，定向材料未定位此入口实际来源，所以补总计划§2.5的有限本机样例。源码313输入及当前b56d5bb4助手模块未变，不重编译或追加审查，原grant2/2余额0和待批的1次请求不变。

正式UI目录添加时间条件00:00–23:59，是/否分别原生等待10秒，主链末尾结束流程；自动启动和开机启动保持关闭。06:01:03手动运行，条件为是，只执行左侧10秒等待，06:01:13到达主链结束，顶部“上次执行完成”，未选右侧节点灰显。各稳定ID见observations.json，由实际UI保存生成，未手写运行/准入/封印记录。

通过真实等待秒数控件把左侧改为60、右侧仍10，自动保存已落盘。06:04:10再次手动运行，06:04:32用正式取消按钮终止，顶部“已取消”、取消按钮置灰，第二次未到主链结束动作；实际取消不到60秒。原生Source中的Await Task.Delay(ct)与VM的取消异常返回可追查，但本结论直接绑定真实UI和日志来源。

真实打开“启动流程图”，适应窗口后显示开始、条件、是/否两等待和结束；点击左右图节点分别跳回对应60/10秒参数编辑器。随后37892正常退出0，冷启27160（Session1）；正式页面为“尚未执行过”、自动启动关闭，展开后同一树/ID及60/10参数保持。两次冷读回配置对象完全相同；第二助手正常退出0、两Job树0后，原自有startup-flow.json28字节已精确恢复。

无BGI或游戏启动，无CMD/第三方进程动作，无脚本/宏伪等待，无压力循环。真实User9522、产物User9501、任务中心runs全文件哈希和313源码输入都不变。这证明启动中心该正常条件树、取消、参数保存冷加载和图导航入口可用，不代替TaskCenter资源Skip、定时/电子狗/日志触发的全部环境、游戏/账号/队友/关机或整版验收。

普通后修STARTUP-CANCELLED-PATH-DISPLAY-1：全局取消已经返回并准确显示“已取消”，等待小卡片/图仍保留旧“执行中”路径标签；不影响本次已观测取消/退出，保留现象和条件，不伪闭合、不因它改源码或消耗复核额度。

原始两段UI-source、参数前后、argv/进程/Job与数据hash完整保存在本目录，observations.json逐项绑定tool-output行哈希；不以助手自己的进度文字替代来源。产品Goal仍未完成，新增到达边界源码复核授权仍待用户，游戏效果只等主动反馈。继续当前聊天的理由：本次是已确认缺来源的有限实际入口，可安全一次核到正常终态，没有新源码或跨文件一致性施工，不按累计时间强制开接班。
'''
    s.write(BASE/'ACTUAL.md',actual.encode('utf-8'))
    version=load(PRODUCT/'VERSION.json');assert all(sha((PRODUCT/m['path']).read_bytes())==m['sha256'] for m in version['assistant_modules'])
    s.write(BASE/'private/VERSION-before.json',(PRODUCT/'VERSION.json').read_bytes())
    version.setdefault('additional_non_game_actual',[]).append(dict(kind='Startup Center finite tree/cancel/save/cold reload/diagram',evidence=str(BASE/'observations.json'),sha256=sha((BASE/'observations.json').read_bytes()),source_modified=False))
    s.write(PRODUCT/'VERSION.json',json.dumps(version,ensure_ascii=False,indent=2).encode('utf-8'),mode='wb');write(BASE/'VERSION.json',version)
    before=guide.read_bytes();s.write(BASE/'private/guide-before.md',before)
    text=before.decode('utf-8');anchor='## 当前交付边界';assert text.count(anchor)==1
    addition='''## 启动中心本机使用

在“槲寄生 → 启动中心”从“＋”添加时间条件、原生等待和主链结束；条件的是/否子链分别添加节点。参数自动保存，手动“立即执行”显示实际分支与完成状态，“取消”可终止等待。“流程图”支持适应窗口和点击节点跳回参数编辑器。

当前同一产物已用自有数据实际核到：条件→选中10秒等待→主链结束；60秒等待在约22秒取消；冷重启保持同一节点ID/树及60/10参数，自动启动关闭且未补跑。样例配置已恢复原件。取消后小卡片/图可能保留旧“执行中”路径标签，顶部“已取消”和取消按钮状态已实际核对；该显示问题保留后修。本证据不覆盖游戏动作或TaskCenter资源执行中的Skip，也不外推全部定时/电子狗/日志环境。

'''
    s.write(guide,text.replace(anchor,addition+anchor).encode('utf-8'),mode='wb')
    refs=[BASE/'ACTUAL.md',BASE/'observations.json',BASE/'result.json']
    for phase in ['first-tree-run-cancel-save','cold-reload-tree']:
        refs.extend(BASE/phase/name for name in ['startup-before.json','startup-after.json','result.json','app-process-request.json','app-process-identity.json','app-tree-terminal.json','private/native-ui-source.jsonl'])
    write(BASE/'evidence-index.json',[dict(path=str(p),sha256=sha(p.read_bytes()),bytes=p.stat().st_size) for p in refs])
    print('finite Startup Center actual UI witnesses bound; no source/module/review change; whole-product pending',flush=True)
