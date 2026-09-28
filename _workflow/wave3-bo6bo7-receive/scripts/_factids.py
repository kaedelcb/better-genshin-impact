import xml.etree.ElementTree as ET, json, io
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def load(p):
    root=ET.parse(p).getroot()
    defs={d.get('id'):(d.find('t:TestMethod',NS).get('className'),d.find('t:TestMethod',NS).get('name')) for d in root.find('t:TestDefinitions',NS)}
    out={}
    for r in root.find('t:Results',NS):
        tid=r.get('testId'); cls,m=defs[tid]
        out[m]={'testId':tid,'class':cls.split('.')[-1],'outcome':r.get('outcome')}
    return out
f=load('_workflow/wave3-bo6bo7-receive/regression/final/targeted-93.trx')
s=load(r'C:\Users\Administrator\.codex\worktrees\wave3-bo6-bo7\better-genshin-impact-LCB\_workflow\wave3-bo6-bo7\regression\final\runner-localwait-final.trx')
print('final targeted count',len(f),'all passed:',all(v['outcome']=='Passed' for v in f.values()))
print('identical to source final TRX:', {k:v['testId'] for k,v in f.items()}=={k:v['testId'] for k,v in s.items()})
keys=['Resume_ReinsertedParksBeforeCompletedAnchors_RedrivesAllParksAndSkipsCompletedOccurrences',
 'Resume_TailWithUnresolvedParkedObligationsFailsWithoutTerminalCompletion',
 'Resume_CandidateAndRescueAcrossLoop_UsesFullPlanOrderAndAdvancesThroughRunner',
 'Resume_RescueBeforeNextLoopCandidate_UsesFullPlanOrderAndFiltersCompletedAnchor',
 'RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate',
 'RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed',
 'RecomputeSuccessor_MultiParkingWithStale_ReturnsEarliestActiveMarker',
 'RecomputeSuccessor_ParkedReorderedBeforeCompletedAnchor_ReturnsParkedMarker',
 'RecomputeSuccessor_AnchorLocatableParkedBeforeItAndWorkAfter_ReturnsParkedMarker',
 'RecomputeSuccessor_AnchorAtTail_ParkedFollowedByCompleted_ReturnsParkedMarker']
sel={k:f[k]['testId'] for k in keys}
print(json.dumps(sel,ensure_ascii=False,indent=1))
io.open('_workflow/wave3-bo6bo7-receive/regression/final/targeted-fact-testids.json','w',encoding='utf-8',newline='\n').write(json.dumps(sel,ensure_ascii=False,indent=2)+"\n")
