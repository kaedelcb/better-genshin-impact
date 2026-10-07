"""C06 normal preview/start entry. One reserved, contained causal cycle; preserve original batch."""
from pathlib import Path
import hashlib,json,os,subprocess,sys,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'flow-preview'
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
VM='MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs'
VIEW='MultiplayerHoeingAssistant/Views/ScheduleListView.xaml'
PAGE='MultiplayerHoeingAssistant/Views/MistletoePage.xaml'
TEST='Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalFlowContextTests.cs'
FILES=[VM,VIEW,PAGE,TEST]
sha=lambda b:hashlib.sha256(b).hexdigest()
TESTS='''    [Fact]
    public Task NormalPreview_UsesTheSelectedFlowAndSharedStartPoint() => OnSta((host, vm, first, second) =>
    {
        var active = vm.Flows.Single(f => f.IsActive);
        InvokePreview(first);
        Assert.Null(vm.Editing);
        Assert.Equal(active.WorkflowId, vm.Previewing!.WorkflowId);
        Assert.Equal(host.LoadFlowSnapshot(active.WorkflowId).Revision, vm.Previewing.Revision);
        var point = Assert.IsType<TaskCenterPanelViewModel.StartPointVm>(vm.SelectedStartPoint);
        foreach (var view in new[] { first, second })
        {
            Layout(view);
            Assert.Same(active, Choice(view).SelectedItem);
            Assert.Same(point, ((ComboBox)view.FindName("StartPointChoice")).SelectedItem);
            Assert.True(StartButton(view).IsEnabled);
        }
        ((ComboBox)second.FindName("StartPointChoice")).SelectedItem = null;
        Assert.Null(vm.SelectedStartPoint);
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        ((ComboBox)second.FindName("StartPointChoice")).SelectedItem = point;
        foreach (var view in new[] { first, second }) { Layout(view); Assert.Same(point, ((ComboBox)view.FindName("StartPointChoice")).SelectedItem); Assert.True(StartButton(view).IsEnabled); }
        Button(second, "编辑").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.NotNull(vm.Editing);
        Assert.Null(vm.Previewing);
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        Assert.Empty(host.ListActiveRuns());
    });

    [Fact]
    public Task NormalPreview_PreservesDirtyAndConflictingDrafts() => OnSta((host, vm, first, second) =>
    {
        var draft = vm.Editing!;
        draft.Nodes[0].ScheduleTimeText = "invalid raw input";
        InvokePreview(first);
        Assert.Same(draft, vm.Editing);
        Assert.Equal("invalid raw input", draft.Nodes[0].ScheduleTimeText);
        Assert.Null(vm.Previewing);
        Assert.True(vm.StatusIsError);
        draft.Nodes[0].ScheduleTimeText = "";
        var snapshot = host.LoadFlowSnapshot(draft.Draft.WorkflowId!);
        snapshot.Document.Name = "external revision";
        host.SaveFlow(snapshot.Document, snapshot.Revision);
        InvokePreview(second);
        Assert.Same(draft, vm.Editing);
        Assert.Null(vm.Previewing);
        Assert.True(vm.StatusIsError);
        foreach (var view in new[] { first, second }) Assert.Equal(draft.Draft.WorkflowId, ((WorkflowListItemVm)Choice(view).SelectedItem).WorkflowId);
    });

    [Fact]
    public Task CandidatePreview_DisablesMissingAndStaleStartPoints() => OnSta((host, vm, first, second) =>
    {
        vm.PreviewFlowCommand.Execute(vm.Flows.Single(f => f.IsActive));
        var old = vm.SelectedStartPoint;
        vm.PreviewFlowCommand.Execute(vm.Flows.Single(f => f.IsCandidate));
        AssertCandidate(vm, first, second);
        Assert.Empty(vm.StartPoints);
        Assert.Null(vm.SelectedStartPoint);
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        vm.SelectedStartPoint = old;
        foreach (var view in new[] { first, second }) { Layout(view); Assert.False(StartButton(view).IsEnabled); }
        vm.StartFromNodeCommand.Execute(null);
        Assert.Empty(host.ListActiveRuns());
    });

    private static Button StartButton(ScheduleListView view) => Assert.IsType<Button>(view.FindName("StartFromPoint"));
    private static void InvokePreview(ScheduleListView view)
    {
        var button = Button(view, "预览/选择起点");
        Assert.True(button.IsEnabled);
        Assert.NotNull(button.Command);
        button.Command.Execute(button.CommandParameter);
    }

'''
NEW_PREVIEW='''   <StackPanel Margin="3,6">
    <StackPanel.Style><Style TargetType="StackPanel"><Setter Property="Visibility" Value="Collapsed"/><Style.Triggers><DataTrigger Binding="{Binding IsPreviewing}" Value="True"><Setter Property="Visibility" Value="Visible"/></DataTrigger></Style.Triggers></Style></StackPanel.Style>
    <TextBlock Text="{Binding Previewing.Title}" Foreground="#E0C479"/>
    <WrapPanel><ComboBox x:Name="StartPointChoice" ItemsSource="{Binding StartPoints}" DisplayMemberPath="Label" SelectedItem="{Binding SelectedStartPoint, Mode=TwoWay}" MinWidth="220" MaxWidth="360" ToolTip="{Binding SelectedStartPoint.Label}"/><Button x:Name="StartFromPoint" Content="从选定节点启动" Command="{Binding StartFromNodeCommand}" IsEnabled="{Binding CanStartFromNode}"/></WrapPanel>
    <TextBlock Text="{Binding Previewing.Notice}" Foreground="#D8BB76" TextWrapping="Wrap"/>
    <ScrollViewer MaxHeight="160" VerticalScrollBarVisibility="Auto"><ItemsControl ItemsSource="{Binding Previewing.Lines}"><ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding}" Foreground="#AFA58F" TextWrapping="Wrap"/></DataTemplate></ItemsControl.ItemTemplate></ItemsControl></ScrollViewer>
   </StackPanel>
'''
CAN='''    public bool CanStartFromNode => !_startResumeInFlight && Previewing is { } preview
        && SelectedStartPoint is { } point && StartPoints.Contains(point)
        && point.WorkflowId == preview.WorkflowId && point.Revision == preview.Revision
        && Flows.Any(flow => flow.WorkflowId == point.WorkflowId && flow.CanStart);
'''
def atomic(path,data):
    temp=path.with_name(path.name+'.own-01a11405.tmp')
    assert not temp.exists()
    with temp.open('xb') as f:f.write(data);f.flush();os.fsync(f.fileno())
    os.replace(temp,path);assert path.read_bytes()==data and not temp.exists()
def replace(rel,old,new):
    path=ROOT/rel;data=path.read_bytes();ending='\r\n' if b'\r\n' in data else '\n'
    a=old.replace('\n',ending).encode();b=new.replace('\n',ending).encode();assert data.count(a)==1,(rel,old[:80])
    atomic(path,data.replace(a,b))
with s.Session(ROOT,'own-root-01a11405-C06-normal-preview-causal') as budget:
    budget.track(BASE)
    carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant';budget.track(carrier)
    for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    before={}
    for rel in FILES:
        data=(ROOT/rel).read_bytes();s.write(BASE/'opening/before'/rel,data)
        before[rel]=dict(sha256=sha(data),bytes=len(data),lines=len(data.splitlines()),crlf=data.count(b'\r\n'),bom=data.startswith(b'\xef\xbb\xbf'))
    write(BASE/'opening/before.json',before)
    s.write(BASE/'opening/git-status.txt',subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT))
    write(BASE/'opening/admission.json',dict(marker='OWN-ROOT-IDENTITY-UI-20261007-FROM-01a113cc',function='C06',gap='normal selection returns to editing; no normal preview/start-point entry',entry='ScheduleListView in main/popout and existing MistletoePage preview card',consequence='users cannot start a normal saved flow from a chosen node',repair='visible normal-preview entry and shared start-point panel in both views; draft/revision guard retained; active/matching preview-point gate with Host revision validation unchanged',matrix=['active selection and both-window preview/point','dirty invalid or revision-conflicting draft retained','candidate/no point/stale point disabled','edit closes start permission','in-flight and revision change recheck'],agents='shared WPF state; one writer, no independent review request',original_opening_and_budget_unchanged=True,review_requests_new=0,review_budget_remaining=0,product_complete=False,next='same-product actual selected local start, parameter save/reference revision, remaining full matrix'))
    def stage(name,targeted=False):
        out=BASE/name;out.mkdir(parents=True,exist_ok=False)
        hashes={f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and '_wpftmp' not in f.name and not any(q in f.parts for q in ['bin','obj'])}
        write(out/'source-hashes.json',hashes)
        env=os.environ.copy();env.update(FORMAL_UI_EVIDENCE=str(out/'samples'),NEXUSBGI_DATA_ROOT=str(out/'own-data'),TEMP=str(out/'temp'),TMP=str(out/'temp'))
        Path(env['TEMP']).mkdir();Path(env['FORMAL_UI_EVIDENCE']).mkdir()
        args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
        result={'stage':name};result['build'],_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
        if result['build']==0:
            filt='FullyQualifiedName~NormalPreview|FullyQualifiedName~CandidatePreview' if targeted else 'FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
            args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=preview.trx','--ResultsDirectory:'+str(out)]
            result['test'],_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
            rows=ET.parse(out/'preview.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
            for outcome in ['Passed','Failed','NotExecuted']:result[outcome]=[r.get('testName') for r in rows if r.get('outcome')==outcome]
        result['source_drift']=[rel for rel,h in hashes.items() if not (ROOT/rel).is_file() or sha((ROOT/rel).read_bytes())!=h]
        write(out/'result.json',result)
        print(json.dumps(dict(stage=name,build=result['build'],test=result.get('test'),passed=len(result.get('Passed',[])),failed=result.get('Failed'),source_drift=result['source_drift']),ensure_ascii=False),flush=True)
        assert result['build']==0 and not result['source_drift'],result
        return result
    replace(TEST,'    private static void AssertCandidate(',TESTS+'    private static void AssertCandidate(')
    red=stage('red',True);assert len(red['Failed'])==3 and not red['Passed']
    replace(VIEW,'   <Button Content="＋新建"', '   <Button Content="预览/选择起点" Command="{Binding PreviewFlowCommand}" CommandParameter="{Binding SelectedItem, ElementName=FlowChoice}" IsEnabled="{Binding SelectedItem.CanPreview, ElementName=FlowChoice, FallbackValue=False, TargetNullValue=False}"/>\n   <Button Content="＋新建"')
    replace(VIEW,'  </StackPanel>\n  <WrapPanel Grid.Row="1"',NEW_PREVIEW+'  </StackPanel>\n  <WrapPanel Grid.Row="1"')
    replace(PAGE,'Command="{Binding TaskCenter.StartFromNodeCommand}" Margin=', 'Command="{Binding TaskCenter.StartFromNodeCommand}" IsEnabled="{Binding TaskCenter.CanStartFromNode}" Margin=')
    replace(VM,'    public bool CanEdit => IsActive;','    public bool CanEdit => IsActive;\n    public bool CanPreview => !IsQuarantined;')
    replace(VM,'OnPropertyChanged(nameof(IsPreviewing)); } }','OnPropertyChanged(nameof(IsPreviewing)); OnPropertyChanged(nameof(CanStartFromNode)); } }')
    replace(VM,'public StartPointVm? SelectedStartPoint { get => _selectedStartPoint; set => SetProperty(ref _selectedStartPoint, value); }','public StartPointVm? SelectedStartPoint { get => _selectedStartPoint; set { SetProperty(ref _selectedStartPoint, value); OnPropertyChanged(nameof(CanStartFromNode)); } }\n\n'+CAN.rstrip())
    replace(VM,'if (SelectedStartPoint is not { } point || _startResumeInFlight) return;','if (!CanStartFromNode || SelectedStartPoint is not { } point) return;')
    # Every existing shared start/resume flag transition notifies this derived button state.
    data=(ROOT/VM).read_bytes()
    for val in ['true','false']:
        old=('_startResumeInFlight = '+val+';').encode();assert data.count(old)==3
        data=data.replace(old,old+b' OnPropertyChanged(nameof(CanStartFromNode));')
    atomic(ROOT/VM,data)
    replace(VM,'            _refreshing = false;','            _refreshing = false;\n            OnPropertyChanged(nameof(CanStartFromNode));')
    positive={rel:(ROOT/rel).read_bytes() for rel in FILES}
    for rel,data in positive.items():s.write(BASE/'positive-source'/rel,data)
    green=stage('green');assert not green['Failed'] and not green['NotExecuted']
    try:
        replace(VIEW,'Content="预览/选择起点" Command="{Binding PreviewFlowCommand}"','Content="预览/选择起点"')
        replace(VM,CAN,'    public bool CanStartFromNode => true;\n')
        negative=stage('negative',True);assert len(negative['Failed'])==3 and not negative['Passed']
    finally:
        for rel in [VM,VIEW]:atomic(ROOT/rel,positive[rel])
        write(BASE/'recovery.json',{rel:dict(sha256=sha((ROOT/rel).read_bytes()),same=(ROOT/rel).read_bytes()==data) for rel,data in positive.items()})
    restored=stage('restored');assert not restored['Failed'] and not restored['NotExecuted']
    after={rel:dict(sha256=sha((ROOT/rel).read_bytes()),bytes=(ROOT/rel).stat().st_size,lines=len((ROOT/rel).read_bytes().splitlines()),crlf=(ROOT/rel).read_bytes().count(b'\r\n'),bom=(ROOT/rel).read_bytes().startswith(b'\xef\xbb\xbf')) for rel in FILES}
    write(BASE/'after.json',after)
    s.write(BASE/'diff-stat.txt',subprocess.check_output(['git','diff','--stat','--',*FILES],cwd=ROOT))
    write(BASE/'checkpoint.json',dict(source_restored=True,red_failed=3,green_passed=len(green['Passed']),mutant_failed=3,restored_passed=len(restored['Passed']),review_requests_new=0,independent_review=False,actual_ui=False,product_complete=False))
    print('PREVIEW_CYCLE_COMPLETE',flush=True)
