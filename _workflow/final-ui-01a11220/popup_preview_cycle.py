"""C06 follow-up: new popout must preserve the existing preview and selected start point."""
from pathlib import Path
import hashlib,json,os,subprocess,sys,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'flow-preview/popout-r3'
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
VM='MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs'
TEST='Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalFlowContextTests.cs'
OLD='if (p is not WorkflowListItemVm item || Editing?.Draft.WorkflowId == item.WorkflowId) return;'
NEW='if (p is not WorkflowListItemVm item || Editing?.Draft.WorkflowId == item.WorkflowId || Previewing?.WorkflowId == item.WorkflowId) return;'
FACT='''    [Fact]
    public Task Preview_NewPopout_PreservesModeAndSelectedStartPoint() => OnSta((host, vm, first, second) =>
    {
        InvokePreview(first);
        var preview = vm.Previewing;
        var point = vm.SelectedStartPoint;
        Window? popup = null;
        try
        {
            Button(first, "⤢弹出窗口").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            popup = Assert.IsType<Window>(typeof(ScheduleListView).GetField("_popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(first));
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var view = Assert.IsType<ScheduleListView>(popup.Content);
            Layout(view); Layout(first); Layout(second);
            Assert.Same(preview, vm.Previewing);
            Assert.Null(vm.Editing);
            Assert.Same(point, vm.SelectedStartPoint);
            foreach (var current in new[] { first, second, view })
            {
                Assert.Same(point, ((ComboBox)current.FindName("StartPointChoice")).SelectedItem);
                Assert.True(StartButton(current).IsEnabled);
            }
            Assert.Empty(host.ListActiveRuns());
        }
        finally { popup?.Close(); }
    });

'''
sha=lambda b:hashlib.sha256(b).hexdigest()
def atomic(path,data):
    temp=path.with_name(path.name+'.own-popout.tmp');assert not temp.exists()
    with temp.open('xb') as f:f.write(data);f.flush();os.fsync(f.fileno())
    os.replace(temp,path);assert path.read_bytes()==data and not temp.exists()
def replace(rel,a,b):
    file=ROOT/rel;data=file.read_bytes();end='\r\n' if b'\r\n' in data else '\n'
    old=a.replace('\n',end).encode();new=b.replace('\n',end).encode();assert data.count(old)==1
    atomic(file,data.replace(old,new))
with s.Session(ROOT,'own-root-01a11405-C06-new-popout-preview') as budget:
    original_roots=budget.old_roots[:];budget.old_roots=list(dict.fromkeys(original_roots));assert set(original_roots)==set(budget.old_roots)
    budget.track(BASE);carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant';budget.track(carrier)
    for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    before={}
    for rel in [VM,TEST]:
        data=(ROOT/rel).read_bytes();s.write(BASE/'opening/before'/rel,data)
        before[rel]=dict(sha256=sha(data),bytes=len(data),lines=len(data.splitlines()),crlf=data.count(b'\r\n'),bom=data.startswith(b'\xef\xbb\xbf'))
    write(BASE/'opening/before.json',before)
    write(BASE/'opening/admission.json',dict(function='C06',entry='preview then create real ScheduleListView popout',runtime_source=str(ROOT/'_workflow/final-ui-01a11220/own-runtime/seventh-preview/private/native-ui-source.jsonl'),gap='new view selection notification changes active preview into editing',consequence='chosen start point becomes inaccessible after opening popout',repair='same preview identity selection is idempotent; explicit Edit command still changes mode',matrix=['create/show/load new window while previewing','shared selected point and start permission','other-flow selection and dirty/conflict protection regression'],original_opening_and_budget_unchanged=True,review_requests_new=0,review_budget_remaining=0,product_complete=False))
    s.write(BASE/'opening/git-status.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT))
    def stage(name,target=False):
        out=BASE/name;out.mkdir(parents=True,exist_ok=False)
        hashes={f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and '_wpftmp' not in f.name and not any(q in f.parts for q in ['bin','obj'])}
        write(out/'source-hashes.json',hashes);env=os.environ.copy();env.update(FORMAL_UI_EVIDENCE=str(out/'samples'),NEXUSBGI_DATA_ROOT=str(out/'own-data'),TEMP=str(out/'temp'),TMP=str(out/'temp'))
        Path(env['TEMP']).mkdir();Path(env['FORMAL_UI_EVIDENCE']).mkdir()
        args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
        result={'stage':name};result['build'],_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
        if result['build']==0:
            filt='FullyQualifiedName~Preview_NewPopout' if target else 'FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
            args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=popout.trx','--ResultsDirectory:'+str(out)]
            result['test'],_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
            rows=ET.parse(out/'popout.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
            for outcome in ['Passed','Failed','NotExecuted']:result[outcome]=[r.get('testName') for r in rows if r.get('outcome')==outcome]
        result['source_drift']=[n for n,h in hashes.items() if not (ROOT/n).is_file() or sha((ROOT/n).read_bytes())!=h]
        write(out/'result.json',result);print(json.dumps(dict(stage=name,build=result['build'],test=result.get('test'),passed=len(result.get('Passed',[])),failed=result.get('Failed'),source_drift=result['source_drift']),ensure_ascii=False),flush=True)
        assert result['build']==0 and not result['source_drift'],result
        return result
    if b'public Task Preview_NewPopout_PreservesModeAndSelectedStartPoint()' not in (ROOT/TEST).read_bytes():
        replace(TEST,'    private static Button StartButton(',FACT+'    private static Button StartButton(')
    else: assert (ROOT/TEST).read_bytes().count(FACT.encode())==1
    assert (ROOT/VM).read_bytes().count(NEW.encode())==1
    positive=(ROOT/VM).read_bytes();s.write(BASE/'positive-source'/VM,positive)
    green=stage('green');assert not green['Failed'] and not green['NotExecuted']
    try:
        replace(VM,NEW,OLD);negative=stage('negative',True);assert len(negative['Failed'])==1 and not negative['Passed']
    finally:
        atomic(ROOT/VM,positive);write(BASE/'recovery.json',dict(sha256=sha((ROOT/VM).read_bytes()),same=(ROOT/VM).read_bytes()==positive))
    restored=stage('restored');assert not restored['Failed'] and not restored['NotExecuted']
    write(BASE/'checkpoint.json',dict(prior_red='flow-preview/popout-r2/red',green_passed=len(green['Passed']),negative_failed=1,restored_passed=len(restored['Passed']),source_restored=True,actual_ui=False,independent_review=False,review_requests_new=0,product_complete=False))
    print('POPOUT_PREVIEW_CYCLE_COMPLETE',flush=True)
