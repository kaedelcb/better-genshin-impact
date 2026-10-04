from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'); b=p.read_bytes(); s=b.decode('utf-8').replace('\r\n','\n')
start=s.index('    public async Task HistoricalNodeStop_'); end=s.index('    [Theory]',start); t=s[start:end]
needle='                        Assert.True(TerminalReleaseEvidence.RunSettled(runs.Load(run.RunId)!));\n'
addition='''                        var relation = typeof(TaskCenterHost).GetMethod("OriginalAdmissionMappingsPresent", fields)!;
                        var remaining = JsonSerializer.Deserialize<ArbitrationLeaseFile>(File.ReadAllBytes(leasePath))!.Handoff!.Operations;
                        Assert.False((bool)relation.Invoke(reopened, [legacy, remaining])!, "missing historical node relation must remain unproven");
'''
# Use the store's actual parsed operation list, avoiding a separately guessed wire model type.
addition=addition.replace('JsonSerializer.Deserialize<ArbitrationLeaseFile>(File.ReadAllBytes(leasePath))!.Handoff!.Operations','store.Read().File!.Handoff!.Operations')
assert t.count(needle)==1; t=t.replace(needle,needle+addition); s=s[:start]+t+s[end:]; p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
