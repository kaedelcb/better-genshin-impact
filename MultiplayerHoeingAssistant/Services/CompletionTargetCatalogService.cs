using System.Text.Json;
using System.Text.Json.Serialization;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed record CompletionTargetNode(
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled);

public sealed record CompletionWorkflowTarget(
    [property: JsonPropertyName("workflowId")] string WorkflowId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("nodes")] IReadOnlyList<CompletionTargetNode> Nodes);

public sealed record CompletionTarget(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("workflowId")] string WorkflowId,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("nodes")] IReadOnlyList<CompletionTargetNode> Nodes);

public sealed record CompletionTargetCatalog(
    [property: JsonPropertyName("workflows")] IReadOnlyList<CompletionWorkflowTarget> Workflows,
    [property: JsonPropertyName("target")] CompletionTarget? Target,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>Only reads the selected executor's saved plans and live BGI descriptions; never writes task start settings.</summary>
public sealed class CompletionTargetCatalogService
{
    public const string Version = "1";
    private readonly Func<IReadOnlyList<WorkflowCatalogEntry>> _list;
    private readonly Func<string, WorkflowSnapshot> _load;
    private readonly IResourceCatalogTransport? _transport;

    public CompletionTargetCatalogService(TaskCenterHost host, IResourceCatalogTransport? transport)
        : this(host.ListFlows, host.LoadFlowSnapshot, transport) { }

    public CompletionTargetCatalogService(Func<IReadOnlyList<WorkflowCatalogEntry>> list,
        Func<string, WorkflowSnapshot> load, IResourceCatalogTransport? transport)
        => (_list, _load, _transport) = (list, load, transport);

    public async Task<CompletionTargetCatalog> LoadAsync(string type, string name, string workflowId = "",
        CancellationToken cancellationToken = default)
    {
        var workflows = new List<CompletionWorkflowTarget>();
        try
        {
            foreach (var entry in _list().Where(e => e.Status == WorkflowFileStatus.Ready
                         && e.UnsupportedKinds.Count == 0 && e.ActivationStatus != "candidate-ready"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var snapshot = _load(entry.WorkflowId);
                    if (snapshot.Document.WorkflowId != entry.WorkflowId || string.IsNullOrWhiteSpace(snapshot.Revision)
                        || snapshot.Document.Activation?.Status == "candidate-ready"
                        || WorkflowKindCatalog.FindUnsupportedKinds(snapshot.Document).Count != 0
                        || snapshot.Document.Nodes.Any(n => string.IsNullOrWhiteSpace(n.NodeId))
                        || snapshot.Document.Nodes.Select(n => n.NodeId).Distinct(StringComparer.Ordinal).Count() != snapshot.Document.Nodes.Count) continue;
                    workflows.Add(new(entry.WorkflowId, snapshot.Document.Name, snapshot.Revision,
                        snapshot.Document.Nodes.Select((n, i) => new CompletionTargetNode(n.NodeId,
                            NodeName(n, i), !IsPlanDisabled(n))).ToArray()));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* A disappearing plan is not a usable target. */ }
            }
            if (type == "workflow")
            {
                var flow = workflows.SingleOrDefault(w => w.WorkflowId == workflowId);
                return flow is null
                    ? new(workflows, null, "计划表已失效或不可执行，请重新选择。")
                    : new(workflows, new(type, flow.Name, flow.WorkflowId, flow.Revision, flow.Nodes));
            }
            if (type is not ("group" or "onedragon")) return new(workflows, null, "未知指定任务类型。 ");
            if (string.IsNullOrWhiteSpace(name)) return new(workflows, null, "请选择执行目标。");
            if (_transport is not { IsReady: true }) return new(workflows, null, "执行端 BGI 目录未就绪。");
            var json = await _transport.SendAsync(BgiExternalCatalogTransport.OpConfigDescribe,
                new { groupName = type == "group" ? name : null, configName = type == "onedragon" ? name : null }, cancellationToken);
            if (string.IsNullOrWhiteSpace(json)) return new(workflows, null, "执行目标不存在或无法读取目录。");
            return new(workflows, ParseDescription(type, name, json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new(workflows, null, "读取完成动作目录失败：" + ex.Message); }
    }

    public static CompletionTarget ParseDescription(string type, string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var revision = root.GetProperty("configRevision").GetString();
        if (string.IsNullOrWhiteSpace(revision)) throw new InvalidOperationException("执行目标缺少配置修订号。");
        var nodes = root.GetProperty("tasks").EnumerateArray().Select(t => new CompletionTargetNode(
            t.GetProperty("taskId").GetString() ?? "", t.GetProperty("name").GetString() ?? "",
            t.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)).ToArray();
        if (nodes.Any(n => string.IsNullOrWhiteSpace(n.TaskId)) || nodes.Select(n => n.TaskId).Distinct(StringComparer.Ordinal).Count() != nodes.Length)
            throw new InvalidOperationException("执行目标缺少唯一的稳定任务标识。");
        return new(type, name, "", revision, nodes);
    }

    public async Task<string?> ValidateAsync(TaskConflictPolicySettings settings, CancellationToken cancellationToken = default)
    {
        var shapeError = ValidateShape(settings);
        if (shapeError is not null || settings.Policy != TaskConflictPolicy.RunSpecified || settings.SpecifiedStartMode == "default") return shapeError;
        var catalog = await LoadAsync(settings.SpecifiedTaskType, settings.SpecifiedTaskName, settings.SpecifiedWorkflowId, cancellationToken);
        return Validate(settings, catalog);
    }

    public static string? ValidateShape(TaskConflictPolicySettings settings)
    {
        if (!Enum.IsDefined(settings.Policy)) return "未知完成后动作。";
        if (settings.SpecifiedTaskType is not ("group" or "onedragon" or "workflow")) return "未知指定任务类型。";
        if (settings.SpecifiedStartMode is not ("default" or "beginning" or "node")) return "未知执行起点。";
        if (settings.SpecifiedTaskType == "workflow" && settings.SpecifiedStartMode == "default") return "计划表必须明确选择执行起点。";
        if (settings.Policy != TaskConflictPolicy.RunSpecified) return null;
        if (string.IsNullOrWhiteSpace(settings.SpecifiedTaskName)) return "请选择指定任务名称。";
        if (settings.SpecifiedTaskType == "workflow" && string.IsNullOrWhiteSpace(settings.SpecifiedWorkflowId)) return "请选择具有稳定标识的计划表。";
        if (settings.SpecifiedStartMode == "default")
            return !string.IsNullOrEmpty(settings.SpecifiedStartTaskId) || !string.IsNullOrEmpty(settings.SpecifiedStartTaskName)
                || !string.IsNullOrEmpty(settings.SpecifiedConfigRevision) || !string.IsNullOrEmpty(settings.SpecifiedWorkflowId)
                ? "旧入口不能携带新起点字段。" : null;
        if (settings.SpecifiedTaskType != "workflow" && !string.IsNullOrEmpty(settings.SpecifiedWorkflowId))
            return "配置组或一条龙不能携带计划表身份。";
        if (string.IsNullOrWhiteSpace(settings.SpecifiedConfigRevision)) return "执行目标尚未确认目录版本，请刷新后选择起点。";
        if (settings.SpecifiedStartMode == "node" && string.IsNullOrWhiteSpace(settings.SpecifiedStartTaskId)) return "请选择开始执行的任务节点。";
        if (settings.SpecifiedStartMode == "beginning"
            && (!string.IsNullOrEmpty(settings.SpecifiedStartTaskId) || !string.IsNullOrEmpty(settings.SpecifiedStartTaskName)))
            return "从头执行不能同时指定任务节点。";
        return null;
    }

    public static string? Validate(TaskConflictPolicySettings settings, CompletionTargetCatalog catalog)
    {
        var shapeError = ValidateShape(settings);
        if (shapeError is not null || settings.Policy != TaskConflictPolicy.RunSpecified || settings.SpecifiedStartMode == "default") return shapeError;
        if (catalog.Target is not { } target) return catalog.Error ?? "执行目标已失效。";
        if (target.Type != settings.SpecifiedTaskType
            || (target.Type == "workflow" ? target.WorkflowId != settings.SpecifiedWorkflowId : target.Name != settings.SpecifiedTaskName))
            return "目录目标不匹配，请重新选择。";
        if (target.Revision != settings.SpecifiedConfigRevision) return "执行目标已修改，请重新选择起点后保存。";
        if (!target.Nodes.Any(n => n.Enabled)) return "执行目标没有启用的任务。";
        if (settings.SpecifiedStartMode == "node"
            && !target.Nodes.Any(n => n.TaskId == settings.SpecifiedStartTaskId && n.Enabled)) return "原起点节点已删除或禁用，请重新选择。";
        return null;
    }

    private static bool IsPlanDisabled(WorkflowNode node)
        => node.ExtensionData?.TryGetValue("planDisabled", out var disabled) == true && disabled.ValueKind == JsonValueKind.True;

    private static string NodeName(WorkflowNode node, int index)
        => node.ExtensionData?.TryGetValue("name", out var name) == true && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? node.NodeId : node.Ref?.Config ?? $"任务 {index + 1}（{node.Kind}）";
}
