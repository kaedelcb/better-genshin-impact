using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 5（A6 第二步）占用者级别事实解析**夹具：BGI `executionRunId` → 运行台账 `WireRunId`（须唯一命中）
/// → 该 run 的流程级登记操作（`RunBinding = RunId`、`Intent = start`）候选快照 → Tier/Priority。
/// 任一环节缺失、多命中或候选冲突 ⇒ **未知**（不凭空推断归属）；`HighestClass` 无受信来源时保持 null。
/// </summary>
public sealed class OccupantLevelResolverTests
{
    private static readonly Guid WireRunId = Guid.Parse("aabbccddeeff00112233445566778899");

    private static WorkflowRunRecord Run(string runId, Guid? wireRunId = null)
        => new() { RunId = runId, WireRunId = (wireRunId ?? WireRunId).ToString("N") };

    private static OperationRecord FlowOperation(string runBinding, ArbitrationTier tier, int priority,
        OperationType operationType = OperationType.FlowRegistration, string intent = "start")
        => new()
        {
            RunBinding = runBinding,
            Intent = intent,
            OperationType = operationType,
            Candidate = new ArbitrationCandidate { Tier = tier, Priority = priority },
        };

    [Fact]
    public void Resolve_FillsLevelFromUniqueRunAndFlowOperation()
    {
        var facts = OccupantLevelResolver.Resolve(WireRunId.ToString("N"),
            [Run("run-1")], [FlowOperation("run-1", ArbitrationTier.System, 7)]);

        Assert.Equal(ArbitrationTier.System, facts.Tier);
        Assert.Equal(7, facts.Priority);
        Assert.Null(facts.HighestClass); // 无受信最高级来源：不得冒充
        Assert.Equal("resolved_from_run_and_flow_operation", facts.Reference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Resolve_UnknownWhenExecutionRunIdMissingOrInvalid(string? raw)
    {
        var facts = OccupantLevelResolver.Resolve(raw, [Run("run-1")], [FlowOperation("run-1", ArbitrationTier.System, 7)]);

        Assert.Null(facts.Tier);
        Assert.Null(facts.Priority);
        Assert.Equal("execution_run_id_missing_or_invalid", facts.Reference);
    }

    [Fact]
    public void Resolve_UnknownWhenRunRecordMissingOrAmbiguous()
    {
        Assert.Equal("run_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [], []).Reference);
        Assert.Equal("run_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"),
                [Run("run-1", Guid.NewGuid())], [FlowOperation("run-1", ArbitrationTier.System, 7)]).Reference);
        // 同一 wireRunId 出现在两条运行记录 ⇒ 多命中，保守未知
        Assert.Equal("run_record_ambiguous",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"),
                [Run("run-1"), Run("run-2")], [FlowOperation("run-1", ArbitrationTier.System, 7)]).Reference);
        // RunId 为空白的坏记录不得参与命中
        Assert.Equal("run_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"),
                [new WorkflowRunRecord { RunId = "", WireRunId = WireRunId.ToString("N") }], []).Reference);
    }

    [Fact]
    public void Resolve_UnknownWhenOperationMissingOrNotFlowRegistration()
    {
        Assert.Equal("operation_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")], []).Reference);
        // 真实生产形状：后继节点提交同样 `RunBinding = run.RunId` 且 `Intent = "start"`，但类型是 NodeExecution
        // ⇒ 必须被排除（否则会采信节点候选，或把有效流程来源误判为冲突）。
        Assert.Equal("operation_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
                [FlowOperation("run-1", ArbitrationTier.System, 7, OperationType.NodeExecution)]).Reference);
        Assert.Equal("operation_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
                [FlowOperation("run-1", ArbitrationTier.System, 7, OperationType.ExternalStart)]).Reference);
        Assert.Equal("operation_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
                [FlowOperation("run-1", ArbitrationTier.System, 7, OperationType.Unknown)]).Reference);
        // RunBinding 指向别的运行也不命中
        Assert.Equal("operation_record_not_found",
            OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
                [FlowOperation("run-2", ArbitrationTier.System, 7)]).Reference);
    }

    [Fact]
    public void Resolve_NodeOperationsDoNotVote_SoFlowLevelStaysAuthoritative()
    {
        // 流程级 = System/7；节点级 = Plan/0（真实生产形状）⇒ 只采信流程级，不构成冲突
        var facts = OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
        [
            FlowOperation("run-1", ArbitrationTier.System, 7),
            FlowOperation("run-1", ArbitrationTier.Plan, 0, OperationType.NodeExecution),
            FlowOperation("run-1", ArbitrationTier.Plan, 0, OperationType.NodeExecution),
        ]);

        Assert.Equal(ArbitrationTier.System, facts.Tier);
        Assert.Equal(7, facts.Priority);
        Assert.Equal("resolved_from_run_and_flow_operation", facts.Reference);
    }

    [Fact]
    public void Resolve_AcceptsIdenticalCandidates_ButRejectsConflict()
    {
        var identical = OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
            [FlowOperation("run-1", ArbitrationTier.Fixed, 3), FlowOperation("run-1", ArbitrationTier.Fixed, 3)]);
        Assert.Equal(ArbitrationTier.Fixed, identical.Tier);
        Assert.Equal(3, identical.Priority);

        var conflictTier = OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
            [FlowOperation("run-1", ArbitrationTier.Fixed, 3), FlowOperation("run-1", ArbitrationTier.System, 3)]);
        Assert.Null(conflictTier.Tier);
        Assert.Equal("operation_candidate_conflict", conflictTier.Reference);

        var conflictPriority = OccupantLevelResolver.Resolve(WireRunId.ToString("N"), [Run("run-1")],
            [FlowOperation("run-1", ArbitrationTier.Fixed, 3), FlowOperation("run-1", ArbitrationTier.Fixed, 4)]);
        Assert.Equal("operation_candidate_conflict", conflictPriority.Reference);
    }

    [Fact]
    public void WithLevelFacts_CopiesOtherFieldsAndKeepsUnknownAsIs()
    {
        var occupant = new RunningOccupantFacts
        {
            State = OccupantFactsState.Occupied,
            Reference = "test_seam_occupied",
            HasTrustedIdentity = true,
            ExecutionInstanceId = "aabbccddeeff00112233445566778899",
            RunId = WireRunId.ToString("N"),
            JobId = "0102030405060708090a0b0c0d0e0f10",
            Kind = "Group",
            Source = "Ext",
            Name = "占用者",
            HoeingClass = true,
            HighestClass = null,
            StopRequested = true,
        };

        var withLevel = occupant.WithLevelFacts(ArbitrationTier.System, 12, null);

        Assert.Equal(ArbitrationTier.System, withLevel.Tier);
        Assert.Equal(12, withLevel.Priority);
        Assert.Equal(occupant.ExecutionInstanceId, withLevel.ExecutionInstanceId);
        Assert.Equal(occupant.Name, withLevel.Name);
        Assert.Equal(occupant.State, withLevel.State);
        Assert.Equal(occupant.Reference, withLevel.Reference);
        Assert.Equal(occupant.RunId, withLevel.RunId);
        Assert.Equal(occupant.JobId, withLevel.JobId);
        Assert.Equal(occupant.Kind, withLevel.Kind);
        Assert.Equal(occupant.Source, withLevel.Source);
        Assert.True(withLevel.HasTrustedIdentity);
        Assert.True(withLevel.HoeingClass);
        Assert.True(withLevel.StopRequested);
        Assert.Null(withLevel.HighestClass);
        // 原对象不被修改（不可变复制）
        Assert.Null(occupant.Tier);
        Assert.Null(occupant.Priority);

        // 解析未知（全 null）时不得覆盖已有值
        var prefilled = withLevel.WithLevelFacts(null, null, null);
        Assert.Equal(ArbitrationTier.System, prefilled.Tier);
        Assert.Equal(12, prefilled.Priority);

        // 已有 HighestClass=true 时传 null 保持 true；传 false 才覆盖
        var proven = withLevel.WithLevelFacts(null, null, true);
        Assert.True(proven.HighestClass);
        Assert.False(proven.WithLevelFacts(null, null, false).HighestClass);
    }
}
