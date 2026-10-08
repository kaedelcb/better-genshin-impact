"""Same-work-package repair of the observed cold paused Stop terminal writeback gap."""
from pathlib import Path
import sys, os, json, hashlib, time, subprocess, xml.etree.ElementTree as ET
R=Path(__file__).resolve().parents[3]; B=Path(__file__).resolve().parent/'paused-stop-repair'
C=R/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
H=R/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs'
T=R/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FastLocalTerminalAdmissionTests.cs'
sys.path.insert(0,str(R/'tools/mistletoe')); import storage_limits as s; import process_runner as p
sys.stdout.reconfigure(encoding='utf-8'); sha=lambda b:hashlib.sha256(b).hexdigest()
original_size=s.size; retries=[]
generated=[R/'MultiplayerHoeingAssistant/obj',R/'Test/MultiplayerHoeingAssistant.UnitTest/obj']
def measured(roots):
    for attempt in range(3):
        try:return original_size(list(dict.fromkeys(str(x) for x in roots)))
        except FileNotFoundError as e:
            path=Path(e.filename).absolute()
            if attempt==2 or not any(path.is_relative_to(g) for g in generated):raise
            retries.append(dict(path=str(path),full_rescan=True));time.sleep(.1)
s.size=measured
def atomic(path,b):
    tmp=path.with_name(path.name+'.paused-stop-01a11940.tmp');assert not tmp.exists()
    s.write(tmp,b);os.replace(tmp,path);assert path.read_bytes()==b
def sources():
    return {f.relative_to(R).as_posix():sha(f.read_bytes()) for root in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (R/root).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(x in f.parts for x in ['bin','obj'])}
test='''    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdPausedStop_ReconcilesOriginalAdmission_OrRetainsFailureForExplicitRetry(bool failSeal)
    {
        var root=Root();var flow=SaveFlow(root,true);using var client=new BgiExternalClient();
        var flows=new WorkflowStore(Path.Combine(root,"flows"));var doc=flows.Load(flow);
        doc.Nodes[0].Strategies.Add(new(){Kind="schedule.time",Params=new()
        {
            ["mode"]=JsonSerializer.SerializeToElement("sequence"),
            ["time"]=JsonSerializer.SerializeToElement(DateTimeOffset.Now.AddMinutes(30).ToString("HH:mm")),
        }});
        flows.Save(doc,null);var host=Host(root,client);string runId;
        try
        {
            var started=await host.StartWorkflowAsync(flow);
            Assert.True(started.Status==HostActionStatus.Registered,started.Message);
            runId=Assert.Single(host.Runs.List()).RunId;
            await WaitForRun(host,runId,r=>r.Wait is not null);
            Assert.Equal(HostActionStatus.Registered,(await host.RequestRunActionAsync(runId,WorkflowRunAction.Pause)).Status);
            var paused=await WaitForRun(host,runId,r=>r.State==WorkflowRunState.Paused);
            Assert.Empty(TerminalReleaseEvidence.Submissions(paused));Assert.Empty(paused.NodeOutcomes);
            Assert.Equal(OperationRequestState.Accepted,OriginalOperation(root,runId).RequestState);
        }
        finally{await host.ShutdownAsync();}
        var cold=Host(root,client);
        try
        {
            if(failSeal)cold.Runs.PublishFaultForTest=r=>r.TerminalRelease is null?null:new IOException("paused seal unavailable");
            var stopped=await cold.RequestRunActionAsync(runId,WorkflowRunAction.Stop);
            if(failSeal)
            {
                Assert.Equal(HostActionStatus.Unavailable,stopped.Status);
                var retained=cold.Runs.Load(runId)!;
                Assert.Equal(WorkflowRunState.Cancelled,retained.State);Assert.True(retained.StopRequested);
                Assert.Null(retained.TerminalRelease);
                Assert.Equal(OperationRequestState.Accepted,OriginalOperation(root,runId).RequestState);
                cold.Runs.PublishFaultForTest=null;
                stopped=await cold.RequestRunActionAsync(runId,WorkflowRunAction.Stop);
            }
            Assert.Equal(HostActionStatus.Effective,stopped.Status);
            var after=cold.Runs.Load(runId)!;
            Assert.Equal(WorkflowRunState.Cancelled,after.State);Assert.True(after.StopRequested);
            Assert.Empty(TerminalReleaseEvidence.Submissions(after));Assert.Empty(after.NodeOutcomes);
            Assert.True(TerminalReleaseEvidence.ValidRunSeal(after),"Paused Stop must seal before claiming Effective");
            var operation=OriginalOperation(root,runId);
            Assert.Equal(OperationRequestState.TerminalCompleted,operation.RequestState);
            Assert.Equal("runstore-seal:"+after.TerminalRelease!.Id,operation.TerminalReleaseEvidence);
            Assert.Equal(HostActionStatus.Effective,(await cold.RequestRunActionAsync(runId,WorkflowRunAction.Stop)).Status);
            Assert.Equal(after.RecordRevision,cold.Runs.Load(runId)!.RecordRevision);
        }
        finally{await cold.ShutdownAsync();}
    }

    private static async Task<WorkflowRunRecord> WaitForRun(TaskCenterHost host,string runId,Func<WorkflowRunRecord,bool> predicate)
    {
        for(var i=0;i<400;i++)
        {
            var run=host.Runs.Load(runId)!;if(predicate(run))return run;
            await Task.Delay(25);
        }
        throw new TimeoutException("Waiting for local schedule/pause boundary");
    }

'''
old=b'            return HostActionResult.Effective("'+ '已停止（暂停态终态化，未触发收尾）'.encode()+b'");'
session=s.Session(R,'same-package-observed-cold-paused-stop-repair-01a11940'); original_policy=dict(session.policy)
session.policy=dict(original_policy,operation_bytes=256*1024*1024)
assert session.policy['operation_bytes']<=original_policy['operation_bytes']
with session:
    assert not B.exists();session.track(B);session.track(C)
    for g in generated:session.track(g)
    B.mkdir();write=lambda path,obj:s.write(path,s.encode(obj))
    hb=H.read_bytes();tb=T.read_bytes();ending=b'\r\n' if b'\r\n' in hb else b'\n'
    new=ending.join([b'            return await ReconcileAdmissionTerminalForExplicitStopAsync(runId,',b'                "'+ '已停止（暂停态终态化，未触发收尾）'.encode()+b'").ConfigureAwait(false);'])
    assert hb.count(old)==1;assert tb.count(b'    [Theory]')==2
    s.write(B/'before/TaskCenterHost.cs',hb);s.write(B/'before/FastLocalTerminalAdmissionTests.cs',tb)
    write(B/'admission.json',dict(thread=os.environ['CODEX_THREAD_ID'],finding='PAUSED-STOP-TERMINAL-RECONCILIATION-1',severity='important',status='open',function='normal TaskCenter cold paused Stop',actual_red='../runtime-hostfix/result.json',actual_operation_state='Accepted after Effective/Cancelled; no terminal seal',minimal_fix='reuse existing terminal reconciliation helper after paused cancellation, including failure and retry semantics',next_delivery='same-product replay then bounded unified independent disposition',new_reviews=0,review_remaining=0,new_budget_domain=False,policy_original=original_policy,operation_bytes=session.limit,previous_related_growth_bytes=130514084,protected='real User, third-party JS, original opening/reports/failures/grades, unrelated work; no Startup Center or game',metadata={str(q.relative_to(R)):dict(bytes=len(b),lines=len(b.splitlines()),sha256=sha(b),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n')) for q,b in [(H,hb),(T,tb)]}))
    s.write(B/'opening-status.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=R))
    lease=R/'_workflow/final-ui-01a11220/own-runtime/assistant-data/arbitration/arbitration-lease.json'
    s.write(B/'actual-arbitration-before.json',lease.read_bytes())
    te=b'\r\n' if b'\r\n' in tb else b'\n'; anchor=b'    [Theory]'+te+b'    [InlineData(true)]'
    assert tb.count(anchor)==2;patched=tb.replace(anchor,test.encode().replace(b'\n',te)+anchor,1);atomic(T,patched)
    baseline=json.loads((Path(__file__).resolve().parent/'host-mode-repair/restored-2/source-hashes.json').read_bytes())
    assert all(sha((R/n).read_bytes())==v for n,v in baseline.items() if (R/n) not in [T])
    full=json.loads((Path(__file__).resolve().parent/'host-mode-repair/restored-2/test-process-request.json').read_bytes())['argv'][3]
    env=os.environ.copy();env.update(NEXUSBGI_DATA_ROOT=str(B/'own-data'),TEMP=str(B/'temp'),TMP=str(B/'temp'),FORMAL_TERMINAL_EVIDENCE_DIR=str(B/'test-facts'))
    Path(env['TEMP']).mkdir();Path(env['FORMAL_TERMINAL_EVIDENCE_DIR']).mkdir(); outcomes=[]
    def run(label,filt):
        out=B/label;out.mkdir();hashes=sources();write(out/'source-hashes.json',hashes)
        build,_,_=p.run(['C:/Program Files/dotnet/dotnet.exe','build',str(R/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(C)],cwd=R,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
        assert build==0,label+' build failed'
        code,_,_=p.run(['C:/Program Files/dotnet/dotnet.exe','vstest',str(C/'MultiplayerHoeingAssistant.UnitTest.dll'),filt,'--logger:trx;LogFileName=paused-stop.trx','--ResultsDirectory:'+str(out)],cwd=R,env=env,directory=out,recovery_directory=out,phase='test',timeout=900)
        rows=ET.parse(out/'paused-stop.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
        summary=dict(label=label,build=build,test=code,total=len(rows),passed=sum(r.get('outcome')=='Passed' for r in rows),failures=[dict(test_id=r.get('testId'),name=r.get('testName'),message=''.join(r.itertext())) for r in rows if r.get('outcome')!='Passed'],source_drift=[n for n,h in hashes.items() if sha((R/n).read_bytes())!=h])
        assert not summary['source_drift'];write(out/'result.json',summary);outcomes.append(summary);print(label,summary['passed'],len(summary['failures']),flush=True);return summary
    def good(x):return x['total']==344 and x['passed']==342 and {f['test_id'] for f in x['failures']}=={'7ff2dc0d-a3c3-d14e-a619-fee985a3deca','62d390bb-e91f-98d9-6e78-38ce05d3ea47'}
    red=run('red','--TestCaseFilter:FullyQualifiedName~ColdPausedStop_ReconcilesOriginalAdmission')
    assert red['total']==2 and len(red['failures'])==2 and all('ColdPausedStop' in f['name'] for f in red['failures'])
    atomic(H,hb.replace(old,new));fixed=H.read_bytes();green=run('green',full);assert good(green)
    try:
        atomic(H,hb);negative=run('negative','--TestCaseFilter:FullyQualifiedName~ColdPausedStop_ReconcilesOriginalAdmission')
    finally:
        atomic(H,fixed);write(B/'source-restored.json',dict(host_sha256=sha(H.read_bytes()),same=H.read_bytes()==fixed,test_sha256=sha(T.read_bytes())))
    restored=run('restored',full);assert good(restored)
    assert negative['total']==2 and len(negative['failures'])==2 and any('Paused Stop must seal' in f['message'] for f in negative['failures'])
    write(B/'verification.json',dict(phases=outcomes,scan_retries=retries,host_original=sha(hb),host_fixed=sha(fixed),test_fixed=sha(patched),source_restored=True,new_reviews=0,independent_verified=False,product_refreshed=False,actual_green=False,product_complete=False))
    print('PAUSED_STOP_REPAIR_SOURCE_VERIFIED',flush=True)
