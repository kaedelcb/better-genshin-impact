import io, json, os, re
d='_workflow/wave3-bo6bo7-receive'
snap=d+'/review-intake-20260928-v1'
files=[
 d+'/consultation/review-request-v1.md',
 d+'/context.md', d+'/findings.md', d+'/budget.md',
 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 d+'/mutation-records.json',
 d+'/regression/testid-comparison-receive.json',
 d+'/regression/claim-surface/manifest-diff-summary.json',
 d+'/regression/claim-surface/manifest-before.sha256',
 d+'/regression/claim-surface/manifest-after-regen.sha256',
 d+'/regression/claim-surface/manifest-after-no-env.sha256',
 d+'/regression/final/targeted-fact-testids.json',
 '_workflow/wave3-bo6-bo7/owner-checkpoint.md',
 '_workflow/wave3-bo6-bo7/consultation/owner-approved-requests-9-10.md',
 '_workflow/wave3-bo6-bo7/consultation/current-ledger-reconciliation.json',
 '_workflow/wave3-bo6-bo7/review-cli-a-bo6/run/review.md',
 '_workflow/wave3-bo6-bo7/review-cli-b-bo7/run/review.md',
 '_workflow/wave3-bo6-bo7/review-cli-a-bo6/run-metadata.json',
 '_workflow/wave3-bo6-bo7/review-cli-b-bo7/run-metadata.json',
 snap+'/git-status.txt', snap+'/scoped-staged.diff', snap+'/scoped-unstaged.diff',
]
def est(p):
    b=open(p,'rb').read()
    t=b.decode('utf-8',errors='replace')
    cjk=len(re.findall(r'[\u3000-\u9fff\uff00-\uffef]',t))
    other=len(t)-cjk
    return len(b), cjk, other, cjk/1.45 + other/3.6
tot_b=tot_t=0
rows=[]
for p in files:
    if not os.path.exists(p):
        rows.append((p,None,None,None,None)); continue
    b,c,o,t=est(p); tot_b+=b; tot_t+=t
    rows.append((p,b,c,t))
for r in rows:
    print('%-72s %s' % (r[0].replace('_workflow/wave3-bo6bo7-receive/', 'RECV/').replace('_workflow/wave3-bo6-bo7/','SRC/'), ('%8d B  ~%7d tok' % (r[1], r[3])) if r[1] is not None else 'MISSING'))
print('-'*100)
print('TOTAL bytes=%d  estimated_tokens=%d' % (tot_b, tot_t))
json.dump({'files':[r[0] for r in rows if r[1] is not None],'missing':[r[0] for r in rows if r[1] is None],
           'total_bytes':tot_b,'estimated_input_tokens':int(tot_t),
           'method':'tokens ~= cjk_chars/1.45 + non_cjk_chars/3.6 over each file decoded as UTF-8; conservative order-of-magnitude estimate, not a tokenizer run',
           'model':'gpt-6-astra','assumed_model_window':272000,'assumed_effective_window_95pct':258400,
           'estimated_input_fraction_of_effective_window':round(tot_t/258400,3)},
          io.open(d+'/consultation/preflight-v1.json','w',encoding='utf-8'),ensure_ascii=False,indent=2)
