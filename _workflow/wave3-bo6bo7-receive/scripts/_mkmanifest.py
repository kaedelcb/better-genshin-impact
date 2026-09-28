import io, json, hashlib, os, re
d='_workflow/wave3-bo6bo7-receive'
SRC=['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt']
def sha(p): return hashlib.sha256(open(p,'rb').read()).hexdigest()
CUR={s:sha(s) for s in SRC}
print('current source hashes:'); [print('  ',k.split('/')[-1],v[:16]) for k,v in CUR.items()]

# --- locate the 3 added claim-surface lines for excerpt ranges
manifest='Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt'
lines=open(manifest,encoding='utf-8').read().split('\n')
targets=['3D08ED876CA155691F693248D0B93254D9E4549AA77AAA3A6899C0306F114608',
         '8BB3C28725E15D48B2F7205F587007050DE5591AF604693BA44A00E96F15AC77',
         'FC153C1C4E1F5B6CFFEBBC5CCB331E2304C86AC1D4838E206C499EA4E2163EA9']
ranges=[]
for t in targets:
    idx=[i+1 for i,L in enumerate(lines) if t in L]
    print(t[:12],'-> manifest line',idx)
    ranges.append(idx[0])
ranges.sort()

ev=[]
def add(**kw): ev.append(kw)
def cond(txt): return txt
cur_bind=lambda: dict(CUR)
open_sh=json.load(io.open(d+'/opening.json',encoding='utf-8'))['source_hashes']

add(id='receive-assistant-project-rebuild', path=d+'/regression/final/assistant-project-rebuild.log', level='build', binding='current',
    purpose='Integrated mainline rebuild of the assistant product project with deployment disabled.', source_sha256=cur_bind(),
    conditions='dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0; deployment target not written (see receive-deploy-target-before).')
add(id='receive-testproject-rebuild', path=d+'/regression/final/testproject-rebuild.log', level='build', binding='current',
    purpose='Integrated mainline rebuild of the assistant unit-test project.', source_sha256=cur_bind(),
    conditions='dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0.')
add(id='receive-targeted-93', path=d+'/regression/final/targeted-93.trx', level='test', binding='current',
    purpose='Integrated-version targeted runner/local-wait suite (3 classes); the counterexample evidence for the behaviour risk rows.', source_sha256=cur_bind(),
    conditions='dotnet test --no-build --filter <LocalWaitIdentityTranslationTests|LocalWaitParkingStateContractTests|WorkflowRunnerTests>; 93/93 passed; executed after the final claim-surface regeneration so the binding is exact.')
add(id='receive-full-1564', path=d+'/regression/final/assistant-full-1564.trx', level='test', binding='current',
    purpose='Integrated-version full assistant regression.', source_sha256=cur_bind(),
    conditions='dotnet test --no-build (no filter); 1562 passed / 2 skipped / 0 failed / 1564; executed after the final claim-surface regeneration.')
add(id='receive-claim-surface-regen', path=d+'/regression/claim-surface/regen/claim-surface-regen.trx', level='test', binding='current',
    purpose='Declaration-surface regeneration after the R5.3 24.126 registration was added.', source_sha256=cur_bind(),
    conditions='CLAIM_SURFACE_REGENERATE=1; ClaimSurfaceGuardTests 1/1 passed; manifest 599 -> 602 lines (+3/-0).')
add(id='receive-claim-surface-no-env', path=d+'/regression/claim-surface/no-env/claim-surface-no-env.trx', level='test', binding='current',
    purpose='Declaration-surface guard re-run with the regeneration variable cleared.', source_sha256=cur_bind(),
    conditions='CLAIM_SURFACE_REGENERATE unset; ClaimSurfaceGuardTests 1/1 passed; manifest SHA stable at 5D914611... across regen and no-env.')
add(id='receive-fact-testids', path=d+'/regression/final/targeted-fact-testids.json', level='document', binding='current',
    purpose='testIds of the four new real-Runner facts and the six relocated/changed helper fixtures.', source_sha256=cur_bind(),
    conditions='Extracted from receive-targeted-93.trx by testMethod name.')
add(id='receive-testid-comparison', path=d+'/regression/testid-comparison-receive.json', level='document', binding='current',
    purpose='testId-level structural comparison of the opening baseline full TRX against the integrated full TRX.', source_sha256=cur_bind(),
    conditions='tools/mistletoe/workflow.py trx --baseline <opening full> --final <integrated full>; 1556 unchanged / 8 added / 4 removed / 0 changed; added and removed id sets identical to the source delivery.')
add(id='receive-targeted-identity', path=d+'/regression/targeted-testid-identity.json', level='document', binding='current',
    purpose='Proof that the integrated targeted suite has exactly the source delivery final targeted testId set.', source_sha256=cur_bind(),
    conditions='93 testIds, names and outcomes identical to the source worktree final targeted TRX.')
add(id='receive-mutation-records', path=d+'/mutation-records.json', level='document', binding='current',
    purpose='Machine-readable records of the eight reverse mutations re-executed on the integrated bytes.', source_sha256=cur_bind(),
    conditions='Produced by _workflow/wave3-bo6bo7-receive/run-receive-mutants.ps1 under pwsh 7; per-mutation build/test logs and TRX under _workflow/wave3-bo6bo7-receive/mutations/<id>/.')
add(id='receive-manifest-diff', path=d+'/regression/claim-surface/manifest-diff-summary.json', level='document', binding='current',
    purpose='Reviewed diff of the declaration-surface regeneration.', source_sha256=cur_bind(),
    conditions='Exactly 3 added lines (all from the new R5.3 24.126 text), 0 removed, no pre-existing claim line altered.')
add(id='receive-deploy-target-before', path=d+'/deploy-target-before.txt', level='document', binding='current',
    purpose='Deployment-target state captured before the first build; the same LastWriteTimeUtc/file count holds afterwards.', source_sha256=cur_bind(),
    conditions='Path BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant; 1158 files; 09/26/2026 21:41:14 UTC unchanged after all builds/tests.')
add(id='receive-intake-note', path='_workflow/wave3-bo6-bo7/INTAKE-NOTE.md', level='document', binding='current',
    purpose='Boundary note for the partially imported source evidence directory.', source_sha256=cur_bind(),
    conditions='States what is imported and what remains only in the retained source worktree.')
add(id='receive-findings', path=d+'/findings.md', level='document', binding='current',
    purpose='Batch findings, mutation summary, regressions, and residual items.', source_sha256=cur_bind(), conditions='Packet findings role.')
add(id='receive-budget', path=d+'/budget.md', level='document', binding='current',
    purpose='Consultation budget, fixed model/strength, original severities, unclosed items.', source_sha256=cur_bind(), conditions='Packet budget role.')
add(id='receive-context', path=d+'/context.md', level='document', binding='current',
    purpose='Batch objective and completion criteria.', source_sha256=cur_bind(), conditions='Packet objective role.')
for eid,path,purpose in [
 ('source-owner-checkpoint','_workflow/wave3-bo6-bo7/owner-checkpoint.md','Authoritative source checkpoint; SHA-256 A4AB77476298FD7AB3847930C0A92BA72F234424EC6A4D3B1706AB17809C75B1 verified at intake.'),
 ('source-risk-matrix','_workflow/wave3-bo6-bo7/risk-matrix.json','Source batch risk matrix and its coverage mapping.'),
 ('source-mutation-index','_workflow/wave3-bo6-bo7/mutations/raw-mutation-evidence-index.md','Source batch mutation evidence index.'),
 ('source-ledger-reconciliation','_workflow/wave3-bo6-bo7/consultation/current-ledger-reconciliation.json','Source consultation ledger reconciliation (8/8 conservative count, balance 0).'),
 ('source-disposition-9-10','_workflow/wave3-bo6-bo7/consultation/owner-approved-requests-9-10.md','Item-by-item disposition of the two owner-approved additional consultations.'),
 ('source-testid-comparison','_workflow/wave3-bo6-bo7/regression/final/testid-comparison-final.json','Source delivery testId comparison used for the independent cross-check.'),
 ('source-context','_workflow/wave3-bo6-bo7/context.md','Source batch objective (context role).'),
 ('source-findings','_workflow/wave3-bo6-bo7/findings.md','Source batch findings (contrast material).'),
 ('source-budget','_workflow/wave3-bo6-bo7/budget.md','Source batch budget ledger (contrast material).'),
]:
    add(id=eid, path=path, level='document', binding='current', purpose=purpose, source_sha256=cur_bind(),
        conditions='Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.')
add(id='source-review-a-bo6', path='_workflow/wave3-bo6-bo7/review-cli-a-bo6/run/review.md', level='consult', binding='current',
    purpose='Independent read-only review report closing BO-6 R19 MUST and BO-6 R21 F4 IMPORTANT at the delivered bytes.', source_sha256=cur_bind(),
    conditions='Local Codex CLI read-only, gpt-6-astra / medium, exit 0, 182.8 s, fixed snapshot HEAD 0f46128f; report SHA-256 de8a8176...b4e8bc.')
add(id='source-review-b-bo7', path='_workflow/wave3-bo6-bo7/review-cli-b-bo7/run/review.md', level='consult', binding='current',
    purpose='Independent read-only review report closing BO-7 R21 F1/F2 MUST at the delivered bytes.', source_sha256=cur_bind(),
    conditions='Local Codex CLI read-only, gpt-6-astra / medium, exit 0, 183.0 s, fixed snapshot HEAD 0f46128f; report SHA-256 5b886136...438479.')
add(id='source-review-a-meta', path='_workflow/wave3-bo6-bo7/review-cli-a-bo6/run-metadata.json', level='document', binding='current',
    purpose='Run metadata (model, strength, read-only, exit code, usage) for the BO-6 review.', source_sha256=cur_bind(), conditions='Request 9; model verified from the local rollout record.')
add(id='source-review-b-meta', path='_workflow/wave3-bo6-bo7/review-cli-b-bo7/run-metadata.json', level='document', binding='current',
    purpose='Run metadata (model, strength, read-only, exit code, usage) for the BO-7 review.', source_sha256=cur_bind(), conditions='Request 10; model verified from the local rollout record.')

add(id='opening-baseline-build', path=d+'/baseline/testproject-build.log', level='build', binding='historical',
    purpose='Pre-intake mainline baseline build (opening HEAD 6fd6207e5).', source_sha256=dict(open_sh),
    conditions='dotnet build -t:Rebuild -p:DeployToBgiTools=false; exit 0; executed before any file was imported.')
add(id='opening-baseline-targeted', path=d+'/baseline/targeted-baseline.trx', level='test', binding='historical',
    purpose='Pre-intake targeted three-class baseline (89 tests) for the testId differential.', source_sha256=dict(open_sh),
    conditions='Same filter as receive-targeted-93; 89/89 passed; 4 helper tests carry their pre-intake names.')
add(id='opening-baseline-full', path=d+'/baseline/assistant-full-baseline.trx', level='test', binding='historical',
    purpose='Pre-intake full assistant baseline; same-condition reference for the comparison.', source_sha256=dict(open_sh),
    conditions='1558 passed / 2 skipped / 0 failed / 1560; matches the source delivery opening baseline.')
add(id='receive-preRegen-targeted', path=d+'/regression/targeted-integrated-93.trx', level='test', binding='historical',
    purpose='Intermediate integrated targeted run executed before the declaration-surface regeneration.', source_sha256={s:sha(s) for s in SRC},
    conditions='93/93; superseded by receive-targeted-93 after ClaimSurfaceManifest.txt changed, kept for traceability (same outcome, not used as current green evidence).')
add(id='receive-preRegen-full', path=d+'/regression/assistant-full-integrated.trx', level='test', binding='historical',
    purpose='Intermediate integrated full run executed before the declaration-surface regeneration.', source_sha256={s:sha(s) for s in SRC},
    conditions='1562/2/0/1564; superseded by receive-full-1564 for the same reason; kept for traceability.')

tests=[{'id':'receive-targeted-93','path':d+'/regression/final/targeted-93.trx','expect_success':True},
 {'id':'receive-full-1564','path':d+'/regression/final/assistant-full-1564.trx','expect_success':True},
 {'id':'receive-claim-surface-regen','path':d+'/regression/claim-surface/regen/claim-surface-regen.trx','expect_success':True},
 {'id':'receive-claim-surface-no-env','path':d+'/regression/claim-surface/no-env/claim-surface-no-env.trx','expect_success':True}]

recs=json.load(io.open(d+'/mutation-records.json',encoding='utf-8-sig'))
mutations=[]
for r in recs:
    mutations.append({k:r[k] for k in ('id','description','source','test_source','test_source_sha256','original_sha256','mutant_sha256','restored_sha256',
        'target_test_id','target_name','failure_contains','assertion_contains','assertion_message','baseline_trx','mutant_trx','restored_trx',
        'baseline_build_log','mutant_build_log','restored_build_log','build_log','build_exit','baseline_build_exit','restored_build_exit',
        'baseline_exit','mutant_exit','restored_exit','mutation_label')})

TID={'mixed':'398aed81-8691-87dd-a9cd-c62780f5f924','tail':'113f020c-1c29-e336-4c1a-3a50b69cbcc8',
     'cand':'1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc','resc':'7a665a8f-f8e9-11b4-598f-a1f8d24b805a',
     'helper1':'ec3b4575-60e7-a499-c37f-c9ae12b11e0e','helper2':'14addbd1-4d7d-814d-ec3d-c070098441ad',
     'cs':'2a110364-66d8-3e33-fbe4-06d907c54f09'}
rm=json.load(io.open(d+'/risk-matrix.json',encoding='utf-8'))
rows_covered={
 'S1':dict(counterexample_ids=['receive-targeted-93'],test_ids=[TID['mixed'],TID['tail']],
    mutation_ids=['bo6-resume-live-park-v3','bo7-earliest-park-v3']),
 'S2':dict(counterexample_ids=['receive-targeted-93'],test_ids=[TID['mixed'],TID['helper1'],TID['helper2']],
    mutation_ids=['bo6-completed-filter-v3','bo7-stable-identity-v3']),
 'S3':dict(counterexample_ids=['receive-targeted-93'],test_ids=[TID['tail']],
    mutation_ids=['bo6-tail-fail-closed-v3','bo6-tail-persisted-failure-v4']),
 'S5':dict(counterexample_ids=['receive-targeted-93'],test_ids=[TID['cand'],TID['resc']],
    mutation_ids=['bo7-candidate-first-v3','bo7-rescue-first-v3']),
 'S4':dict(counterexample_ids=['receive-claim-surface-regen','receive-claim-surface-no-env'],test_ids=[TID['cs']],mutation_ids=[]),
 'C1':dict(counterexample_ids=['receive-targeted-93'],test_ids=[TID['tail']],
    mutation_ids=['bo6-tail-persisted-failure-v4']),
 'C2':dict(status='not_applicable',reason='多有效停驻跨轮次推进与多实例/跨进程并发写属 BO-9 R34 F5（IMPORTANT）与 BO-8/BO-9 范围，本接收批按范围不并入，故本批不对此类场景作任何结论；同 loop 内的多有效停驻已由 S1/S2 的真实 Runner 事实覆盖。'),
 'F1':dict(counterexample_ids=['receive-targeted-93'],test_ids=[TID['mixed'],TID['helper1'],TID['helper2']],
    mutation_ids=['bo7-stable-identity-v3']),
 'F2':dict(counterexample_ids=['receive-targeted-93'],test_ids=[TID['tail']],
    mutation_ids=['bo6-tail-fail-closed-v3'])}
for row in rm['rows']:
    upd=rows_covered[row['id']]
    if upd.get('status')=='not_applicable':
        row['status']='not_applicable'; row['reason']=upd['reason']
    else:
        row['status']='covered'; row['counterexample_ids']=upd['counterexample_ids']
        row['test_ids']=upd['test_ids']
        if upd['mutation_ids']: row['mutation_ids']=upd['mutation_ids']
        row.pop('reason',None)
io.open(d+'/risk-matrix.json','w',encoding='utf-8',newline='\n').write(json.dumps(rm,ensure_ascii=False,indent=2)+"\n")

packet=[{'path':d+'/context.md','role':'objective'},
 {'path':d+'/findings.md','role':'findings'},
 {'path':d+'/budget.md','role':'budget'},
 {'path':SRC[0],'role':'source'},
 {'path':SRC[1],'role':'source'},
 {'path':SRC[2],'role':'source'}]
# ClaimSurfaceManifest excerpt: the three entries added by the R5.3 24.126 registration
cl='\n'.join('')
for i,n in enumerate(ranges):
    packet.append({'path':SRC[3],'role':'source','start_line':max(1,n-1),'end_line':n+1,
      'coverage_notes':('Declaration-surface manifest entry added by the new R5.3 24.126 registration text (generated file; %d of 3). '
                        'The manifest is a generated 220 KB list of %d claim lines; only the entries this batch introduced are excerpted. '
                        'The complete file hash is bound in every evidence source_sha256 and the whole-file regeneration result is proven by receive-claim-surface-regen / -no-env.'
                        % (i+1, len(lines)-1))})

man=json.load(io.open(d+'/manifest.json',encoding='utf-8-sig'))
man['evidence']=ev; man['tests']=tests; man['mutations']=mutations; man['packet']=packet
man['comparison']={'baseline':d+'/baseline/assistant-full-baseline.trx','final':d+'/regression/final/assistant-full-1564.trx'}
man['mutation_scope']=('Eight independent reverse mutations re-executed on the integrated mainline bytes: seven v3 Runner-behaviour mutations '
 '(explicit-Resume parked recomputation, park-recovery completion filter, unresolved-parked tail fail-closed aggregation, cross-loop candidate/rescue ordering both directions, '
 'earliest park selection, stable completion identity) plus v4 (removal of the durable _runs.Update(run) in the Failed aggregation branch, assertion at WorkflowRunnerTests.cs:1080). '
 'Every mutation has its own baseline/mutant/restored build log and TRX, a named target assertion, exact restore and a recorded mutant SHA-256 that matches the source delivery record. '
 'Not covered: no mutation is taken against the production entry points, real User state, hotkeys or live processes, because those gates stay closed in this receive batch; '
 'BO-8/BO-9 behaviours (ordinary revision progression filtering, multi-park cross-loop advancement) are deliberately outside scope and are not mutated.')
man['outside_changes']=('本批只改下列路径：接收批工作目录 `_workflow/wave3-bo6bo7-receive/**`（新增，本批证据与登记）、'
 '`_workflow/wave3-bo6-bo7/**` 的 14 个逐字节导入文件 + 新增 `INTAKE-NOTE.md`、'
 '以及导入并追加登记的 8 个来源文件（WorkflowRunner.cs、WorkflowRunnerTests.cs、LocalWaitIdentityTranslationTests.cs、ClaimSurfaceManifest.txt、'
 'R5.3、_batch21/b21_plan.md、_batch21/sb21-4-handoff-2026-09-28.md、槲寄生调度器总计划.md）。'
 '材料外变更（非本批写入者）：`Docs/design/mistletoe-session-relay-2026-09-24.md` 与 `Docs/design/unified-job-registry-master-plan.md` 的既有未提交修改（开工前即存在，本批未触碰、未提交）；'
 '工作区另有大量既有未跟踪的 `.bak`/`.stale`/历史批次目录/TestResults/日志/截图与 `_workflow/sb21-4/**`、`_batch21/**` 未跟踪工件，均保持原样。'
 '本批不据路径推断这些材料外变更的写者，也不清理它们。')
io.open(d+'/manifest.json','w',encoding='utf-8',newline='\n').write(json.dumps(man,ensure_ascii=False,indent=2)+"\n")
print('evidence',len(ev),'tests',len(tests),'mutations',len(mutations),'packet',len(packet))
print('manifest bytes',os.path.getsize(d+'/manifest.json'))
