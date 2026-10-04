from pathlib import Path
import subprocess,json,hashlib
r=Path.cwd();p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs'
b=p.read_bytes();s=b.decode('utf-8-sig').replace('\r\n','\n');old=subprocess.check_output(['git','show','HEAD:MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs']).decode('utf-8-sig').replace('\r\n','\n')
start=old.index('    private static string? ResolveParentRequestIdentity(');end=old.index('\n    /// <summary>',start)
resolver=old[start:end].replace('ResolveParentRequestIdentity','ResolveComponentParentRequestIdentity')
start=old.index('    private static bool IsOwnParentOccupationExempt(');end=old.index('\n    /// <summary>',start)
legacy=old[start:end].replace('IsOwnParentOccupationExempt','IsComponentOwnParentOccupationExempt')
s=s.replace('                        ParentRequestIdentity = parentSource?.RequestIdentity,\n                        ParentSource = parentSource,','                        ParentRequestIdentity = frz.Candidate.Namespace == "successor" ? parentSource?.RequestIdentity\n                            : ResolveComponentParentRequestIdentity(file.Handoff, frz.RunBinding, frz.Candidate.WorkflowId),\n                        ParentSource = frz.Candidate.Namespace == "successor" ? parentSource : null,')
anchor='        // ③ 全局未决发送槽为空（其他节点在飞/父未结清 ⇒ 不豁免）'
idx=s.index(anchor,s.index('private bool IsOwnParentOccupationExempt'))
s=s[:idx]+'''        if (op.Candidate?.Namespace != "successor" && op.ParentSource is null)
            return IsComponentOwnParentOccupationExempt(request, facts, handoff);
'''+s[idx:]
s+='\n' if not s.endswith('\n') else ''
idx=s.index('    private AdmissionParentSource? ResolveTypedParent(');s=s[:idx]+resolver+'\n\n'+legacy+'\n\n'+s[idx:]
assert len(s)>len(b.decode('utf-8-sig').replace('\r\n','\n'))*.95;p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
