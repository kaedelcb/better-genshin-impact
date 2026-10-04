from pathlib import Path
import json,subprocess,hashlib,datetime,os
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004';relay=base/'auto-relay-typed-parent-20261004-from-01a105cd';out=base/'parent-capacity'
def git(*args):return subprocess.check_output(['git',*args],text=True,encoding='utf-8').strip()
candidate=json.loads((relay/'candidate-commit-observation.json').read_text(encoding='utf-8'))['commit']
rollouts=list(Path('C:/Users/Administrator/.codex/sessions/2026/10/04').glob('*01a105cd-ffd8-73e2-8d7f-cfb7b9dbd9e7.jsonl')); assert len(rollouts)==1
contexts=[r for line in rollouts[0].read_text(encoding='utf-8').splitlines() if (r:=json.loads(line)).get('type')=='turn_context'];ctx=contexts[-1];p=ctx['payload'];settings=p['collaboration_mode']['settings']
assert p['model']==settings['model']=='gpt-6.1-sol' and p['effort']==settings['reasoning_effort']=='medium'
(relay/'source-model-observation.json').write_text(json.dumps({'thread_id':'01a105cd-ffd8-73e2-8d7f-cfb7b9dbd9e7','rollout':str(rollouts[0]),'ordinal':ctx['ordinal'],'model':p['model'],'effort':p['effort'],'settings':settings,'turn_context':ctx,'evidence_level':'actual local rollout configuration; not inference about service-side model'},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
prompt=(relay/'relay-prompt.txt').read_text(encoding='utf-8');assert prompt.count('@CANDIDATE_COMMIT@')==1 and prompt.count('@FINAL_SUMMARY@')==1
prompt=prompt.replace('@CANDIDATE_COMMIT@',candidate).replace('@FINAL_SUMMARY@','Rebuild exit0；TaskCenter1939 passed/18 failed/2 skip=1959、exit1；同DLL定向/声明面/互导16/16、exit0；精确合集1949/18/2=1969，对前序1966新增3、删除0、共有变化0，18失败ID集合一致；旧失败不豁免')
prompt=prompt.replace('manifest evidence audit缺列表仍blocked；','manifest旧缺列表阻断保留；本轮实际audit exit2为原native request缺receipt.json，原件见parent-capacity/mechanical-review.log；')
(relay/'relay-prompt.txt').write_text(prompt,encoding='utf-8')
handoff=f'''# 异步停止候选后，类型化唯一父来源

来源01a105cd-ffd8-73e2-8d7f-cfb7b9dbd9e7，标记AUTO-TYPED-PARENT-RELAY-20261004-FROM-01a105cd。同原local-wait-admission-gates共享包，原opening/历史/请求/累计预算/important implementation open保持，总交付未完成，生产门关闭。

本地产品候选{candidate}，六个源码/测试文件及过程证据明确范围提交；实际文件集/source SHA核验读candidate-commit-observation.json。文档HEAD/暂存/工作区/进程读opening-observation.json。未push/发布/部署。

Host扫描与终局回写WaitAsync后共用原封印/迁区核心；面板await异步RequestRunAction，Unknown与停驻停止await原对账链。同步API仅兼容，生产面板无同步调用；磁盘工作并非全部异步。对账Task.Run独立调度必须保留，移除曾使旧Stop超时并发重试退化；恢复调度后原断言与全量通过该项。初次1938/19/2原TRX保留。

最终final-r2 Rebuild0，TaskCenter1939/18/2=1959 exit1；定向/声明面/互导16/16 exit0。精确合集1949/18/2=1969，新增3、删除0、共有变化0，18旧失败身份相同，不永久豁免。两项lock-mutation-r4/panel-mutation-r2指定阻塞断言P/F/P且SHA恢复；六输入前后字节一致，声明面再生成前后同SHA且无环境再跑通过。真正Host/Runner/RunStore/LeaseStore/Panel命令配受控端口和受控本地Accepted父责任，不是真IPC/游戏/User验收。全部普通进程/TRX/byte观察，非认证receipt/独立pass/产品交付。

唯一下一依赖：类型化原父来源跨存储绑定→33节点逐次原因码/原身份墓碑迁区归档重开→四真实入口及原G4/G7恢复停止全影响。parent-capacity/PARENT-SOURCE-DESIGN.md为待实施设计，TypedAdmissionParentTests.pending未入测试项目、未执行；先执行红例，不当已实现/已跑。保留受理时精确原HandoffIdentity与scope、禁止普通/合并写者补造/改删、同Lease事务绑定节点原父并于占位重试核；旧缺锚/删除/歧义/不一致/错run/workflow拒绝，不用run-source/Any补造。移交不另造租约父，RunStore-only hook不在Lease mutation内回读Lease/await端口。旧scope字段改动和原绑定删改尚无守卫。共享调用方全查，现33普通Fact保留并增强真实逐次原因/归档/锁因果。

当前机械audit exit2原native请求9f85a4b85f46400dbff20b97ebec4fda缺receipt.json；旧manifest缺列表及policy=false/native prepare别Goal授权/implgate planpass阻断保持。审计输出在候选提交时尚未终态，源码/测试已终态且字节核验；审计最终日志/退出另在本次元数据提交补存，原候选的中间过程不重写。没有伪receipt/翻授权/倒签/扩工具。正式认证/综合独立实现后审仍欠；原可定位5次不是全历史已核清，本来源新增请求0，不自称余额。

保护User/JS/.kiro/旧D盘及材料外R56/csproj/工具/文档/暂存；仅编译服务器驻留，无本批测试/突变在途，不杀它。并行r61报告缺失未知，未当已集成。全功能范围仍为本relay-prompt全文及原总计划/DELIVERY-COVERAGE，不能缩为异步候选。

交接理由：真实节点/面板异步停止转换达到已验证边界，下一来源权威与跨存储模型适合独立上下文集中枚举；不按时间/数量强制交接。旧Goal暂停与新完整activeGoal/实际模型都需原生/rollout核，不凭本文自述。源模型实际sol/medium证据已保存，复杂审查sol/high保持。旧执行者在暂停确认后停源码写入，新执行者握手后自主续办。
'''
(relay/'HANDOFF.md').write_text(handoff,encoding='utf-8')
current=base/'CURRENT-HANDOFF.md';before=current.read_bytes();nl='\r\n' if before.count(b'\r\n') else '\n';bom=b'\xef\xbb\xbf' if before.startswith(b'\xef\xbb\xbf') else b''
(relay/'current-handoff-before.json').write_text(json.dumps({'path':current.relative_to(root).as_posix(),'bytes':len(before),'lines':len(before.splitlines()),'sha256':hashlib.sha256(before).hexdigest()},indent=2)+'\n')
prefix=f'''## 2026-10-04 异步节点终局与面板停止候选（当前优先）

产品候选{candidate}，本来源01a105cd-ffd8-73e2-8d7f-cfb7b9dbd9e7。六个源码/测试文件明确范围提交，源码字节与final-r2一致。Host扫描/统一终局回写WaitAsync共用原锁内封印核心；面板await异步宿主动作用于Unknown/停驻停止。同步兼容API保留。对账Task.Run独立调度保留，使Stop超时包住同步前置工作；移除调度造成一次原Stop并发重试退化，恢复后原断言通过，首轮19失败原件保留。

最终Rebuild0；TaskCenter1939 Passed/18 Failed/2 skip=1959、exit1；同DLL定向/声明面/互导16/16。精确合集1949/18/2=1969，对旧1966新增3、删除0、共有变化0，18失败ID相同；旧失败不豁免。两项关键PFP指定阻塞断言红、源码SHA恢复；声明面前后相同SHA。证据parent-capacity/final-r2、lock-mutation-r4、panel-mutation-r2及LOCK-CANDIDATE.md，都是普通进程/TRX/byte观察，非认证receipt/独立pass/产品验收。实际Host/Runner/RunStore/LeaseStore/Panel命令配受控端口与受控本地Accepted父责任，不是真IPC/游戏/User验收。

当前audit exit2引用原native请求缺receipt；旧缺列表和policy=false/原planpass机械约束仍保留，没有翻授权/倒签/伪收据/扩工具。新增独立请求0，原可定位5次不是全历史核清，不自称余额。总交付、G2(e)/G4/G4a/G7/G8/G10/⑤⑥ important implementation open与生产门保持。

唯一下一共享依赖：类型化唯一受理父来源→33逐次容量原因/原身份Tombstone/TerminalPendingTransfer/ArchivedOperations/重启恢复→四真实入口因果矩阵及原G4/G7链。PARENT-SOURCE-DESIGN.md仅设计，TypedAdmissionParentTests.pending未入项目/未执行；普通source Scope/原Handoff绑定删改尚无守卫，不能凭Scope+Any/合成run-source补造授权。全原目标与保护范围读本次auto-relay-typed-parent-20261004-from-01a105cd/relay-prompt.txt，旧握手/下一项由本段覆盖。自然交接按原生paused/active及实际模型继承核验，未取得证据不自述成功；User/JS/.kiro/材料外改动保护，未push/部署/发布。

'''
temp=current.with_name(current.name+'.typed-parent.tmp');temp.write_bytes(bom+prefix.replace('\n',nl).encode()+before[len(bom):]);os.replace(temp,current)
assert current.read_bytes().endswith(before[len(bom):])
candidate_doc=out/'LOCK-CANDIDATE.md';text=candidate_doc.read_text(encoding='utf-8');text+='\n本轮实际机械审计终态：exit2，原native request缺receipt.json（mechanical-review.log）。候选提交时该审计输出尚未终态，源码/测试已终态；元数据提交保存最终日志/退出，不改原中间记录、不作为pass。\n';candidate_doc.write_text(text,encoding='utf-8')
metadata=[current.relative_to(root).as_posix(),candidate_doc.relative_to(root).as_posix(),(out/'mechanical-review.log').relative_to(root).as_posix(),(out/'mechanical-review-exit.txt').relative_to(root).as_posix()]+[p.relative_to(root).as_posix() for p in sorted(relay.rglob('*')) if p.is_file()]
assert not set(git('diff','--cached','--name-only').splitlines())&set(metadata)
subprocess.run(['git','add','--',*metadata],check=True)
with (relay/'metadata-commit.log').open('wb') as log:subprocess.run(['git','commit','--only','-m','docs(task-center): retain terminal audit and typed-parent relay','--',*metadata],stdout=log,stderr=subprocess.STDOUT,check=True)
head=git('rev-parse','HEAD');actual=git('diff-tree','--no-commit-id','--name-only','-r',head).splitlines();assert set(actual)==set(metadata)
(relay/'metadata-commit-observation.json').write_text(json.dumps({'commit':head,'actual_paths':actual,'expected_paths_match':True,'candidate':candidate,'remaining':'original complete delivery goal and original important implementation open retained'},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('metadata',head,'candidate',candidate,'source actual sol/medium observed',flush=True)
