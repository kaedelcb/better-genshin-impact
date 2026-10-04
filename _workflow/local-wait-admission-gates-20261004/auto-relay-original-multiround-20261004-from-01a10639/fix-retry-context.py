from pathlib import Path
import hashlib,json
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-original-multiround-20261004-from-01a10639'
p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';b=p.read_bytes();s=b.decode('utf-8')
def replace(old,new):
 global s
 assert s.count(old)==1,old[:90]
 s=s.replace(old,new)
replace('    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);', '''    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);
    // Same-instance explicit retries may reuse only the original trusted adapter's frozen request.
    // This cache grants no admission authority and is never reconstructed from persisted definitions.
    private readonly Dictionary<string, AdmissionRequest> _originalRetryContexts = new(StringComparer.Ordinal);'''.replace('\n','\r\n'))
replace('            _queue.Add(pending);', '''            if (request.OperationType == OperationType.NodeExecution && request.ProcessLocalContext is not null)
                _originalRetryContexts.TryAdd(request.RequestIdentity, request);
            _queue.Add(pending);'''.replace('\n','\r\n'))
replace('                            captured = read.File.Lease;\r\n                            lock (_queueLock)\r\n                            {\r\n                                if (!_inflight.Add(requestIdentity))', '''                            captured = read.File.Lease;
                            lock (_queueLock)
                            {
                                if (_originalRetryContexts.TryGetValue(requestIdentity, out var original)
                                    && original.OperationType == op.OperationType
                                    && original.RunBinding == op.RunBinding && original.WireSubmitKey == op.WireSubmitKey
                                    && original.CursorRef == op.CursorRef && original.CursorRevision == op.CursorRevision
                                    && BuildStableIdentity(original.Candidate) == BuildStableIdentity(candidate)
                                    && original.Candidate.PayloadFingerprint == candidate.PayloadFingerprint)
                                {
                                    retryRequest.ProcessLocalContext = original.ProcessLocalContext;
                                    retryRequest.CallerToken = original.CallerToken;
                                    retryRequest.ParentSource = original.ParentSource;
                                }
                                if (!_inflight.Add(requestIdentity))'''.replace('\n','\r\n'))
p.write_bytes(s.encode('utf-8'));(d/'retry-context-edit.json').write_text(json.dumps(dict(path=str(p.relative_to(r)),before_bytes=len(b),after_bytes=p.stat().st_size,before_sha=hashlib.sha256(b).hexdigest(),after_sha=hashlib.sha256(p.read_bytes()).hexdigest()),indent=2),encoding='utf-8')
p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs';b=p.read_bytes();s=b.decode('utf-8');assert s.count('            Assert.Empty(failures);')==1;s=s.replace('            Assert.Empty(failures);','            Assert.True(failures.Count == 0, string.Join("\\n", failures));');p.write_bytes(s.encode('utf-8'))
