from pathlib import Path
r=Path.cwd();p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs';s=p.read_bytes().decode('utf-8');s=s.replace('    [InlineData(false, "old-acceptance")]\r\n','').replace('    [InlineData(true, "old-acceptance")]\r\n','')
start=s.index('                                var file = store.Read().File!;',s.index('OriginalMultiRound_HostExplicitRetriesUseOriginalFrozenRequest'))
end=s.index('                            var denied = facade.RetryAsync',start)
replacement=r'''                                var originalFile = store.Read().File!;
                                var originalBytes = JsonSerializer.Serialize(originalFile);
                                var lease = originalFile.Lease!;
                                var deniedWrite = store.MutateHandoffLatest(lease.LeaseId, lease.OwnerEpoch, file =>
                                {
                                    var damaged = file.Handoff!.Operations.Single(o => o.RequestIdentity == requestId);
                                    if (scenario == "missing-rejection") damaged.RejectedSendRounds!.RemoveAt(0);
                                    if (scenario == "duplicate-rejection") damaged.RejectedSendRounds!.Add(damaged.RejectedSendRounds[0]);
                                    return null;
                                });
                                Assert.False(deniedWrite.Success);
                                Assert.Equal(originalBytes, JsonSerializer.Serialize(store.Read().File));
                                Assert.Equal(2, originalPort.SendCount);
                                return; // This proves immutable write rejection, not a corrupted-file Host retry.
                            }
'''.replace('\n','\r\n')
s=s[:start]+replacement+s[end:];p.write_bytes(s.encode('utf-8'))
p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs';s=p.read_bytes().decode('utf-8')
s=s.replace('Dictionary<string, AdmissionRequest> _originalRetryContexts','Dictionary<string, PendingAdmission> _originalRetryContexts')
s=s.replace('_originalRetryContexts.TryAdd(request.RequestIdentity, request);','_originalRetryContexts.TryAdd(request.RequestIdentity, pending);')
s=s.replace('out var original)\r\n                                    && original.OperationType','out var cached)\r\n                                    && cached.CapturedLeaseId == captured.LeaseId && cached.CapturedOwnerEpoch == captured.OwnerEpoch\r\n                                    && cached.Request is { } original\r\n                                    && original.OperationType')
p.write_bytes(s.encode('utf-8'))
