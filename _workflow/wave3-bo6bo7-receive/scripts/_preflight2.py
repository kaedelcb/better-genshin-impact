import io, json, os, re
d='_workflow/wave3-bo6bo7-receive'; snap=d+'/review-intake-20260928-v1'
files=[d+'/consultation/review-request-v1.md', d+'/context.md', d+'/findings.md', d+'/budget.md',
 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs',
 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitIdentityTranslationTests.cs',
 d+'/mutation-records.json', d+'/regression/testid-comparison-summary.json',
 d+'/regression/final/targeted-fact-testids.json',
 d+'/regression/claim-surface/manifest-diff-summary.json',
 d+'/regression/claim-surface/manifest-before.sha256', d+'/regression/claim-surface/manifest-after-regen.sha256',
 d+'/regression/claim-surface/manifest-after-no-env.sha256',
 '_workflow/wave3-bo6-bo7/owner-checkpoint.md','_workflow/wave3-bo6-bo7/consultation/owner-approved-requests-9-10.md',
 '_workflow/wave3-bo6-bo7/consultation/current-ledger-reconciliation.json',
 '_workflow/wave3-bo6-bo7/review-cli-a-bo6/run/review.md','_workflow/wave3-bo6-bo7/review-cli-b-bo7/run/review.md',
 '_workflow/wave3-bo6-bo7/review-cli-a-bo6/run-metadata.json','_workflow/wave3-bo6-bo7/review-cli-b-bo7/run-metadata.json',
 snap+'/git-status.txt', snap+'/scoped-staged.diff', snap+'/scoped-unstaged.diff']
def est(p):
    b=open(p,'rb').read(); t=b.decode('utf-8',errors='replace')
    cjk=len(re.findall(r'[\u3000-\u9fff\uff00-\uffef]',t)); other=len(t)-cjk
    return len(b), cjk/1.45 + other/3.6
tot_b=0; tot_t=0.0
print('%-70s %10s %10s' % ('file','bytes','est_tokens'))
for p in files:
    if not os.path.exists(p): print('%-70s %10s' % (p,'MISSING')); continue
    b,t=est(p); tot_b+=b; tot_t+=t
    print('%-70s %10d %10d' % (p.replace('_workflow/wave3-bo6bo7-receive/','RECV/').replace('_workflow/wave3-bo6-bo7/','SRC/'), b, int(t)))
print('-'*94)
print('files=%d  total_bytes=%d  estimated_tokens=%d  fraction_of_258400=%.3f' % (len(files), tot_b, int(tot_t), tot_t/258400))
json.dump({'kind':'consultation capacity pre-check (record only; no request dispatched as part of this step)',
 'channel':'existing GPT consultation tool (gpt_workspace.gpt_review)', 'model':'gpt-6-astra','effort':'medium',
 'fixed_snapshot':{'branch':'main-OldTeaBag-B168','head':'6fd6207e58ec93dcc38645c12131a9a512f5fc69',
   'note':'uncommitted intake state; audit snapshot _workflow/wave3-bo6bo7-receive/review-intake-20260928-v1'},
 'allowed_files':files, 'total_bytes':tot_b, 'estimated_input_tokens':int(tot_t),
 'estimate_method':'tokens ~= cjk_chars/1.45 + non_cjk_chars/3.6 per file decoded as UTF-8. Order-of-magnitude estimate, not a tokenizer run; deliberately conservative for the code-heavy mix.',
 'assumed_model_window':272000,'assumed_effective_window_95pct':258400,
 'estimated_fraction_of_effective_window':round(tot_t/258400,3),
 'attachment_transport_limits':'30 files / 512 KiB per file / 2 MiB total: within limits (25 files, max 132,815 B, total %d B).' % tot_b,
 'excluded_material':[{'path':d+'/regression/testid-comparison-receive.json','bytes':1027277,
    'reason':'Generator output embeds two complete parsed TRX row sets (~1 MB) and would consume ~285k tokens; its reviewable substance is carried by regression/testid-comparison-summary.json and findings.md. Exclusion recorded, not silently dropped.'},
   {'path':snap+'/packet.md','bytes':504184,
    'reason':'The local audit packet duplicates the individual materials already listed above; sending both would roughly double the token load without adding material.'}],
 'fallback_assessment':'Estimated input is ~%.0f%% of the assumed effective window, so condition 2 of the facilities fallback rule is judged NOT met on this estimate alone; the original GPT consultation tool is used first. If the same-scope call returns without a report (non-auth/quota/model error), the local read-only CLI fallback is pre-justified because the estimate exceeds one third of the window.'
   % (100*tot_t/258400)},
 io.open(d+'/consultation/preflight-v1.json','w',encoding='utf-8',newline='\n'),ensure_ascii=False,indent=2)
