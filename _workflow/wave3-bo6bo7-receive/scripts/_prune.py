import io, json
d='_workflow/wave3-bo6bo7-receive'
man=json.load(io.open(d+'/manifest.json',encoding='utf-8-sig'))
drop={'receive-preRegen-targeted','receive-preRegen-full','source-context','source-findings','source-budget',
      'receive-targeted-identity','receive-deploy-target-before'}
before=len(man['evidence'])
kept=[e for e in man['evidence'] if e['id'] not in drop]
print('dropped',before-len(kept))
# adjust the two references to the dropped deploy-target entry
for e in kept:
    if e['id']=='receive-assistant-project-rebuild':
        e['conditions']='dotnet build MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0. Deployment target BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant (and the non-x64 twin) still had LastWriteTimeUtc 09/26/2026 21:41:14 UTC and 1158 files after every build and test run of this batch; capture before the first build is recorded in _workflow/wave3-bo6bo7-receive/deploy-target-before.txt and restated in findings.md.'
    if e['id']=='receive-testproject-rebuild':
        e['conditions']='dotnet build Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj -t:Rebuild -p:DeployToBgiTools=false; exit 0.'
    if e['id']=='receive-mutation-records':
        e['conditions']='Produced by _workflow/wave3-bo6bo7-receive/run-receive-mutants.ps1 under pwsh 7; per-mutation build/test logs and TRX under _workflow/wave3-bo6bo7-receive/mutations/<id>/; compact per-mutation verification and source-hash comparison in verification/mutation-verification.json.'
    if e['id']=='source-mutation-index':
        e['conditions']='Imported byte-for-byte from the source delivery commit; context for the intake, not a re-verification by this batch.'
    if e['id']=='source-testid-comparison':
        e['conditions']='Imported byte-for-byte; used for the independent added/removed testId cross-check reported in verification/targeted-93-ids.json.'
    if e['id']=='source-ledger-reconciliation':
        e['conditions']='Imported byte-for-byte; the source sub-batch budget (8/8 plus owner-approved 2/2) stays recorded and is not reset by this receive batch.'
man['evidence']=kept
man['packet_pruning_note']='Compliant de-duplication to stay inside the local 524288-byte packet limit: removed are the two superseded pre-regeneration regression runs (files retained on disk, outcome restated in findings.md), the three source-batch documents whose content duplicates packet roles already carried in the source checkpoint, the targeted-identity file subsumed by verification/targeted-93-ids.json, and the deploy-target capture restated in the rebuild conditions and findings.md. No required raw evidence was deleted; no excerpt was trimmed to fit.'
io.open(d+'/manifest.json','w',encoding='utf-8',newline='\n').write(json.dumps(man,ensure_ascii=False,indent=2)+"\n")
print('evidence now',len(man['evidence']))
