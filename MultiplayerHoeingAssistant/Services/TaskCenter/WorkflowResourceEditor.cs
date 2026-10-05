using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal static class WorkflowResourceEditor
{
    internal const string Operation = "ext.config.openResourceEditor";
    internal const string Capability = "config.resourceEditor";

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

    internal static async Task<string> ReadRevisionAsync(WorkflowNode node, IResourceCatalogTransport transport)
    {
        var payload = Payload(node);
        if (!transport.IsReady || !transport.HasCapability("config.revision"))
            throw new InvalidOperationException("执行端资源不可达，不能使用缓存更新引用");
        var result = await transport.SendAsync(BgiExternalCatalogTransport.OpConfigDescribe, payload, CancellationToken.None);
        if (result == null) throw new InvalidOperationException("资源读取失败，原引用已保留");
        using var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        if (node.Kind == "resource.singleTask" && (!root.TryGetProperty("tasks", out var tasks) ||
            !tasks.EnumerateArray().Any(t => t.GetProperty("taskId").GetString() == node.Ref!.TaskId)))
            throw new InvalidOperationException("原单项已不存在，原引用已保留；请重新选择资源");
        var revision = root.GetProperty("configRevision").GetString();
        return !string.IsNullOrWhiteSpace(revision) ? revision : throw new InvalidOperationException("执行端未返回有效修订");
    }
}
