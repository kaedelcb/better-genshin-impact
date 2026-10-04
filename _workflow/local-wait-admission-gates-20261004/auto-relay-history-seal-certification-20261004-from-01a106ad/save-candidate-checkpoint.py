from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd(); d=Path(__file__).parent; auth=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/certified-executions/55fcd1ef82ae4a3f97e4d54fb1ce5033'
owned=['MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LegacyHistoricalSealIntegrityTests.cs']
assert subprocess.check_output(['git','branch','--show-current']).decode().strip()=='main-OldTeaBag-B168'
assert hashlib.sha256((r/owned[0]).read_bytes()).hexdigest()=='1bd71a8ef2902425be038fe630030709b27e989801dbc346d9c3b6a14576e084'
assert json.loads((auth/'run-tree-terminal.json').read_text())['active_processes']==0
staged=subprocess.check_output(['git','diff','--cached','--name-only']).decode('utf-8'); assert not staged.strip(), 'unexpected material-out staged writer; preserve and inspect'
head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(); assert head=='eb50b8716594258d4bc66ed19fd789737db7838f',head
h=r/'_workflow/local-wait-admission-gates-20261004/CURRENT-HANDOFF.md'; before=h.read_bytes(); h.write_bytes(before+('''

## 2026-10-04 旧历史封印完整性与当前真实来源（当前优先）

来源01a106d9-5916-7bb0-8fa7-475405d0ee94，仍原共享包；完整状态见auto-relay-history-seal-certification-20261004-from-01a106ad/CANDIDATE.md及legacy-final、legacy-mutation-observations、authenticated-source-observation、current-evidence-readback。Accepted/Permit均缺的旧历史封印红例12失败/4合法通过；已集中补全部关联与旧outcome集合/原terminal对应，原件不改、不补造身份。96定向/125含原Host矩阵通过；三PFP恢复SHA1bd71a8e...。全TaskCenter2066/6/2、相邻602/0/2、精确合集2076/6/2=2084；对2068新增16/删0/共有结果变化0，六失败身份同。声明面同SHA。

首次本候选完整进程/输入/产物来源为g10-completion/certified-executions/55fcd1ef82ae4a3f97e4d54fb1ce5033/receipt.json，generic provenance验证通过、绿色current_regression验证因exit1拒绝；TRX身份/结果与普通执行同。271源码输入/125产物/Job终态保存，不当绿认证、独立pass或IPC游戏User验收；package锁及SDK导入未单独纳入认证输入，不能称依赖闭包已穷尽。原G10保存正文与原final一致、事件在rollout唯一命中，本轮证据纠正旧派生false而不改旧文件；blocked义务仍open。

audit2仍原9f85缺receipt，verify无report拒绝，不翻policy/倒签/扩工具。新增独立请求0、原全预算尚未核清；inventory目录计数不是全计数（CLI用intent等文件），不据0认无请求。下一共享责任为剩余history/严格结清/封印声明完整性、六R56实际责任/安全修复许可、原账/完整依赖认证与全源域sol-high统一综合后审/原级成批闭环及全部功能实际交付。所有G与迁移重要implementation义务open，生产门关闭、总Goal未完成。保护User/JS/.kiro/材料外/暂存，无push/部署；提交仅候选。最新接班握手只依后续relay，不执行旧握手。
''').encode('utf-8'))
(d/'handoff-append-integrity.json').write_text(json.dumps(dict(before_bytes=len(before),before_sha256=hashlib.sha256(before).hexdigest(),after_bytes=h.stat().st_size,original_prefix_preserved=h.read_bytes().startswith(before)),indent=2),encoding='utf-8')
status=subprocess.run(['git','status','--porcelain'],capture_output=True,check=True);(d/'git-status-before-checkpoint.txt').write_bytes(status.stdout)
paths=owned+[str(h.relative_to(r)).replace('\\','/')]+[str(p.relative_to(r)).replace('\\','/') for p in d.rglob('*') if p.is_file() and p.name!='checkpoint-paths.nul']
paths += [str(p.relative_to(r)).replace('\\','/') for p in auth.rglob('*') if p.is_file() and 'products' not in p.relative_to(auth).parts]
paths=sorted(set(paths)); (d/'checkpoint-preview.json').write_text(json.dumps(dict(before_head=head,paths=paths,source_sha256='1bd71a8ef2902425be038fe630030709b27e989801dbc346d9c3b6a14576e084',auth_receipt=str((auth/'receipt.json').relative_to(r)),status='candidate only; full regression failed six; no independent pass or product acceptance'),ensure_ascii=False,indent=2),encoding='utf-8')
paths.append(str((d/'checkpoint-preview.json').relative_to(r)).replace('\\','/')); paths=sorted(set(paths)); pf=d/'checkpoint-paths.nul'; pf.write_bytes(b'\0'.join(x.encode('utf-8') for x in paths)+b'\0')
subprocess.run(['git','add','-f','--pathspec-from-file='+str(pf),'--pathspec-file-nul'],check=True,capture_output=True)
with (d/'checkpoint-commit.log').open('wb') as f: result=subprocess.run(['git','commit','--only','-m','fix: validate legacy historical run seals and capture execution provenance','--pathspec-from-file='+str(pf),'--pathspec-file-nul'],stdout=f,stderr=subprocess.STDOUT)
assert result.returncode==0
commit=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(); actual=subprocess.check_output(['git','diff-tree','--no-commit-id','--name-only','-r',commit]).decode('utf-8').splitlines(); assert set(owned)<=set(actual)<=set(paths)
assert not subprocess.check_output(['git','diff','--cached','--name-only']).strip()
(d/'candidate-commit-observation.json').write_text(json.dumps(dict(before_head=head,commit=commit,actual_paths=actual,actual_changed_files=len(actual),product_source_files=1,new_test_source_files=1,source_sha256=hashlib.sha256((r/owned[0]).read_bytes()).hexdigest(),staged_after='',goal_complete=False,production_gate_open=False),ensure_ascii=False,indent=2),encoding='utf-8')
(d/'git-status-after-checkpoint.txt').write_bytes(subprocess.check_output(['git','status','--porcelain']))
print(commit,len(actual),'explicit paths, candidate only')
