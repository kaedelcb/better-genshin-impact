import io, json, hashlib, os
d='_workflow/wave3-bo6bo7-receive'
SRC=['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt']
CUR={s:hashlib.sha256(open(s,'rb').read()).hexdigest() for s in SRC}
man=json.load(io.open(d+'/manifest.json',encoding='utf-8-sig'))
extra=[
 dict(id='receive-testid-comparison-summary', path=d+'/regression/testid-comparison-summary.json', level='document', binding='current',
   purpose='Reviewable testId differential summary (counts, added/removed id sets, and the two retained-id tests whose assertions changed).', source_sha256=dict(CUR),
   conditions='Derived from testid-comparison-receive.json; the generator output itself (~1.0 MB) is not attached because it embeds two full parsed TRX row sets.'),
 dict(id='receive-consult-request', path=d+'/consultation/review-request-v1.md', level='document', binding='current',
   purpose='Frozen consultation request: objective, six questions, boundaries, material list, exclusions.', source_sha256=dict(CUR),
   conditions='Written before dispatch so the reviewed scope cannot drift with the result.'),
 dict(id='receive-consult-preflight', path=d+'/consultation/preflight-v1.json', level='document', binding='current',
   purpose='Capacity pre-check record: allowed files, bytes, token estimate, transport limits, exclusions, fallback assessment.', source_sha256=dict(CUR),
   conditions='Local computation only; no request dispatched by this step.'),
]
man['evidence']=man['evidence']+extra
io.open(d+'/manifest.json','w',encoding='utf-8',newline='\n').write(json.dumps(man,ensure_ascii=False,indent=2)+"\n")
print('evidence now', len(man['evidence']))
