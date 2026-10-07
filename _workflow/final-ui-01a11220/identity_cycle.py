"""C01 visible-identity red/fix/green/cause/restore and exact own-product refresh."""
from pathlib import Path
import hashlib,json,os,sys,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'flow-identity'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
sha=lambda data:hashlib.sha256(data).hexdigest()
vm='MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs'
xaml='MultiplayerHoeingAssistant/Views/ScheduleListView.xaml'
carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
with s.Session(ROOT,'own-root-01a113cc-identity-red-green-causal-refresh') as budget:
    budget.track(BASE);budget.track(carrier)
    for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
    def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    def run(stage):
        out=BASE/stage;out.mkdir(parents=True,exist_ok=False)
        hashes={f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
        write(out/'source-hashes.json',hashes)
        env=os.environ.copy();env['FORMAL_UI_EVIDENCE']=str(out/'samples');env['NEXUSBGI_DATA_ROOT']=str(out/'own-data');env['TEMP']=env['TMP']=str(out/'temp')
        Path(env['FORMAL_UI_EVIDENCE']).mkdir();Path(env['TEMP']).mkdir()
        print('IDENTITY_STAGE',stage,'Rebuild',flush=True)
        args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
        build,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
        assert build==0,('Rebuild failed',stage,build)
        filt='FullyQualifiedName~ImportedCandidate_RealActivation_RefreshesPreviewAndRenderedLabel' if stage in ['red','negative'] else 'FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
        args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=identity.trx','--ResultsDirectory:'+str(out)]
        code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
        rows=ET.parse(out/'identity.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
        result=dict(stage=stage,build=build,test=code,source_drift=[rel for rel,h in hashes.items() if sha((ROOT/rel).read_bytes())!=h])
        for kind in ['Passed','Failed','NotExecuted']:result[kind]=[r.get('testName') for r in rows if r.get('outcome')==kind]
        write(out/'result.json',result)
        assert not result['source_drift'] and not result['NotExecuted']
        for job in ['build','test']:assert json.loads((out/(job+'-tree-terminal.json')).read_text(encoding='utf-8'))['active_processes']==0
        print(json.dumps(dict(stage=stage,build=build,test=code,passed=len(result['Passed']),failed=result['Failed'],skipped=result['NotExecuted'],source_drift=[]),ensure_ascii=False),flush=True)
        return result
    originals={rel:(ROOT/rel).read_bytes() for rel in [vm,xaml]}
    for rel,raw in originals.items():assert raw==(BASE/'opening/before'/rel).read_bytes()
    red=run('red');assert red['test']==1 and len(red['Failed'])==1 and not red['Passed']
    def replace(rel,old,new):
        path=ROOT/rel;raw=path.read_bytes();nl='\r\n' if b'\r\n' in raw else '\n'
        old=old.replace('\n',nl).encode();new=new.replace('\n',nl).encode();assert raw.count(old)==1,rel
        patched=raw.replace(old,new);assert len(patched)>=len(raw)
        path.write_bytes(patched)
    replace(vm,'    public string RevisionShort =>', '    public string ChoiceToolTip => $"{Name}\\n流程 ID：{WorkflowId}\\n修订：{_entry.Revision}\\n{StateBadge}";\n    public string RevisionShort =>')
    replace(xaml,'<ComboBox x:Name="FlowChoice" ItemsSource="{Binding Flows}" MinWidth="160" MaxWidth="240" SelectionChanged="FlowChanged"><ComboBox.ItemTemplate><DataTemplate><TextBlock Text="{Binding ChoiceLabel}"/></DataTemplate></ComboBox.ItemTemplate></ComboBox>',
        '<ComboBox x:Name="FlowChoice" ItemsSource="{Binding Flows}" MinWidth="160" MaxWidth="240" ToolTip="{Binding SelectedItem.ChoiceToolTip, RelativeSource={RelativeSource Self}}" SelectionChanged="FlowChanged"><ComboBox.ItemTemplate><DataTemplate><StackPanel><TextBlock Text="{Binding ChoiceLabel}" TextTrimming="CharacterEllipsis"/><TextBlock Text="{Binding WorkflowId}" FontSize="10" Foreground="#A4977E"/></StackPanel></DataTemplate></ComboBox.ItemTemplate></ComboBox>')
    final={rel:(ROOT/rel).read_bytes() for rel in [vm,xaml]}
    write(BASE/'source-patch.json',{rel:dict(before_sha256=sha(originals[rel]),after_sha256=sha(raw),before_bytes=len(originals[rel]),after_bytes=len(raw),lines=len(raw.splitlines()),crlf=raw.count(b'\r\n')) for rel,raw in final.items()})
    green=run('green');assert green['test']==0 and not green['Failed'] and len(green['Passed'])>=83
    for rel,raw in final.items():s.write(BASE/'causal-source'/Path(rel).name,raw)
    try:
        raw=final[xaml];old=b'<TextBlock Text="{Binding WorkflowId}" FontSize="10" Foreground="#A4977E"/>'
        assert raw.count(old)==1
        mutant=raw.replace(old,b'<TextBlock Text="" FontSize="10" Foreground="#A4977E"/>')
        (ROOT/xaml).write_bytes(mutant)
        write(BASE/'mutation.json',dict(target=xaml,original_sha256=sha(raw),mutant_sha256=sha(mutant),change='remove visible stable identity while leaving tooltip intact'))
        negative=run('negative');assert negative['test']==1 and len(negative['Failed'])==1 and not negative['Passed']
    finally:
        (ROOT/xaml).write_bytes(final[xaml])
        assert all((ROOT/rel).read_bytes()==raw for rel,raw in final.items())
        write(BASE/'source-restored.json',{rel:sha(raw) for rel,raw in final.items()})
    restored=run('restored');assert restored['test']==0 and sorted(restored['Passed'])==sorted(green['Passed']) and not restored['Failed']
    product=ROOT/'_workflow/runtime-unified-01a10e1b/product';runtime=BASE.parent/'own-runtime'
    prior=json.loads((runtime/'refresh-context/result.json').read_text(encoding='utf-8'))
    assert sha((product/'BetterGI.dll').read_bytes())==prior['bgi_sha256'] and sha((product/'BetterGI.exe').read_bytes())==prior['bgi_exe_sha256']
    assert all(sha((product/m['path']).read_bytes())==m['sha256'] for m in prior['updated'])
    out=runtime/'refresh-identity';out.mkdir(exist_ok=False);budget.track(out);budget.track(product/'Tools/MultiplayerHoeingAssistant');budget.track(product/'User')
    before={f.relative_to(product/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(product/'User')};write(out/'private/product-user-before.json',before)
    updated=[]
    for name in ['MultiplayerHoeingAssistant.dll','MultiplayerHoeingAssistant.exe','MultiplayerHoeingAssistant.pdb','MultiplayerHoeingAssistant.deps.json','MultiplayerHoeingAssistant.runtimeconfig.json']:
        target=product/'Tools/MultiplayerHoeingAssistant'/name;s.write(out/'before'/name,target.read_bytes());s.write(target,(carrier/name).read_bytes(),mode='wb')
        assert target.read_bytes()==(carrier/name).read_bytes();updated.append(dict(path=target.relative_to(product).as_posix(),sha256=sha(target.read_bytes())))
    after={f.relative_to(product/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(product/'User')};assert before==after
    write(out/'result.json',dict(marker='OWN-ROOT-DEV-MATRIX-20261007-FROM-01a11380',updated=updated,bgi_sha256=prior['bgi_sha256'],bgi_exe_sha256=prior['bgi_exe_sha256'],product_user_changed=[],user_files=len(before),source_hashes=str(BASE/'restored/source-hashes.json'),review_requests_new=0,independent_review=False,product_complete=False))
    print('IDENTITY_FINAL restored, eight Jobs terminal, five modules refreshed, BGI/User unchanged',flush=True)
