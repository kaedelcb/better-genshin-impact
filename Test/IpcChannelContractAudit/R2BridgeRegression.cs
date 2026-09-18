using System.Text;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using BetterGenshinImpact.Service.OneDragon;
using Newtonsoft.Json.Linq;

/// <summary>
/// R2 兼容桥与执行事实回归（2026-09-18）：整组写入合同、引用服务、幂等身份残余、离线全链。
/// 仅使用临时目录夹具与内存对象；不触碰真实 User 配置、UI、管道或游戏。
/// </summary>
internal static class R2BridgeRegression
{
    private static void Must(bool value, string message) { if (!value) throw new Exception(message); }
    private static string Key() => Guid.NewGuid().ToString("N");

    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("T60 declared group-write contract requires revision, legacy entry stays compatible", () => {
            var missing = InstanceIpcEnvelope.Request("ext.config.applyGroup",
                new { groupName = "g", configWriteContract = 1, scriptGroupConfigJson = "{}" });
            Must(GroupConfigWriteContract.ValidateApplyGroup(missing)?.ErrorCode == "invalid_request",
                "declared contract without revision applied");
            var complete = InstanceIpcEnvelope.Request("ext.config.applyGroup",
                new { groupName = "g", configWriteContract = 1, expectedConfigRevision = "rev", scriptGroupConfigJson = "{}" });
            Must(GroupConfigWriteContract.ValidateApplyGroup(complete) == null, "declared contract with revision rejected");
            var legacy = InstanceIpcEnvelope.Request("config.apply_group", new { groupName = "g", scriptGroupConfigJson = "{}" });
            Must(GroupConfigWriteContract.ValidateApplyGroup(legacy) == null, "legacy entry without revision blocked");
            var legacyWithRevision = InstanceIpcEnvelope.Request("config.apply_group",
                new { groupName = "g", expectedConfigRevision = "rev", scriptGroupConfigJson = "{}" });
            Must(GroupConfigWriteContract.ValidateApplyGroup(legacyWithRevision) == null, "legacy entry with revision blocked");
            return Task.CompletedTask;
        });

        await check("T61 protected legacy tuple configs are skipped by reference rename, bytes untouched and reported", () => {
            // R3.0 硬门槛：茶包 tuple 形状受保护（待迁移）——引用重命名只读跳过并逐文件报告，绝不改写
            using var fixture = new ReferenceFixture();
            var a = fixture.WriteOneDragon("a.json",
                "{\"TaskEnabledList\":{\"1\":{\"Item1\":true,\"Item2\":\"锄地组\"},\"2\":{\"Item1\":true,\"Item2\":\"其他\"}},"
                + "\"CustomDomainList\":[\"锄地组\",\"别的\"],\"MondayDomainName\":\"锄地组\",\"partyName\":\"不变\"}");
            var b = fixture.WriteOneDragon("b.json", "{\"TaskEnabledList\":{\"1\":{\"Item1\":true,\"Item2\":\"别的\"}}}");
            var report = OneDragonConfigReferenceService.RenameGroupReferences(fixture.OneDragonDir, "锄地组", "新锄地");
            Must(report.FilesScanned == 2 && report.FilesChanged == 0, $"protected files must not change: {report}");
            Must(report.Details.Any(d => d.Contains("a.json") && d.Contains("待迁移"))
                && report.Details.Any(d => d.Contains("b.json") && d.Contains("待迁移")), "protection skip not reported per file");
            Must(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "a.json")) == a
                && File.ReadAllText(Path.Combine(fixture.OneDragonDir, "b.json")) == b, "protected file bytes changed");
            return Task.CompletedTask;
        });

        await check("T62 protected legacy tuple configs are skipped by reference delete, bytes untouched and reported", () => {
            using var fixture = new ReferenceFixture();
            var a = fixture.WriteOneDragon("a.json",
                "{\"TaskEnabledList\":{\"1\":{\"Item1\":true,\"Item2\":\"锄地组\"},\"2\":{\"Item1\":false,\"Item2\":\"保留\"}},"
                + "\"CustomDomainList\":[\"锄地组\"],\"FridayDomainName\":\"锄地组\",\"SundayDomainName\":\"留着\"}");
            var report = OneDragonConfigReferenceService.DeleteGroupReferences(fixture.OneDragonDir, "锄地组");
            Must(report.FilesChanged == 0, $"protected file must not change: {report}");
            Must(report.Details.Any(d => d.Contains("a.json") && d.Contains("待迁移")), "protection skip not reported");
            Must(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "a.json")) == a, "protected file bytes changed");
            return Task.CompletedTask;
        });

        await check("T63 native schema updates taskDefinitions only and never synthesizes tuples", () => {
            using var fixture = new ReferenceFixture();
            fixture.WriteOneDragon("native.json",
                "{\"TaskEnabledList\":{\"id-a\":true,\"id-b\":false},\"TaskOrder\":[\"id-a\",\"id-b\"],"
                + "\"TaskDefinitions\":{\"id-a\":\"锄地组\",\"id-b\":\"秘境\"},\"NextTaskId\":\"id-a\"}", withBom: true);
            var report = OneDragonConfigReferenceService.RenameGroupReferences(fixture.OneDragonDir, "锄地组", "新锄地");
            Must(report.FilesChanged == 1, $"rename report wrong: {report}");
            var renamed = JObject.Parse(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "native.json")));
            Must(renamed["TaskDefinitions"]!["id-a"]!.ToString() == "新锄地", "taskDefinitions not renamed");
            Must(renamed["TaskEnabledList"]!["id-a"]!.Type == JTokenType.Boolean, "native bool entry turned into tuple");
            Must(File.ReadAllBytes(Path.Combine(fixture.OneDragonDir, "native.json")).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }),
                "BOM lost on rename");
            var deleteReport = OneDragonConfigReferenceService.DeleteGroupReferences(fixture.OneDragonDir, "新锄地");
            Must(deleteReport.FilesChanged == 1, $"delete report wrong: {deleteReport}");
            var deleted = JObject.Parse(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "native.json")));
            Must(deleted["TaskDefinitions"]!["id-a"] == null, "definition not removed");
            Must(deleted["TaskEnabledList"]!["id-a"] == null, "enabled entry not removed");
            Must(((JArray)deleted["TaskOrder"]!).Count == 1, "order entry not removed");
            Must(deleted["NextTaskId"]!.ToString() == "", "nextTaskId not cleared");
            Must(deleted["TaskDefinitions"]!["id-b"]!.ToString() == "秘境", "unrelated definition changed");
            return Task.CompletedTask;
        });

        await check("T64 malformed file is isolated, others still processed, nothing overwritten", () => {
            using var fixture = new ReferenceFixture();
            var broken = fixture.WriteOneDragon("broken.json", "{ 这不是合法 json");
            fixture.WriteOneDragon("ok.json", "{\"TaskEnabledList\":{\"guid-1\":true},\"TaskDefinitions\":{\"guid-1\":\"锄地组\"},\"TaskOrder\":[\"guid-1\"]}");
            var report = OneDragonConfigReferenceService.RenameGroupReferences(fixture.OneDragonDir, "锄地组", "新锄地");
            Must(report.FilesScanned == 2 && report.FilesChanged == 1, $"report wrong: {report}");
            Must(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "broken.json")) == broken, "broken file overwritten");
            Must(JObject.Parse(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "ok.json")))["TaskDefinitions"]!["guid-1"]!
                .ToString() == "新锄地", "healthy file not processed");
            return Task.CompletedTask;
        });

        await check("T65 explicit keys never merge with same-name jobs, kind and legacy adoption preserved", () => {
            var registry = new JobRegistry(false);
            var a = registry.Submit(JobKind.OneDragon, "同名", JobSource.Ext, generation: 1, idempotencyKey: Key());
            var b = registry.Submit(JobKind.OneDragon, "同名", JobSource.Ext, generation: 1, idempotencyKey: Key());
            Must(!b.Adopted && a.Job.JobId != b.Job.JobId, "explicit different keys merged into one job");
            var c = registry.Submit(JobKind.Group, "同名", JobSource.Ext, generation: 1, idempotencyKey: Key());
            Must(!c.Adopted && c.Job.JobId != a.Job.JobId, "same name different kind merged");
            var d = registry.Submit(JobKind.OneDragon, "旧名", JobSource.V2, generation: 2);
            var e = registry.Submit(JobKind.OneDragon, "旧名", JobSource.V2, generation: 2);
            Must(e.Adopted && e.Job.JobId == d.Job.JobId, "legacy no-key same-name adoption lost");
            var f = registry.Submit(JobKind.OneDragon, "旧名", JobSource.Ext, generation: 2, idempotencyKey: Key());
            Must(!f.Adopted && f.Job.JobId != d.Job.JobId, "explicit key fell back to same-name job");
            return Task.CompletedTask;
        });

        await check("T66 offline full chain: describe/apply/guarded start preparation without any server", async () => {
            // 离线断言：全程无服务器/网络——本机内存对象 + 临时目录即完成 describe→apply→守卫启动准备。
            using var fixture = new ReferenceFixture();
            fixture.WriteOneDragon("dragon.json",
                "{\"TaskEnabledList\":{\"guid-a\":true,\"guid-b\":false},\"TaskDefinitions\":{\"guid-a\":\"秘境\",\"guid-b\":\"锄地组\"},\"TaskOrder\":[\"guid-a\",\"guid-b\"]}");
            var store = new TaskConfigurationContract(fixture.Root);
            var described = await ExternalInterfaceConfigurationPlane.DispatchAsync(
                InstanceIpcEnvelope.Request("ext.config.describe", new { configName = "dragon" }), store);
            Must(described.Success == true, "describe failed offline");
            var revision = described.Data!["configRevision"]!.ToString();
            var applied = await ExternalInterfaceConfigurationPlane.DispatchAsync(
                InstanceIpcEnvelope.Request("ext.config.applyTaskState",
                    new { configName = "dragon", taskId = "guid-b", enabled = true, expectedConfigRevision = revision }), store);
            Must(applied.Success == true && applied.Data!["status"]!.ToString() == "config_applied"
                && applied.Data!["configRevision"]!.ToString() != revision, "apply failed offline");
            var stale = await ExternalInterfaceConfigurationPlane.DispatchAsync(
                InstanceIpcEnvelope.Request("ext.config.applyTaskState",
                    new { configName = "dragon", taskId = "guid-a", enabled = false, expectedConfigRevision = revision }), store);
            Must(stale.ErrorCode == "configuration_changed", "stale revision accepted offline");
            var prepared = await ExternalInterfaceConfigurationPlane.PrepareExecutionAsync(
                InstanceIpcEnvelope.Request("ext.task.start", new {
                    configName = "dragon", taskId = "guid-b",
                    expectedConfigRevision = applied.Data!["configRevision"]!.ToString() }), store);
            Must(prepared.SingleIndex == null, "native single-task preparation must not resolve to a numeric index");

            // [会诊发现 #7] 不停在准备阶段：真实经会话路由启动，由协调器跑到终态，再查 ext.job.status。
            // 执行体是惰性桩（不碰磁盘/游戏），验证的是 守卫准备→路由→协调器→终态查询 的离线全链本身。
            // R3：一条龙起点为字符串任务 ID（startFromTaskId），数字索引已退役。
            var handler = new InstanceRequestHandler();
            var session = ExternalInterfaceSession.GetOrCreate(new InstanceConnection());
            var started = await session.RouteAsync(handler, InstanceIpcEnvelope.Request("ext.task.start", new {
                configName = "dragon",
                startFromTaskId = "guid-b",
                expectedConfigRevision = applied.Data!["configRevision"]!.ToString(),
                idempotencyKey = Key() }), default);
            Must(started.Success == true && started.Data!["taskHandle"] != null,
                "guarded start rejected offline: " + started.ErrorCode);
            var handle = Guid.Parse(started.Data!["taskHandle"]!.ToString());
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (BgiTaskCoordinator.Instance.QueryItemStatus(handle).Status != "completed"
                   && DateTime.UtcNow < deadline) await Task.Delay(5);
            Must(BgiTaskCoordinator.Instance.QueryItemStatus(handle).Status == "completed",
                "started job did not reach terminal state");
            Must(handler.Executions == 1, "start did not reach execution segment exactly once");
            // [会诊第二轮 #6] 准备阶段选定的配置与任务项必须原样到达执行边界（不是只断言「跑过一次」）
            Must(handler.LastConfig == "dragon" && handler.LastTaskId == "guid-b",
                $"prepared config/task did not reach execution boundary: config={handler.LastConfig}, taskId={handler.LastTaskId}");
            var status = await session.RouteAsync(handler,
                InstanceIpcEnvelope.Request("ext.job.status", new { jobId = handle }), default);
            // 作业状态词汇表（BgiJobState）：succeeded/failed/cancelled；协调器条目的 "completed" 对应作业态 succeeded
            Must(status.Success == true && status.Data!["status"]!.ToString() == "succeeded",
                "job.status query did not report terminal state: " + status.Data!["status"]);
            return;
        });

        await check("T67 legacy name-key (v0) configs are protected from reference writes, bytes untouched and reported", () => {
            // R3.0 硬门槛：旧 name→bool 形状受保护（待迁移）——重命名/删除引用一律只读跳过，legacy 改写分支不可达
            using var fixture = new ReferenceFixture();
            var legacy = fixture.WriteOneDragon("legacy.json",
                "{\"TaskEnabledList\":{\"锄地组\":true,\"其他\":false},\"CustomDomainList\":[\"锄地组\"],\"WednesdayDomainName\":\"锄地组\"}");
            var renamed = OneDragonConfigReferenceService.RenameGroupReferences(fixture.OneDragonDir, "锄地组", "新锄地");
            Must(renamed.FilesChanged == 0 && renamed.Details.Any(d => d.Contains("legacy.json") && d.Contains("待迁移")),
                $"legacy rename not protected: {renamed}");
            Must(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "legacy.json")) == legacy, "legacy file rewritten by rename");
            var deleted = OneDragonConfigReferenceService.DeleteGroupReferences(fixture.OneDragonDir, "锄地组");
            Must(deleted.FilesChanged == 0 && deleted.Details.Any(d => d.Contains("legacy.json") && d.Contains("待迁移")),
                $"legacy delete not protected: {deleted}");
            Must(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "legacy.json")) == legacy, "legacy file rewritten by delete");
            return Task.CompletedTask;
        });

        await check("T68 mixed-shape document is isolated for both rename and delete, bytes untouched", () => {
            using var fixture = new ReferenceFixture();
            var original = fixture.WriteOneDragon("mixed.json",
                "{\"taskEnabledList\":{\"1\":{\"Item1\":true,\"Item2\":\"锄地组\"},\"锄地组\":true},"
                + "\"customDomainList\":[\"锄地组\"]}");
            var renamed = OneDragonConfigReferenceService.RenameGroupReferences(fixture.OneDragonDir, "锄地组", "新锄地");
            Must(renamed.FilesChanged == 0, "mixed document renamed despite isolation");
            Must(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "mixed.json")) == original, "mixed document rewritten by rename");
            var deleted = OneDragonConfigReferenceService.DeleteGroupReferences(fixture.OneDragonDir, "锄地组");
            Must(deleted.FilesChanged == 0, "mixed document deleted despite isolation");
            Must(File.ReadAllText(Path.Combine(fixture.OneDragonDir, "mixed.json")) == original, "mixed document rewritten by delete");
            return Task.CompletedTask;
        });

        await check("T69 rename preserves date-like strings, float representation and unknown fields", () => {
            using var fixture = new ReferenceFixture();
            fixture.WriteOneDragon("keep.json",
                "{\"TaskEnabledList\":{\"guid-1\":true},\"TaskDefinitions\":{\"guid-1\":\"锄地组\"},\"TaskOrder\":[\"guid-1\"],"
                + "\"lastRun\":\"2026-09-18T08:30:00+08:00\",\"ratio\":0.30000000000000004,"
                + "\"unknownField\":{\"nested\":[1,2,\"x\"]}}");
            var report = OneDragonConfigReferenceService.RenameGroupReferences(fixture.OneDragonDir, "锄地组", "新锄地");
            Must(report.FilesChanged == 1, $"report wrong: {report}");
            var text = File.ReadAllText(Path.Combine(fixture.OneDragonDir, "keep.json"));
            Must(text.Contains("\"2026-09-18T08:30:00+08:00\""), "date-like string mutated");
            Must(text.Contains("0.30000000000000004"), "float representation lost precision");
            Must(text.Contains("\"nested\"") && text.Contains("\"x\""), "unknown field lost");
            Must(JObject.Parse(text)["TaskDefinitions"]!["guid-1"]!.ToString() == "新锄地", "rename not applied");
            return Task.CompletedTask;
        });

        await check("T70 files with non-round-trippable number literals are isolated, bytes untouched", () => {
            // [会诊第三轮 #5] 科学计数法/下溢字面量经 decimal 往返会改变表示：此类文件必须隔离跳过且字节不动
            using var fixture = new ReferenceFixture();
            var exp = fixture.WriteOneDragon("exp.json",
                "{\"TaskEnabledList\":{\"guid-1\":true},\"TaskDefinitions\":{\"guid-1\":\"锄地组\"},\"TaskOrder\":[\"guid-1\"],\"ratio\":1e2}");
            var tiny = fixture.WriteOneDragon("tiny.json",
                "{\"TaskEnabledList\":{\"guid-1\":true},\"TaskDefinitions\":{\"guid-1\":\"锄地组\"},\"TaskOrder\":[\"guid-1\"],\"epsilon\":1e-29}");
            fixture.WriteOneDragon("ok.json",
                "{\"TaskEnabledList\":{\"guid-1\":true},\"TaskDefinitions\":{\"guid-1\":\"锄地组\"},\"TaskOrder\":[\"guid-1\"],\"ratio\":0.5}");
            // 超精度样例（33 位小数，超 decimal 28-29 位精度）
            var prec = fixture.WriteOneDragon("prec.json",
                "{\"TaskEnabledList\":{\"guid-1\":true},\"TaskDefinitions\":{\"guid-1\":\"锄地组\"},\"TaskOrder\":[\"guid-1\"],\"ratio\":0.123456789012345678901234567890123}");
            var report = OneDragonConfigReferenceService.RenameGroupReferences(fixture.OneDragonDir, "锄地组", "新锄地");
            Must(report.FilesChanged == 1, $"expected only ok.json changed: {report}");
            // [会诊第四轮 #3] 字节级比较（ReadAllText 可能掩盖编码差异）+ 逐文件报告必须含文件名定位
            var utf8 = new UTF8Encoding(false);
            Must(File.ReadAllBytes(Path.Combine(fixture.OneDragonDir, "exp.json")).SequenceEqual(utf8.GetBytes(exp)),
                "exponent-literal file bytes changed");
            Must(File.ReadAllBytes(Path.Combine(fixture.OneDragonDir, "tiny.json")).SequenceEqual(utf8.GetBytes(tiny)),
                "tiny-literal file bytes changed");
            Must(File.ReadAllBytes(Path.Combine(fixture.OneDragonDir, "prec.json")).SequenceEqual(utf8.GetBytes(prec)),
                "precision-literal file bytes changed");
            Must(report.Details.Any(d => d.Contains("数值字面") && d.Contains("exp.json")), "exp.json isolation not reported by name");
            Must(report.Details.Any(d => d.Contains("数值字面") && d.Contains("tiny.json")), "tiny.json isolation not reported by name");
            Must(report.Details.Any(d => d.Contains("数值字面") && d.Contains("prec.json")), "prec.json isolation not reported by name");
            return Task.CompletedTask;
        });

        await check("T71 group rename file move: verified convergence, conflicts never delete or overwrite", () => {
            // [会诊第五-七轮] 改名文件操作决策矩阵：助手绝不删除文件；半成品跳过移动需目标身份校验；
            // 双存/冒名/歧义一律冲突拒绝且所有文件字节不动；纯大小写改名按同文件别名处理。
            using var fixture = new ReferenceFixture();
            var dir = Path.Combine(fixture.Root, "ScriptGroup");
            var utf8 = new UTF8Encoding(false);
            string Bytes(string f) => Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir, f)));

            // 1. 正常移动
            File.WriteAllText(Path.Combine(dir, "a.json"), "{\"name\":\"a\"}");
            Must(BetterGenshinImpact.Service.ScriptGroupFileRename.MoveForRename(dir, "a", "b"), "normal move skipped");
            Must(!File.Exists(Path.Combine(dir, "a.json")) && Bytes("b.json") == Convert.ToBase64String(utf8.GetBytes("{\"name\":\"a\"}")),
                "normal move wrong");

            // 2. 可验证半成品：旧不在、新文件内部名==旧名 → 跳过移动、目标字节不动
            var half = "{\"name\":\"a2\"}";
            File.WriteAllText(Path.Combine(dir, "b2.json"), half);
            Must(!BetterGenshinImpact.Service.ScriptGroupFileRename.MoveForRename(dir, "a2", "b2"),
                "verified half-done not detected");
            Must(Bytes("b2.json") == Convert.ToBase64String(utf8.GetBytes(half)), "half-done target bytes changed");

            // 3. 两文件并存（半成品期 UI 重建旧文件）→ 冲突拒绝、两文件字节都不动（不删除任何文件）
            File.WriteAllText(Path.Combine(dir, "a3.json"), "{\"name\":\"a3\",\"fresh\":1}");
            File.WriteAllText(Path.Combine(dir, "b3.json"), "{\"name\":\"a3\"}");
            var b3before = Bytes("b3.json"); var a3before = Bytes("a3.json");
            var conflict = false;
            try { BetterGenshinImpact.Service.ScriptGroupFileRename.MoveForRename(dir, "a3", "b3"); }
            catch (IOException) { conflict = true; }
            Must(conflict, "both-exist not rejected");
            Must(Bytes("a3.json") == a3before && Bytes("b3.json") == b3before, "conflict path modified files (a3/b3)");

            // 4. 冒名目标：旧不在、新文件内部名是别的组 → 冲突拒绝、目标字节不动
            var impostor = "{\"name\":\"c4\"}";
            File.WriteAllText(Path.Combine(dir, "b4.json"), impostor);
            conflict = false;
            try { BetterGenshinImpact.Service.ScriptGroupFileRename.MoveForRename(dir, "a4", "b4"); }
            catch (IOException) { conflict = true; }
            Must(conflict, "impostor target accepted");
            Must(Bytes("b4.json") == Convert.ToBase64String(utf8.GetBytes(impostor)), "impostor target bytes changed");

            // 5. 两并存且目标内部名==新名（可能无关组占名）→ 冲突拒绝、两文件字节不动
            File.WriteAllText(Path.Combine(dir, "a5.json"), "{\"name\":\"a5\"}");
            File.WriteAllText(Path.Combine(dir, "b5.json"), "{\"name\":\"b5\"}");
            var a5before = Bytes("a5.json"); var b5before = Bytes("b5.json");
            conflict = false;
            try { BetterGenshinImpact.Service.ScriptGroupFileRename.MoveForRename(dir, "a5", "b5"); }
            catch (IOException) { conflict = true; }
            Must(conflict && Bytes("a5.json") == a5before && Bytes("b5.json") == b5before,
                "ambiguous both-exist not rejected cleanly");

            // 6. 源缺失且目标不在 → 原生 FileNotFoundException（失败可见）
            var notFound = false;
            try { BetterGenshinImpact.Service.ScriptGroupFileRename.MoveForRename(dir, "missing", "x"); }
            catch (FileNotFoundException) { notFound = true; }
            Must(notFound, "missing source silently accepted");

            // 7. 纯大小写改名（Windows 同文件别名）→ 跳过移动、文件字节不动（内容保存由调用方负责）
            var aliasContent = "{\"name\":\"alias\"}";
            File.WriteAllText(Path.Combine(dir, "alias.json"), aliasContent);
            Must(!BetterGenshinImpact.Service.ScriptGroupFileRename.MoveForRename(dir, "alias", "ALIAS"),
                "case-alias not treated as same file");
            Must(Bytes("alias.json") == Convert.ToBase64String(utf8.GetBytes(aliasContent)), "alias file bytes changed");
            return Task.CompletedTask;
        });
    }

    /// <summary>临时目录夹具（路径安全校验与 ContractRegression.Fixture 同纪律）。</summary>
    private sealed class ReferenceFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bgi-channel-contract-" + Guid.NewGuid().ToString("N"));
        public string OneDragonDir => Path.Combine(Root, "OneDragon");

        public ReferenceFixture()
        {
            Directory.CreateDirectory(OneDragonDir);
            Directory.CreateDirectory(Path.Combine(Root, "ScriptGroup"));
        }

        /// <summary>写入一条龙配置并返回原文（用于"未变更文件不被改写"断言）。</summary>
        public string WriteOneDragon(string fileName, string content, bool withBom = false)
        {
            File.WriteAllText(Path.Combine(OneDragonDir, fileName), content, new UTF8Encoding(withBom));
            return content;
        }

        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(path) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(path).StartsWith("bgi-channel-contract-", StringComparison.Ordinal))
                throw new Exception("Unsafe test cleanup path");
            Directory.Delete(path, true);
        }
    }
}