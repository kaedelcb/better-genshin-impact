from pathlib import Path
import json,hashlib,subprocess,datetime
r=Path.cwd();d=Path(__file__).parent
owned=['MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/HistoricalExecutionObservationTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs']
before_files=['RunStoreTerminalRelease.cs.before','TaskCenterHost.cs.before','historical-tests.before','successor-tests.before']
origins=[]
for path,before in zip(owned,before_files):
 b=(d/before).read_bytes();head=subprocess.check_output(['git','show','HEAD:'+path]);origins.append(dict(path=path,before_sha256=hashlib.sha256(b).hexdigest(),before_matches_head=b==head,before_matches_head_with_git_line_endings=b.replace(b'\r\n',b'\n')==head.replace(b'\r\n',b'\n'),current_sha256=hashlib.sha256((r/path).read_bytes()).hexdigest()));assert b.replace(b'\r\n',b'\n')==head.replace(b'\r\n',b'\n'),path
final=json.loads((d/'final-r3/candidate-observation.json').read_text(encoding='utf-8'))
assert final['sources_unchanged'] and final['build_exit']==0 and final['target_exit']==0 and final['full_counts']['Failed']==6
for x in final['sources']:assert hashlib.sha256((r/x['path']).read_bytes()).hexdigest()==x['sha256'],x['path']
assert hashlib.sha256((r/owned[0]).read_bytes()).hexdigest()==json.loads((d/'post-mutation-byte-observation-m5.json').read_text())['sha256']
probe=r/'_workflow/local-wait-admission-gates-20261004/g10-completion';copies=d/'real-probe-evidence';copies.mkdir(exist_ok=False);probe_rows=[]
for p in list(probe.glob('fault-*.jsonl'))+list((probe/'controlled-writer-processes').rglob('*.jsonl')):
 b=p.read_bytes();dest=copies/p.name;assert not dest.exists();dest.write_bytes(b);probe_rows.append(dict(original=str(p.relative_to(r)),copy=str(dest.relative_to(r)),sha256=hashlib.sha256(b).hexdigest(),rows=[json.loads(x) for x in b.decode('utf-8-sig').splitlines() if x]))
(d/'real-probe-evidence-observation.json').write_text(json.dumps(probe_rows,ensure_ascii=False,indent=2),encoding='utf-8')
status=subprocess.check_output(['git','status','--porcelain']).decode('utf-8');(d/'git-status-before-commit.txt').write_text(status,encoding='utf-8');staged=subprocess.check_output(['git','diff','--cached','--name-only']).decode('utf-8');assert not set(staged.splitlines())&set(owned)
handoff=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md';b=handoff.read_bytes();(d/'current-handoff-before.json').write_text(json.dumps(dict(bytes=len(b),sha256=hashlib.sha256(b).hexdigest())),encoding='utf-8')
addition='''

## 2026-10-04 原历史结果唯一关联与直接封印候选（当前优先）

来源01a106ad-e250-7f51-87a4-bfed249bfaa7，仍原共享包。读auto-relay-history-outcome-20261004-from-01a10699/CANDIDATE.md及final-r3原TRX/log/product/source字节、per-execution-comparison、mutation-observations-r2（M1-M4）/mutation-observations-m5、恢复SHA和提交观察。原Host outcome重复/跨键身份/业务结果冲突红例、直接RunStore重复/冲突恢复关联红例已定向修复，原件不改；109定向通过，当前5项PFP恢复同SHA。29原Host场景含三历史前两真实Host完成封印、末条未知恢复与发布/结清中断/活跃原任务退出/归档；不外推三条全未知或全部旧格式矩阵。

final首轮19失败有原18+旧bad-nonce夹具共享写冲突；target通过不能覆盖该原失败。夹具已改同目录原子替换/有界争用重试且核心断言保持。final-r2全量2038/18/2、18身份同，重复结果无差异。再补真实所需ControlledWriterProbe（源码未改）Rebuild和源/产物哈希，final-r3全量2050/6/2=2058、精确合集2060/6/2=2068，对2046新增22/删0/12原Failed→Passed（此前缺探针），相邻回归无失败；24输入前后不变、声明面同SHA。11退出71及双写者窗口是真实临时根子进程证据，不是IPC游戏User验收。

六剩余R56失败详原TRX/比较，原级保留不豁免、材料外R56未改。下一共享责任是未证明history/outcome/旧缺许可缺身份/关联index-hash读回/结清封印组合及原全账、认证真实来源、全源域sol/high统一综合后审/成批闭环。原四失败/G10独立blocked/control两plan receipt实际已读，完整预算仍未核清、新请求0，不自称余额。audit2仍原9f85 receipt缺失，旧manifest/policy=false保持，不翻policy/倒签/伪receipt/扩工具。r61报告缺失未知。

原G2(e)/G4/G4a/G7/G8/G10/⑤⑥important implementation open，总Goal及全部约定功能新产物实际运行/停止/重启/数据保留/可运行版本仍未完成，生产门关闭。保护User/第三方JS/.kiro/材料外/暂存，明确范围本地候选提交不代交付。旧握手只读历史；必要新接班最新relay/HANDOFF才有效。
'''
handoff.write_bytes(b+addition.replace('\n','\r\n').encode('utf-8'))
paths=owned+[str(handoff.relative_to(r))]
# The original relay handoff files and predecessor-created handshake evidence are necessary inherited materials, not claimed as new product edits.
paths += [str(p.relative_to(r)).replace('\\','/') for p in sorted(d.rglob('*')) if p.is_file()]
paths=list(dict.fromkeys(x.replace('\\','/') for x in paths))
untracked=set(subprocess.check_output(['git','ls-files','--others','--exclude-standard','-z']).decode('utf-8').split('\0'));new=[x for x in paths if x in untracked]
if new:subprocess.run(['git','add','--',*new],check=True)
before_head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip();(d/'commit-preview-paths.json').write_text(json.dumps(paths,ensure_ascii=False,indent=2),encoding='utf-8')
# The preview file itself is generated here and included explicitly.
paths.append(str((d/'commit-preview-paths.json').relative_to(r)).replace('\\','/'));subprocess.run(['git','add','--',paths[-1]],check=True)
result=subprocess.run(['git','commit','--only','-m','fix: reject ambiguous historical outcomes and recovery seals','--',*paths],capture_output=True,text=True,encoding='utf-8');(d/'commit-command.log').write_text(result.stdout+result.stderr,encoding='utf-8');assert result.returncode==0,result.stderr
head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip();actual=subprocess.check_output(['git','diff-tree','--no-commit-id','--name-only','-r',head]).decode('utf-8').splitlines();assert set(owned).issubset(actual);assert set(actual).issubset(paths)
after_status=subprocess.check_output(['git','status','--porcelain']).decode('utf-8');(d/'git-status-after-commit.txt').write_text(after_status,encoding='utf-8')
observation=dict(kind='local candidate checkpoint; not independent pass/certification/product acceptance',before_head=before_head,commit=head,origins=origins,expected_paths=paths,actual_paths=actual,actual_changed_files=len(actual),full_counts=final['full_counts'],production_gate_open=False,goal_complete=False,staged_before=staged,staged_after=subprocess.check_output(['git','diff','--cached','--name-only']).decode('utf-8'),probe_evidence_files=len(probe_rows))
(d/'candidate-commit-observation.json').write_text(json.dumps(observation,ensure_ascii=False,indent=2),encoding='utf-8');print('candidate commit',head,'changed',len(actual),'probe evidence',len(probe_rows),flush=True)
