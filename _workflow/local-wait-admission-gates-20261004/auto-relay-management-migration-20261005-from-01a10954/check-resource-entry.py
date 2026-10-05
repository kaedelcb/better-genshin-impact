from pathlib import Path
import sys,os,json,hashlib,time
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954'
stage=sys.argv[1];ev=base/stage
dotnet='C:/Program Files/dotnet/dotnet.exe'
projects=[('assistant','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','MultiplayerHoeingAssistant.UnitTest.dll','FullyQualifiedName~WorkflowResourceEditorTests|FullyQualifiedName~TaskCenterPanelViewModelTests'),('bgi','Test/BetterGenshinImpact.UnitTest/BetterGenshinImpact.UnitTest.csproj','BetterGenshinImpact.UnitTest.dll','FullyQualifiedName~ExternalResourceEditorTests')]
if stage.startswith('migration-'):
    projects=[('assistant','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','MultiplayerHoeingAssistant.UnitTest.dll','FullyQualifiedName~WorkflowMigrationConsumerTests|FullyQualifiedName~LegacyMigrationCandidateServiceTests|FullyQualifiedName~R56ReferenceActivationWiringTests.Added')]
if len(sys.argv)>2:projects=[x for x in projects if x[0]==sys.argv[2]]
inputs=[p for directory in ['BetterGenshinImpact/Service/ExternalInterface','MultiplayerHoeingAssistant/Services/TaskCenter','MultiplayerHoeingAssistant/ViewModels','Test/BetterGenshinImpact.UnitTest/ServiceTests/Instance','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter'] for p in (root/directory).glob('*.cs')]
def hashes():return {str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs}
before=hashes()
class BuildScanSession(storage.Session):
    def check(self,*args,**kwargs):
        # MSBuild会替换复制中的资源。仅对枚举后消失的文件重做完整读数；
        # 不将失败计为0、不减少额度检查、不忽略权限/链接/超限异常。
        for attempt in range(5):
            try:return super().check(*args,**kwargs)
            except FileNotFoundError:
                if attempt==4:raise
                time.sleep(.1)
with BuildScanSession(root,'delivery-resource-controlled-build') as budget:
    budget.track(ev);budget.check(location=ev);ev.mkdir(exist_ok=False)
    storage.write(ev/'input-before.json',json.dumps(before,indent=2).encode())
    code=0
    for name,project,dll,filter in projects:
        # 同一预算域/卷；避免Windows深层资源的长路径，不修改额度或绕过监测。
        out=root/'_workflow/runtime-01a1097f'/stage/name;budget.track(out);budget.check(location=out)
        out.mkdir(parents=True,exist_ok=False)
        for phase,args in [('build',[dotnet,'build',str(root/project),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-p:RestoreLockedMode=true','-t:Rebuild','-maxcpucount:1','-o',str(out)]),('test',[dotnet,'vstest',str(out/dll),'--TestCaseFilter:'+filter,'--logger:trx;LogFileName='+name+'.trx','--ResultsDirectory:'+str(ev)])]:
            phase=name+'-'+phase
            exit_code,stdout,stderr=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=ev,recovery_directory=base,phase=phase,timeout=1800)
            storage.write(ev/(phase+'-argv.json'),json.dumps({'argv':args,'exit_code':exit_code,'source':'ordinary controlled execution; no certification receipt'},indent=2).encode())
            print(phase,exit_code,flush=True)
            code=code or exit_code
            if exit_code:break
        storage.write(ev/(name+'-products.json'),json.dumps([dict(path=str(p),bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in out.iterdir() if p.is_file()],indent=2).encode())
        if code:break
    after=hashes();storage.write(ev/'input-after.json',json.dumps(after,indent=2).encode());assert before==after,'input drift'
    budget.check(measure=True)
sys.exit(code)
