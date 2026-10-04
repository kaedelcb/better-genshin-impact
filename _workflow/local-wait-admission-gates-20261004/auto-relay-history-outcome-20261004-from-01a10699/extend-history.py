from pathlib import Path
import json,hashlib
r=Path.cwd();d=Path(__file__).parent;p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs';b=p.read_bytes();(d/'successor-tests.pre-extend').write_bytes(b);s=b.decode('utf-8').replace('\r\n','\n')
needle='    [InlineData("history-bound-duplicate-identity")]'
fields=['node','occurrence','loop','attempt','raw','result','missing-identity','duplicate-history']
multi=['valid','publish','settle','active','archive','archive-conflict']
assert s.count(needle)==1;s=s.replace(needle,needle+''.join('\n    [InlineData("history-bound-'+v+'")]' for v in fields)+''.join('\n    [InlineData("history-multi3-'+v+'")]' for v in multi))
needle='            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true, nodeIds: scenario == "history-multiple" ? ["n-1", "n-2"] : ["n-1"],\n                configurePort: p => { originalPort = p; p.ThrowOnSend = scenario != "history-multiple"; },\n                onBeforeSend: (_, node) => { if (scenario == "history-multiple" && node == 2) originalPort!.ThrowOnSend = true; },'
assert s.count(needle)==1;s=s.replace(needle,'''            var multi3 = scenario.StartsWith("history-multi3-", StringComparison.Ordinal);
            var nodeCount = multi3 ? 3 : scenario == "history-multiple" ? 2 : 1;
            var probe = await ProbeNodeSubmitRoutingAsync(root, successorWired: true,
                nodeIds: Enumerable.Range(1, nodeCount).Select(n => "n-" + n).ToArray(),
                configurePort: p => { originalPort = p; p.ThrowOnSend = nodeCount == 1; },
                onBeforeSend: (_, node) => { if (node == nodeCount) originalPort!.ThrowOnSend = true; },''')
s=s.replace('scenario == "history-multiple" ? 2 : 1,','nodeCount,')
needle='                        var invalidBoundHistory = scenario is "history-bound-duplicate-key" or "history-bound-duplicate-identity";'
assert s.count(needle)==1;s=s.replace(needle,'                        var invalidBoundHistory = scenario.StartsWith("history-bound-", StringComparison.Ordinal) && scenario != "history-bound-valid";')
needle='                            if (invalidBoundHistory)\n                            {'
assert s.count(needle)==1;s=s.replace(needle,'                            if (scenario is "history-bound-duplicate-key" or "history-bound-duplicate-identity")\n                            {')
needle='                                run.NodeOutcomes.Add(conflict);\n                            }'
assert s.count(needle)==1;s=s.replace(needle,needle+'''
                            switch (scenario)
                            {
                                case "history-bound-node": outcome.NodeId = "wrong-original-node"; break;
                                case "history-bound-occurrence": outcome.Occurrence++; break;
                                case "history-bound-loop": outcome.LoopIteration++; break;
                                case "history-bound-attempt": outcome.Attempt++; break;
                                case "history-bound-raw": outcome.RawTerminal = "succeeded"; break;
                                case "history-bound-result": outcome.Result = "succeeded"; break;
                                case "history-bound-missing-identity": outcome.AcceptedSendIdentity = null; break;
                                case "history-bound-duplicate-history": run.SubmissionHistory.Add(JsonSerializer.Deserialize<WorkflowSubmission>(JsonSerializer.Serialize(sub))!); break;
                            }
''')
needle='                        port.OriginalStatusJob = port.OriginalReconcileSnapshot.Jobs[0];'
assert s.count(needle)==1;s=s.replace(needle,needle+'''
                        if (multi3)
                        {
                            Assert.Equal(3, run.SubmissionHistory.Count);
                            Assert.Equal(3, run.SubmissionHistory.Select(s => s.Key).Distinct().Count());
                            Assert.Equal(3, run.SubmissionHistory.Select(s => s.SendPermit!.OriginalSendIdentity).Distinct().Count());
                            Assert.Equal(2, run.NodeReleaseSeals.Count);
                            if (scenario == "history-multi3-active")
                            {
                                var active = JsonSerializer.Deserialize<BgiJobInfo>(JsonSerializer.Serialize(port.OriginalStatusJob))!;
                                active.State = "running"; active.ExecutionExitConfirmed = false; active.ExecutionExitDisposition = null;
                                port.OriginalReconcileSnapshot.Jobs = [active];
                            }
                        }
''')
s=s.replace('if (scenario == "history-publish") hostRuns!', 'if (scenario is "history-publish" or "history-multi3-publish") hostRuns!')
s=s.replace('if (scenario == "history-settle") originalSeams.', 'if (scenario is "history-settle" or "history-multi3-settle") originalSeams.')
s=s.replace('if (scenario is "history-settle" or "history-publish")', 'if (scenario is "history-settle" or "history-publish" or "history-multi3-settle" or "history-multi3-publish")')
s=s.replace('scenario == "history-settle" ? 1 : 0','scenario is "history-settle" or "history-multi3-settle" ? 1 : 0')
s=s.replace('if (scenario.StartsWith("history-archive", StringComparison.Ordinal))','if (scenario.StartsWith("history-archive", StringComparison.Ordinal) || scenario.StartsWith("history-multi3-archive", StringComparison.Ordinal))')
s=s.replace('scenario == "history-archive-conflict" && op.OperationType','scenario is "history-archive-conflict" or "history-multi3-archive-conflict" && op.OperationType')
s=s.replace('scenario == "history-archive" ? HostActionStatus.Effective','scenario is "history-archive" or "history-multi3-archive" ? HostActionStatus.Effective')
needle='            Assert.Equal(scenario is "history-bound-duplicate-key" or "history-bound-duplicate-identity" ? WorkflowRunState.Unknown'
assert s.count(needle)==1;s=s.replace(needle,'            Assert.Equal(scenario.StartsWith("history-bound-", StringComparison.Ordinal) && scenario != "history-bound-valid" ? WorkflowRunState.Unknown')
# Direct bound-node outcome must retain its business result as well as raw terminal.
out=s.replace('\n','\r\n').encode('utf-8');p.write_bytes(out);(d/'extended-test-edit.json').write_text(json.dumps(dict(before=hashlib.sha256(b).hexdigest(),after=hashlib.sha256(out).hexdigest(),bytes_before=len(b),bytes_after=len(out),lines_before=len(b.splitlines()),lines_after=len(out.splitlines())),indent=2),encoding='utf-8')
print('extended fields',fields,'multi3',multi)
