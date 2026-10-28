using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class DodocoLocalServiceTests
{
    private static JsonObject Json(object? value) => JsonSerializer.SerializeToNode(value, GatewayJson.Options)!.AsObject();
    private static LocalBrowserRequest Req(string session, string op, JsonObject? args = null)
        => new(session, Guid.NewGuid().ToString("N"), op, args ?? new JsonObject(), "bridge-1");

    [Fact]
    public async Task RealLoopbackPairingConnectsToActualLocalDodocoServices()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "dodoco-local-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "better-genshin-impact20261028.log"),
                "[12:01:00.000] [INF] example\n真实本机日志\n");
            var settings = new DodocoSettingsService(root);
            Assert.True(settings.TryUpdate(x => { x.ShareLogFiles = false; x.ShareDodocoWeb = false; }));
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, root, () => root);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => root), () => null, () => root);
            using var service = new DodocoLocalService(tail, relay, new ScreenshotService(() => 1280), settings,
                () => root, instance => new { uid = "local_" + instance }, () => root,
                () => new ScreenFrame { JpegBytes = [1, 2, 3], Width = 2, Height = 3, Time = new DateTime(2026, 10, 28) });
            using var bridge = new LocalBrowserBridge(service.ExecuteAsync, service.ReleaseSession);
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(bridge.Origin), Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpResponseMessage> Post(string path, object body, string? session = null)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, path)
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("Origin", bridge.Origin);
                if (session != null) req.Headers.TryAddWithoutValidation("X-BGI-Session", session);
                return await client.SendAsync(req);
            }
            var payload = new { requestId = "not-paired", operation = "watch.read", args = new { } };
            using (var unauthorized = await Post("/api/execute", payload)) Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            var token = new Uri(bridge.CreatePairingUrl()).Fragment[7..];
            using var pairing = await Post("/api/pair", new { token });
            Assert.Equal(HttpStatusCode.OK, pairing.StatusCode);
            var session = JsonNode.Parse(await pairing.Content.ReadAsStringAsync())!["sessionId"]!.GetValue<string>();
            async Task<JsonObject> Execute(string operation, object? args = null)
            {
                using var reply = await Post("/api/execute", new { requestId = Guid.NewGuid().ToString("N"), operation, args = args ?? new { } }, session);
                Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
                var result = JsonNode.Parse(await reply.Content.ReadAsStringAsync())!.AsObject();
                Assert.True(result["status"]!.GetValue<string>() == "ok", result["message"]?.GetValue<string>());
                return result["data"]!.AsObject();
            }
            var identity = await Execute("local.identity");
            Assert.StartsWith("local_", identity["uid"]!.GetValue<string>());
            var files = (await Execute("log.files"))["files"]!.AsArray();
            var file = files.Single(x => x!["name"]!.GetValue<string>() == "better-genshin-impact20261028.log")!;
            var chunk = await Execute("log.chunk", new { id = file["id"]!.GetValue<string>(), offset = 0 });
            Assert.Contains("真实本机日志", Encoding.UTF8.GetString(Convert.FromBase64String(chunk["base64"]!.GetValue<string>())));
            var rule = new { id = "through-http", name = "HTTP规则", pattern = "warn", minLevel = "WRN", enabled = true };
            await Execute("watch.rule.upsert", new { rule });
            Assert.Contains((await Execute("watch.read"))["rules"]!.AsArray(), x => x!["id"]!.GetValue<string>() == "through-http");
            Assert.Contains((await Execute("report.dates"))["dates"]!.AsArray(), x => x!.GetValue<string>() == "2026-10-28");
            var frame = await Execute("screenshot.capture");
            Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(frame["jpegBase64"]!.GetValue<string>()));
            using (var end = await Post("/api/end", new { }, session)) Assert.Equal(HttpStatusCode.OK, end.StatusCode);
            using var revoked = await Post("/api/execute", payload, session);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        finally { /* Retain isolated fixtures, including failure evidence. */ }
    }

    [Fact]
    public async Task PairedObserverWithLivePeerWritesOnlySharedRules()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "测试必须使用隔离 NEXUSBGI_DATA_ROOT");
        var root = Path.Combine(AssistantDataDirectory.Root, "observer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var executorPeer = new LocalPeerSyncService(() => false, () => "uid");
            using var observerPeer = new LocalPeerSyncService(() => true, () => "uid");
            Assert.True(observerPeer.IsLocalExecutorAlive());
            var personal = new WatchConfig { BuiltinStallSeeded = true, Rules = [new WatchRule { Id = "personal", Name = "个人" }] };
            var personalPath = Path.Combine(root, "dodoco_watch_rules.json");
            File.WriteAllText(personalPath, JsonSerializer.Serialize(personal));
            var shared = new WatchConfig { BuiltinStallSeeded = true, Rules = [new WatchRule { Id = "peer", Name = "执行端" }] };
            Assert.True(LocalPeerSyncService.WriteFileAtomic(LocalPeerSyncService.SharedRulesPath, JsonSerializer.Serialize(shared)));
            var settings = new DodocoSettingsService(root);
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, () => true, observerPeer, root, () => root);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => root), () => null, () => root);
            using var service = new DodocoLocalService(tail, relay, new ScreenshotService(() => 1280), settings, () => root, _ => new { }, () => root);
            var args = JsonNode.Parse("""{"rule":{"id":"local","name":"网页","pattern":"warn","minLevel":"WRN","enabled":true}}""")!.AsObject();
            var result = await service.ExecuteAsync(Req("paired", "watch.rule.upsert", args), default);
            Assert.True(result.Status == "ok", result.Message);
            var saved = JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(LocalPeerSyncService.SharedRulesPath))!;
            Assert.Contains(saved.Rules, r => r.Id == "peer");
            Assert.Contains(saved.Rules, r => r.Id == "local");
            Assert.Equal(JsonSerializer.Serialize(personal), File.ReadAllText(personalPath));
        }
        finally { /* Retain isolated fixtures, including failure evidence. */ }
    }

    [Fact]
    public async Task PairedObserverEditsLatestLocalRulesButRemoteObserverStillRejects()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "dodoco-local-observer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new DodocoSettingsService(root);
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, () => true, null, root, () => root);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => root), () => null, () => root);
            using var service = new DodocoLocalService(tail, relay, new ScreenshotService(() => 1280), settings, () => root, _ => new { }, () => root);
            // 模拟另一端更新落盘，观察端内存尚未同步；单项编辑不能覆盖这一更新。
            var current = new WatchConfig { BuiltinStallSeeded = true, Rules = [new WatchRule { Id = "other", Name = "另一端", Pattern = "test" }] };
            File.WriteAllText(Path.Combine(root, "dodoco_watch_rules.json"), JsonSerializer.Serialize(current));
            var args = JsonNode.Parse("""{"rule":{"id":"local","name":"本机","pattern":"warn","minLevel":"WRN","enabled":true}}""")!.AsObject();
            Assert.Throws<InvalidOperationException>(() => relay.Execute("watch.rule.upsert", args));
            var result = await service.ExecuteAsync(Req("paired", "watch.rule.upsert", args), default);
            Assert.True(result.Status == "ok", result.Message);
            var saved = JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(Path.Combine(root, "dodoco_watch_rules.json")))!;
            Assert.Contains(saved.Rules, r => r.Id == "other");
            Assert.Contains(saved.Rules, r => r.Id == "local");
        }
        finally { /* Retain isolated fixtures, including failure evidence. */ }
    }

    [Fact]
    public async Task LocalFilesAreOpaqueSessionBoundAndRejectReplacement()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "dodoco-local-" + Guid.NewGuid().ToString("N"));
        var bgi = Path.Combine(root, "bgi"); var assist = Path.Combine(root, "assist");
        Directory.CreateDirectory(bgi); Directory.CreateDirectory(assist);
        Directory.CreateDirectory(Path.Combine(assist, "remote_downloads", "member"));
        Directory.CreateDirectory(Path.Combine(assist, "imported"));
        try
        {
            var target = Path.Combine(bgi, "better-genshin-impact20261028.log");
            await File.WriteAllTextAsync(target, "本地日志内容");
            await File.WriteAllTextAsync(Path.Combine(assist, "assistant_runtime.2026-10-28.s1.log"), "助手日志");
            await File.WriteAllTextAsync(Path.Combine(assist, "remote_downloads", "member", "download.log"), "已下载日志");
            await File.WriteAllTextAsync(Path.Combine(assist, "imported", "foreign.txt"), "已导入日志");
            var settings = new DodocoSettingsService(root);
            Assert.True(settings.TryUpdate(x => { x.ShareLogFiles = false; x.ShareDodocoWeb = false; x.ShareRealtimeLog = false; }));
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, root, () => assist);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => bgi), () => null, () => assist);
            using var service = new DodocoLocalService(tail, relay, new ScreenshotService(() => 1280), settings, () => bgi,
                instance => new { uid = "local_" + instance }, () => assist);
            var result = await service.ExecuteAsync(Req("owner", "log.files"), default);
            Assert.Equal("ok", result.Status);
            var files = Json(result.Data)["files"]!.AsArray();
            Assert.Equal(4, files.Count);
            Assert.Equal(4, files.Select(x => x!["group"]!.GetValue<string>()).Distinct().Count());
            Assert.DoesNotContain(root, Json(result.Data).ToJsonString(), StringComparison.OrdinalIgnoreCase);
            var selected = files.Single(x => x!["name"]!.GetValue<string>() == Path.GetFileName(target))!;
            var id = selected["id"]!.GetValue<string>();
            var args = new JsonObject { ["id"] = id, ["offset"] = 0 };
            Assert.Equal("error", (await service.ExecuteAsync(Req("other", "log.chunk", args), default)).Status);
            var first = await service.ExecuteAsync(Req("owner", "log.chunk", args), default);
            Assert.True(first.Status == "ok", first.Message);
            Assert.Equal("本地日志内容", Encoding.UTF8.GetString(Convert.FromBase64String(Json(first.Data)["base64"]!.GetValue<string>())));
            File.Move(target, target + ".old");
            await File.WriteAllTextAsync(target, "替换后的文件");
            var replaced = await service.ExecuteAsync(Req("owner", "log.chunk", args), default);
            Assert.Equal("error", replaced.Status);
            Assert.Contains("身份", replaced.Message);
            var outside = Path.Combine(root, "private");
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "private.log"), "不属于日志目录的文件");
            // Windows junction does not require symbolic-link privileges; both are reparse points.
            var junction = Path.Combine(assist, "remote_downloads", "link");
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
            start.ArgumentList.Add(junction); start.ArgumentList.Add(outside);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, output + error);
            }
            var withLink = await service.ExecuteAsync(Req("owner", "log.files"), default);
            Assert.Equal("ok", withLink.Status);
            Assert.DoesNotContain(Json(withLink.Data)["files"]!.AsArray(), x => x!["name"]!.GetValue<string>().Contains("private.log"));
            Assert.Contains(Json(withLink.Data)["unavailable"]!.AsArray(), x => x!.GetValue<string>().Contains("链接"));
            service.ReleaseSession("owner");
            Assert.Equal("error", (await service.ExecuteAsync(Req("owner", "log.chunk", args), default)).Status);
        }
        finally { /* Retain isolated fixtures, including failure evidence. */ }
    }

    [Fact]
    public async Task LocalAuthorizationDoesNotDependOnRemoteSharingAndCachesAreSessionBound()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "dodoco-local-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "better-genshin-impact20261028.log"),
                "[12:01:00.000] [INF] example\n本机诊断\n");
            var settings = new DodocoSettingsService(root);
            Assert.True(settings.TryUpdate(x => { x.ShareLogFiles = false; x.ShareDodocoWeb = false; }));
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, root, () => root);
            var diagnostic = new DiagnosticPackageService(() => root, () => [], () => root);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => root), () => null, () => root, diagnostic);
            using var service = new DodocoLocalService(tail, relay, new ScreenshotService(() => 1280), settings, () => root, _ => new { }, () => root);
            Assert.Equal("ok", (await service.ExecuteAsync(Req("local-a", "watch.read"), default)).Status);
            var start = new JsonObject { ["start"] = "2026-10-28T12:00:00", ["end"] = "2026-10-28T12:10:00" };
            var result = await service.ExecuteAsync(Req("local-a", "watch.diagnostic.start", start), default);
            Assert.Equal("ok", result.Status);
            var id = Json(result.Data)["id"]!.GetValue<string>();
            var args = new JsonObject { ["id"] = id, ["offset"] = 0 };
            Assert.Equal("error", (await service.ExecuteAsync(Req("local-b", "watch.diagnostic.chunk", args), default)).Status);
            Assert.Equal("ok", (await service.ExecuteAsync(Req("local-a", "watch.diagnostic.chunk", args), default)).Status);
            service.ReleaseSession("local-a");
            Assert.Equal("error", (await service.ExecuteAsync(Req("local-a", "watch.diagnostic.chunk", args), default)).Status);
        }
        finally { /* Retain isolated fixtures, including failure evidence. */ }
    }

    [Fact]
    public async Task TailPollUsesIncrementalCursorWithoutServerOrRemoteSharing()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "dodoco-local-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "better-genshin-impact20261028.log");
            await File.WriteAllTextAsync(path, "[10:00:00.000] [INF] Tail\n历史标记\n");
            string? active = null;
            var settings = new DodocoSettingsService(root);
            Assert.True(settings.TryUpdate(x => x.ShareRealtimeLog = false));
            using var tail = new BgiLogTailService(() => active);
            using var watch = new KeywordWatchService(tail, null, null, null, root, () => root);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => root), () => null, () => root);
            using var service = new DodocoLocalService(tail, relay, new ScreenshotService(() => 1280), settings, () => active, _ => new { }, () => root);
            active = root; tail.Poke();
            JsonObject? page = null;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var response = await service.ExecuteAsync(Req("a", "log.poll"), default);
                page = Json(response.Data);
                if (page["sourceAvailable"]!.GetValue<bool>() && page["lines"]!.AsArray().Any(x => x!.GetValue<string>().Contains("历史标记"))) break;
                await Task.Delay(100);
            }
            Assert.NotNull(page);
            Assert.True(page!["lines"]!.AsArray().Any(x => x!.GetValue<string>().Contains("历史标记")));
            var cursor = page["cursor"]!.GetValue<long>();
            await File.AppendAllTextAsync(path, "[10:01:00.000] [ERR] Tail\n增量标记\n");
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var response = await service.ExecuteAsync(Req("a", "log.poll", new JsonObject { ["cursor"] = cursor }), default);
                page = Json(response.Data);
                if (page["lines"]!.AsArray().Any(x => x!.GetValue<string>().Contains("增量标记"))) break;
                await Task.Delay(100);
            }
            Assert.True(page!["lines"]!.AsArray().Any(x => x!.GetValue<string>().Contains("增量标记")));
        }
        finally { /* Retain isolated fixtures, including failure evidence. */ }
    }
}
