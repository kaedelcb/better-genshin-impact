using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.Services;

public static partial class ConfigTransferResources
{
    private static bool RewriteAssistant(JsonNode root, Dictionary<string, string> groups, Dictionary<string, string> dragons,
        IReadOnlyDictionary<string, byte[]> resources, IReadOnlyDictionary<string, byte[]> sourceResources, List<string> warnings)
    {
        var changed = false;
        Visit(root);
        return changed;
        void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Visit(child); return; }
            if (node is not JsonObject obj) return;
            var kind = Text(Get(obj, "kind"));
            if (Get(obj, "ref") is JsonObject reference)
            {
                var map = kind is "resource.oneDragonConfig" or "resource.singleTask" ? dragons : groups;
                var old = Text(Get(reference, "config"));
                if (old.Length == 0) old = Text(Get(reference, "configKey")).Split('#')[0];
                var name = map.GetValueOrDefault(old, old);
                if (name != old) { Set(reference, "config", name); changed = true; }
                var directory = ReferenceEquals(map, dragons) ? "OneDragon" : "ScriptGroup";
                if (resources.TryGetValue(Prefix + directory + "/" + old + ".json", out var bytes))
                {
                    var revision = Hash(bytes);
                    if (Text(Get(reference, "revision")) != revision) { Set(reference, "revision", revision); changed = true; }
                    var configKey = Text(Get(reference, "configKey"));
                    // ConfigKey is Name#sourceHash8, a migration mapping identity. Its source
                    // hash is provenance, not the current revision of the transformed bytes.
                    if (name != old && configKey.StartsWith(old + "#", StringComparison.Ordinal))
                    { Set(reference, "configKey", name + configKey[old.Length..]); changed = true; }
                }
            }
            if (Get(obj, "onlineHoeingGroupNames") is JsonArray names)
            {
                var types = Get(obj, "onlineHoeingGroupTypes") as JsonArray;
                for (var i = 0; i < names.Count; i++)
                {
                    var map = types != null && i < types.Count && Text(types[i]) == "onedragon" ? dragons : groups;
                    if (map.TryGetValue(Text(names[i]), out var renamed)) { names[i] = renamed; changed = true; }
                }
            }
            var specifiedType = Text(Get(obj, "onlineHoeingSpecifiedTaskType"));
            var specifiedName = Text(Get(obj, "onlineHoeingSpecifiedTaskName"));
            var specifiedMap = specifiedType == "onedragon" ? dragons : groups;
            if (specifiedType is "group" or "onedragon")
            {
                var name = specifiedMap.GetValueOrDefault(specifiedName, specifiedName);
                if (name != specifiedName) { Set(obj, "onlineHoeingSpecifiedTaskName", name); changed = true; }
                if (HasSwitchTask(obj))
                    changed |= RebindSwitchResource(obj, specifiedType, specifiedName, name, sourceResources, resources, warnings);
            }
            var startupMap = kind == "startOneClick" ? dragons : groups;
            if (kind is "startGroup" or "startOneClick" && startupMap.TryGetValue(Text(Get(obj, "taskName")), out var startupName))
            { Set(obj, "taskName", startupName); changed = true; }
            if (kind == "startBgi" && Get(obj, "arguments") is JsonValue arguments && arguments.TryGetValue<string>(out var originalArguments))
            {
                var rewritten = RewriteBgiArguments(originalArguments, groups, dragons);
                if (rewritten != originalArguments) { Set(obj, "arguments", rewritten); changed = true; }
            }
            changed |= RewriteCacheReferences(obj, "configGroups", "configGroupTasksWithStatus", groups);
            changed |= RewriteCacheReferences(obj, "oneClickConfigs", "oneClickTasksWithStatus", dragons);
            if (Get(obj, "quickCommands") is JsonObject quick)
                foreach (var binding in quick.ToArray())
                {
                    var text = Text(binding.Value); var oneDragon = text.StartsWith("ONEDRAGON:", StringComparison.Ordinal);
                    var prefix = oneDragon ? "ONEDRAGON:" : "GROUP:";
                    if (text.StartsWith(prefix, StringComparison.Ordinal) && (oneDragon ? dragons : groups).TryGetValue(text[prefix.Length..], out var renamed))
                    { quick[binding.Key] = prefix + renamed; changed = true; }
                }
            foreach (var field in obj.ToArray()) Visit(field.Value);
        }
    }

    private static bool HasSwitchTask(JsonObject obj) => Text(Get(obj, "onlineHoeingCompletionPolicy")) == "switchRoom"
        && Get(Get(obj, "onlineHoeingRoomSwitchCompletion"), "runAfterSwitch") is JsonValue value
        && value.TryGetValue<bool>(out var runAfter) && runAfter;

    /// <summary>只有冻结修订与包内资源一致才可重绑到确定的迁入资源；不能授权目标机碰巧同名的任务。</summary>
    private static bool RebindSwitchResource(JsonObject obj, string type, string sourceName, string name,
        IReadOnlyDictionary<string, byte[]> sourceResources, IReadOnlyDictionary<string, byte[]> resources, List<string> warnings)
    {
        var key = Prefix + (type == "onedragon" ? "OneDragon/" : "ScriptGroup/") + sourceName + ".json";
        var oldRevision = Text(Get(obj, "onlineHoeingSpecifiedConfigRevision"));
        var newRevision = "";
        if (sourceResources.TryGetValue(key, out var source) && Hash(source) == oldRevision
            && resources.TryGetValue(key, out var target))
        {
            // 一条龙旧格式的投影 ID 可能依赖文件名或旧任务键；有显式稳定身份时才能随改名重绑节点。
            var stableStart = type != "onedragon" || Text(Get(obj, "onlineHoeingSpecifiedStartMode")) != "node"
                || sourceName == name && source.AsSpan().SequenceEqual(target)
                || HasEnabledNativeNode(source) && HasEnabledNativeNode(target);
            if (stableStart) newRevision = Hash(target);
        }
        if (newRevision.Length == 0)
            warnings.Add($"切房后指定任务「{name}」的迁入资源或起点无法确认，请在对应房间重新选择任务和起点；确认前不会执行。");
        if (newRevision == oldRevision) return false;
        Set(obj, "onlineHoeingSpecifiedConfigRevision", newRevision);
        return true;

        bool HasEnabledNativeNode(byte[] bytes)
        {
            var nodeId = Text(Get(obj, "onlineHoeingSpecifiedStartTaskId"));
            var doc = Parse(bytes);
            // 公版 TaskDefinitions / TaskEnabledList 的同一个稳定 ID；不猜测 legacy 投影 ID 或同名任务。
            return nodeId.Length > 0 && Get(doc, "taskDefinitions") is JsonObject definitions
                && definitions.ContainsKey(nodeId) && Get(doc, "taskEnabledList") is JsonObject enabled
                && enabled[nodeId] is JsonValue value && value.TryGetValue<bool>(out var active) && active;
        }
    }

    internal static byte[] RevalidateSwitchTasksWithoutResources(byte[] assistantConfig, List<string> warnings)
    {
        var root = Parse(assistantConfig) as JsonObject ?? throw new InvalidDataException("助手主配置须为对象");
        var empty = new Dictionary<string, byte[]>();
        var changed = Revalidate(root);
        if (Get(root, "roomProfiles") is JsonArray profiles)
            foreach (var profile in profiles.OfType<JsonObject>()) changed |= Revalidate(profile);
        return changed ? Bytes(root) : assistantConfig;

        bool Revalidate(JsonObject config)
        {
            var type = Text(Get(config, "onlineHoeingSpecifiedTaskType"));
            if (type is not ("group" or "onedragon") || !HasSwitchTask(config)) return false;
            var name = Text(Get(config, "onlineHoeingSpecifiedTaskName"));
            return RebindSwitchResource(config, type, name, name, empty, empty, warnings);
        }
    }

    private static bool RewriteCacheReferences(JsonObject obj, string listKey, string tasksKey, Dictionary<string, string> map)
    {
        var changed = false;
        if (Get(obj, listKey) is JsonArray names)
            for (var i = 0; i < names.Count; i++)
                if (map.TryGetValue(Text(names[i]), out var replacement)) { names[i] = replacement; changed = true; }
        if (Get(obj, tasksKey) is JsonObject tasks)
        {
            var rewritten = new JsonObject();
            foreach (var pair in tasks)
            {
                var key = map.GetValueOrDefault(pair.Key, pair.Key);
                if (rewritten.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("成员缓存资源改名后任务键冲突：" + key);
                rewritten.Add(key, pair.Value?.DeepClone());
                changed |= key != pair.Key;
            }
            var actual = obj.First(p => p.Key.Equals(tasksKey, StringComparison.OrdinalIgnoreCase)).Key;
            obj[actual] = rewritten;
        }
        return changed;
    }

    /// <summary>保留本机缓存项，再合入已完成资源改名的迁入缓存；任务体和未知字段不重建为类型化对象。</summary>
    internal static byte[] MergeMemberCache(byte[] imported, byte[]? local)
    {
        var incoming = Parse(imported) as JsonObject ?? throw new InvalidDataException("迁入成员缓存须为对象");
        if (local is null) return imported.ToArray();
        var prior = Parse(local) as JsonObject ?? throw new InvalidDataException("本机成员缓存须为对象，原件已保留");
        var merged = (JsonObject)prior.DeepClone();
        foreach (var pair in incoming)
        {
            var actual = merged.FirstOrDefault(p => p.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).Key;
            if (actual is null) { merged.Add(pair.Key, pair.Value?.DeepClone()); continue; }
            if (pair.Key.Equals("configGroups", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("oneClickConfigs", StringComparison.OrdinalIgnoreCase))
            {
                if (merged[actual] is not JsonArray oldNames || pair.Value is not JsonArray newNames)
                    throw new InvalidDataException("成员缓存名称列表须为数组");
                var names = oldNames.Select(Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var name in newNames)
                    if (names.Add(Text(name))) oldNames.Add(name?.DeepClone());
            }
            else if (pair.Key.Equals("configGroupTasksWithStatus", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("oneClickTasksWithStatus", StringComparison.OrdinalIgnoreCase))
            {
                if (merged[actual] is not JsonObject oldTasks || pair.Value is not JsonObject newTasks)
                    throw new InvalidDataException("成员缓存任务索引须为对象");
                MergeMissing(oldTasks, newTasks);
            }
            else if (merged[actual] is JsonObject oldObject && pair.Value is JsonObject newObject)
                MergeMissing(oldObject, newObject);
            // Shared scalar/array fields remain local (including machine-specific hotkeys and cache age).
        }
        return Bytes(merged);

        static void MergeMissing(JsonObject target, JsonObject source)
        {
            foreach (var pair in source)
            {
                var actual = target.FirstOrDefault(p => p.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).Key;
                if (actual is null) target.Add(pair.Key, pair.Value?.DeepClone());
                else if (target[actual] is JsonObject oldObject && pair.Value is JsonObject newObject)
                    MergeMissing(oldObject, newObject);
            }
        }
    }

    private sealed record ArgumentToken(int Start, int End, string Value);

    /// <summary>只改 BGI 正式命令的资源参数，保留其他参数和原有空白；不解释 CMD 或外部程序命令。</summary>
    internal static string RewriteBgiArguments(string arguments, IReadOnlyDictionary<string, string> groups,
        IReadOnlyDictionary<string, string> dragons)
    {
        if (groups.Count == 0 && dragons.Count == 0 || string.IsNullOrWhiteSpace(arguments)) return arguments;
        var tokens = ReadArgumentTokens(arguments);
        var command = new List<ArgumentToken>();
        // Mirror CommandLineOptions.Parse: these global options and their next token are removed before choosing the action.
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Value.Trim().Equals("--instance", StringComparison.OrdinalIgnoreCase)
                || token.Value.Trim().Equals("--instance-name", StringComparison.OrdinalIgnoreCase)
                || token.Value.Trim().Equals("--restart-from-pid", StringComparison.OrdinalIgnoreCase))
            { if (i + 1 < tokens.Count) i++; continue; }
            command.Add(token);
        }
        if (command.Count == 0) return arguments;
        var verb = command[0].Value.Trim();
        IReadOnlyDictionary<string, string> map;
        int end;
        if (verb.Contains("startOneDragon", StringComparison.OrdinalIgnoreCase))
        { map = dragons; end = Math.Min(command.Count, 2); }
        else if (verb.Equals("--startGroups", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("--TaskProgress", StringComparison.OrdinalIgnoreCase))
        { map = groups; end = command.Count; }
        else return arguments;
        var replacements = new List<(ArgumentToken Token, string Name)>();
        for (var i = 1; i < end; i++)
            if (map.TryGetValue(command[i].Value.Trim(), out var name)) replacements.Add((command[i], name));
        var result = new StringBuilder(arguments);
        foreach (var replacement in replacements.OrderByDescending(p => p.Token.Start))
        {
            result.Remove(replacement.Token.Start, replacement.Token.End - replacement.Token.Start);
            result.Insert(replacement.Token.Start, QuoteArgument(replacement.Name));
        }
        return result.ToString();
    }

    private static List<ArgumentToken> ReadArgumentTokens(string arguments)
    {
        var tokens = new List<ArgumentToken>();
        var index = 0;
        while (index < arguments.Length)
        {
            while (index < arguments.Length && arguments[index] is ' ' or '\t') index++;
            if (index == arguments.Length) break;
            var start = index;
            var value = new StringBuilder();
            var quoted = false;
            while (index < arguments.Length && (quoted || arguments[index] is not (' ' or '\t')))
            {
                var ch = arguments[index];
                if (ch is '\0' or '\r' or '\n') throw InvalidArguments();
                if (ch == '\\')
                {
                    var slashStart = index;
                    while (index < arguments.Length && arguments[index] == '\\') index++;
                    var count = index - slashStart;
                    if (index < arguments.Length && arguments[index] == '"')
                    {
                        value.Append('\\', count / 2);
                        if (count % 2 == 1) { value.Append('"'); index++; }
                        else ReadQuote();
                    }
                    else value.Append('\\', count);
                }
                else if (ch == '"') ReadQuote();
                else { value.Append(ch); index++; }
            }
            if (quoted) throw InvalidArguments();
            tokens.Add(new(start, index, value.ToString()));

            void ReadQuote()
            {
                if (quoted && index + 1 < arguments.Length && arguments[index + 1] == '"')
                { value.Append('"'); index += 2; }
                else { quoted = !quoted; index++; }
            }
        }
        return tokens;
    }

    private static string QuoteArgument(string value)
    {
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            quoted.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(ch);
            slashes = 0;
        }
        quoted.Append('\\', slashes * 2);
        return quoted.Append('"').ToString();
    }

    private static InvalidDataException InvalidArguments()
        => new("资源发生改名，但启动 BGI 参数含未闭合引号或无法可靠解析的字符；未写入任何设置，请先修正该节点参数。");
}
