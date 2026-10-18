using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>设置包的槲寄生配置合同；只处理调用方提供的快照，不读写用户目录或运行账。</summary>
internal static class ConfigTransferMistletoe
{
    internal const string CurrentWorkflowFileName = "taskcenter-current-workflow.txt";
    private const string StartupFile = "startup-flow.json";
    private const string SchemesFile = "startup-flow-schemes.json";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    internal sealed record ImportPreparation(
        Dictionary<string, byte[]> Files,
        IReadOnlyDictionary<string, string> WorkflowIdMap,
        IReadOnlyList<string> ReusedWorkflowIds,
        bool AutomaticStartupRequiresAuthorization,
        IReadOnlyList<string> Warnings);

    internal static bool IsFlowEntry(string name)
    {
        const string prefix = "flows/";
        const string suffix = ".flow.json";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(suffix, StringComparison.Ordinal)
            && IsSafeId(name[prefix.Length..^suffix.Length]);
    }

    internal static Dictionary<string, byte[]> NormalizeForExport(IReadOnlyDictionary<string, byte[]> entries)
    {
        var files = Copy(entries);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in entries)
        {
            if (IsFlowEntry(pair.Key))
            {
                if (!identities.Add(FlowId(pair.Key))) throw Invalid("计划身份存在 Windows 大小写冲突");
                var flow = ValidateFlow(pair.Key, pair.Value);
                if (flow.Remove(WorkflowMigrationConsumer.TransactionField)) files[pair.Key] = Encode(flow);
            }
            else if (pair.Key == StartupFile)
            {
                var config = ValidateStartup(pair.Value);
                if (config.Remove("automaticStopAuthority")) files[pair.Key] = Encode(config);
            }
            else if (pair.Key == SchemesFile)
            {
                var schemes = ValidateSchemes(pair.Value);
                var changed = false;
                foreach (var scheme in schemes.Cast<JsonObject>())
                    changed |= ((JsonObject)scheme["config"]!).Remove("automaticStopAuthority");
                if (changed) files[pair.Key] = Encode(schemes);
            }
        }
        ValidateSelection(files, files.Keys.Where(IsFlowEntry).Select(FlowId).ToHashSet(StringComparer.Ordinal));
        return files;
    }

    /// <summary>
    /// 计划追加合并；已有文件不写入返回集。旧机器的运行停止权威不移植；导入的自动启动意图
    /// 保留为已撤销意图，明确重新开启后才重新授权。相同启动定义可复用目标机自己的原授权。
    /// </summary>
    internal static ImportPreparation PrepareImport(IReadOnlyDictionary<string, byte[]> incoming,
        IReadOnlyDictionary<string, byte[]> existingFlows, string? localStartupJson = null,
        string? localSchemesJson = null)
    {
        var files = NormalizeForExport(incoming);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var reused = new List<string>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingById = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in existingFlows)
        {
            if (!IsFlowEntry(pair.Key)) throw new InvalidDataException("本机计划快照包含非法名称。");
            var id = FlowId(pair.Key);
            if (!existingById.TryAdd(id, pair.Value)) throw new InvalidDataException("本机计划身份重复。");
            reserved.Add(id);
        }
        foreach (var key in files.Keys.Where(IsFlowEntry)) reserved.Add(FlowId(key));
        foreach (var pair in files.Where(p => IsFlowEntry(p.Key)).ToArray())
        {
            var id = FlowId(pair.Key);
            if (!existingById.TryGetValue(id, out var prior)) { map.Add(id, id); continue; }
            // 对导出时已去除本机事务字段的快照，比较同样的可移植副本；不覆写目标机事务资格。
            var equal = pair.Value.AsSpan().SequenceEqual(prior);
            if (!equal)
            {
                try
                {
                    var local = ValidateFlow(pair.Key, prior);
                    var imported = ValidateFlow(pair.Key, pair.Value);
                    local.Remove(WorkflowMigrationConsumer.TransactionField);
                    equal = JsonNode.DeepEquals(local, imported);
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            }
            if (equal)
            {
                map.Add(id, id);
                reused.Add(id);
                files.Remove(pair.Key);
                continue;
            }
            string replacement;
            do { replacement = WorkflowStore.NewWorkflowId(); } while (!reserved.Add(replacement));
            map.Add(id, replacement);
            var flow = ValidateFlow(pair.Key, pair.Value);
            flow["workflowId"] = replacement;
            files.Remove(pair.Key);
            files.Add("flows/" + replacement + ".flow.json", Encode(flow));
        }

        var requiresAuthorization = false;
        if (files.TryGetValue(StartupFile, out var startupBytes))
        {
            var startup = ValidateStartup(startupBytes);
            var changed = RewriteStartupReferences(startup, map);
            JsonObject? local = null;
            if (localStartupJson is not null)
            {
                try { local = ValidateStartup(Utf8.GetBytes(localStartupJson)); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            }
            requiresAuthorization |= NormalizeAuthorization(startup, local);
            if (changed || !JsonNode.DeepEquals(Parse(startupBytes), startup)) files[StartupFile] = Encode(startup);
        }
        if (files.TryGetValue(SchemesFile, out var schemeBytes))
        {
            var schemes = ValidateSchemes(schemeBytes);
            var merged = localSchemesJson is null ? new JsonArray() : ValidateSchemes(Utf8.GetBytes(localSchemesJson));
            var names = merged.Cast<JsonObject>().Select(item => String(item, "name")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var scheme in schemes.Cast<JsonObject>())
            {
                var config = (JsonObject)scheme["config"]!;
                RewriteStartupReferences(config, map);
                var name = String(scheme, "name")!;
                var matching = merged.Cast<JsonObject>().FirstOrDefault(item => string.Equals(String(item, "name"), name, StringComparison.OrdinalIgnoreCase)
                    && JsonNode.DeepEquals(PortableStartup((JsonObject)item["config"]!), PortableStartup(config)));
                if (matching is not null) continue; // 复用目标机方案及其自己的授权/撤销事实。
                requiresAuthorization |= NormalizeAuthorization(config, null);
                var copy = (JsonObject)scheme.DeepClone();
                if (!names.Add(name))
                {
                    var index = 1;
                    string renamed;
                    do { renamed = name + "-导入-" + index++; } while (!names.Add(renamed));
                    copy["name"] = renamed;
                }
                merged.Add(copy);
            }
            if (!JsonNode.DeepEquals(Parse(schemeBytes), merged)) files[SchemesFile] = Encode(merged);
        }
        if (files.TryGetValue(CurrentWorkflowFileName, out var selection))
        {
            var id = Text(selection).Trim();
            if (map.TryGetValue(id, out var replacement) && replacement != id)
                files[CurrentWorkflowFileName] = Utf8.GetBytes(replacement);
        }
        var warnings = new List<string>();
        if (requiresAuthorization) warnings.Add("槲寄生自动执行授权需重新确认或手动运行一次；已保留自动执行开关及流程配置。");
        return new(files, map, reused, requiresAuthorization, warnings);
    }

    private static bool NormalizeAuthorization(JsonObject config, JsonObject? local)
    {
        var comparable = PortableStartup(config);
        if (local is not null)
        {
            var localComparable = PortableStartup(local);
            if (JsonNode.DeepEquals(comparable, localComparable))
            {
                config.Remove("automaticStopAuthority");
                config.Remove("automaticAuthorization");
                foreach (var key in new[] { "automaticStopAuthority", "automaticAuthorization" })
                    if (local.TryGetPropertyValue(key, out var value)) config[key] = value?.DeepClone();
                return false;
            }
        }
        config.Remove("automaticStopAuthority");
        config.Remove("automaticAuthorization");
        if (config["enabled"]?.GetValue<bool>() != true) return false;
        config["automaticAuthorization"] = JsonSerializer.SerializeToNode(
            new StartupAutomaticAuthorization(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), Revoked: true), Options);
        return true;
    }

    private static JsonObject PortableStartup(JsonObject config)
    {
        var copy = (JsonObject)config.DeepClone();
        copy.Remove("automaticStopAuthority");
        copy.Remove("automaticAuthorization");
        return copy;
    }

    private static bool RewriteStartupReferences(JsonObject config, IReadOnlyDictionary<string, string> map)
    {
        var changed = false;
        void Walk(JsonArray steps)
        {
            foreach (var step in steps.Cast<JsonObject>())
            {
                if (step["taskCenterFlowId"] is JsonValue value && value.TryGetValue<string>(out var id)
                    && map.TryGetValue(id, out var replacement) && id != replacement)
                { step["taskCenterFlowId"] = replacement; changed = true; }
                foreach (var branch in new[] { "trueSteps", "falseSteps", "fireSteps" })
                    if (step[branch] is JsonArray child) Walk(child);
            }
        }
        Walk((JsonArray)config["steps"]!);
        return changed;
    }

    private static JsonObject ValidateFlow(string entry, byte[] bytes)
    {
        var root = Parse(bytes) as JsonObject ?? throw Invalid("计划须为 JSON 对象");
        if (String(root, "schema") != WorkflowDocumentSchema.Name
            || root["schemaVersion"] is not JsonValue version || !version.TryGetValue<int>(out var number)
            || number != WorkflowDocumentSchema.Version || String(root, "workflowId") != FlowId(entry)
            || string.IsNullOrWhiteSpace(String(root, "name"))) throw Invalid("计划版本、身份或名称不合法");
        var nodes = Array(root, "nodes");
        foreach (var item in nodes)
        {
            var node = item as JsonObject ?? throw Invalid("计划节点须为对象");
            if (string.IsNullOrWhiteSpace(String(node, "nodeId")) || string.IsNullOrWhiteSpace(String(node, "kind")))
                throw Invalid("计划节点缺少身份或类型");
            ValidateKindItems(Array(node, "strategies"));
            OptionalObject(node, "ref"); OptionalObject(node, "path");
        }
        ValidateKindItems(Array(root, "triggers"));
        ValidateKindItems(Array(root, "terminal"));
        OptionalObject(root, "activation"); OptionalObject(root, "execution"); OptionalObject(root, "loop");
        _ = root.Deserialize<WorkflowDocument>(Options) ?? throw Invalid("计划反序列化为空");
        return root;
    }

    private static JsonObject ValidateStartup(byte[] bytes)
    {
        var root = Parse(bytes) as JsonObject ?? throw Invalid("启动流程须为 JSON 对象");
        void Walk(JsonArray steps)
        {
            foreach (var item in steps)
            {
                var step = item as JsonObject ?? throw Invalid("启动节点须为对象");
                if (string.IsNullOrWhiteSpace(String(step, "kind"))) throw Invalid("启动节点缺少类型");
                foreach (var branch in new[] { "trueSteps", "falseSteps", "fireSteps" })
                    if (step.ContainsKey(branch)) Walk(Array(step, branch));
            }
        }
        Walk(Array(root, "steps"));
        OptionalObject(root, "automaticAuthorization"); OptionalObject(root, "automaticStopAuthority");
        _ = root.Deserialize<StartupFlowConfig>(Options) ?? throw Invalid("启动流程反序列化为空");
        return root;
    }

    private static JsonArray ValidateSchemes(byte[] bytes)
    {
        var schemes = Parse(bytes) as JsonArray ?? throw Invalid("启动方案库须为 JSON 数组");
        foreach (var item in schemes)
        {
            var scheme = item as JsonObject ?? throw Invalid("启动方案须为对象");
            if (string.IsNullOrWhiteSpace(String(scheme, "name")) || scheme["config"] is not JsonObject config)
                throw Invalid("启动方案缺少名称或配置");
            _ = ValidateStartup(Encode(config));
            _ = scheme.Deserialize<StartupFlowScheme>(Options) ?? throw Invalid("启动方案反序列化为空");
        }
        return schemes;
    }

    private static void ValidateSelection(IReadOnlyDictionary<string, byte[]> files, ISet<string> ids)
    {
        if (!files.TryGetValue(CurrentWorkflowFileName, out var bytes)) return;
        var id = Text(bytes).Trim();
        if (!IsSafeId(id) || !ids.Contains(id)) throw Invalid("当前计划选择未指向包内计划");
    }

    private static JsonNode Parse(byte[] bytes)
    {
        var text = Text(bytes);
        using var document = JsonDocument.Parse(text);
        void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw Invalid("JSON 字段重复或大小写冲突");
                    Check(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Check(child);
        }
        Check(document.RootElement);
        return JsonNode.Parse(text) ?? throw Invalid("JSON 不能为 null");
    }

    private static void ValidateKindItems(JsonArray array)
    {
        foreach (var item in array)
            if (item is not JsonObject obj || string.IsNullOrWhiteSpace(String(obj, "kind")))
                throw Invalid("策略、触发器或收尾动作缺少类型");
    }
    private static void OptionalObject(JsonObject obj, string key)
    {
        if (obj[key] is { } value && value is not JsonObject) throw Invalid(key + " 须为对象或 null");
    }
    private static JsonArray Array(JsonObject obj, string key)
        => obj[key] as JsonArray ?? throw Invalid(key + " 须为数组");
    private static string? String(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
    private static string FlowId(string entry) => entry["flows/".Length..^".flow.json".Length];
    private static bool IsSafeId(string id)
        => !string.IsNullOrWhiteSpace(id) && id.Length <= 180 && !id.Contains("..", StringComparison.Ordinal)
            && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains('/') && !id.Contains('\\')
            && id == id.Trim() && !id.EndsWith(".", StringComparison.Ordinal);
    private static string Text(byte[] bytes) => Utf8.GetString(bytes).TrimStart('\uFEFF');
    private static byte[] Encode(JsonNode node) => Utf8.GetBytes(node.ToJsonString(Options));
    private static InvalidDataException Invalid(string message) => new("槲寄生设置无效：" + message + "。");
    private static Dictionary<string, byte[]> Copy(IReadOnlyDictionary<string, byte[]> source)
        => source.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
}
