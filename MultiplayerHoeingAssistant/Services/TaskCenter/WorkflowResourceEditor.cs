using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal static class WorkflowResourceEditor
{
    internal const string Operation = "ext.config.openResourceEditor";
    internal const string Capability = "config.resourceEditor";
    internal const string LayoutCapability = "config.taskLayout";
    internal const string ApplyLayoutOperation = "ext.config.applyTaskLayout";

    internal sealed record ResourceTask(string TaskId, string Name, bool Enabled);
    internal sealed record ResourceTaskLayout(string Revision, string? StartTaskId, IReadOnlyList<ResourceTask> Tasks);

    private static Dictionary<string, object?> Payload(WorkflowNode node)
    {
        if (node.Ref is not { Config: { Length: > 0 } name } reference ||
            node.Kind is not ("resource.oneDragonConfig" or "resource.configGroup" or "resource.singleTask") ||
            node.Kind == "resource.singleTask" && string.IsNullOrWhiteSpace(reference.TaskId))
            throw new InvalidOperationException("节点没有有效的资源引用");
        return new()
        {
            [node.Kind == "resource.configGroup" ? "groupName" : "configName"] = name,
            ["taskId"] = node.Kind == "resource.singleTask" ? reference.TaskId : null,
        };
    }

    internal static async Task OpenAsync(WorkflowNode node, IResourceCatalogTransport transport)
    {
        var payload = Payload(node);
        if (!transport.IsReady || !transport.HasCapability(Capability))
            throw new InvalidOperationException("执行端离线或不支持资源编辑，请先连接本版BGI");
        if (string.IsNullOrWhiteSpace(node.Ref!.Revision))
            throw new InvalidOperationException("资源引用缺少修订，请先更新引用");
        payload["expectedConfigRevision"] = node.Ref.Revision;
        var result = await transport.SendAsync(Operation, payload, CancellationToken.None);
        if (result == null) throw new InvalidOperationException("资源未打开：引用可能已变化，请更新引用后重试；执行端错误请查看日志");
        using var json = JsonDocument.Parse(result);
        if (!json.RootElement.TryGetProperty("status", out var status) || status.GetString() != "editor_opened")
            throw new InvalidOperationException("执行端未确认资源编辑器已打开");
    }

    internal static async Task<string> ReadRevisionAsync(WorkflowNode node, IResourceCatalogTransport transport,
        CancellationToken ct = default)
    {
        var payload = Payload(node);
        if (!transport.IsReady || !transport.HasCapability("config.revision"))
            throw new InvalidOperationException("执行端资源不可达，不能使用缓存更新引用");
        var result = await transport.SendAsync(BgiExternalCatalogTransport.OpConfigDescribe, payload, ct);
        if (result == null) throw new InvalidOperationException("资源读取失败，原引用已保留");
        using var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        if (node.Kind == "resource.singleTask" && (!root.TryGetProperty("tasks", out var tasks) ||
            !tasks.EnumerateArray().Any(t => t.GetProperty("taskId").GetString() == node.Ref!.TaskId)))
            throw new InvalidOperationException("原单项已不存在，原引用已保留；请重新选择资源");
        var revision = root.GetProperty("configRevision").GetString();
        return !string.IsNullOrWhiteSpace(revision) ? revision : throw new InvalidOperationException("执行端未返回有效修订");
    }

    internal static async Task<ResourceTaskLayout> ReadLayoutAsync(WorkflowNode node, IResourceCatalogTransport transport)
    {
        if (!transport.IsReady || !transport.HasCapability(LayoutCapability))
            throw new InvalidOperationException("BGI 未连接或版本不支持任务列表设置，请连接当前版本的 BGI");
        var result = await transport.SendAsync(BgiExternalCatalogTransport.OpConfigDescribe, Payload(node), CancellationToken.None);
        if (result == null) throw new InvalidOperationException("读取 BGI 配置失败，请确认 BGI 已连接");
        using var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        var revision = root.GetProperty("configRevision").GetString();
        if (string.IsNullOrWhiteSpace(revision)) throw new InvalidOperationException("BGI 未返回配置修订");
        var start = root.TryGetProperty("startTaskId", out var startElement)
            && startElement.ValueKind == JsonValueKind.String ? startElement.GetString() : null;
        var tasks = root.GetProperty("tasks").EnumerateArray().Select(task => new ResourceTask(
            task.GetProperty("taskId").GetString() ?? throw new InvalidOperationException("任务缺少 ID"),
            task.GetProperty("name").GetString() ?? "未命名任务",
            task.GetProperty("enabled").GetBoolean())).ToArray();
        return new(revision, start, tasks);
    }

    internal static async Task<string> ApplyLayoutAsync(WorkflowNode node, IResourceCatalogTransport transport,
        ResourceTaskLayout current, IReadOnlyList<ResourceTask> tasks, string? startTaskId)
    {
        if (!transport.IsReady || !transport.HasCapability(LayoutCapability))
            throw new InvalidOperationException("BGI 连接已断开，任务列表未保存");
        var payload = Payload(node);
        payload["expectedConfigRevision"] = current.Revision;
        payload["tasks"] = tasks.Select(task => new { taskId = task.TaskId, enabled = task.Enabled }).ToArray();
        payload["startTaskId"] = startTaskId;
        var result = await transport.SendAsync(ApplyLayoutOperation, payload, CancellationToken.None);
        if (result == null) throw new InvalidOperationException("BGI 未确认保存；配置可能已变化，请重新读取任务列表");
        using var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        if (root.GetProperty("status").GetString() != "config_applied"
            || root.GetProperty("configRevision").GetString() is not { Length: > 0 } revision)
            throw new InvalidOperationException("BGI 未返回保存后的配置修订");
        return revision;
    }
}
