using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneDragonMigration.Core;

/// <summary>
/// R1 只读迁移引擎：三格式识别 → 标准配置（公版形状）+ 槲寄生增强流程 + 映射 manifest + dry-run 报告。
/// 纪律：JSON DOM 无副作用解析；输入字节不变；候选只写自建目录（路径越界防护）；不复用旧降级器；
/// 同名重复不合并；枚举顺序即执行顺序；同名/大小写配置冲突报出不覆盖；结构错误逐文件隔离。
/// </summary>
public static partial class OneDragonMigrationEngine
{
    public static readonly string[] EventMarkers = { "首领讨伐", "幽境危战" };

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    /// <summary>茶包专有字段：标准投影一律移除并登记（删除项决策 2026-09-18）。</summary>
    private static readonly string[] TeabagOnlyFields =
    {
        "Version", "IndexId", "NextConfiguration", "NextTaskIndex", "Period", "PeriodList",
        "EverySelectedValueListConverter", "ScheduleName", "CustomDomainList",
        "ResinCount", "SpecifyResinUse", "GenshinUid", "AccountBinding", "AccountBindingCode",
        "IsCraftingResinExpanded", "IsAutoDomainExpanded", "IsAutoLeyLineExpanded",
        "IsClaimRewardsExpanded", "IsSereniteaPotExpanded", "IsCompletionActionExpanded", "IsBindUidExpanded",
    };

    /// <summary>公版字段白名单（origin-lcb/main OneDragonFlowConfig）：之外的非茶包字段登记为未识别（保留+报告）。</summary>
    private static readonly HashSet<string> KnownPublicFields = new()
    {
        "Name", "NextTaskId", "TaskEnabledList", "TaskOrder", "TaskDefinitions",
        "CraftingBenchCountry", "AdventurersGuildCountry", "PartyName", "DomainName", "WeeklyDomainEnabled",
        "AutoBossName", "AutoBossStrategyName", "AutoBossTeamName", "AutoBossSpecifyRunCount", "AutoBossRunCount",
        "AutoBossUseTransientResin", "AutoBossUseFragileResin", "AutoBossReviveRetryCount",
        "AutoBossReturnToStatueAfterEachRound", "AutoBossRewardRecognitionEnabled", "AutoBossTimeout",
        "DailyRewardPartyName", "MinResinToKeep", "SundayEverySelectedValue", "SundayWeeklySelectedValue",
        "SundaySelectedValue", "SereniteaPotTpType", "SecretTreasureObjects",
        "LeyLineOneDragonMode", "LeyLineRunMonday", "LeyLineRunTuesday", "LeyLineRunWednesday",
        "LeyLineRunThursday", "LeyLineRunFriday", "LeyLineRunSaturday", "LeyLineRunSunday",
        "LeyLineMondayType", "LeyLineMondayCountry", "LeyLineTuesdayType", "LeyLineTuesdayCountry",
        "LeyLineWednesdayType", "LeyLineWednesdayCountry", "LeyLineThursdayType", "LeyLineThursdayCountry",
        "LeyLineFridayType", "LeyLineFridayCountry", "LeyLineSaturdayType", "LeyLineSaturdayCountry",
        "LeyLineSundayType", "LeyLineSundayCountry", "LeyLineRunCount", "LeyLineResinExhaustionMode",
        "LeyLineOpenModeCountMin",
        "MondayPartyName", "MondayDomainName", "MondaySelectedValue",
        "TuesdayPartyName", "TuesdayDomainName", "TuesdaySelectedValue",
        "WednesdayPartyName", "WednesdayDomainName", "WednesdaySelectedValue",
        "ThursdayPartyName", "ThursdayDomainName", "ThursdaySelectedValue",
        "FridayPartyName", "FridayDomainName", "FridaySelectedValue",
        "SaturdayPartyName", "SaturdayDomainName", "SaturdaySelectedValue",
        "SundayPartyName", "SundayDomainName", "CompletionAction",
    };

    private static readonly string[] WeekdayDomainFields =
    {
        "DomainName", "MondayDomainName", "TuesdayDomainName", "WednesdayDomainName",
        "ThursdayDomainName", "FridayDomainName", "SaturdayDomainName", "SundayDomainName",
    };

    public static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ---------------- 安全访问器：结构错误不抛批，逐文件隔离 ----------------

    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---------------- 解析 ----------------

    public static ConfigSnapshot ParseConfig(string path)
    {
        var snap = new ConfigSnapshot { SourceFile = path, Sha256 = string.Empty };
        try
        {
            var bytes = File.ReadAllBytes(path);
            snap.Sha256 = Sha256Of(bytes);
            var raw = JsonNode.Parse(bytes) as JsonObject;
            if (raw == null) { snap.ParseError = "根节点不是 JSON 对象"; return snap; }
            snap.Raw = raw;
            snap.Name = Str(raw["Name"]) ?? Path.GetFileNameWithoutExtension(path);
            if (raw["TaskEnabledList"] is JsonArray)
            {
                snap.ParseError = "TaskEnabledList 形状非法（数组），已隔离，不猜测";
                return snap;
            }
            snap.Format = DetectFormat(raw);
            ReadCommonFields(snap, raw);
            ReadTasks(snap, raw);
            return snap;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            snap.ParseError ??= "解析失败: " + ex.Message;
            return snap;
        }
    }

    public static OneDragonFormat DetectFormat(JsonObject raw)
    {
        if (raw["TaskEnabledList"] is not JsonObject list || list.Count == 0)
        {
            if (raw.ContainsKey("NextTaskIndex")) return OneDragonFormat.TeabagTuple;
            if (raw.ContainsKey("TaskOrder") || raw.ContainsKey("TaskDefinitions")) return OneDragonFormat.PublicCurrent;
            return raw.ContainsKey("TaskEnabledList") ? OneDragonFormat.LegacyNameBool : OneDragonFormat.Unknown;
        }
        foreach (var kv in list)
        {
            if (kv.Value is JsonObject tuple && tuple.ContainsKey("Item1"))
                return OneDragonFormat.TeabagTuple;
            break;
        }
        var hasDefs = raw["TaskDefinitions"] is JsonObject defs && defs.Count > 0;
        return hasDefs || raw.ContainsKey("TaskOrder") ? OneDragonFormat.PublicCurrent : OneDragonFormat.LegacyNameBool;
    }

    private static void ReadCommonFields(ConfigSnapshot snap, JsonObject raw)
    {
        snap.IndexId = Int(raw["IndexId"]) ?? 1;
        snap.NextConfiguration = Bool(raw["NextConfiguration"]) ?? false;
        snap.NextTaskIndex = Int(raw["NextTaskIndex"]) ?? 0;
        snap.NextTaskIdRaw = Str(raw["NextTaskId"]);
        snap.ScheduleName = Str(raw["ScheduleName"]) is { Length: > 0 } s ? s : "默认计划表";
        snap.Period = Str(raw["Period"]) ?? "每日";
        if (raw["PeriodList"] is JsonObject pl)
            foreach (var kv in pl) snap.PeriodList[kv.Key] = Bool(kv.Value) ?? false;
        snap.AccountBinding = Bool(raw["AccountBinding"]) ?? false;
        snap.GenshinUid = Str(raw["GenshinUid"]) ?? string.Empty;
        snap.AccountBindingCode = Str(raw["AccountBindingCode"]) ?? string.Empty;
        snap.CompletionAction = Str(raw["CompletionAction"]) ?? string.Empty;
        if (raw["CustomDomainList"] is JsonArray cdl)
            foreach (var item in cdl) if (Str(item) is { Length: > 0 } g) snap.CustomDomainList.Add(g);
        snap.HasResinOverride =
            Bool(raw["SpecifyResinUse"]) == true ||
            raw["ResinCount"] is JsonObject rc && rc.Count > 0;
    }

    private static void ReadTasks(ConfigSnapshot snap, JsonObject raw)
    {
        if (raw["TaskEnabledList"] is not JsonObject list) return;
        var order = 0;
        if (snap.Format == OneDragonFormat.TeabagTuple)
        {
            // 枚举顺序（文档顺序）= 旧真实执行顺序，绝不按数字键升序
            foreach (var kv in list)
            {
                if (kv.Value is not JsonObject tuple || tuple["Item1"] is not JsonValue)
                    throw new FormatException($"任务「{kv.Key}」形状非法（混合形状字典），拒绝猜测");
                var enabled = Bool(tuple["Item1"]) ?? throw new FormatException($"任务「{kv.Key}」Item1 非布尔");
                var name = Str(tuple["Item2"]) ?? throw new FormatException($"任务「{kv.Key}」Item2 非字符串");
                snap.Tasks.Add(new TaskEntry(kv.Key, name, enabled, order++));
            }
        }
        else if (snap.Format == OneDragonFormat.PublicCurrent)
        {
            // 公版现行：TaskOrder 非空则为显式顺序；缺/空时按 TaskEnabledList 枚举序，键始终是稳定 ID
            var defs = raw["TaskDefinitions"] as JsonObject;
            var emitted = new HashSet<string>();
            if (raw["TaskOrder"] is JsonArray taskOrder && taskOrder.Count > 0)
            {
                foreach (var idNode in taskOrder)
                {
                    if (Str(idNode) is not { Length: > 0 } id) continue;
                    emitted.Add(id);
                    snap.Tasks.Add(new TaskEntry(id, Str(defs?[id]) ?? string.Empty,
                        Bool(list[id]) ?? false, order++));
                }
            }
            foreach (var kv in list)
                if (!emitted.Contains(kv.Key))
                    snap.Tasks.Add(new TaskEntry(kv.Key, Str(defs?[kv.Key]) ?? string.Empty,
                        Bool(kv.Value) ?? false, order++));
        }
        else
        {
            // 旧 name→bool：键即任务名，枚举顺序；重复名以出现序区分
            var occurrence = new Dictionary<string, int>();
            foreach (var kv in list)
            {
                occurrence.TryGetValue(kv.Key, out var n);
                snap.Tasks.Add(new TaskEntry(kv.Key + "#" + n, kv.Key, Bool(kv.Value) ?? false, order++));
                occurrence[kv.Key] = n + 1;
            }
        }
    }

    public static ScheduleSnapshot ParseSchedule(string? path)
    {
        var s = new ScheduleSnapshot();
        if (path == null || !File.Exists(path)) return s;
        try
        {
            var raw = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
            if (raw == null) { s.ParseError = "全局调度文件不是 JSON 对象"; return s; }
            s.Present = true;
            s.ScheduleStartOnTime = Bool(raw["ScheduleStartOnTime"]) ?? false;
            s.ScheduleStartTime = Str(raw["ScheduleStartTime"]) ?? "00:00";
            s.ScheduleLoop = Bool(raw["ScheduleLoop"]) ?? false;
            s.CycleMode = Bool(raw["CycleMode"]) ?? false;
            s.CycleTime = Str(raw["CycleTime"]) ?? "04:00";
            s.ScheduleLoopSkip = Bool(raw["ScheduleLoopSkip"]) ?? false;
            s.ContinuousCompletionAction = Str(raw["ContinuousCompletionAction"]) ?? string.Empty;
            s.AutoRedeemCodeCheckEnabled = Bool(raw["AutoRedeemCodeConfig"]?["AutoRedeemCodeCheckEnabled"])
                ?? Bool(raw["AutoRedeemCodeCheckEnabled"]) ?? false;
            s.SelectedPlanName = Str(raw["SelectedOneDragonFlowPlanName"]) ?? string.Empty;
            if (raw["ScheduleList"] is JsonArray sl)
                foreach (var item in sl) if (Str(item) is { Length: > 0 } n) s.ScheduleList.Add(n);
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ScheduleSnapshot { ParseError = "全局调度文件解析失败: " + ex.Message };
        }
    }
}


public static partial class OneDragonMigrationEngine
{
    // ---------------- 编译：标准配置 + 增强流程 + manifest ----------------

    public static MigrationResult Migrate(IReadOnlyList<string> configPaths, string? schedulePath,
        string candidateRoot, MigrationResult? prior = null)
    {
        // prior 仅作只读种子（ID 映射/调度快照），结果对象永远是新实例，不累积旧产物
        var result = new MigrationResult();
        if (prior != null)
        {
            foreach (var kv in prior.IdMappings)
                result.IdMappings[kv.Key] = new Dictionary<string, string>(kv.Value);
            result.Schedule = prior.Schedule;
        }
        var sched = ParseSchedule(schedulePath);
        if (sched.ParseError != null)
            result.Issues.Add(new("error", "(全局调度)", sched.ParseError + "；本次不使用全局调度字段"));
        if (sched.Present || !result.Schedule.Present) result.Schedule = sched;
        if (sched.Present && schedulePath != null)
            result.Schedule.Sha256 = Sha256Of(File.ReadAllBytes(schedulePath));

        foreach (var path in configPaths)
        {
            var snap = ParseConfig(path);
            result.Configs.Add(snap);
            if (snap.ParseError != null)
                result.Issues.Add(new("error", string.IsNullOrEmpty(snap.Name) ? Path.GetFileName(path) : snap.Name,
                    $"坏文件隔离：{snap.ParseError}；不生成候选、不覆盖原件"));
            else if (snap.Format == OneDragonFormat.Unknown)
                result.Issues.Add(new("error", snap.Name, "无法识别格式，跳过（不猜测、不丢数据）"));
        }

        // 同名（含大小写）冲突：报出 + 唯一键，不覆盖彼此产物
        var usable = result.Configs.Where(c => c.ParseError == null && c.Format != OneDragonFormat.Unknown).ToList();
        foreach (var g in usable.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (g.Count() > 1)
                result.Issues.Add(new("error", g.Key,
                    $"同名/大小写冲突配置 {g.Count()} 份（{string.Join("、", g.Select(c => Path.GetFileName(c.SourceFile)))}），产物以唯一键区分，不覆盖"));
            foreach (var c in g)
            {
                c.ConfigKey = $"{g.Key}#{c.Sha256[..8]}"; // 与冲突无关的稳定身份
                c.OutputName = g.Count() == 1 ? g.Key : c.ConfigKey;
            }
        }

        var candidateDir = Path.Combine(candidateRoot, "candidate-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var baseDir = candidateDir; var suffix = 1;
        while (Directory.Exists(candidateDir)) candidateDir = baseDir + "-" + (++suffix); // 同秒重跑不覆盖既有候选
        Directory.CreateDirectory(Path.Combine(candidateDir, "standard", "OneDragon"));
        Directory.CreateDirectory(Path.Combine(candidateDir, "flows"));
        result.CandidateDir = candidateDir;

        foreach (var snap in usable) CompileStandardConfig(snap, result, candidateDir);

        foreach (var group in usable.GroupBy(c => c.Format == OneDragonFormat.TeabagTuple ? c.ScheduleName : "默认"))
            CompileFlow(group.Key, group.ToList(), result, candidateDir);

        // 阻断状态：任何 error 级问题都阻止候选被当作可激活产物
        foreach (var i in result.Issues.Where(i => i.Severity == "error"))
            result.ActivationBlockers.Add($"[{i.Config}] {i.Message}");

        WriteManifestAndReport(result, candidateDir, configPaths, schedulePath);
        return result;
    }

    private static string StableTaskId(ConfigSnapshot snap, TaskEntry task, MigrationResult result)
    {
        if (snap.Format == OneDragonFormat.PublicCurrent)
            return task.SourceKey; // 已有公版 ID 原样保留，不重新生成
        if (!result.IdMappings.TryGetValue(snap.ConfigKey, out var map))
            result.IdMappings[snap.ConfigKey] = map = new Dictionary<string, string>();
        if (!map.TryGetValue(task.SourceKey, out var id))
            map[task.SourceKey] = id = Guid.NewGuid().ToString();
        return id;
    }

    /// <summary>输出文件名：非法字符替换，且最终路径必须位于候选根内（防 .. / 绝对路径越界）。</summary>
    private static string SafeOutputPath(string candidateDir, string subdir, string name, string ext, MigrationResult result, string configName)
    {
        var safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (safe.Contains("..")) safe = safe.Replace("..", "_"); // 路径段上溢归一化
        if (safe != name)
            result.Issues.Add(new("warn", configName, $"名称含非法文件名字符，输出文件名已归一化为「{safe}」（不改动来源）"));
        var full = Path.GetFullPath(Path.Combine(candidateDir, subdir, safe + ext));
        var rootFull = Path.GetFullPath(candidateDir);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"输出路径越界防护命中：{name}");
        return full;
    }

    private static void CompileStandardConfig(ConfigSnapshot snap, MigrationResult result, string candidateDir)
    {
        var std = (JsonObject)JsonNode.Parse(snap.Raw!.ToJsonString())!;
        var enabled = new JsonObject();
        var orderArr = new JsonArray();
        var defs = new JsonObject();
        var idByKey = new Dictionary<string, string>();
        foreach (var task in snap.Tasks.OrderBy(t => t.Order))
        {
            var id = StableTaskId(snap, task, result);
            idByKey[task.SourceKey] = id;
            enabled[id] = task.Enabled;
            orderArr.Add(id);
            defs[id] = task.Name;
        }
        std["TaskEnabledList"] = enabled;
        std["TaskOrder"] = orderArr;
        std["TaskDefinitions"] = defs;

        // NextTaskId：目标缺失/禁用 → 阻断级待处理，不悄悄从头跑
        string nextId = string.Empty;
        if (snap.Format == OneDragonFormat.TeabagTuple && snap.NextTaskIndex > 0)
        {
            var target = snap.Tasks.FirstOrDefault(t => t.SourceKey == snap.NextTaskIndex.ToString());
            if (target == null)
                result.Issues.Add(new("error", snap.Name, $"NextTaskIndex={snap.NextTaskIndex} 无对应任务，起点失效：置空并标记阻断，需人工指定后才可激活"));
            else if (!target.Enabled)
                result.Issues.Add(new("error", snap.Name, $"NextTaskIndex 指向已禁用任务「{target.Name}」，起点失效：置空并标记阻断"));
            else nextId = idByKey[target.SourceKey];
        }
        else if (snap.Format == OneDragonFormat.PublicCurrent && !string.IsNullOrEmpty(snap.NextTaskIdRaw))
        {
            var target = snap.Tasks.FirstOrDefault(t => t.SourceKey == snap.NextTaskIdRaw);
            if (target == null)
                result.Issues.Add(new("error", snap.Name, $"NextTaskId={snap.NextTaskIdRaw} 不在任务表内，起点失效：置空并标记阻断"));
            else if (!target.Enabled)
                result.Issues.Add(new("error", snap.Name, $"NextTaskId 指向已禁用任务「{target.Name}」，起点失效：置空并标记阻断"));
            else nextId = snap.NextTaskIdRaw;
        }
        std["NextTaskId"] = nextId;
        std.Remove("NextTaskIndex");

        // 字段改名：SundayDaySelectedValue → SundaySelectedValue；补公版 SundayWeeklySelectedValue
        if (std["SundayDaySelectedValue"] is JsonValue dayVal)
        {
            if (!std.ContainsKey("SundaySelectedValue")) std["SundaySelectedValue"] = dayVal.GetValue<string>();
            std.Remove("SundayDaySelectedValue");
        }
        std["SundayWeeklySelectedValue"] ??= "0";

        // 茶包专有字段移除并登记删除项
        foreach (var field in TeabagOnlyFields)
        {
            if (!std.ContainsKey(field)) continue;
            std.Remove(field);
            var reason = field switch
            {
                "ResinCount" or "SpecifyResinUse" => "C12 配置级树脂删除（退役），回退公版全局树脂",
                "CustomDomainList" => "C13 自定义秘境组删除（退役），秘境格回退标准秘境",
                "GenshinUid" or "AccountBinding" or "AccountBindingCode" => "账号绑定上移槲寄生策略节点（本机保留，公开导出脱敏）",
                _ when field.StartsWith("Is") && field.EndsWith("Expanded") => "C16 UI 折叠状态删除（退役）",
                _ => "茶包编排字段上移槲寄生流程",
            };
            result.Dropped.Add(new(snap.Name, field, reason));
        }
        if (snap.HasResinOverride)
            result.Issues.Add(new("warn", snap.Name, "该配置设有配置级树脂覆盖（C12 已决策删除），迁移后使用公版全局树脂设置"));

        // 未识别字段（非茶包、非公版白名单）：保留但入报告
        foreach (var kv in std.ToList())
            if (!KnownPublicFields.Contains(kv.Key) && !TeabagOnlyFields.Contains(kv.Key))
                result.Unrecognized.Add(new(snap.Name, kv.Key, "未识别字段：保留于标准投影，需人工确认归属"));

        // 秘境格事件标记/自定义组回退（C13/C14 删除）
        foreach (var field in WeekdayDomainFields)
        {
            var v = Str(std[field]);
            if (string.IsNullOrEmpty(v)) continue;
            if (EventMarkers.Contains(v))
            {
                result.Dropped.Add(new(snap.Name, field, $"C14 事件标记「{v}」删除（退役），回退标准秘境", v));
                result.Issues.Add(new("warn", snap.Name, $"{field} 的事件标记「{v}」已回退为标准秘境"));
                std[field] = string.Empty;
            }
            else if (snap.CustomDomainList.Contains(v))
            {
                result.Dropped.Add(new(snap.Name, field, $"C13 自定义秘境组「{v}」删除（退役），回退标准秘境", v));
                result.Issues.Add(new("warn", snap.Name, $"{field} 的自定义秘境组「{v}」已回退为标准秘境"));
                std[field] = string.Empty;
            }
        }

        // 依赖清单：任务引用的全部名称（配置组存在性未验证，R2 对账）
        result.Dependencies[snap.ConfigKey] = snap.Tasks.Select(t => t.Name).Distinct().ToList();

        string outPath;
        try { outPath = SafeOutputPath(candidateDir, Path.Combine("standard", "OneDragon"), snap.OutputName, ".json", result, snap.Name); }
        catch (InvalidOperationException ex)
        {
            result.Issues.Add(new("error", snap.Name, ex.Message + "；已跳过该配置输出，不中断整批"));
            return;
        }
        File.WriteAllText(outPath, std.ToJsonString(Indented), new UTF8Encoding(false));
        result.StandardHashes[snap.ConfigKey] = Sha256Of(File.ReadAllBytes(outPath));
        result.WrittenFiles.Add(outPath);
    }

    private static void CompileFlow(string flowName, List<ConfigSnapshot> configs, MigrationResult result, string candidateDir)
    {
        var ordered = configs.OrderBy(c => c.IndexId).ThenBy(c => c.ConfigKey, StringComparer.Ordinal).ToList();
        var nodes = new JsonArray();
        var nodeIdByConfig = new Dictionary<string, string>();
        foreach (var cfg in ordered)
        {
            // 确定性节点身份：内容哈希派生，插入其他配置不改变本节点 ID
            var nodeId = "n-" + Sha256Of(Encoding.UTF8.GetBytes(cfg.ConfigKey))[..8]; // SHA256 派生，跨进程稳定
            nodeIdByConfig[cfg.ConfigKey] = nodeId;
            var strategies = new JsonArray();

            // C04 整配置周期 → 通用星期条件修饰。有效语义（茶包 VM:1673-1675）：「每日」键优先，Period 只是展示串
            var everyDay = cfg.PeriodList.TryGetValue("每日", out var ed) && ed;
            if (!everyDay)
            {
                var days = new JsonArray();
                foreach (var d in new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" })
                    if (cfg.PeriodList.TryGetValue(d, out var on) && on) days.Add(d);
                if (days.Count == 0)
                {
                    if (cfg.PeriodList.Count == 0 && cfg.Period == "每日")
                    { /* PeriodList 缺失且显示为每日：按每日处理，不生成条件 */ }
                    else
                    {
                        result.Issues.Add(new("warn", cfg.Name, "周期有效选择为空（旧界面要求至少一项）：生成空条件=不自动运行，待人工确认"));
                        strategies.Add(new JsonObject { ["kind"] = "condition.weekdays", ["days"] = new JsonArray(),
                            ["dayBoundary"] = "localMidnight", ["note"] = "旧有效选择为空，阻断自动运行" });
                    }
                }
                else
                {
                    strategies.Add(new JsonObject { ["kind"] = "condition.weekdays", ["days"] = days,
                        ["dayBoundary"] = "localMidnight", ["note"] = "旧茶包为本地日期语义，非游戏服4点" });
                }
            }

            // C10 账号绑定 → 节点前置策略（敏感值仅本机）
            if (cfg.AccountBinding)
            {
                strategies.Add(new JsonObject { ["kind"] = "prerequisite.account",
                    ["uid"] = cfg.GenshinUid, ["bindingCode"] = cfg.AccountBindingCode,
                    ["sensitive"] = true, ["note"] = "识别/切号由 BGI 原子能力执行" });
                if (string.IsNullOrEmpty(cfg.GenshinUid))
                    result.Issues.Add(new("warn", cfg.Name, "账号绑定已启用但 UID 为空（旧行为为空码轮切），待人工确认"));
            }
            // C11 前置兑换码（全局开关 → 节点前置策略）
            if (result.Schedule.AutoRedeemCodeCheckEnabled)
                strategies.Add(new JsonObject { ["kind"] = "prerequisite.redeemCode",
                    ["checkDailyHistory"] = true, ["note"] = "策略归槲寄生，兑换服务/历史留 BGI" });

            var node = new JsonObject
            {
                ["nodeId"] = nodeId,
                ["kind"] = "resource.oneDragonConfig",
                ["ref"] = new JsonObject
                {
                    ["config"] = cfg.Name,
                    ["configKey"] = cfg.ConfigKey,
                    ["revision"] = result.StandardHashes.TryGetValue(cfg.ConfigKey, out var h) ? h : null,
                },
                ["strategies"] = strategies,
            };
            // 旧连续计划按账号绑定过滤（VM:1673/2073）：未绑定配置原不参与——保留过滤标记，不偷偷纳入
            if (cfg.Format == OneDragonFormat.TeabagTuple && !cfg.AccountBinding)
            {
                node["legacyFiltered"] = true;
                node["legacyFilterNote"] = "旧连续计划按账号绑定过滤，本配置原不参与；默认保留过滤标记，待人工确认是否纳入";
                result.Issues.Add(new("warn", cfg.Name, "旧连续计划中因未绑定账号被过滤；已保留 legacyFiltered 标记，待确认是否纳入"));
            }
            nodes.Add(node);
        }

        var flow = new JsonObject
        {
            ["schema"] = "mistletoe.workflow",
            ["schemaVersion"] = 1,
            ["name"] = flowName,
            ["nodes"] = nodes,
        };

        // 全局调度只作用于旧选中计划（AllConfig.SelectedOneDragonFlowPlanName），不复制到所有流程
        var s = result.Schedule;
        var isSelectedPlan = s.Present && !string.IsNullOrEmpty(s.SelectedPlanName)
            && string.Equals(flowName, s.SelectedPlanName, StringComparison.Ordinal);
        var hasGlobals = s.Present && (s.ScheduleStartOnTime || s.ScheduleLoop || !string.IsNullOrEmpty(s.ContinuousCompletionAction));
        if (hasGlobals && isSelectedPlan)
        {
            if (s.ScheduleStartOnTime)
                flow["triggers"] = new JsonArray { new JsonObject { ["kind"] = "trigger.time",
                    ["time"] = s.ScheduleStartTime, ["missPolicy"] = "nextDay",
                    ["note"] = "旧语义：当日已过则顺延明天；不套联机宽限" } };
            if (s.ScheduleLoop)
                flow["loop"] = new JsonObject
                {
                    ["mode"] = s.CycleMode ? "scheduled" : "immediate",
                    ["time"] = s.CycleMode ? s.CycleTime : null,
                    ["skipAcrossDays"] = s.ScheduleLoopSkip,
                    ["note"] = "跨天跳过按每轮开始/截止重定义，不复制旧 startTime 缺陷",
                };
            if (!string.IsNullOrEmpty(s.ContinuousCompletionAction))
                flow["terminal"] = new JsonArray { new JsonObject { ["kind"] = "terminal.completionAction",
                    ["action"] = s.ContinuousCompletionAction,
                    ["note"] = "仅流程成功边界执行；停止/失败/未知不触发" } };
            // 旧连续模式抑制每份配置自身收尾（C17 承接合同）：执行器不得逐配置执行 CompletionAction
            flow["execution"] = new JsonObject
            {
                ["suppressConfigCompletionAction"] = true,
                ["note"] = "旧连续模式抑制单配置收尾；收尾只发生在流程终止边界（terminal）",
            };
        }
        else if (hasGlobals && s.Present)
        {
            result.Issues.Add(new("info", flowName, $"全局调度仅属旧选中计划「{s.SelectedPlanName}」，本流程不附带触发器/循环/终止"));
        }

        // C06 入口水位：迁移种子的初始水位（运行时水位归 RunStore，见总计划 §3.1a 锚点 5）
        var marked = ordered.Where(c => c.NextConfiguration).ToList();
        if (marked.Count == 1)
            flow["watermark"] = new JsonObject { ["entryNodeId"] = nodeIdByConfig[marked[0].ConfigKey],
                ["once"] = true, ["source"] = "NextConfiguration",
                ["note"] = "迁移种子初始水位；运行时水位由 RunStore 持有" };
        else if (marked.Count > 1)
            result.Issues.Add(new("error", flowName,
                $"多个 NextConfiguration 标记（{string.Join("、", marked.Select(m => m.Name))}），水位不自动设定，阻止自动激活"));

        // 流程级阻断状态：本流程相关 error 汇总
        var flowErrors = result.Issues.Where(i => i.Severity == "error" &&
            (i.Config == flowName || ordered.Any(c => c.Name == i.Config))).Select(i => i.Message).ToList();
        flow["activation"] = flowErrors.Count == 0
            ? new JsonObject { ["status"] = "candidate-ready", ["note"] = "dry-run 候选，未激活" }
            : new JsonObject { ["status"] = "blocked", ["reasons"] = new JsonArray(flowErrors.Select(e => (JsonNode)e).ToArray()) };

        string outPath;
        try { outPath = SafeOutputPath(candidateDir, "flows", flowName, ".flow.json", result, flowName); }
        catch (InvalidOperationException ex)
        {
            result.Issues.Add(new("error", flowName, ex.Message + "；已跳过该流程输出，不中断整批"));
            return;
        }
        File.WriteAllText(outPath, flow.ToJsonString(Indented), new UTF8Encoding(false));
        result.WrittenFiles.Add(outPath);
    }

    /// <summary>公开分享副本：账号绑定值脱敏（UID/绑定码置空）。</summary>
    public static JsonObject ExportShareCopy(string flowPath)
    {
        var flow = (JsonObject)JsonNode.Parse(File.ReadAllText(flowPath))!;
        foreach (var node in flow["nodes"]!.AsArray().OfType<JsonObject>())
        foreach (var st in node["strategies"]!.AsArray().OfType<JsonObject>())
            if (st["kind"]?.GetValue<string>() == "prerequisite.account")
            {
                st["uid"] = string.Empty;
                st["bindingCode"] = string.Empty;
            }
        return flow;
    }
}


public static partial class OneDragonMigrationEngine
{
    // ---------------- manifest、dry-run 报告、回滚预览 ----------------

    private static void WriteManifestAndReport(MigrationResult result, string candidateDir,
        IReadOnlyList<string> configPaths, string? schedulePath)
    {
        var manifest = new JsonObject
        {
            ["schema"] = "mistletoe.migration.manifest",
            ["schemaVersion"] = 1,
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["mode"] = "dry-run",
            ["activation"] = result.ActivationBlockers.Count == 0
                ? new JsonObject { ["status"] = "candidate-ready", ["note"] = "dry-run 候选，未激活；正式激活属 R5 事务" }
                : new JsonObject { ["status"] = "blocked",
                    ["reasons"] = new JsonArray(result.ActivationBlockers.Select(b => (JsonNode)b).ToArray()) },
            ["sources"] = new JsonArray(result.Configs.Select(c => (JsonNode)new JsonObject
            {
                ["file"] = Path.GetFileName(c.SourceFile), ["sha256"] = c.Sha256,
                ["format"] = c.Format.ToString(), ["configKey"] = c.ConfigKey,
                ["status"] = c.ParseError == null ? "parsed" : "isolated",
            }).ToArray()),
            ["scheduleSource"] = schedulePath != null && result.Schedule.Present
                ? new JsonObject { ["file"] = Path.GetFileName(schedulePath), ["sha256"] = result.Schedule.Sha256,
                    ["selectedPlan"] = result.Schedule.SelectedPlanName }
                : null,
            ["idMappings"] = new JsonObject(result.IdMappings.Select(m =>
                new KeyValuePair<string, JsonNode?>(m.Key, new JsonObject(m.Value.Select(kv =>
                    new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)).ToArray()))).ToArray()),
            ["dropped"] = new JsonArray(result.Dropped.Select(d => (JsonNode)new JsonObject
            {
                ["config"] = d.Config, ["field"] = d.Field, ["reason"] = d.Reason, ["detail"] = d.Detail,
            }).ToArray()),
            ["unrecognizedFields"] = new JsonArray(result.Unrecognized.Select(d => (JsonNode)new JsonObject
            {
                ["config"] = d.Config, ["field"] = d.Field, ["reason"] = d.Reason,
            }).ToArray()),
            ["dependencies"] = new JsonObject(result.Dependencies.Select(d =>
                new KeyValuePair<string, JsonNode?>(d.Key, new JsonArray(d.Value.Select(v => (JsonNode)v).ToArray()))).ToArray()),
            ["issues"] = new JsonArray(result.Issues.Select(i => (JsonNode)new JsonObject
            {
                ["severity"] = i.Severity, ["config"] = i.Config, ["message"] = i.Message,
            }).ToArray()),
            ["outputs"] = new JsonArray(result.WrittenFiles.Select(f =>
                (JsonNode)new JsonObject { ["file"] = Path.GetRelativePath(candidateDir, f),
                    ["sha256"] = Sha256Of(File.ReadAllBytes(f)) }).ToArray()),
            ["rollbackPreview"] = new JsonObject
            {
                ["originalsTouched"] = false,
                ["plannedTargets"] = new JsonArray(result.WrittenFiles.Select(f =>
                    (JsonNode)Path.GetRelativePath(candidateDir, f)).ToArray()),
                ["rule"] = "正式激活前逐文件备份原件；回滚前比对现状哈希，迁移后的用户新编辑不得被覆盖；先停止新调度所有者再回滚，避免双跑",
            },
        };
        var manifestPath = Path.Combine(candidateDir, "manifest.json");
        File.WriteAllText(manifestPath, manifest.ToJsonString(Indented), new UTF8Encoding(false));
        result.WrittenFiles.Add(manifestPath);

        var sb = new StringBuilder();
        sb.AppendLine("# 一条龙迁移 dry-run 报告").AppendLine();
        sb.AppendLine($"生成时间（UTC）：{DateTime.UtcNow:O}；模式：dry-run（原文件未改动）").AppendLine();
        if (result.ActivationBlockers.Count > 0)
        {
            sb.AppendLine($"## ⛔ 候选阻断（{result.ActivationBlockers.Count} 条，解决前不得激活）").AppendLine();
            foreach (var b in result.ActivationBlockers) sb.AppendLine($"- {b}");
            sb.AppendLine();
        }
        sb.AppendLine($"## 配置（{result.Configs.Count} 份）").AppendLine();
        foreach (var c in result.Configs)
        {
            if (c.ParseError != null) { sb.AppendLine($"- ❌ {Path.GetFileName(c.SourceFile)}：{c.ParseError}（已隔离）"); continue; }
            var sched = c.Format == OneDragonFormat.TeabagTuple ? $"；计划「{c.ScheduleName}」" : string.Empty;
            sb.AppendLine($"- ✅ {c.Name}（{c.Format}，{c.Tasks.Count} 项任务{sched}）");
        }
        var retired = result.Dropped.Where(d => d.Reason.Contains("删除")).ToList();
        var relocated = result.Dropped.Where(d => !d.Reason.Contains("删除")).ToList();
        sb.AppendLine().AppendLine($"## 退役项（{retired.Count} 条，决策 2026-09-18 删除）").AppendLine();
        foreach (var d in retired) sb.AppendLine($"- [{d.Config}] {d.Field}：{d.Reason}");
        sb.AppendLine().AppendLine($"## 上移项（{relocated.Count} 条，迁入槲寄生流程/策略，不在标准配置保留）").AppendLine();
        foreach (var d in relocated) sb.AppendLine($"- [{d.Config}] {d.Field}：{d.Reason}");
        if (result.Unrecognized.Count > 0)
        {
            sb.AppendLine().AppendLine($"## 未识别字段（{result.Unrecognized.Count} 条，已保留，需人工确认归属）").AppendLine();
            foreach (var d in result.Unrecognized) sb.AppendLine($"- [{d.Config}] {d.Field}");
        }
        sb.AppendLine().AppendLine("## 依赖（任务引用名称，配置组存在性未验证）").AppendLine();
        foreach (var d in result.Dependencies) sb.AppendLine($"- [{d.Key}] {string.Join("、", d.Value)}");
        sb.AppendLine().AppendLine($"## 待确认问题（{result.Issues.Count(i => i.Severity != "error")} 条）").AppendLine();
        foreach (var i in result.Issues.Where(i => i.Severity != "error")) sb.AppendLine($"- **{i.Severity}** [{i.Config}] {i.Message}");
        sb.AppendLine().AppendLine("## 产物").AppendLine();
        foreach (var f in result.WrittenFiles) sb.AppendLine($"- {Path.GetRelativePath(candidateDir, f)}");
        var reportPath = Path.Combine(candidateDir, "report.md");
        File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
        result.WrittenFiles.Add(reportPath);
    }
}
