using System;
using System.Collections.Generic;
using System.Linq;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 占用者**级别事实**解析结果：未知一律为 null，且 <see cref="Reference"/> 说明未知/命中的来源。
/// </summary>
public sealed record OccupantLevelFacts(ArbitrationTier? Tier, int? Priority, bool? HighestClass, string Reference);

/// <summary>
/// 槲寄生 · R5 批次 5（A6 第二步）**占用身份 → 运行台账 → 级别/优先级**的唯一解析路径。
/// 关联键（审计结论）：BGI `task.status.executionRunId`（= 线协议 `workflowRunId`）
/// ≡ 运行台账 `WorkflowRunRecord.WireRunId`；运行台账 `RunId` ≡ 租约里流程级登记操作的 `OperationRecord.RunBinding`
/// （`TaskCenterHost.Admission.cs` 两处 `RunBinding = run.RunId`）；级别/优先级取该操作的**候选快照**
/// （`OperationRecord.Candidate`）。任一环节缺失、多命中或候选相互冲突 ⇒ **未知**（不得凭空推断归属）。
/// 纯函数：全部输入由参数提供，无副作用、不读时钟、不做 I/O。
/// </summary>
public static class OccupantLevelResolver
{
    public static OccupantLevelFacts Resolve(string? bgiExecutionRunId,
        IReadOnlyList<WorkflowRunRecord>? runs,
        IReadOnlyList<OperationRecord>? operations)
    {
        if (string.IsNullOrWhiteSpace(bgiExecutionRunId) || !Guid.TryParse(bgiExecutionRunId, out _))
        {
            return Unknown("execution_run_id_missing_or_invalid");
        }

        var runMatches = (runs ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r.RunId)
                        && string.Equals(r.WireRunId, bgiExecutionRunId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (runMatches.Count == 0) return Unknown("run_record_not_found");
        if (runMatches.Count > 1) return Unknown("run_record_ambiguous");

        var runId = runMatches[0].RunId;
        var candidates = (operations ?? [])
            .Where(o => string.Equals(o.RunBinding, runId, StringComparison.Ordinal)
                        // 只采信**流程级登记**（E1 面板启动）的可信类型：节点执行/外部启动/恢复等同样可能
                        // `RunBinding = run.RunId` 且 `Intent = "start"`（后继节点提交即如此），若一并采信会
                        // 得到错误级别事实、或把有效流程来源误判为冲突。
                        && o.OperationType == OperationType.FlowRegistration
                        && o.Candidate is not null)
            .Select(o => o.Candidate!)
            .Select(c => (c.Tier, c.Priority))
            .Distinct()
            .ToList();

        if (candidates.Count == 0) return Unknown("operation_record_not_found");
        if (candidates.Count > 1) return Unknown("operation_candidate_conflict");

        var (tier, priority) = candidates[0];
        return new OccupantLevelFacts(tier, priority, HighestClass: null, "resolved_from_run_and_flow_operation");
    }

    private static OccupantLevelFacts Unknown(string reason) => new(null, null, null, reason);
}
