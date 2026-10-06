from pathlib import Path
import hashlib,json,os,sys,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent/'isolated-data'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
out=BASE/'green3';assert not out.exists()
carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
prior=BASE/'green2'
hashes=json.loads((prior/'source-hashes.json').read_text(encoding='utf-8'))
assert all(hashlib.sha256((ROOT/rel).read_bytes()).hexdigest()==h for rel,h in hashes.items())
assert json.loads((prior/'negative/build-process-result.json').read_text(encoding='utf-8'))['exit_code']==0
with s.Session(ROOT,'final-ui-01a11220-safe-restored-regression') as budget:
 budget.track(BASE);budget.track(carrier)
 for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']:budget.track(ROOT/folder)
 out.mkdir();env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(out/'own-data');env['FORMAL_UI_EVIDENCE']=str(out)
 def write(path,value):s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
 write(out/'source-hashes.json',hashes)
 # Current carrier is the stopped run's built mutant. No live source mutation
 # is performed here. Its original interrupted build is never certified anew.
 write(out/'prior-mutant-products.json',{f.name:hashlib.sha256(f.read_bytes()).hexdigest() for f in carrier.glob('*.dll')})
 negative=out/'negative-artifact';negative.mkdir()
 args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),
     '--TestCaseFilter:FullyQualifiedName~IsolatedDataRootTests.ProductionStores_ShareExplicitRoot',
     '--logger:trx;LogFileName=interaction.trx','--ResultsDirectory:'+str(negative)]
 nc,_,_=p.run(args,cwd=ROOT,env=env,directory=negative,recovery_directory=negative,phase='test',timeout=600)
 tests=ET.parse(negative/'interaction.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
 assert nc!=0 and len(tests)==1 and tests[0].get('outcome')=='Failed'
 assert 'unexpected' in (negative/'interaction.trx').read_text(encoding='utf-8-sig')
 write(out/'negative-result.json',dict(exit_code=nc,failed=1,source_not_mutated=True,
     artifact_oracle_detected=True,original_build_interrupted=True,complete_authenticated_pfp=False))
 print('Existing mutant artifact fails root oracle before writes; source stays restored',flush=True)
 args=['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),
     '--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false',
     '-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
 code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
 if code==0:
  args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),
      '--TestCaseFilter:FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~Formal|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit',
      '--logger:trx;LogFileName=interaction.trx','--ResultsDirectory:'+str(out)]
  code,_,_=p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
 drift=[rel for rel,h in hashes.items() if hashlib.sha256((ROOT/rel).read_bytes()).hexdigest()!=h]
 write(out/'result.json',dict(exit_code=code,source_drift=drift,
     prior_mutant_negative_exit=nc,prior_build_remains_interrupted=True,
     assistant_sha256=hashlib.sha256((carrier/'MultiplayerHoeingAssistant.dll').read_bytes()).hexdigest()))
 print('Restored current source regression',code,'drift',drift,flush=True)
