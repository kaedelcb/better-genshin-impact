import json,hashlib
from pathlib import Path
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs')
b=p.read_bytes()
(Path(__file__).parent/'observer-product-before.json').write_text(json.dumps(dict(path=str(p),bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b)),encoding='utf-8')
s=b.decode('utf-8').replace('\r\n','\n')
changes=[('        var terminalRunId = entry.RunId;\n','        var terminalRunId = entry.RunId;\n        var reconcileObservedTerminal = false;\n'),('            terminalRunId = run.RunId;\n','            terminalRunId = run.RunId;\n            reconcileObservedTerminal = run.IsTerminal;\n'),('            ConvergeDriveException(entry, ex);\n','            ConvergeDriveException(entry, ex);\n            try { reconcileObservedTerminal = terminalRunId is not null && _runs.Load(terminalRunId)?.IsTerminal == true; }\n            catch (Exception) { /* Preserve the unreadable record for explicit recovery. */ }\n'),('                await MarkAdmissionTerminalIfAnyAsync(terminalRunId).ConfigureAwait(false);','                // A parked/nonterminal drive does not own a later explicit Stop transaction.\n                if (reconcileObservedTerminal)\n                    await MarkAdmissionTerminalIfAnyAsync(terminalRunId).ConfigureAwait(false);')]
for old,new in changes:
    assert s.count(old)==1,old
    s=s.replace(old,new)
p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
