using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>网页「本人」授权：授权码一次有效、限时、限次；执行端只存凭据哈希；嘟嘟可修改类只认本人。</summary>
public class WebOwnerAuthorizationTests
{
    private sealed class Clock { public DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero); }

    private static (WebOwnerAuthorizationStore Store, Clock Clock, string Path, string Dir) NewStore()
    {
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "web-owner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var clock = new Clock(); var path = System.IO.Path.Combine(dir, "web-owner-browsers.json");
        return (new WebOwnerAuthorizationStore(path, () => clock.Now), clock, path, dir);
    }

    [Fact]
    public void CodeRedeemsOnce_AndOnlySecretHashIsPersisted()
    {
        var (store, _, path, dir) = NewStore();
        try
        {
            var (code, _) = store.CreateCode();
            Assert.Matches("^[0-9]{8}$", code);
            var (id, secret) = store.Redeem(code[..4] + " " + code[4..], "电脑网页");
            Assert.True(store.Verify(id, secret));
            Assert.False(store.Verify(id, secret[..63] + (secret[63] == '0' ? '1' : '0')));
            Assert.False(store.Verify("wb-unknown", secret));
            Assert.False(store.Verify(id, null));
            Assert.Throws<InvalidOperationException>(() => store.Redeem(code, "第二次"));
            var file = File.ReadAllText(path);
            Assert.DoesNotContain(secret, file, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(id, file);
            // 新实例从磁盘读取同一份记录（助手重启后凭据仍有效）。
            Assert.True(new WebOwnerAuthorizationStore(path).Verify(id, secret));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CodeExpiresAfterFiveMinutes_AndNewCodeReplacesOld()
    {
        var (store, clock, _, dir) = NewStore();
        try
        {
            var (first, expires) = store.CreateCode();
            Assert.Equal(clock.Now.AddMinutes(5), expires);
            var (second, _) = store.CreateCode();
            if (first != second) Assert.Throws<UnauthorizedAccessException>(() => store.Redeem(first, "旧码"));
            clock.Now = clock.Now.AddMinutes(5).AddSeconds(1);
            Assert.Null(store.PendingCodeExpires);
            Assert.Throws<InvalidOperationException>(() => store.Redeem(second, "过期"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FiveWrongAttemptsInvalidateTheCode()
    {
        var (store, _, _, dir) = NewStore();
        try
        {
            var (code, _) = store.CreateCode();
            var wrong = code == "00000000" ? "11111111" : "00000000";
            for (var i = 0; i < 4; i++) Assert.Throws<UnauthorizedAccessException>(() => store.Redeem(wrong, "猜测"));
            var fifth = Assert.Throws<UnauthorizedAccessException>(() => store.Redeem(wrong, "猜测"));
            Assert.Contains("作废", fifth.Message);
            Assert.Throws<InvalidOperationException>(() => store.Redeem(code, "正确但已作废"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BrowserLimitAndRevokeAreEnforced()
    {
        var (store, _, _, dir) = NewStore();
        try
        {
            var issued = new List<(string Id, string Secret)>();
            for (var i = 0; i < WebOwnerAuthorizationStore.MaxBrowsers; i++) issued.Add(store.Redeem(store.CreateCode().Code, "浏览器" + i));
            Assert.Equal(WebOwnerAuthorizationStore.MaxBrowsers, store.List().Count);
            Assert.Throws<InvalidOperationException>(() => store.Redeem(store.CreateCode().Code, "超出"));
            Assert.True(store.Revoke(issued[0].Id));
            Assert.False(store.Verify(issued[0].Id, issued[0].Secret));
            Assert.True(store.Verify(issued[1].Id, issued[1].Secret));
            Assert.False(store.Revoke(issued[0].Id));
            var label = store.Redeem(store.CreateCode().Code, new string('长', 80));
            Assert.Equal(40, store.List().Single(b => b.Id == label.BrowserId).Label.Length);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DodocoMutationsRequireOwnerWhileReadsDoNot()
    {
        var (store, _, _, dir) = NewStore();
        try
        {
            var settings = new DodocoSettingsService(dir);
            using var tail = new BgiLogTailService(() => null);
            using var watch = new KeywordWatchService(tail, null, null, null, dir);
            using var relay = new DodocoWebRelayService(settings, watch, new DailyReportService(() => dir), () => null);
            foreach (var op in new[] { "watch.rule.upsert", "watch.rule.delete", "watch.settings", "watch.clear" })
                Assert.True(DodocoWebRelayService.IsOwnerOnly(op), op);
            foreach (var op in new[] { "watch.read", "watch.records", "watch.frame", "report.day", "watch.diagnostic.start" })
                Assert.False(DodocoWebRelayService.IsOwnerOnly(op), op);
            // 未注入校验器：修改类一律拒绝，查看类不受影响。
            Assert.Equal("owner_required", relay.OwnerError("watch.settings", new JsonObject()));
            Assert.Null(relay.OwnerError("watch.read", new JsonObject()));
            relay.OwnerVerifier = store.Verify;
            var (id, secret) = store.Redeem(store.CreateCode().Code, "本人");
            JsonObject Owner(string browserId, string value) => new() { ["owner"] = new JsonObject { ["browserId"] = browserId, ["secret"] = value } };
            Assert.Null(relay.OwnerError("watch.rule.upsert", Owner(id, secret)));
            Assert.Equal("owner_required", relay.OwnerError("watch.rule.upsert", new JsonObject()));
            Assert.Equal("owner_required", relay.OwnerError("watch.clear", Owner(id, new string('0', 64))));
            store.Revoke(id);
            Assert.Equal("owner_required", relay.OwnerError("watch.rule.delete", Owner(id, secret)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
