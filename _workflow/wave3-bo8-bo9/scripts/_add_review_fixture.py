import io
p = r"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"
t = io.open(p, encoding="utf-8").read()
nl = "\r\n"
idx = t.rfind("}")
head, tail = t[:idx], t[idx:]
test = r'''
    [Fact]
    public async Task Resume_LooplessPlan_RescuePreInsertBeforeLaterRoundPark_IsReconstructedAtTail()
    {
        // [BO-9 / R34 F5；第 1 轮会诊 IMPORTANT-1 反例] 真实 Runner 复现：无循环计划 [A,P]，
        // A@0 已完成、P@1/P@2 仍有效停驻。RecomputeSuccessor 取 candidate=P@0、rescue=A@1（P@1 同轮
        // 前插的从未执行出现）并返回较早的 candidate；无循环时线性推进到 P@0 后即链尾，rescue A@1
        // 不会自然到达。修复前（仅重入停驻点）A@1/A@2 永不被驱动，停驻清偿后运行**假成功**；
        // 修复后链尾按计划全序重建未履行义务（停驻 + 其同轮前插未执行出现）逐条重驱。
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "无循环链尾重建前插 rescue",
            Nodes = [DragonNode("A", "配置A"), DragonNode("P", "配置P")],
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
        run.Cursor = new WorkflowNodeCursor { NodeId = "P", Occurrence = 0, LoopIteration = 1, Attempt = 1 };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "P", SequenceIndex = 1, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "P", SequenceIndex = 1, Occurrence = 0, LoopIteration = 2, Result = WorkflowRunner.LocalWaitResultWord });
        _runs.Update(run);

        var (runner, boundary, terminal) = MakeRunner();
        var submitted = new List<(string NodeId, int LoopIteration)>();
        boundary.OnSubmit = req =>
        {
            submitted.Add((req.Occurrence.NodeId, req.Occurrence.LoopIteration));
            return Task.CompletedTask;
        };

        var resumed = await runner.ResumeAsync(run.RunId);

        // 计划全序：candidate P@0 → rescue A@1 → P@1 → A@2 → P@2；已完成的 A@0 不重跑。
        Assert.Equal(new[] { ("P", 0), ("A", 1), ("P", 1), ("A", 2), ("P", 2) }, submitted);
        Assert.DoesNotContain(submitted, x => x is { NodeId: "A", LoopIteration: 0 });
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        Assert.Equal(1, terminal.Actions.Count);
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 1, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "P", LoopIteration: 1, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 2, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "P", LoopIteration: 2, Result: "succeeded" }));
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, persisted.State);
        Assert.Equal(1, persisted.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 1, Result: "succeeded" }));
        Assert.Equal(1, persisted.NodeOutcomes.Count(o => o is { NodeId: "P", LoopIteration: 2, Result: "succeeded" }));
    }
'''
body = (nl + test.strip("\n") + nl).replace("\n", nl)
io.open(p, "w", encoding="utf-8", newline="").write(head + body + tail)
print("appended BO-9 review-finding fixture")
