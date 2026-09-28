import io, json, hashlib, os
d='_workflow/wave3-bo6bo7-receive'
SRC=['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt']
CUR={s:hashlib.sha256(open(s,'rb').read()).hexdigest() for s in SRC}
man=json.load(io.open(d+'/manifest.json',encoding='utf-8-sig'))
def E(i,p,l,purp,cond): return dict(id=i,path=p,level=l,binding='current',purpose=purp,conditions=cond,source_sha256=dict(CUR))
extra=[
 E('verify-intake-equivalence', d+'/verification/intake-equivalence.json','document',
   'Per-file import fidelity (index blob == delivery blob) plus byte-identical comparison of the imported delta against the source delivery delta, and the post-import modification list. (Review round 1 IMPORTANT-1 repair evidence.)',
   'delta_byte_identical=true; both deltas SHA-256 0642971729f3eff0...; only the four status documents and ClaimSurfaceManifest.txt changed after import.'),
 E('verify-import-delta', d+'/verification/import-delta-8files.diff','document',
   'Complete imported delta over all eight source files, including the four status-document patches that review round 1 found missing. (IMPORTANT-1 repair evidence.)',
   'git diff --cached --no-ext-diff --no-textconv -- <8 files>; byte-identical to the source delivery delta.'),
 E('verify-post-import-delta', d+'/verification/post-import-delta.diff','document',
   'Everything this batch changed after the verbatim import, so imported bytes are distinguishable from registration edits. (IMPORTANT-1 repair evidence.)',
   'git diff -- <8 files>; contains the four registration appends and the mandated declaration-surface regeneration only.'),
 E('verify-regression-outcomes', d+'/verification/regression-outcomes.json','document',
   'Corrected row-level outcome accounting for every cited run, including the two NotExecuted rows that the earlier summary mis-stated. (Review round 1 IMPORTANT-2 repair evidence.)',
   'Counters@notExecuted is 0 in this VSTest/xUnit shape while two rows carry NotExecuted: P50_LoadRepro_WholeClass_UnderControlledLoad and P50_DiagnosticRepeat_OptIn; total = passed + failed + NotExecuted rows in every run.'),
 E('verify-targeted-93-ids', d+'/verification/targeted-93-ids.json','document',
   'Complete 93-entry testId/name/outcome comparison against the source delivery final targeted TRX. (Review round 1 suggestion repair evidence.)',
   'testId sets identical; name and outcome identical for every testId; zero mismatches.'),
 E('verify-mutation-verification', d+'/verification/mutation-verification.json','document',
   'Compact per-mutation verification: outcomes, target identity, assertion location, file digests, and the source-vs-receive mutant SHA equality. (Review round 1 suggestion repair evidence.)',
   'All eight mutant SHA-256 values equal the source delivery manifest records; every mutation is Passed/Failed/Passed on the integrated bytes.'),
 E('receive-consult-outcome-v1', d+'/consultation/review-outcome-v1.md','consult',
   'Round 1 consultation outcome and item-by-item disposition (2 IMPORTANT kept at original severity, 1 suggestion accepted).',
   'gpt-6-astra / medium, existing GPT tool, attempts=1, read-only; batch counter 1/8.'),
 E('receive-consult-request-v2', d+'/consultation/review-request-v2.md','document',
   'Verification-round request (round 2): strict scope of the two IMPORTANT items and the no-new-findings question.',
   'Frozen before dispatch so the reviewed scope cannot drift with the result.'),
]
man['evidence']=man['evidence']+extra
man['review_control']['review_round']=2
man['review_control']['repair_batch_id']='wave3-bo6bo7-receive-repair-r1'
man['review_control']['prior_findings']=[
 {'id':'R1-IMPORTANT-1','severity':'important','source_review_evidence_id':'receive-consult-outcome-v1',
  'disposition':'candidate_fixed','repair_evidence_ids':['verify-intake-equivalence','verify-import-delta','verify-post-import-delta']},
 {'id':'R1-IMPORTANT-2','severity':'important','source_review_evidence_id':'receive-consult-outcome-v1',
  'disposition':'candidate_fixed','repair_evidence_ids':['verify-regression-outcomes','receive-testid-comparison-summary']},
 {'id':'R1-SUGGESTION-1','severity':'suggestion','source_review_evidence_id':'receive-consult-outcome-v1',
  'disposition':'accepted','reason':'D1 comment scope widened to ~1400-1403 in findings.md (no source edit, deferral unchanged); the 93-entry comparison and compact mutation/hash chain were added as verification/targeted-93-ids.json and verification/mutation-verification.json.'},
]
io.open(d+'/manifest.json','w',encoding='utf-8',newline='\n').write(json.dumps(man,ensure_ascii=False,indent=2)+"\n")
print('evidence',len(man['evidence']))
