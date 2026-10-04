from pathlib import Path
import json,hashlib
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs');b=p.read_bytes();Path('_workflow/local-wait-admission-gates-20261004/typed-parent/host-before-comment-correction.json').write_text(json.dumps(dict(bytes=len(b),sha256=hashlib.sha256(b).hexdigest())));s=b.decode().replace('\r\n','\n')
def summary(method,text):
 global s
 pos=s.index(method);start=s.rfind('    /// <summary>',0,pos);end=s.index('    /// </summary>',start)+len('    /// </summary>');s=s[:start]+text+s[end:]
summary('    private AdmissionParentSource? TryGetAdmissionParent','''    /// <summary>
    /// 两类来源返回精确类型化原父引用：面板来源读取租约唯一 FlowRegistration；
    /// 移交来源读取受理时同 CreateRun 保存的原 HandoffIdentity。旧缺锚、冲突或读取失败不补造。
    /// </summary>''')
summary('    internal static AdmissionParentSource? ResolveAdmissionParent','''    /// <summary>
    /// 面板来源须唯一、同 run/workflow 且 Scope 规范；与移交原来源共存时拒绝选择。
    /// 移交来源须具有支持版本、原 run/workflow/Scope 和唯一且完整未改的首个受理绑定。
    /// 后续追加的 resume 绑定不替换原来源；旧 Scope+列表不能回填类型化授权。
    /// </summary>''')
s=s.replace('            return op.Candidate?.WorkflowId == workflowId && IsCanonicalAdmissionScope(scope0) && !string.IsNullOrEmpty(op.RequestIdentity)\n                ? run?.AdmissionParentSource is not null ? null\n                    : new AdmissionParentSource(1, AdmissionParentKind.PanelFlowRegistration, runId, workflowId, scope0!, op.RequestIdentity)\n                : null;', '            if (run?.AdmissionParentSource is not null) return null;\n            return op.Candidate?.WorkflowId == workflowId && IsCanonicalAdmissionScope(scope0) && !string.IsNullOrEmpty(op.RequestIdentity)\n                ? new AdmissionParentSource(1, AdmissionParentKind.PanelFlowRegistration, runId, workflowId, scope0!, op.RequestIdentity)\n                : null;')
assert len(s)>len(b.decode().replace('\r\n','\n'))*.98;p.write_bytes(s.replace('\n','\r\n').encode())
