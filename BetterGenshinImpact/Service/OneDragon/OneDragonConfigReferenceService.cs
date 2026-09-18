using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BetterGenshinImpact.Service.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.OneDragon;

/// <summary>
/// 一条龙配置引用服务（F12 / R2.4，2026-09-18）。
/// 配置组重命名/删除时更新 User/OneDragon/*.json 中对组名的全部引用，替代旧
/// ScriptControlViewModel「new OneDragonFlowViewModel() + InitConfigList + 反射 + WriteConfig」路径：
/// - 纯 JSON DOM 操作：不构造 ViewModel、不触发 F02 旧升级器（AdaptVersions/RestoreOldVersions）、无 UI 副作用；
/// - 形状分类后按形状处理：tuple（Item1/Item2 对象值）、native（bool 值 + taskDefinitions/taskOrder）、
///   旧版名键（bool 值、键即任务名、无 taskDefinitions）各自走对应分支；混合/矛盾形状**隔离跳过并报告**，
///   绝不跨形状改动；
/// - 解析显式关闭日期识别、浮点用 decimal（DateParseHandling.None / FloatParseHandling.Decimal）；
///   写回前逐一校验数值字面量的 decimal 往返无损，不能无损保留的（科学计数法/超高精度/下溢）整文件隔离跳过；
/// - 只写真正发生变化的文件；写前复检文件字节（读后被锁外写入者改动则放弃本次更新并报告）；
///   每文件互斥复用 TaskConfigurationContract 锁表，临时文件 + Move 原子落盘并保留原 BOM；
/// - 单文件解析失败只跳过该文件（隔离），不阻断其余配置，也不回空对象覆盖原件。
/// </summary>
internal static class OneDragonConfigReferenceService
{
    /// <summary>一次引用更新的结果报告（供 UI 提示与日志）。Details 含逐文件变更与跳过原因。</summary>
    public sealed record ChangeReport(int FilesScanned, int FilesChanged, IReadOnlyList<string> Details)
    {
        public static readonly ChangeReport Empty = new(0, 0, Array.Empty<string>());
    }

    /// <summary>配置形状（审计 §5.1 三种序列化形状 + 混合冲突）。</summary>
    private enum ConfigShape
    {
        /// <summary>无任务表/定义：无可更新引用。</summary>
        Empty,

        /// <summary>茶包当前：taskEnabledList 值为 {Item1,Item2} 对象。</summary>
        Tuple,

        /// <summary>公版：taskEnabledList 值为 bool（键=任务 ID）且有 taskDefinitions。</summary>
        Native,

        /// <summary>旧版名键：taskEnabledList 值为 bool、键即任务名、无 taskDefinitions。</summary>
        LegacyNameKey,

        /// <summary>tuple 与 bool/定义并存：矛盾形状，隔离跳过。</summary>
        Mixed,
    }

    /// <summary>组重命名：所有引用 oldName 的位置改写为 newName。返回变更报告。</summary>
    public static ChangeReport RenameGroupReferences(string oneDragonDirectory, string oldName, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldName);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        var conflicts = 0;
        var report = UpdateAll(oneDragonDirectory, doc => RenameInDocument(doc, oldName, newName, ref conflicts));
        // 键冲突要进入报告（会诊第二轮 #4：跳过必须可见，不能只用成功变更数判断）
        return conflicts == 0 ? report : report with
        {
            Details = report.Details.Concat(new[]
                { $"{conflicts} 处旧版名键重命名因目标键已存在而跳过（未覆盖既有条目，请手工处理）" }).ToArray()
        };
    }

    /// <summary>组删除：移除/清空所有引用 groupName 的位置（按形状：tuple 键移除、native 定义+顺序清理、名键移除、委派清空）。</summary>
    public static ChangeReport DeleteGroupReferences(string oneDragonDirectory, string groupName)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupName);
        return UpdateAll(oneDragonDirectory, doc => DeleteInDocument(doc, groupName));
    }

    private static ChangeReport UpdateAll(string oneDragonDirectory, Func<JObject, int> update)
    {
        if (!Directory.Exists(oneDragonDirectory))
        {
            return ChangeReport.Empty;
        }

        var scanned = 0;
        var changedFiles = 0;
        var details = new List<string>();
        foreach (var file in Directory.GetFiles(oneDragonDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            scanned++;
            // [会诊第三轮 #1] 逐文件 I/O 失败同样隔离：读取/复检/临时写入/替换出错只跳过该文件并保留完整报告，
            // 绝不因单文件异常中断整批（半完成引用 + 无报告）。
            int changes;
            string? ioError = null;
            try
            {
            changes = TaskConfigurationContract.ExecuteFileLockedSync(file, () =>
            {
                var bytes = File.ReadAllBytes(file);
                var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                JObject doc;
                try
                {
                    // 关闭日期识别、浮点走 decimal：防止整文档重排改变无关字符串/数值表示
                    using var reader = new JsonTextReader(new StringReader(text))
                    {
                        DateParseHandling = DateParseHandling.None,
                        FloatParseHandling = FloatParseHandling.Decimal,
                    };
                    doc = JObject.Load(reader);
                }
                catch (JsonException)
                {
                    return -1; // 坏文件隔离跳过：不阻断其他配置，绝不回空覆盖
                }

                if (Classify(doc) == ConfigShape.Mixed)
                {
                    return -2; // 混合/矛盾形状：隔离跳过，绝不跨形状改动
                }

                var changed = update(doc);
                if (changed <= 0)
                {
                    return changed;
                }

                // [会诊第三轮 #5] 数值字面量无损守卫：decimal 往返后字面表示变化的数值
                // （科学计数法 1e2、超高精度、1e-29 下溢等）会被重排改变——此类文件隔离跳过，
                // 绝不把静默改变的数值随引用更新写回。
                if (HasNonRoundTrippableNumber(text))
                {
                    return -5;
                }

                // 写前复检：读后被锁外写入者（本机 UI 直写/外部编辑器）改动则放弃本次更新，不静默覆盖
                if (!File.ReadAllBytes(file).SequenceEqual(bytes))
                {
                    return -3;
                }

                var hasBom = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191;
                var temporary = file + ".refsvc-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, doc.ToString(Formatting.Indented), new UTF8Encoding(hasBom));
                    File.Move(temporary, file, true);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                return changed;
            });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                changes = -4;
                ioError = ex.Message;
            }
            switch (changes)
            {
                case > 0:
                    changedFiles++;
                    details.Add($"{Path.GetFileName(file)}: {changes} 处引用已更新");
                    break;
                case -1:
                    details.Add($"{Path.GetFileName(file)}: 解析失败，已隔离跳过（原件未动）");
                    break;
                case -2:
                    details.Add($"{Path.GetFileName(file)}: 混合/矛盾形状（tuple 与 bool/定义并存），已隔离跳过");
                    break;
                case -4:
                    details.Add($"{Path.GetFileName(file)}: I/O 失败已隔离跳过（原件未动）: {ioError}");
                    break;
                case -5:
                    details.Add($"{Path.GetFileName(file)}: 含无法无损保留的数值字面量（科学计数法/超高精度等），已隔离跳过（原件未动）");
                    break;
                case -3:
                    details.Add($"{Path.GetFileName(file)}: 写前检测到外部修改，已放弃本次更新（重试即可）");
                    break;
            }
        }
        return new ChangeReport(scanned, changedFiles, details);
    }

    private static ConfigShape Classify(JObject doc)
    {
        var list = Property(doc, "taskEnabledList") as JObject;
        var hasDefinitions = Property(doc, "taskDefinitions") is JObject;
        var hasTuple = list?.Properties().Any(p => p.Value is JObject) == true;
        var hasBool = list?.Properties().Any(p => p.Value.Type == JTokenType.Boolean) == true;
        if (hasTuple && (hasBool || hasDefinitions))
        {
            return ConfigShape.Mixed;
        }
        if (hasTuple)
        {
            return ConfigShape.Tuple;
        }
        if (hasDefinitions)
        {
            return ConfigShape.Native;
        }
        return hasBool ? ConfigShape.LegacyNameKey : ConfigShape.Empty;
    }

    /// <summary>重命名：按形状更新任务表引用 + customDomainList + *DomainName 委派属性。</summary>
    private static int RenameInDocument(JObject doc, string oldName, string newName, ref int conflicts)
    {
        var changes = 0;
        switch (Classify(doc))
        {
            case ConfigShape.Tuple:
                foreach (var entry in ((JObject)Property(doc, "taskEnabledList")!).Properties())
                {
                    if (entry.Value is JObject tuple && FindProperty(tuple, "Item2") is { } nameProp
                        && nameProp.Value.Type == JTokenType.String && nameProp.Value.Value<string>() == oldName)
                    {
                        nameProp.Value = newName;
                        changes++;
                    }
                }
                break;
            case ConfigShape.Native:
                foreach (var def in ((JObject)Property(doc, "taskDefinitions")!).Properties())
                {
                    if (def.Value.Type == JTokenType.String && def.Value.Value<string>() == oldName)
                    {
                        def.Value = newName;
                        changes++;
                    }
                }
                break;
            case ConfigShape.LegacyNameKey:
                var list = (JObject)Property(doc, "taskEnabledList")!;
                foreach (var entry in list.Properties().ToList())
                {
                    if (entry.Name == oldName && entry.Value.Type == JTokenType.Boolean)
                    {
                        // [会诊第二轮 #3] 目标键冲突守卫：新名已存在（内置任务/悬空引用撞名）时跳过，
                        // 绝不覆盖既有条目；原位替换键名（JProperty.Replace），保持枚举顺序不变。
                        if (list.Properties().Any(p => p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                        {
                            conflicts++;
                            continue;
                        }

                        entry.Replace(new JProperty(newName, entry.Value));
                        changes++;
                    }
                }
                break;
        }

        if (Property(doc, "customDomainList") is JArray custom)
        {
            foreach (var token in custom.ToList())
            {
                if (token.Type == JTokenType.String && token.Value<string>() == oldName)
                {
                    token.Replace(new JValue(newName));
                    changes++;
                }
            }
        }

        // *DomainName 顶层字符串属性（逐星期秘境委派等）；与旧反射口径一致——JSON 文件只含已序列化模型属性
        foreach (var prop in doc.Properties())
        {
            if (prop.Name.EndsWith("DomainName", StringComparison.OrdinalIgnoreCase)
                && prop.Value.Type == JTokenType.String && prop.Value.Value<string>() == oldName)
            {
                prop.Value = newName;
                changes++;
            }
        }
        return changes;
    }

    /// <summary>删除：按形状移除引用；native 额外清理 taskOrder/nextTaskId 死引用；委派属性清空。</summary>
    private static int DeleteInDocument(JObject doc, string groupName)
    {
        var changes = 0;
        switch (Classify(doc))
        {
            case ConfigShape.Tuple:
                var tupleList = (JObject)Property(doc, "taskEnabledList")!;
                foreach (var entry in tupleList.Properties().ToList())
                {
                    if (entry.Value is JObject tuple && FindProperty(tuple, "Item2") is { } nameProp
                        && nameProp.Value.Type == JTokenType.String && nameProp.Value.Value<string>() == groupName)
                    {
                        entry.Remove();
                        changes++;
                    }
                }
                break;
            case ConfigShape.Native:
                var definitions = (JObject)Property(doc, "taskDefinitions")!;
                var deadIds = definitions.Properties()
                    .Where(p => p.Value.Type == JTokenType.String && p.Value.Value<string>() == groupName)
                    .Select(p => p.Name).ToList();
                var boolList = Property(doc, "taskEnabledList") as JObject;
                foreach (var id in deadIds)
                {
                    definitions.Remove(id);
                    // 只移除 bool 形状的同名 ID 键，绝不触碰 tuple 条目（跨形状保护）
                    if (boolList?[id] is { Type: JTokenType.Boolean })
                    {
                        boolList.Remove(id);
                    }
                    changes++;
                }
                if (deadIds.Count > 0 && Property(doc, "taskOrder") is JArray order)
                {
                    foreach (var token in order.ToList())
                    {
                        if (token.Type == JTokenType.String && deadIds.Contains(token.Value<string>()))
                        {
                            token.Remove();
                        }
                    }
                }
                if (deadIds.Count > 0 && Property(doc, "nextTaskId") is { Type: JTokenType.String } next
                    && deadIds.Contains(next.Value<string>()))
                {
                    SetProperty(doc, "nextTaskId", new JValue(string.Empty));
                }
                break;
            case ConfigShape.LegacyNameKey:
                var nameList = (JObject)Property(doc, "taskEnabledList")!;
                if (nameList[groupName] is { Type: JTokenType.Boolean })
                {
                    nameList.Remove(groupName);
                    changes++;
                }
                break;
        }

        if (Property(doc, "customDomainList") is JArray custom)
        {
            foreach (var token in custom.ToList())
            {
                if (token.Type == JTokenType.String && token.Value<string>() == groupName)
                {
                    token.Remove();
                    changes++;
                }
            }
        }

        foreach (var prop in doc.Properties())
        {
            if (prop.Name.EndsWith("DomainName", StringComparison.OrdinalIgnoreCase)
                && prop.Value.Type == JTokenType.String && prop.Value.Value<string>() == groupName)
            {
                prop.Value = string.Empty;
                changes++;
            }
        }
        return changes;
    }

    /// <summary>
    /// JSON 文本中是否存在 decimal 往返后字面表示变化的数值（如 1e2、1e-29、超 decimal 精度）。
    /// 用 System.Text.Json 读取原始字面量逐一比对；任何无法证明无损的数值都视为不可保留。
    /// </summary>
    private static bool HasNonRoundTrippableNumber(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var stack = new Stack<System.Text.Json.JsonElement>();
            stack.Push(document.RootElement);
            while (stack.Count > 0)
            {
                var element = stack.Pop();
                switch (element.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject()) stack.Push(property.Value);
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) stack.Push(item);
                        break;
                    case System.Text.Json.JsonValueKind.Number:
                        var raw = element.GetRawText();
                        if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var value)
                            || !string.Equals(value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                raw, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        break;
                }
            }
            return false;
        }
        catch
        {
            // 主解析已成功而这里失败 = 无法证明无损，按不可保留处理（隔离跳过）
            return true;
        }
    }

    private static JToken? Property(JObject doc, string name)
        => doc.Properties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static JProperty? FindProperty(JObject doc, string name)
        => doc.Properties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void SetProperty(JObject doc, string name, JToken value)
    {
        var prop = FindProperty(doc, name);
        if (prop != null)
        {
            prop.Value = value;
        }
    }
}