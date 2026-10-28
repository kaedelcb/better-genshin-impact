using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services.Gateway;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class DodocoWebRelayTests
{
    [Fact]
    public void RulesAndSettingsUseIsolatedPersistentStoreAndRejectInvalidInput()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "dodoco-web-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new DodocoSettingsService(dir);
            Assert.False(settings.Current.ShareDodocoWeb);
            Assert.Equal("sharing_disabled", DodocoWebRelayService.AuthorizationError(settings.Current, "watch.read"));
            Assert.True(settings.TryUpdate(s => { s.ShareDodocoWeb = true; s.ShareLogFiles = false; }));
            Assert.Null(DodocoWebRelayService.AuthorizationError(settings.Current, "watch.read"));
            Assert.Equal("log_sharing_disabled", DodocoWebRelayService.AuthorizationError(settings.Current, "report.day"));
            Assert.True(settings.TryUpdate(s => s.ShareDodocoWeb = false));
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, dir);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => dir), () => null);
            var args = JsonNode.Parse("""{"rule":{"id":"web-rule","name":"超时","pattern":"超时","minLevel":"WRN","enabled":true}}""")!.AsObject();
            relay.Execute("watch.rule.upsert", args);
            Assert.Contains(watch.GetRules(), x => x.Id == "web-rule" && x.Name == "超时");
            Assert.Contains("web-rule", File.ReadAllText(Path.Combine(dir, "dodoco_watch_rules.json")));
            Assert.ThrowsAny<Exception>(() => relay.Execute("watch.settings", JsonNode.Parse("""{"settings":{"shareDodocoWeb":true}}""")!.AsObject()));
            Assert.False(settings.Current.ShareDodocoWeb);
            relay.Execute("watch.rule.delete", JsonNode.Parse("""{"id":"web-rule"}""")!.AsObject());
            Assert.DoesNotContain(watch.GetRules(), x => x.Id == "web-rule");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReportsRequireAnExistingDirectoryAndParseTheRequestedDay()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "dodoco-report-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var service = new DailyReportService(() => dir);
            var date = new DateOnly(2026, 10, 28);
            File.WriteAllText(Path.Combine(dir, "better-genshin-impact20261028.log"),
                "[10:00:00.000] [INF] test\n配置组 \"示例\" 加载完成，共1个脚本，开始执行\n" +
                "[10:05:00.000] [INF] test\n配置组 \"示例\" 执行结束\n");
            Assert.Contains(date, service.EnumerateDatesStrict());
            var report = service.BuildReport(date, strict: true);
            Assert.Single(report.Groups);
            Assert.Equal("示例", report.Groups[0].Name);
            Assert.Equal(TimeSpan.FromMinutes(5), report.TotalDuration);
            Assert.Throws<DirectoryNotFoundException>(() => new DailyReportService(() => Path.Combine(dir, "missing")).EnumerateDatesStrict());
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void RecordsPageByCursorAndSnapshotFramesAreInChronologicalOrder()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "dodoco-record-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new DodocoSettingsService(dir);
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, dir, () => dir);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => dir), () => null, () => dir);
            var when = new DateTime(2026, 10, 28, 12, 0, 0);
            var path = Path.Combine(dir, "dodoco_exceptions.2026-10-28.jsonl");
            File.WriteAllLines(path, Enumerable.Range(0, 3).Select(i => JsonSerializer.Serialize(new ExceptionRecord
                { Time = when, RuleName = "Rule", RuleId = "rule", Message = $"line {i}" })));
            var framesDir = Path.Combine(dir, "incidents", "2026-10-28", "20261028_120000_Rule");
            Directory.CreateDirectory(framesDir);
            foreach (var name in new[] { "frame_+03.0.jpg", "frame_-01.0.jpg", "frame_-03.0.jpg", "frame_+00.0.jpg" })
                File.WriteAllBytes(Path.Combine(framesDir, name), [1, 2, 3]);
            var first = JsonSerializer.SerializeToNode(relay.Execute("watch.records", JsonNode.Parse("""{"date":"2026-10-28","limit":2}""")!.AsObject()), GatewayJson.Options)!.AsObject();
            Assert.Equal(3, first["total"]!.GetValue<int>());
            Assert.Equal("line 2", first["records"]![0]!["message"]!.GetValue<string>());
            var cursor = first["nextCursor"]!.AsObject();
            var nextArgs = new JsonObject { ["date"] = "2026-10-28", ["limit"] = 2,
                ["beforeTime"] = cursor["beforeTime"]!.GetValue<string>(), ["beforeLine"] = cursor["beforeLine"]!.GetValue<int>() };
            var second = JsonSerializer.SerializeToNode(relay.Execute("watch.records", nextArgs), GatewayJson.Options)!.AsObject();
            Assert.Equal("line 0", second["records"]![0]!["message"]!.GetValue<string>());
            Assert.Null(second["nextCursor"]);
            var incidentArgs = new JsonObject { ["date"] = "2026-10-28", ["time"] = when.ToString("O"), ["ruleName"] = "Rule" };
            var frames = JsonSerializer.SerializeToNode(relay.Execute("watch.incidents", incidentArgs), GatewayJson.Options)!["frames"]!.AsArray();
            Assert.Equal(new[] { "frame_-03.0.jpg", "frame_-01.0.jpg", "frame_+00.0.jpg", "frame_+03.0.jpg" },
                frames.Select(x => x!["name"]!.GetValue<string>()).ToArray());
            relay.Execute("watch.clear", new JsonObject { ["date"] = "2026-10-28" });
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DiagnosticScansLargeSourceAndBindsChunksToRequestingRoomAndConnection()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "dodoco-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = Path.Combine(dir, "better-genshin-impact20261028.log");
            using (var writer = new StreamWriter(log))
            {
                writer.WriteLine("[01:00:00.000] [INF] old");
                var filler = new string('x', 128 * 1024);
                for (var i = 0; i < 260; i++) writer.WriteLine(filler);
                writer.WriteLine("[12:01:00.000] [INF] chosen");
                writer.WriteLine("诊断范围中的日志");
            }
            Assert.True(new FileInfo(log).Length > 32 * 1024 * 1024);
            var settings = new DodocoSettingsService(dir);
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, dir, () => dir);
            var diag = new DiagnosticPackageService(() => dir, () => [], () => dir);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => dir), () => null, () => dir, diag);
            var start = JsonNode.Parse("""{"start":"2026-10-28T12:00:00","end":"2026-10-28T12:10:00"}""")!.AsObject();
            var result = JsonSerializer.SerializeToNode(relay.Execute("watch.diagnostic.start", start, "viewer-a", 1, default, "room-a"), GatewayJson.Options)!.AsObject();
            var id = result["id"]!.GetValue<string>();
            var chunk = new JsonObject { ["id"] = id, ["offset"] = 0 };
            Assert.Throws<InvalidOperationException>(() => relay.Execute("watch.diagnostic.chunk", chunk, "viewer-b", 1, default, "room-a"));
            Assert.Throws<InvalidOperationException>(() => relay.Execute("watch.diagnostic.chunk", chunk, "viewer-a", 1, default, "room-b"));
            var data = JsonSerializer.SerializeToNode(relay.Execute("watch.diagnostic.chunk", chunk, "viewer-a", 1, default, "room-a"), GatewayJson.Options)!.AsObject();
            Assert.True(data["done"]!.GetValue<bool>());
            using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(data["base64"]!.GetValue<string>())), ZipArchiveMode.Read);
            var entry = Assert.Single(zip.Entries.Where(e => e.FullName.StartsWith("bgi_log_slice_")));
            using var reader = new StreamReader(entry.Open());
            Assert.Contains("诊断范围中的日志", reader.ReadToEnd());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void RecordFiltersCoverEntireDayAndCursorKeepsOriginalLineIdentity()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "dodoco-filter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new DodocoSettingsService(dir);
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, dir, () => dir);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => dir), () => null, () => dir);
            var time = new DateTime(2026, 10, 28, 12, 0, 0);
            File.WriteAllLines(Path.Combine(dir, "dodoco_exceptions.2026-10-28.jsonl"), Enumerable.Range(0, 360).Select(i =>
                JsonSerializer.Serialize(new ExceptionRecord { Time = time, RuleId = i % 2 == 0 ? "r1" : "r2",
                    Level = i % 3 == 0 ? "ERR" : "INF", Message = $"记录 {i}", ContextBefore = ["上下文 KEY"] })));
            var args = new JsonObject { ["date"] = "2026-10-28", ["limit"] = 25,
                ["level"] = "err", ["ruleId"] = "r1", ["keyword"] = "key" };
            var seen = new List<string>();
            while (true)
            {
                var page = JsonSerializer.SerializeToNode(relay.Execute("watch.records", args), GatewayJson.Options)!.AsObject();
                Assert.Equal(60, page["total"]!.GetValue<int>());
                seen.AddRange(page["records"]!.AsArray().Select(r => r!["message"]!.GetValue<string>()));
                if (page["nextCursor"] is not JsonObject cursor) break;
                args["beforeTime"] = cursor["beforeTime"]!.GetValue<string>();
                args["beforeLine"] = cursor["beforeLine"]!.GetValue<int>();
            }
            Assert.Equal(Enumerable.Range(0, 360).Where(i => i % 6 == 0).Reverse().Select(i => $"记录 {i}"), seen);
            var original = JsonSerializer.SerializeToNode(relay.Execute("watch.records", new JsonObject
                { ["date"] = "2026-10-28", ["limit"] = 1 }), GatewayJson.Options)!.AsObject();
            Assert.Equal(360, original["total"]!.GetValue<int>());
            Assert.Throws<ArgumentException>(() => relay.Execute("watch.records", new JsonObject
                { ["date"] = "2026-10-28", ["level"] = "bad" }));
        }
        finally { Directory.Delete(dir, true); }
    }

}
