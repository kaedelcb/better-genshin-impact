from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd(); d=r/'_workflow/local-wait-admission-gates-20261004/typed-parent'; p=r/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'
def run(name,args):
    with (d/(name+'.log')).open('wb') as f: c=subprocess.run(args,stdout=f,stderr=subprocess.STDOUT).returncode
    (d/(name+'-exit.txt')).write_text(str(c)); print(name,c,flush=True); return c
assert run('red-build',['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(p),'--nologo'])==0
run('red',['dotnet','vstest',str(p/'MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~TypedAdmissionParentTests','/Logger:trx;LogFileName=red.trx','/ResultsDirectory:'+str(d)])
