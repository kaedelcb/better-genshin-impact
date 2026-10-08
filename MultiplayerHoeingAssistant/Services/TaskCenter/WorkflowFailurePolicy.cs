using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Basic resource failure handling. A retry is a new execution attempt, never an admission retry.</summary>
public sealed record WorkflowFailurePolicy(bool ContinueAfterFailure, int MaxRetries)
{
    public const string StrategyKind = "execution.failure";
    public const int MaximumRetries = 3;

    public static WorkflowFailurePolicy Resolve(WorkflowDocument document, WorkflowNode node, bool legacyContinue = false)
    {
        var mode = document.Execution?.OnNodeFailure;
        var retries = document.Execution?.MaxNodeRetries ?? 0;
        var strategy = node.Strategies.SingleOrDefault(s => s.Kind == StrategyKind);
        if (strategy is not null)
        {
            mode = strategy.GetString("onFailure") ?? mode;
            if (strategy.Params?.TryGetValue("maxRetries", out var value) == true && value.TryGetInt32(out var count))
                retries = count;
        }
        return new(mode is null ? legacyContinue : mode == "continue", retries);
    }

    public static IEnumerable<string> Validate(WorkflowDocument document)
    {
        if (document.Execution?.OnNodeFailure is { } mode && mode is not ("stop" or "continue"))
            yield return "计划失败处理须为停止或继续。";
        if (document.Execution?.MaxNodeRetries is { } retries && retries is < 0 or > MaximumRetries)
            yield return "计划失败重试次数须为0至3。";
        foreach (var node in document.Nodes)
        {
            var overrides = node.Strategies.Where(s => s.Kind == StrategyKind).ToList();
            if (overrides.Count > 1) yield return "同一任务只能设置一份失败处理。";
            foreach (var strategy in overrides)
            {
                if (strategy.Params?.TryGetValue("onFailure", out var failure) == true
                    && (failure.ValueKind != JsonValueKind.String || failure.GetString() is not ("stop" or "continue")))
                    yield return "任务失败处理须为停止或继续。";
                if (strategy.Params?.TryGetValue("maxRetries", out var retry) == true
                    && (retry.ValueKind != JsonValueKind.Number || !retry.TryGetInt32(out var count)
                        || count is < 0 or > MaximumRetries))
                    yield return "任务失败重试次数须为0至3。";
                if (node.Kind.StartsWith("control.", StringComparison.Ordinal))
                    yield return "判断和结束节点不执行资源失败重试。";
            }
        }
    }
}
