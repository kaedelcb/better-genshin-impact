import io
p = r"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"
t = io.open(p, encoding="utf-8").read()
nl = "\r\n"
idx = t.rfind("}")
head, tail = t[:idx], t[idx:]
test = r'''
    [Fact]
    public async Task Resume_SettledParkMarker_DoesNotCreatePreInsertObligationForNewNode()
    {
        // [BO-9；第 2 轮会诊 IMPORTANT-2] 真实 Runner 复现：旧停驻标记**已清偿**（同身份另有完成结果）后，
        // 链尾重建不得再凭该旧标记扫描同轮前插出现（否则会把修订新插入、本不应执行的节点额外执行一次）。
        // 形态：`[A,P,T]` → `[A,X,P,T]`，A@0 完成、P@0 先停驻后完成（旧标记保留）、T@0 待执行。
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "已清偿停驻标记不得引出前插义务",
            Nodes = [DragonNode("A", "配置A"), DragonNode("X", "配置X"),
                DragonNode("P", "配置P"), DragonNode("T", "配置T")],
            Terminal =
            [
                new WorkflowTerminalAction
                {
                    Kind = "terminal.completionAction",
                    Params = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("收尾应只执行一次"),
                    },
                },
            ],
        }).Split('|');
        var workflowRevision = seed[0];
        var workflowId = seed[1];

        var run = _runs.CreateRun(workflowId, workflowRevision);
        run.State = WorkflowRunState.Interrupted;
        run.TriggerConsumed = true;
        run.WorkflowRevision = workflowRevision;
        run.Cursor = new WorkflowNodeCursor { NodeId = "T", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "P", SequenceIndex = 2, Occurrence = 0, LoopIteration = 0, Result = WorkflowRunner.LocalWaitResultWord });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "P", SequenceIndex = 2, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        _runs.Update(run);

        var (runner, boundary, terminal) = MakeRunner();
        var submitted = new List<string>();
        boundary.OnSubmit = req => { submitted.Add(req.Occurrence.NodeId); return Task.CompletedTask; };

        var resumed = await runner.ResumeAsync(run.RunId);

        Assert.Equal(["T"], submitted); // 已清偿的旧停驻标记不得引出 X 的额外执行
        Assert.DoesNotContain(resumed.NodeOutcomes, o => o.NodeId == "X");
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        Assert.Equal(1, terminal.Actions.Count);
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "P", Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "T", Result: "succeeded" }));
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, persisted.State);
        Assert.DoesNotContain(persisted.NodeOutcomes, o => o.NodeId == "X");
    }
'''
body = (nl + test.strip("\n") + nl).replace("\n", nl)
io.open(p, "w", encoding="utf-8", newline="").write(head + body + tail)
print("appended IMPORTANT-2 fixture")
