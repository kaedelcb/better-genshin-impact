from pathlib import Path
import json,hashlib
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-c17-effects-20261005-from-01a1093a'
paths=['MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.csproj','BetterGenshinImpact/Service/ExternalInterface/ExternalInterfacePrerequisitePlane.cs','BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceProtocol.cs','MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs','MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowTerminalExecutor.cs','MultiplayerHoeingAssistant/Services/TaskCenter/BgiWorkflowObservationPersistence.cs']
def snap():
 return [dict(path=p,bytes=(b:=(root/p).read_bytes()).__len__(),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b) for p in paths]
def edit(path,old,new):
 p=root/path;b=p.read_bytes();nl='\r\n' if b'\r\n' in b else '\n';s=b.decode('utf-8');old=old.replace('\n',nl);new=new.replace('\n',nl)
 assert s.count(old)==1,(path,s.count(old),old[:80]);p.write_bytes(s.replace(old,new).encode('utf-8'))
if not (base/'source-before.json').exists(): (base/'source-before.json').write_text(json.dumps(snap(),indent=2),encoding='utf-8')
edit(paths[0],'</Project>','  <ItemGroup>\n    <Compile Include="..\\BetterGenshinImpact\\Service\\ExternalInterface\\TerminalEffectJournal.cs" Link="Services\\TaskCenter\\TerminalEffectJournal.cs" />\n  </ItemGroup>\n</Project>')
