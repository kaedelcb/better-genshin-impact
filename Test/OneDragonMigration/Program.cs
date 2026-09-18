using System.Text.Json.Nodes;
using OneDragonMigration.Core;

// R1 夹具运行器：只读迁移器的合同回归。只写 _out/_out_rerun 自建目录，绝不写回 Fixtures 或任何用户目录。
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var fixtures = Path.Combine(root, "Fixtures");
var outRoot = Path.Combine(root, "_out");
if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true);
Directory.CreateDirectory(outRoot);

var passed = 0; var failed = 0;
void Check(string id, bool cond, string detail)
{
    if (cond) { passed++; Console.WriteLine($"  PASS {id}: {detail}"); }
    else { failed++; Console.WriteLine($"  FAIL {id}: {detail}"); }
}

string F(string name) => Path.Combine(fixtures, name);
var goodConfigs = new[] { "teabag-sample.json", "teabag-full.json", "teabag-nexttask-missing.json",
    "teabag-nextconfig-a.json", "public-current.json", "legacy-namebool.json", "public-no-order.json",
    "dup-name.json", "teabag-daily-wins.json" };
var badConfigs = new[] { "bad-json.json", "structural-array.json", "mixed-shape.json", "evil-name.json" };
var allInputs = goodConfigs.Concat(badConfigs).ToList();
var inputHashes = allInputs.ToDictionary(n => n, n => OneDragonMigrationEngine.Sha256Of(File.ReadAllBytes(F(n))));

var result = OneDragonMigrationEngine.Migrate(allInputs.Select(F).ToList(), F("global-schedule.json"), outRoot);
var cdir = result.CandidateDir!;

JsonObject? StdOrNull(string name)
{
    var p = Path.Combine(cdir, "standard", "OneDragon", name + ".json");
    return File.Exists(p) ? (JsonObject)JsonNode.Parse(File.ReadAllText(p))! : null;
}
JsonObject Std(string name) => StdOrNull(name) ?? throw new InvalidOperationException("missing std output: " + name);
JsonObject Flow(string name) => (JsonObject)JsonNode.Parse(File.ReadAllText(
    Path.Combine(cdir, "flows", name + ".flow.json")))!;
IEnumerable<JsonObject> Nodes(JsonObject flow) => flow["nodes"]!.AsArray().OfType<JsonObject>();
IEnumerable<JsonObject> Strategies(JsonObject node) => node["strategies"]!.AsArray().OfType<JsonObject>();

// T01 三格式识别
Check("T01", result.Configs.First(c => c.Name == "日常综合" && c.SourceFile.Contains("teabag-full")).Format == OneDragonFormat.TeabagTuple
    && result.Configs.First(c => c.Name == "公版现行配置").Format == OneDragonFormat.PublicCurrent
    && result.Configs.First(c => c.Name == "旧版配置").Format == OneDragonFormat.LegacyNameBool
    && result.Configs.First(c => c.SourceFile.Contains("teabag-sample")).Format == OneDragonFormat.TeabagTuple,
    "三种格式 + 仓库样例均正确识别");

// T02 枚举顺序 = 执行顺序
var sampleStd = Std("灭绝提瓦特");
var orderNames = sampleStd["TaskOrder"]!.AsArray()
    .Select(id => sampleStd["TaskDefinitions"]![id!.GetValue<string>()]!.GetValue<string>()).ToList();
var expectedSeq = new[] { "CD采集新-特产炼金", "CD采集-神秘兽肉", "CD采集新-食材", "CD采集新-挖矿", "CD采集-额外狗粮",
    "CD采集新-食材蒙德一条龙", "CD采集新-食材璃月一条龙", "CD采集新-食材稻妻一条龙", "CD采集新-食材须弥一条龙",
    "CD采集新-食材枫丹一条龙", "CD采集新-食材纳塔一条龙", "CD采集新-食材挪德卡莱一条龙", "CD采集新-食材", "砍树" };
Check("T02", orderNames.SequenceEqual(expectedSeq), "样例顺序保持文档枚举序（含同名任务位置不动）");

// T03 同名重复任务各自独立 ID（teabag-full 冲突后 ConfigKey 带哈希后缀）
var fullKey = result.Configs.First(c => c.SourceFile.Contains("teabag-full")).ConfigKey;
var fullStd = Std(fullKey);
var mailIds = fullStd["TaskDefinitions"]!.AsObject()
    .Where(kv => kv.Value!.GetValue<string>() == "领取邮件").Select(kv => kv.Key).ToList();
Check("T03", mailIds.Count == 2 && mailIds.Distinct().Count() == 2
    && fullStd["TaskOrder"]!.AsArray().Count == 5, "同名「领取邮件」×2 各自独立 ID 且全部保留");

// T04 重复迁移复用映射（幂等）：prior 仅作只读种子
var seed = new MigrationResult();
foreach (var kv in result.IdMappings) seed.IdMappings[kv.Key] = new Dictionary<string, string>(kv.Value);
seed.Schedule = result.Schedule;
var outRoot2 = Path.Combine(root, "_out_rerun");
if (Directory.Exists(outRoot2)) Directory.Delete(outRoot2, true);
var rerun = OneDragonMigrationEngine.Migrate(new[] { F("teabag-full.json") }, null, outRoot2, seed);
var rerunStd = (JsonObject)JsonNode.Parse(File.ReadAllText(
    Path.Combine(rerun.CandidateDir!, "standard", "OneDragon", rerun.Configs.First().OutputName + ".json")))!;
var firstIds = fullStd["TaskOrder"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
Check("T04", rerunStd["TaskOrder"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(firstIds),
    "携带既有映射种子重复迁移，任务 ID 不变");

// T05 输入字节不变
Check("T05", inputHashes.All(kv => OneDragonMigrationEngine.Sha256Of(File.ReadAllBytes(F(kv.Key))) == kv.Value),
    "全部输入文件哈希迁移前后一致");

// T06 坏文件隔离（语法坏 + 数组形状 + 混合形状），不中断整批
Check("T06", result.Configs.Count(c => c.ParseError != null) == 3
    && result.Issues.Any(i => i.Severity == "error" && i.Message.Contains("坏文件隔离"))
    && !File.Exists(Path.Combine(cdir, "standard", "OneDragon", "坏文件.json"))
    && fullStd != null,
    "语法坏/数组形状/混合形状三类坏文件均隔离，整批继续");

// T07 NextTaskIndex → NextTaskId
var nextId = fullStd["NextTaskId"]!.GetValue<string>();
Check("T07", nextId.Length > 0 && fullStd["TaskDefinitions"]![nextId]!.GetValue<string>() == "领取邮件",
    "NextTaskIndex=5 映射到首个「领取邮件」的新 taskId");

// T08 失效游标：置空 + error 级阻断（非 warn）
var missingStd = Std("失效游标配置");
Check("T08", missingStd["NextTaskId"]!.GetValue<string>() == string.Empty
    && result.Issues.Any(i => i.Severity == "error" && i.Config == "失效游标配置" && i.Message.Contains("NextTaskIndex=42")),
    "NextTaskIndex=42 无对应任务 → 置空 + error 阻断");

// T09 删除项回退（C12/C13/C14）
Check("T09", fullStd["DomainName"]!.GetValue<string>() == string.Empty
    && fullStd["MondayDomainName"]!.GetValue<string>() == string.Empty
    && result.Dropped.Any(d => d.Field == "ResinCount")
    && result.Dropped.Any(d => d.Field == "DomainName" && d.Detail == "首领讨伐")
    && result.Dropped.Any(d => d.Field == "MondayDomainName" && d.Detail == "CD采集-狗粮"),
    "事件标记/自定义组回退标准秘境，树脂覆盖登记删除");

// T10 全局调度只附着到旧选中计划流程
var workFlow = Flow("工作日计划");
Check("T10", workFlow["triggers"]![0]!["time"]!.GetValue<string>() == "06:30"
    && workFlow["loop"]!["mode"]!.GetValue<string>() == "scheduled"
    && workFlow["loop"]!["time"]!.GetValue<string>() == "04:00"
    && workFlow["loop"]!["skipAcrossDays"]!.GetValue<bool>()
    && workFlow["terminal"]![0]!["action"]!.GetValue<string>() == "关闭游戏并关机"
    && workFlow["execution"]!["suppressConfigCompletionAction"]!.GetValue<bool>(),
    "触发器/循环/跨天/终止/单配置收尾抑制全部生成且仅在选中计划流程");

// T11 账号策略 + 分享脱敏
var accountNode = Nodes(workFlow).First(n => n["ref"]!["config"]!.GetValue<string>() == "日常综合");
var accountSt = Strategies(accountNode).First(s => s["kind"]!.GetValue<string>() == "prerequisite.account");
var share = OneDragonMigrationEngine.ExportShareCopy(Path.Combine(cdir, "flows", "工作日计划.flow.json"));
var shareSt = Nodes(share).SelectMany(Strategies)
    .First(s => s["kind"]!.GetValue<string>() == "prerequisite.account");
Check("T11", accountSt["uid"]!.GetValue<string>() == "100000001"
    && shareSt["uid"]!.GetValue<string>() == string.Empty
    && Strategies(accountNode).Any(s => s["kind"]!.GetValue<string>() == "prerequisite.redeemCode"),
    "账号策略本机保留 UID，分享副本脱敏，兑换码前置策略生成");

// T12 多 NextConfiguration 冲突 → error + 无水位
Check("T12", workFlow["watermark"] == null
    && result.Issues.Any(i => i.Severity == "error" && i.Message.Contains("多个 NextConfiguration")),
    "两个入口标记 → 冲突报错，水位不自动设定");

// T13 标准配置无茶包字段
var teabagOnly = new[] { "Version", "IndexId", "NextConfiguration", "NextTaskIndex", "Period", "PeriodList",
    "ScheduleName", "CustomDomainList", "ResinCount", "SpecifyResinUse", "GenshinUid", "AccountBinding",
    "AccountBindingCode", "IsAutoDomainExpanded", "SundayDaySelectedValue" };
Check("T13", teabagOnly.All(k => !fullStd.ContainsKey(k)), "标准投影不含任何茶包专有字段");

// T14 公版现行原样保留
var pubStd = Std("公版现行配置");
Check("T14", pubStd["TaskOrder"]![0]!.GetValue<string>() == "aaaaaaaa-1111-2222-3333-444444444444"
    && pubStd["NextTaskId"]!.GetValue<string>() == "aaaaaaaa-1111-2222-3333-444444444444",
    "公版 ID/顺序/NextTaskId 原样保留，不重新生成");

// T15 周日字段改名 + 补公版新字段
Check("T15", fullStd["SundaySelectedValue"]!.GetValue<string>() == "2"
    && fullStd["SundayWeeklySelectedValue"]!.GetValue<string>() == "0",
    "SundayDaySelectedValue→SundaySelectedValue=2，SundayWeeklySelectedValue 补默认 0");

// T16 星期条件（本地语义标注）
var weekdays = Strategies(accountNode).First(s => s["kind"]!.GetValue<string>() == "condition.weekdays");
Check("T16", weekdays["days"]!.AsArray().Select(d => d!.GetValue<string>()).SequenceEqual(new[] { "周一", "周三", "周五" })
    && weekdays["dayBoundary"]!.GetValue<string>() == "localMidnight",
    "周一/三/五条件生成，本地午夜语义显式标注");

// T17 公版缺 TaskOrder：ID 保留（不降级为名称键），枚举序
var noOrderStd = Std("公版无顺序表配置");
Check("T17", noOrderStd["TaskOrder"]![0]!.GetValue<string>() == "cccccccc-1111-2222-3333-444444444444"
    && noOrderStd["TaskDefinitions"]!["cccccccc-1111-2222-3333-444444444444"]!.GetValue<string>() == "领取邮件"
    && noOrderStd["NextTaskId"]!.GetValue<string>() == "cccccccc-1111-2222-3333-444444444444",
    "公版无顺序表：ID 不改写、起点保留、枚举补序");

// T18 同名/大小写冲突：报 error + 双产物并存不覆盖
var dupKey = result.Configs.First(c => c.SourceFile.Contains("dup-name")).ConfigKey;
Check("T18", fullKey != dupKey && dupKey.StartsWith("日常综合#")
    && result.Issues.Any(i => i.Severity == "error" && i.Message.Contains("同名/大小写冲突"))
    && StdOrNull(fullKey) != null && StdOrNull(dupKey) != null,
    "同名配置冲突报出，双方产物以唯一键区分");

// T19 每日键优先：不生成星期条件
var weekendFlow = Flow("周末计划");
var dailyNode = Nodes(weekendFlow).First(n => n["ref"]!["config"]!.GetValue<string>() == "每日优先配置");
Check("T19", !Strategies(dailyNode).Any(s => s["kind"]!.GetValue<string>() == "condition.weekdays"),
    "PeriodList「每日」=true 优先，星期勾选不产生条件");

// T20 未绑定账号配置保留 legacyFiltered 标记
var filteredNode = Nodes(workFlow).First(n => n["ref"]!["config"]!.GetValue<string>() == "失效游标配置");
Check("T20", filteredNode["legacyFiltered"]!.GetValue<bool>()
    && result.Issues.Any(i => i.Severity == "warn" && i.Message.Contains("legacyFiltered")),
    "旧连续过滤语义以标记保留，不偷偷纳入");

// T21 全局调度不复制到非选中流程（默认计划表/周末计划无触发器）
var defaultFlow = Flow("默认计划表");
Check("T21", defaultFlow["triggers"] == null && defaultFlow["loop"] == null && defaultFlow["terminal"] == null
    && weekendFlow["triggers"] == null
    && result.Issues.Any(i => i.Severity == "info" && i.Message.Contains("全局调度仅属旧选中计划")),
    "非选中流程无触发器/循环/终止，info 留痕");

// T22 路径越界归一化：非法字符替换，候选根外无产物
Check("T22", result.Issues.Any(i => i.Message.Contains("归一化"))
    && !File.Exists(Path.Combine(outRoot, "evil.json"))
    && !Directory.GetFiles(cdir, "evil.json", SearchOption.AllDirectories).Any()
    && Directory.GetFiles(cdir, "*.json", SearchOption.AllDirectories)
        .All(f => Path.GetFullPath(f).StartsWith(Path.GetFullPath(cdir), StringComparison.OrdinalIgnoreCase)),
    "../../evil 归一化，全部产物位于候选根内");

// T23 manifest 阻断状态
var manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(cdir, "manifest.json")))!;
Check("T23", manifest["activation"]!["status"]!.GetValue<string>() == "blocked"
    && workFlow["activation"]!["status"]!.GetValue<string>() == "blocked",
    "error 级问题 → manifest 与流程均标记 blocked（非仅文本警告）");

// T24 未识别字段与依赖清单入报告
Check("T24", result.Unrecognized.Any(u => u.Field == "SomeFutureField" && u.Config == "日常综合")
    && result.Dependencies[fullKey].Contains("CD采集-狗粮"),
    "SomeFutureField 登记未识别，配置组依赖入清单");

// T25 绑定启用但 UID 为空 → warn
Check("T25", result.Issues.Any(i => i.Severity == "warn" && i.Message.Contains("UID 为空")),
    "空 UID 绑定配置留痕待确认");

// T26 流程引用携带稳定键与修订
Check("T26", accountNode["ref"]!["configKey"]!.GetValue<string>() == fullKey
    && accountNode["ref"]!["revision"]!.GetValue<string>() == result.StandardHashes[fullKey]
    && accountNode["nodeId"]!.GetValue<string>().StartsWith("n-"),
    "资源引用含 configKey + revision，节点 ID 确定性派生");

// T27 节点 ID 跨迁移稳定（重跑同配置同 nodeId）
var rerunFlow = (JsonObject)JsonNode.Parse(File.ReadAllText(
    Path.Combine(rerun.CandidateDir!, "flows", "工作日计划.flow.json")))!;
var rerunNode = Nodes(rerunFlow).First(n => n["ref"]!["config"]!.GetValue<string>() == "日常综合");
Check("T27", rerunNode["nodeId"]!.GetValue<string>() == accountNode["nodeId"]!.GetValue<string>(),
    "同一配置重跑节点 ID 不变（内容哈希派生）");

Console.WriteLine($"\nR1 迁移合同回归：{passed} 通过 / {failed} 失败 / 共 {passed + failed} 项");
Console.WriteLine($"候选目录：{cdir}");
return failed == 0 ? 0 : 1;
