from pathlib import Path
import json,sys,subprocess,hashlib
ROOT=Path(__file__).resolve().parents[2];OUT=Path(__file__).resolve().parent/'own-migration-viewport-checkpoint'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
rollout=Path('E:/CodexData/home/sessions/2026/10/07/rollout-2026-10-07T12-33-40-01a114a3-7bb3-7391-9e23-f3124294a101.jsonl')
rows=[json.loads(line) for line in rollout.read_bytes().splitlines()]
latest=[row for row in rows if row.get('type')=='turn_context'][-1];ctx=latest['payload'];settings=ctx['collaboration_mode']['settings']
assert ctx['model']==settings['model']=='gpt-6.1-sol' and ctx['effort']==settings['reasoning_effort']=='ultra'
with s.Session(ROOT,'own-root-01a114a3-handoff-safe-boundary') as budget:
    previous=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(previous));assert set(previous)==set(budget.old_roots)
    budget.track(OUT)
    model=dict(source_thread='01a114a3-7bb3-7391-9e23-f3124294a101',rollout=str(rollout),timestamp=latest['timestamp'],cwd=ctx['cwd'],model=ctx['model'],effort=ctx['effort'],settings_model=settings['model'],settings_effort=settings['reasoning_effort'])
    s.write(OUT/'handoff-model.json',json.dumps(model,ensure_ascii=False,indent=2).encode())
    s.write(OUT/'handoff-git-status.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT))
    s.write(OUT/'HANDOFF.md',('''# 同产物完整策略表单与剩余入口验收接续

标记OWN-ROOT-MIGRATION-VIEWPORT-20261007-FROM-01a114a3，源01a114a3-7bb3-7391-9e23-f3124294a101。完整产品未完成；源码/因果及迁移检查点fd93ae102，完整事实见同目录CHECKPOINT.md/checkpoint.json，源模型证据handoff-model.json。source/typed-tests八Job和三实际运行Job0，自有进程/突变/构建全终态。父停写后按原生暂停读回及唯一同项目local接班继承实际gpt-6.1-sol/ultra，不是时间/工具数强制交接。

唯一下一项：同product助手b2a51993新模块实机复验完整策略表单可滚动可保存，补纯本机重载/跳过/停止/重启，UI取消自有wf-own-control-01a112a0的22:00验证定时再保存，原44ac05e9仅作证据不覆盖恢复；随后剩余开发侧矩阵、必要综合复核与完整版本/启动恢复步骤。runtime_migration.py旧阶段/rollout/refresh-popout不可盲跑，要准备自身新阶段/新rollout/refresh-viewport身份。全部约束及完整目标在NEXT-PROMPT.txt同一初始消息。

原9500User文件/10run及7正常样本输入同，当前User9501仅本次新安装账，4迁移候选/12flow/11run保留。真实D:Session2不操作不杀；所有助手显式own-runtime/assistant-data；无游戏/账号/关机等代测，第三方JS/历史/旧失败/材料外csproj与两Migration源码保护。会诊98+extra2/2余额0、原重要2及bundle/r61旧问题保持，本轮无独立综合pass，不伪收口。新owner规则登记560853139是材料外变化，撤销A/B协调不得复活。

另保留检查点脚本UTF8读取漏项导致的无模块更换失败及首次活跃共享锁拦截；先核输出空和锁真实终态后才重试，未抢锁。源/测试字节已恢复，当前新DLL尚无实际UI复验。最新HEAD/工作区在暂停前和创建前读回，交接材料不重绑原opening/旧历史。
''').encode())
