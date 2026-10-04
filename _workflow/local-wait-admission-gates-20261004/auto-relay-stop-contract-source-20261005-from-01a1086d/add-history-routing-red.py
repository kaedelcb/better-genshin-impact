import json, hashlib
from pathlib import Path
root = Path.cwd()
base = root / '_workflow/local-wait-admission-gates-20261004/auto-relay-stop-contract-source-20261005-from-01a1086d'
p = root / 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'
b = p.read_bytes()
(base/'routing-test-before.json').write_text(json.dumps(dict(path=str(p.relative_to(root)),sha256=hashlib.sha256(b).hexdigest(),bytes=len(b),lines=len(b.splitlines()),bom=b[:3].hex(),crlf=b'\r\n' in b)),encoding='utf-8')
(base/'routing-test-before.cs.txt').write_bytes(b)
s = b.decode('utf-8-sig').replace('\r\n','\n')
start = s.index('    [Fact]\n    public async Task LegacyTerminalStop_AllNewAnchorsMissingStillRetainsActuallySentResponsibility()')
end = s.index('    [Theory]',start)
test = s[start:end].replace('[Fact]','[Theory]\n    [InlineData(false)]\n    [InlineData(true)]',1).replace('LegacyTerminalStop_AllNewAnchorsMissingStillRetainsActuallySentResponsibility()','HistoricalNodeStop_MissingAnchorsAndNodeCannotUseReopenedHostSwitch(bool removeRoutingProof)')
test = test.replace('legacy-no-anchors-','historical-routing-')
test = test.replace('legacy.TerminalRelease = null;', 'legacy.TerminalRelease = null;')
test = test.replace('lost["handoff"]!["operations"] = new System.Text.Json.Nodes.JsonArray();','''var lostOperations = lost["handoff"]!["operations"]!.AsArray();
                    foreach (var item in lostOperations.Where(o => o!["operationType"]!.GetValue<string>() == "nodeExecution").ToList()) lostOperations.Remove(item);''')
test = test.replace('                    try\n                    {\n                        File.WriteAllText(runPath', '''                    var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var client = ((Func<BgiExternalClient?>)typeof(TaskCenterHost).GetField("_clientAccessor", fields)!.GetValue(host)!)();
                    var seams = (TaskCenterHost.AdmissionTestSeams)typeof(TaskCenterHost).GetField("_admissionSeams", fields)!.GetValue(host)!;
                    await host.ShutdownAsync();
                    var reopened = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                        () => client, log: null, runnerFactory: null, readinessOverride: () => (true, null), localExecutionCapability: () => true,
                        admissionWired: true, admissionSeams: seams, successorAdmissionWired: false);
                    try
                    {
                        File.WriteAllText(runPath''')
test = test.replace('reconcile.Invoke(host,','reconcile.Invoke(reopened,').replace('retry.Invoke(host,','retry.Invoke(reopened,')
test = test.replace('                    Assert.Equal(1, port.SendCount);\n                });','                    Assert.Equal(1, port.SendCount);\n                    await reopened.ShutdownAsync();\n                });')
# Before the durable field exists the two cases exercise the same genuinely old input.
test = test.replace('                    var lost =', '                    _ = removeRoutingProof;\n                    var lost =')
s = s[:end]+test+s[end:]
p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
print('added historical routing red fixture')
