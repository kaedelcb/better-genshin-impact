import io
p = r"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"
t = io.open(p, encoding="utf-8").read()

old_hdr = """        // [BO-9 / R34 F5 重要] 真实 Runner 复现：同一稳定身份 A 在第 1、2 轮各留一条仍有效（可定位、
        // 未完成）的零发送停驻，而当前修订已无循环定义 ⇒ 线性推进只可能到达全序最早的 A@loop1，
        // 较晚者不在 Next 链上。修复前：A@loop2 永不被重驱，运行按链尾聚合 Failed（跳步）；
        // 修复后：真实链尾按计划全序重入存活停驻，逐个驱动恰好一次，义务清偿后正常收敛。
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "跨轮多停驻无循环推进","""
new_hdr = """        // [BO-9 / R34 F5 重要] 真实 Runner 复现：同一稳定身份 A 在第 1、2、3 轮各留一条仍有效
        // （可定位、未完成）的零发送停驻，而当前修订已无循环定义 ⇒ 线性推进只可能到达全序最早的
        // A@loop1，较晚者都不在 Next 链上。修复前：A@loop2/A@loop3 永不被重驱，运行按链尾聚合
        // Failed（跳步）；修复后：真实链尾多次按计划全序重入存活停驻（每次恰好消耗一条义务），
        // 逐个驱动恰好一次，义务清偿后正常收敛。
        var seed = SeedFlow(new WorkflowDocument
        {
            Name = "跨轮多停驻无循环推进","""
assert t.count(old_hdr) == 1
t = t.replace(old_hdr, new_hdr)

old_loop3 = """        run.NodeOutcomes.Add(new WorkflowNodeOutcome
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
        Assert.Equal(1, terminal.Actions.Count);
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 1, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 2, Result: "succeeded" }));
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, persisted.State);
        Assert.Equal(1, persisted.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 2, Result: "succeeded" }));
        Assert.Contains("收尾应只执行一次", terminal.Actions);"""
new_loop3 = """        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 2, Result = WorkflowLocalWaitWord });
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        { NodeId = "A", SequenceIndex = 0, Occurrence = 0, LoopIteration = 3, Result = WorkflowLocalWaitWord });
        _runs.Update(run);

        var (runner, boundary, terminal) = MakeRunner();
        var submitted = new List<(string NodeId, int LoopIteration)>();
        boundary.OnSubmit = req =>
        {
            submitted.Add((req.Occurrence.NodeId, req.Occurrence.LoopIteration));
            return Task.CompletedTask;
        };

        var resumed = await runner.ResumeAsync(run.RunId);

        // 全序：救援点（A@loop1）之后两次链尾重入都必须再取计划全序最早的存活停驻。
        Assert.Equal(new[] { ("A", 1), ("A", 2), ("A", 3) }, submitted);
        Assert.DoesNotContain(submitted, x => x.LoopIteration == 0); // 已完成轮次不重跑
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        Assert.Equal(1, terminal.Actions.Count);
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 0, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 1, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 2, Result: "succeeded" }));
        Assert.Equal(1, resumed.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 3, Result: "succeeded" }));
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, persisted.State);
        Assert.Equal(1, persisted.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 2, Result: "succeeded" }));
        Assert.Equal(1, persisted.NodeOutcomes.Count(o => o is { NodeId: "A", LoopIteration: 3, Result: "succeeded" }));
        Assert.Contains("收尾应只执行一次", terminal.Actions);"""
assert t.count(old_loop3) == 1
t = t.replace(old_loop3, new_loop3)
t = t.replace("Result = WorkflowLocalWaitWord", "Result = WorkflowRunner.LocalWaitResultWord")
io.open(p, "w", encoding="utf-8", newline="\r\n").write(t)
print("bo9 fixture extended to three rounds")
