from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs')
b=p.read_bytes();s=b.decode('utf-8');n='\r\n' if b'\r\n' in b else '\n';s=s.replace('\r\n','\n')
a=s.index('                    // Retained terminal Cancelled records use the real original sending facts.')
z=s.index('                    Assert.Equal(1, port.SendCount);\n                });',a)
s=s[:a]+'''                    var leasePath = Path.Combine(root, "arbitration", "arbitration-lease.json");
                    var originalLease = File.ReadAllBytes(leasePath);
                    var changed = System.Text.Json.Nodes.JsonNode.Parse(originalLease)!;
                    var ops = changed["handoff"]!["operations"]!.AsArray();
                    var selected = ops.Single(o => o!["requestIdentity"]!.GetValue<string>() == node.RequestIdentity)!;
                    if (fault == "missing")
                    {
                        ops.Remove(selected);
                        var observations = changed["handoff"]!["preObservations"]!.AsArray();
                        foreach (var observation in observations.Where(o => o!["submissionIdentity"]!.GetValue<string>() == node.SubmissionIdentity).ToList())
                            observations.Remove(observation);
                    }
                    if (fault == "key") selected["wireSubmitKey"] = "foreign-key";
                    if (fault == "round") { selected["lastSendSeq"] = node.LastSendSeq + 1; selected["submissionIdentity"] = $"sub:{node.RequestIdentity}:{node.LastSendSeq + 1}"; }
                    try
                    {
                        File.WriteAllBytes(leasePath, JsonSerializer.SerializeToUtf8Bytes(changed));
                        Assert.Equal(ArbitrationLeaseStatus.Valid, store.Read().Status);
                        var reconcile = typeof(TaskCenterHost).GetMethod("ReconcileAdmissionTerminalForExplicitStopAsync",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                        var stopped = await (Task<HostActionResult>)reconcile.Invoke(host, [run.RunId, "original terminal probe"])!;
                        Assert.Equal(HostActionStatus.Unavailable, stopped.Status);
                    }
                    finally { File.WriteAllBytes(leasePath, originalLease); }
                    var retry = typeof(TaskCenterHost).GetMethod("ReconcileAdmissionTerminalForExplicitStopAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    Assert.Equal(HostActionStatus.Effective, (await (Task<HostActionResult>)retry.Invoke(host, [run.RunId, "original retry"])!).Status);
                    Assert.Equal(original, JsonSerializer.Serialize(store.Read().File!.Handoff!.Operations.Single(o => o.RequestIdentity == node.RequestIdentity)));
'''+s[z:]
p.write_bytes(s.replace('\n',n).encode('utf-8'))
