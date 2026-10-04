from pathlib import Path
import hashlib,json
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f'
p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';b=p.read_bytes();s=b.decode().replace('\r\n','\n');nl='\r\n' if b.count(b'\r\n') else '\n'
(d/'service-source-before.json').write_text(json.dumps(dict(path=str(p),bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),crlf=b.count(b'\r\n')),indent=2))
start=s.index('    public async Task<AdmissionResult> AdmitRecoveryAsync(');end=s.index('    /// <summary>',start)
seg=s[start:end]
old='''        }
        finally
        {
            _gate.Release();
        }

        if (!register.Success)'''
assert seg.count(old)==1;seg=seg.replace(old,'        if (!register.Success)')
tail='''        return await ReconcileOutcomeAsync(inner, lease, occupy.File!, outcome).ConfigureAwait(false);
    }
'''
assert seg.count(tail)==1;seg=seg.replace(tail,'''        return await ReconcileOutcomeAsync(inner, lease, occupy.File!, outcome).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
''')
# Keep all recovery work in the same async gate as registration. File locks remain scoped to individual mutations.
a=seg.index('        if (!register.Success)');z=seg.index('\n        }\n        finally',a)
seg=seg[:a]+''.join('    '+line+'\n' if line else '\n' for line in seg[a:z].splitlines()).rstrip('\n')+seg[z:]
seg=seg.replace('        await _gate.WaitAsync().ConfigureAwait(false);','''        // Recovery dispatch starts a real Runner before the Sender returns. Keep its successor admission
        // behind the same gate until takeover and Submission close complete, as in DrainRoundAsync.
        await _gate.WaitAsync().ConfigureAwait(false);''')
s=s[:start]+seg+s[end:];new=s.replace('\n',nl).encode();assert len(new)>len(b)-100;p.write_bytes(new)
