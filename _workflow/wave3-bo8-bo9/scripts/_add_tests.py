import io, os
p = r"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"
text = io.open(p, encoding="utf-8", newline="").read()
assert text.endswith("}\r\n") or text.endswith("}\n")
nl = "\r\n"
idx = text.rfind("}")           # class closing brace
head = text[:idx]
tail = text[idx:]
assert head.endswith(nl)

bo8 = r'''
    [Fact]
    public async Task RevisionReload_CompletedIdentityReorderedAfterResumePoint_IsNotResubmitted()
    {
        // [BO-8 / R29 重要（继承缺陷）] 真实 Runner 复现：修订在驱动中到达（ProcessBoundaryActions
        // 热重载，生产可达），把**已完成**出现 n3 重排到恢复点之后。修复前推进段按 plan.Next 线性
        // 前进、无完成过滤 ⇒ n3 被二次提交（外部副作用重复发生，不可撤销）。
        var workflowId = SeedFlow(new WorkflowDocument
        {
            Name = "纯完成重排推进不重提",
            Nodes = [DragonNode("n3", "配置n3"), DragonNode("n2", "配置n2"), DragonNode("Y", "配置Y")],
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
        }).Split('|')[1];
        var (runner, boundary, terminal) = MakeRunner();
        var submitted = new List<string>();
        boundary.OnSubmit = req => { submitted.Add(req.Occurrence.NodeId); return Task.CompletedTask; };

        void Revise(params string[] ids)
        {
            var revision = _workflows.List().Single(x => x.WorkflowId == workflowId).Revision;
            var document = _workflows.Load(workflowId);
            document.Nodes = ids.Select(id => DragonNode(id, "配置" + id)).ToList();
            _workflows.Save(document, revision);
        }

        // n3 已完成后、下一边界前：把已完成 n3 重排到未执行 X 之后（新计划 [n2,X,n3,Y]）。
        boundary.OnAwait = async (jobId, ct) =>
        {
            if (jobId == "job-2") Revise("n2", "X", "n3", "Y");
            await Task.CompletedTask;
            return "succeeded";
        };

        var run = await runner.StartAsync(workflowId);

        Assert.Equal(["n3", "n2", "X", "Y"], submitted); // 已完成 n3 不得二次提交；X/Y 各一次
        Assert.Equal(1, run.NodeOutcomes.Count(o => o is { NodeId: "n3", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(1, terminal.Actions.Count);
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(1, persisted.NodeOutcomes.Count(o => o is { NodeId: "n3", LoopIteration: 0, Result: "succeeded" }));
    }
'''

bo9a = r'''
    [Fact]
    public async Task Resume_MultipleLiveParksAcrossRoundsInLooplessPlan_DrivesEachLiveParkOnceInPlanOrder()
    {
        // [BO-9 / R34 F5 重要] 真实 Runner 复现：同一稳定身份 A 在第 1、2 轮各留一条仍有效（可定位、
        // 未完成）的零发送停驻，而当前修订已无循环定义 ⇒ 线性推进只可能到达全序最早的 A@loop1，
        // 较晚者不在 Next 链上。修复前：A@loop2 永不被重驱，运行按链尾聚合 Failed（跳步）；
        // 修复后：真实链尾按计划全序重入存活停驻，逐个驱动恰好一次，义务清偿后正常收敛。
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "跨轮多停驻无循环推进",
            Nodes = [DragonNode("A", "配置A")],
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
        run.Cursor = new WorkflowNodeCursor { NodeId = "A", Occurrence = 0, LoopIteration = 1, Attempt = 1 };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 2, Result = WorkflowRunner.LocalWaitResultWord });
        _runs.Update(run);

        var (runner, boundary, terminal) = MakeRunner();
        var submitted = new List<(string NodeId, int LoopIteration)>();
        boundary.OnSubmit = req =>
        {
            submitted.Add((req.Occurrence.NodeId, req.Occurrence.LoopIteration));
            return Task.CompletedTask;
        };

        var resumed = await runner.ResumeAsync(run.RunId);

        Assert.Equal(new[] { ("A", 1), ("A", 2) }, submitted); // 每个存活停驻恰好驱动一次，按计划全序
        Assert.DoesNotContain(submitted, x => x.LoopIteration == 0); // 已完成轮次不重跑
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        Assert.Empty(terminal.Actions.Count == 1 ? Array.Empty<string>() : new[] { "收尾次数异常" });
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 1, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 2, Result: "succeeded" }));
        Assert.False(string.IsNullOrEmpty(resumed.Note));
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, persisted.State);
        Assert.Equal(1, persisted.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 2, Result: "succeeded" }));
        Assert.Contains("收尾应只执行一次", terminal.Actions);
    }
'''

bo9b = r'''
    [Fact]
    public async Task Resume_ReentryParkStillRequiresLocalWait_StaysParkingWithoutFalseSuccess()
    {
        // [BO-9 / R34 F5 重要] 同一跨轮多停驻形状的**持续停驻**分支：链尾重入后第 2 轮停驻
        // 仍要求本地等待 ⇒ 不得把未清偿停驻当成链尾成功、不得触发收尾；运行回到 LocalWaitParking。
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "跨轮多停驻持续等待",
            Nodes = [DragonNode("A", "配置A")],
            Terminal =
            [
                new WorkflowTerminalAction
                {
                    Kind = "terminal.completionAction",
                    Params = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["action"] = System.Text.Json.JsonSerializer.SerializeToElement("不应执行收尾"),
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
        run.Cursor = new WorkflowNodeCursor { NodeId = "A", Occurrence = 0, LoopIteration = 1, Attempt = 1 };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 0, Result = "succeeded" });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 1, Result = WorkflowRunner.LocalWaitResultWord });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 2, Result = WorkflowRunner.LocalWaitResultWord });
        _runs.Update(run);

        var (runner, boundary, terminal) = MakeRunner();
        var submitted = new List<(string NodeId, int LoopIteration)>();
        boundary.OnSubmit = req =>
        {
            submitted.Add((req.Occurrence.NodeId, req.Occurrence.LoopIteration));
            boundary.SubmitOverride = req.Occurrence.LoopIteration == 2
                ? BoundarySubmitResult.WaitWith("第 2 轮停驻仍然有效")
                : null;
            return Task.CompletedTask;
        };

        var resumed = await runner.ResumeAsync(run.RunId);

        Assert.Equal(new[] { ("A", 1), ("A", 2) }, submitted);
        Assert.Equal(WorkflowRunState.LocalWaitParking, resumed.State);
        Assert.Empty(terminal.Actions);
        Assert.False(resumed.TailReached);
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 1, Result: "succeeded" }));
        Assert.Contains(resumed.NodeOutcomes, o => o is { NodeId: "A", LoopIteration: 2, Result: WorkflowRunner.LocalWaitResultWord });
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.LocalWaitParking, persisted.State);
        Assert.Contains(persisted.NodeOutcomes, o => o is { NodeId: "A", LoopIteration: 2, Result = WorkflowRunner.LocalWaitResultWord });
    }
'''

body = (bo8 + bo9a + bo9b).replace("\n", nl)
new_text = head + body + tail
io.open(p, "w", encoding="utf-8", newline="").write(new_text)
print("inserted", len(new_text) - len(text), "bytes")
