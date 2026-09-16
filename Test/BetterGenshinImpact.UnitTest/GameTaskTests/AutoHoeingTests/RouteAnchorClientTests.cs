#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 路线边界锚点客户端测试（route-anchor · 阶段 2）。
///
/// 覆盖方案第 11.2 / 12 节（阶段 2）要求：
///   · 未启用时零协议流量（单机与未开启房间行为不变）
///   · Enroll → Report → Ready 的线形与顺序、幂等
///   · 等待放行：事件优先、查询权威、锚点消失/超时/取消分别为 Failed/Cancelled（都不得视为放行）
///   · 本地不自报边界：report 使用服务端返回的边界索引与会话身份
/// </summary>
public class RouteAnchorClientTests : IDisposable
{
    private const string Plan = "plan-1";

    private readonly CoordinatorClient _client;
    private readonly BgiGatewayClient _gateway;
    private readonly List<GatewayEnvelope> _invoked = new();

    public RouteAnchorClientTests()
    {
        _client = new CoordinatorClient();
        _client._testIsConnectedOverride = true;
        _gateway = _client.GetOrCreateGatewayForTest();
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_invoked) _invoked.Add(env);
            return Task.FromResult(BuildResponse(env));
        };
    }

    public void Dispose() => _client.DisposeAsync().AsTask().Wait();

    /// <summary>服务端权威快照模拟：会话身份固定为 server-session / epoch=2，边界由请求推导。</summary>
    private static GatewayEnvelope BuildResponse(GatewayEnvelope req)
    {
        var payload = req.Payload;
        var boundary = payload?["completedRouteIndex"]?.GetValue<int>() ?? 12;
        var next = payload?["nextRouteIndex"]?.GetValue<int>() ?? boundary + 1;
        var released = payload?["__released"]?.GetValue<bool>() ?? false;

        var snapshot = new Dictionary<string, object?>
        {
            ["hasAnchor"] = true,
            ["anchorId"] = $"route-boundary:server-session:2:{Plan}:{boundary}:1",
            ["sessionId"] = "server-session",
            ["worldEpoch"] = 2,
            ["planId"] = Plan,
            ["phase"] = released ? "Released" : "WaitingArrival",
            ["completedRouteIndex"] = boundary,
            ["nextRouteIndex"] = next,
            ["participants"] = new[] { "uid-1", "uid-2" },
            ["memberStates"] = new Dictionary<string, string> { ["uid-1"] = "Ready", ["uid-2"] = "Ready" },
            ["myState"] = "Ready",
            ["released"] = released,
            ["stopped"] = false,
            ["revision"] = 1,
        };

        return new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Response,
            Name = req.Name,
            Payload = GatewayEnvelope.ToPayload(new { anchor = snapshot }),
        };
    }

    private static GatewayEnvelope Evt(string name, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Event,
        Name = name,
        Payload = GatewayEnvelope.ToPayload(payload),
        SentAtUtc = DateTime.UtcNow,
    };

    private List<GatewayEnvelope> InvokedNamed(string name)
    {
        lock (_invoked) return _invoked.FindAll(e => e.Name == name);
    }

    // =========================================================================
    // 未启用：零协议流量
    // =========================================================================

    [Fact]
    public async Task Disabled_DoesNotSendAnyProtocolMessage()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = false };

        Assert.Null(await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None));
        Assert.Null(await anchor.ReportAsync("completed", CancellationToken.None));
        Assert.Null(await anchor.ArriveAsync(CancellationToken.None));
        Assert.Equal(RouteAnchorWaitResult.Disabled,
            await anchor.CompleteBoundaryAsync(Plan, 12, 13, "completed", CancellationToken.None));
        Assert.Equal(RouteAnchorWaitResult.Disabled, await anchor.WaitForReleasedAsync(CancellationToken.None));

        Assert.Empty(_invoked);
    }

    [Fact]
    public async Task Enroll_EmptyPlanId_IsNotSent()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };

        Assert.Null(await anchor.EnrollAsync("", 12, 13, CancellationToken.None));
        Assert.Empty(_invoked);
    }

    // =========================================================================
    // Enroll / Report / Ready 线形
    // =========================================================================

    [Fact]
    public async Task Enroll_StoresServerAuthoritativeSnapshot()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };

        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("server-session", snapshot!.SessionId);          // 服务端权威会话
        Assert.Equal(2, snapshot.WorldEpoch);
        Assert.Equal(12, snapshot.CompletedRouteIndex);
        Assert.True(anchor.HasActiveAnchor);

        var sent = Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorEnroll));
        Assert.Equal(Plan, sent.Payload!["planId"]!.GetValue<string>());
        Assert.Equal(12, sent.Payload!["completedRouteIndex"]!.GetValue<int>());
        Assert.Equal(13, sent.Payload!["nextRouteIndex"]!.GetValue<int>());
    }

    [Fact]
    public async Task Report_UsesServerIdentityAndBoundary_NotClientGuess()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        await anchor.ReportAsync("completed", CancellationToken.None);

        var sent = Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorReport));
        Assert.Equal("server-session", sent.Payload!["sessionId"]!.GetValue<string>());
        Assert.Equal(2, sent.Payload!["worldEpoch"]!.GetValue<int>());
        Assert.Equal(12, sent.Payload!["completedRouteIndex"]!.GetValue<int>());   // 用服务端边界，而非本地自报
        Assert.Equal("completed", sent.Payload!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Report_WithoutAnchor_IsNotSent()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };

        Assert.Null(await anchor.ReportAsync("completed", CancellationToken.None));
        Assert.Empty(InvokedNamed(GatewayProtocol.Names.RouteAnchorReport));
    }

    [Fact]
    public async Task Arrive_SendsReadyWithServerIdentity()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        await anchor.ArriveAsync(CancellationToken.None);

        var sent = Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorArrived));
        Assert.Equal("server-session", sent.Payload!["sessionId"]!.GetValue<string>());
    }

    // =========================================================================
    // 完整边界流程
    // =========================================================================

    [Fact]
    public async Task CompleteBoundary_UnreleasedSnapshot_EndsAsFailedWhenAnchorDisappears()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        // 查询返回"无锚点"（模拟锚点被清空/轮次切换）：不得视为放行
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_invoked) _invoked.Add(env);
            if (env.Name == GatewayProtocol.Names.RouteAnchorStateQuery)
            {
                return Task.FromResult(new GatewayEnvelope
                {
                    Type = GatewayProtocol.MessageTypes.Response,
                    Name = env.Name,
                    Payload = GatewayEnvelope.ToPayload(new { anchor = new { hasAnchor = false } }),
                });
            }
            return Task.FromResult(BuildResponse(env));
        };

        var result = await anchor.CompleteBoundaryAsync(
            Plan, 12, 13, "completed", CancellationToken.None);

        Assert.Equal(RouteAnchorWaitResult.Failed, result);
    }

    [Fact]
    public async Task WaitForReleased_EventArrives_ReturnsReleased()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        await anchor.ReportAsync("completed", CancellationToken.None);
        await anchor.ArriveAsync(CancellationToken.None);

        var waitTask = anchor.WaitForReleasedAsync(
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(50));

        // 服务端广播放行（事件路径）
        _client.DispatchEvt(Evt(GatewayProtocol.Events.SyncRouteAnchorReleased, new
        {
            anchorId = anchor.Current!.AnchorId,
            sessionId = "server-session",
            worldEpoch = 2,
            planId = Plan,
            completedRouteIndex = 12,
            nextRouteIndex = 13,
        }));

        var result = await waitTask;
        Assert.Equal(RouteAnchorWaitResult.Released, result);
    }

    [Fact]
    public async Task WaitForReleased_StoppedEvent_ReturnsStopped_NotReleased()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        await anchor.ReportAsync("completed", CancellationToken.None);
        await anchor.ArriveAsync(CancellationToken.None);

        var waitTask = anchor.WaitForReleasedAsync(
            CancellationToken.None, pollInterval: TimeSpan.FromMilliseconds(50));

        _client.DispatchEvt(Evt(GatewayProtocol.Events.SyncRouteAnchorStopped, new
        {
            anchorId = anchor.Current!.AnchorId,
            sessionId = "server-session",
            worldEpoch = 2,
            planId = Plan,
            completedRouteIndex = 12,
            nextRouteIndex = 13,
        }));

        Assert.Equal(RouteAnchorWaitResult.Stopped, await waitTask);
    }

    [Fact]
    public async Task WaitForReleased_OtherAnchorEvent_IsIgnored()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        await anchor.ReportAsync("completed", CancellationToken.None);
        await anchor.ArriveAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var waitTask = anchor.WaitForReleasedAsync(cts.Token, pollInterval: TimeSpan.FromMilliseconds(50));

        // 别的锚点的放行事件：不能放行本机
        _client.DispatchEvt(Evt(GatewayProtocol.Events.SyncRouteAnchorReleased, new
        {
            anchorId = "route-boundary:other:2:plan-1:99:1",
            sessionId = "server-session",
            worldEpoch = 2,
            planId = Plan,
            completedRouteIndex = 99,
            nextRouteIndex = 100,
        }));

        Assert.Equal(RouteAnchorWaitResult.Cancelled, await waitTask);
    }

    [Fact]
    public async Task WaitForReleased_Cancellation_ReturnsCancelled()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        await anchor.ReportAsync("completed", CancellationToken.None);
        await anchor.ArriveAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waitTask = anchor.WaitForReleasedAsync(cts.Token, pollInterval: TimeSpan.FromMilliseconds(50));
        cts.Cancel();

        Assert.Equal(RouteAnchorWaitResult.Cancelled, await waitTask);
    }

    [Fact]
    public async Task WaitForReleased_NoAnchor_ReturnsFailed()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };

        Assert.Equal(RouteAnchorWaitResult.Failed, await anchor.WaitForReleasedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WaitForReleased_AlreadyReleased_ReturnsImmediately()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_invoked) _invoked.Add(env);
            var resp = BuildResponse(env);
            // 直接把快照标记为已放行
            var json = resp.Payload!["anchor"]!.ToJsonString().Replace("\"released\":false", "\"released\":true");
            var released = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = env.Name,
                Payload = GatewayEnvelope.ToPayload(new
                {
                    anchor = new
                    {
                        hasAnchor = true,
                        anchorId = released!["anchorId"].GetString(),
                        sessionId = released["sessionId"].GetString(),
                        worldEpoch = released["worldEpoch"].GetInt32(),
                        planId = released["planId"].GetString(),
                        phase = "Released",
                        completedRouteIndex = released["completedRouteIndex"].GetInt32(),
                        nextRouteIndex = released["nextRouteIndex"].GetInt32(),
                        released = true,
                        stopped = false,
                    },
                }),
            });
        };

        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        Assert.True(anchor.IsReleased);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await anchor.WaitForReleasedAsync(CancellationToken.None);
        sw.Stop();

        Assert.Equal(RouteAnchorWaitResult.Released, result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), "已放行时必须立即返回，不应进入轮询等待");
    }

    // =========================================================================
    // 轮末收口（Finished）
    // =========================================================================

    [Fact]
    public async Task CompleteRoundEnd_SendsRoundEndSentinel_AndReturnsReleased()
    {
        // 服务端已放行的路径（未放行路径由 CompleteBoundary_UnreleasedSnapshot_EndsAsFailedWhenAnchorDisappears 覆盖；
        // 注意：未放行的快照会进入"权威轮询"等待，测试绝不能依赖它的自然超时——否则会挂住测试主机）
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        MakeAllRouteAnchorResponsesReleased();

        var result = await anchor.CompleteRoundEndAsync(Plan, 14, "completed", CancellationToken.None);

        Assert.Equal(RouteAnchorWaitResult.Released, result);
        var sent = Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorEnroll));
        Assert.Equal(
            BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.RoundEndNextRouteIndex,
            sent.Payload!["nextRouteIndex"]!.GetValue<int>());
        Assert.Equal(14, sent.Payload!["completedRouteIndex"]!.GetValue<int>());
    }

    /// <summary>让所有锚点响应都返回"已放行"快照（避免测试依赖 10 分钟轮询上限）。</summary>
    private void MakeAllRouteAnchorResponsesReleased()
    {
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_invoked) _invoked.Add(env);
            var boundary = env.Payload?["completedRouteIndex"]?.GetValue<int>() ?? 0;
            var next = env.Payload?["nextRouteIndex"]?.GetValue<int>() ?? boundary + 1;
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = env.Name,
                Payload = GatewayEnvelope.ToPayload(new
                {
                    anchor = new
                    {
                        hasAnchor = true,
                        anchorId = $"route-boundary:server-session:2:{Plan}:{boundary}:1",
                        sessionId = "server-session",
                        worldEpoch = 2,
                        planId = Plan,
                        phase = "Released",
                        completedRouteIndex = boundary,
                        nextRouteIndex = next,
                        members = Array.Empty<string>(),
                        memberStates = new Dictionary<string, string>(),
                        pullCommandIds = new Dictionary<string, string>(),
                        pullAttempts = new Dictionary<string, int>(),
                        myState = "Ready",
                        myPullCommandId = "",
                        released = true,
                        stopped = false,
                        revision = 2,
                    },
                }),
            });
        };
    }

    [Fact]
    public async Task CompleteRoundEnd_Disabled_SendsNothing()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = false };

        Assert.Equal(RouteAnchorWaitResult.Disabled,
            await anchor.CompleteRoundEndAsync(Plan, 14, "completed", CancellationToken.None));
        Assert.Empty(_invoked);
    }

    // =========================================================================
    // 阶段 4：Pull 命令接收、幂等与回报
    // =========================================================================

    private static GatewayEnvelope PullEvt(string anchorId, string commandId, int target, string sessionId = "server-session")
        => Evt(GatewayProtocol.Events.SyncRouteAnchorPull, new
        {
            anchorId,
            sessionId,
            worldEpoch = 2,
            planId = Plan,
            commandId,
            targetRouteIndex = target,
            attempt = 1,
        });

    [Fact]
    public async Task Pull_MatchingCommand_IsRegistered_NotExecutedInCallback()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        _client.DispatchEvt(PullEvt(snapshot!.AnchorId, "cmd-1", 13));

        var command = anchor.TryGetPullCommand();
        Assert.NotNull(command);
        Assert.Equal("cmd-1", command!.CommandId);
        Assert.Equal(13, command.TargetRouteIndex);
        // 网络回调里不做游戏动作：只有登记，没有自动回报
        Assert.Empty(InvokedNamed(GatewayProtocol.Names.RouteAnchorPullAck));
    }

    [Fact]
    public async Task Pull_DuplicateCommandId_IsIdempotent()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        _client.DispatchEvt(PullEvt(snapshot!.AnchorId, "cmd-1", 13));
        _client.DispatchEvt(PullEvt(snapshot.AnchorId, "cmd-1", 13));

        Assert.Equal("cmd-1", anchor.TryGetPullCommand()!.CommandId);
        Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorEnroll));   // 未产生额外协议消息
    }

    [Fact]
    public async Task Pull_WrongAnchorOrSessionOrTarget_IsIgnored()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        _client.DispatchEvt(PullEvt("route-boundary:other:2:plan-1:12:9", "cmd-x", 13));
        _client.DispatchEvt(PullEvt(snapshot!.AnchorId, "cmd-y", 13, sessionId: "other-session"));
        _client.DispatchEvt(PullEvt(snapshot.AnchorId, "cmd-z", -1));

        Assert.Null(anchor.TryGetPullCommand());
    }

    [Fact]
    public async Task Pull_Disabled_IsIgnored()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = false };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);   // Disabled → 无快照

        _client.DispatchEvt(PullEvt("any-anchor", "cmd-1", 13));

        Assert.Null(anchor.TryGetPullCommand());
    }

    [Fact]
    public async Task ReportPullApplied_SendsAck_AndClearsPending_OnlyOnce()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        _client.DispatchEvt(PullEvt(snapshot!.AnchorId, "cmd-1", 13));

        Assert.True(await anchor.ReportPullAppliedAsync(true, "", CancellationToken.None));

        var ack = Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorPullAck));
        Assert.Equal("cmd-1", ack.Payload!["commandId"]!.GetValue<string>());
        Assert.Equal(snapshot.AnchorId, ack.Payload!["anchorId"]!.GetValue<string>());
        Assert.True(ack.Payload!["success"]!.GetValue<bool>());
        Assert.Null(anchor.TryGetPullCommand());

        // 已回报过 → 不重复发送
        Assert.False(await anchor.ReportPullAppliedAsync(true, "", CancellationToken.None));
        Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorPullAck));
    }

    [Fact]
    public async Task ReportPullApplied_NoPendingCommand_SendsNothing()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        Assert.False(await anchor.ReportPullAppliedAsync(true, "", CancellationToken.None));
        Assert.Empty(InvokedNamed(GatewayProtocol.Names.RouteAnchorPullAck));
    }

    [Fact]
    public async Task Reset_ClearsPendingPull()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        _client.DispatchEvt(PullEvt(snapshot!.AnchorId, "cmd-1", 13));
        Assert.NotNull(anchor.TryGetPullCommand());

        anchor.Reset();

        Assert.Null(anchor.TryGetPullCommand());
    }

    // =========================================================================
    // 重连/重复上报健壮性：服务端已收到过报告（Duplicate）时不得误停整轮
    // =========================================================================

    /// <summary>报告被拒（如 Duplicate）但权威快照显示已就绪：按"已报告"继续，而不是判失败。</summary>
    private void FakeReportRejectedButAlreadyReported(int boundary, bool released)
    {
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_invoked) _invoked.Add(env);
            // 报告与就绪命令都返回"已就绪/也许已放行"的快照；查询同样
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = env.Name,
                Payload = GatewayEnvelope.ToPayload(new
                {
                    anchor = new
                    {
                        hasAnchor = true,
                        anchorId = $"route-boundary:server-session:2:{Plan}:{boundary}:1",
                        sessionId = "server-session",
                        worldEpoch = 2,
                        planId = Plan,
                        phase = released ? "Released" : "WaitingArrival",
                        completedRouteIndex = boundary,
                        nextRouteIndex = boundary + 1,
                        members = Array.Empty<string>(),
                        memberStates = new Dictionary<string, string>(),
                        pullCommandIds = new Dictionary<string, string>(),
                        pullAttempts = new Dictionary<string, int>(),
                        myState = "Reported",       // 服务端认为本机已提交过
                        myPullCommandId = "",
                        released,
                        stopped = false,
                        revision = 2,
                    },
                }),
            });
        };
    }

    [Fact]
    public async Task CompleteBoundary_ReportRejectedButAlreadyReported_ContinuesInsteadOfFailing()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        FakeReportRejectedButAlreadyReported(boundary: 12, released: true);

        var result = await anchor.CompleteBoundaryAsync(Plan, 12, 13, "completed", CancellationToken.None);

        // 关键：不得因为"服务端已收到过"而把整轮判失败
        Assert.Equal(RouteAnchorWaitResult.Released, result);
    }

    [Fact]
    public async Task CompleteBoundary_ReportRejectedAndNotReported_FailsAsBefore()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        // 报告失败，且快照显示本机是 Pathing（没有"已提交"前提）→ 仍应判失败
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_invoked) _invoked.Add(env);
            if (env.Name == GatewayProtocol.Names.RouteAnchorStateQuery)
            {
                return Task.FromResult(new GatewayEnvelope
                {
                    Type = GatewayProtocol.MessageTypes.Response,
                    Name = env.Name,
                    Payload = GatewayEnvelope.ToPayload(new
                    {
                        anchor = new
                        {
                            hasAnchor = true,
                            anchorId = $"route-boundary:server-session:2:{Plan}:12:1",
                            sessionId = "server-session",
                            worldEpoch = 2,
                            planId = Plan,
                            phase = "Waiting",
                            completedRouteIndex = 12,
                            nextRouteIndex = 13,
                            members = Array.Empty<string>(),
                            memberStates = new Dictionary<string, string>(),
                            pullCommandIds = new Dictionary<string, string>(),
                            pullAttempts = new Dictionary<string, int>(),
                            myState = "Pathing",
                            myPullCommandId = "",
                            released = false,
                            stopped = false,
                            revision = 1,
                        },
                    }),
                });
            }
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = env.Name,
                Payload = GatewayEnvelope.ToPayload(new { error = new { code = "bad_request", message = "route_anchor:config_disabled" } }),
            });
        };

        var result = await anchor.CompleteBoundaryAsync(Plan, 12, 13, "completed", CancellationToken.None);

        Assert.Equal(RouteAnchorWaitResult.Failed, result);
    }

    // =========================================================================
    // 阶段 5：取消与重连对账
    // =========================================================================

    [Fact]
    public async Task Cancel_SendsCancelCommand_AndRefreshesLocalState()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        Assert.True(await anchor.CancelAsync("user-stop", CancellationToken.None));

        var sent = Assert.Single(InvokedNamed(GatewayProtocol.Names.RouteAnchorCancel));
        Assert.Equal(snapshot!.AnchorId, sent.Payload!["anchorId"]!.GetValue<string>());
        Assert.Equal("user-stop", sent.Payload!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancel_DisabledOrNoAnchor_SendsNothing()
    {
        var disabled = new RouteAnchorClient(_client) { Enabled = false };
        Assert.False(await disabled.CancelAsync("x", CancellationToken.None));

        var enabledNoAnchor = new RouteAnchorClient(_client) { Enabled = true };
        Assert.False(await enabledNoAnchor.CancelAsync("x", CancellationToken.None));

        Assert.Empty(InvokedNamed(GatewayProtocol.Names.RouteAnchorCancel));
    }

    [Fact]
    public async Task Cancel_ClearsPendingPull_SoNoStaleJumpHappens()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        var snapshot = await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        _client.DispatchEvt(PullEvt(snapshot!.AnchorId, "cmd-1", 13));
        Assert.NotNull(anchor.TryGetPullCommand());

        await anchor.CancelAsync("user-stop", CancellationToken.None);

        Assert.Null(anchor.TryGetPullCommand());
    }

    [Fact]
    public async Task ReconcileAfterReconnect_NoAnchorOnServer_ReportsThat()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        _gateway._testInvokeOverride = (_, env, _) => Task.FromResult(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Response,
            Name = env.Name,
            Payload = GatewayEnvelope.ToPayload(new { anchor = new { hasAnchor = false } }),
        });

        var snapshot = await anchor.ReconcileAfterReconnectAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.HasAnchor);
    }

    [Fact]
    public async Task ReconcileAfterReconnect_ServerHasPullCommand_RegistersItLocally()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        // 模拟"重连期间错过了 Pull 事件"：服务端快照里带 MyPullCommandId，本地却没有登记
        _gateway._testInvokeOverride = (_, env, _) => Task.FromResult(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Response,
            Name = env.Name,
            Payload = GatewayEnvelope.ToPayload(new
            {
                anchor = new
                {
                    hasAnchor = true,
                    anchorId = "route-boundary:server-session:2:plan-1:12:1",
                    sessionId = "server-session",
                    worldEpoch = 2,
                    planId = Plan,
                    phase = "Pulling",
                    completedRouteIndex = 12,
                    nextRouteIndex = 13,
                    members = Array.Empty<string>(),
                    memberStates = new Dictionary<string, string> { ["uid-1"] = "PullRequested" },
                    pullCommandIds = new Dictionary<string, string> { ["uid-1"] = "cmd-9" },
                    pullAttempts = new Dictionary<string, int> { ["uid-1"] = 1 },
                    myState = "PullRequested",
                    myPullCommandId = "cmd-9",
                    released = false,
                    stopped = false,
                    revision = 3,
                },
            }),
        });

        await anchor.ReconcileAfterReconnectAsync(CancellationToken.None);

        var command = anchor.TryGetPullCommand();
        Assert.NotNull(command);
        Assert.Equal("cmd-9", command!.CommandId);
        Assert.Equal(13, command.TargetRouteIndex);
    }

    [Fact]
    public async Task ReconcileAfterReconnect_QueryFails_ReturnsNull()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        _gateway._testInvokeOverride = (_, _, _) => throw new InvalidOperationException("connection lost");

        Assert.Null(await anchor.ReconcileAfterReconnectAsync(CancellationToken.None));
    }

    // =========================================================================
    // 配置门控：默认关闭
    // =========================================================================

    [Fact]
    public void ClientConfig_EnableRouteAnchor_DefaultsToFalse()
    {
        // 发布纪律：新机制未实机验证前默认关闭，且第一版不进设置界面（仅配置 JSON / 房主下发）
        Assert.False(new BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingConfig().EnableRouteAnchor);
        Assert.False(new BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models.RoomConfig().EnableRouteAnchor);
    }

    [Fact]
    public async Task DisabledByDefaultConfig_NoProtocolTraffic_EvenIfClientObjectExists()
    {
        // 门控未被调用方打开时，即使构造了锚点客户端也不得产生任何协议流量
        var anchor = new RouteAnchorClient(_client);
        Assert.False(anchor.Enabled);

        Assert.Null(await anchor.EnrollAsync(Plan, 1, 2, CancellationToken.None));
        Assert.Equal(RouteAnchorWaitResult.Disabled, await anchor.WaitForReleasedAsync(CancellationToken.None));
        Assert.Empty(_invoked);
    }

    // =========================================================================
    // 能力协商（禁止半启用）
    // =========================================================================

    [Fact]
    public async Task Hello_DeclaresRouteAnchorCapability()
    {
        GatewayEnvelope? helloReq = null;
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            if (env.Name == GatewayProtocol.Names.SessionHello) helloReq = env;
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = env.Name,
                Payload = GatewayEnvelope.ToPayload(new { serverVersion = "test", capabilities = Array.Empty<string>() }),
            });
        };

        await _gateway.HelloAsync();

        Assert.NotNull(helloReq);
        var caps = helloReq!.Payload!["capabilities"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains(BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Capability, caps);
        Assert.Contains(BetterGenshinImpact.Shared.CooperativeRerun.RerunProtocol.Capability, caps);
    }

    [Fact]
    public void SharedProtocolConstants_MatchWireValues()
    {
        // 单一来源共享文件：字面量一旦改动即破坏线上兼容，这里用测试把它钉住
        Assert.Equal("hoeing.routeAnchor.v1", BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Capability);
        Assert.Equal("sync.routeAnchorEnroll", BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Enroll);
        Assert.Equal("sync.routeAnchorReport", BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Report);
        Assert.Equal("sync.routeAnchorArrived", BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Arrived);
        Assert.Equal("sync.routeAnchorState", BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.State);
        Assert.Equal("sync.routeAnchorReleased", BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Released);
        Assert.Equal("sync.routeAnchorStopped", BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Stopped);

        // 客户端协议常量确实指向共享文件（不漂移）
        Assert.Equal(BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Enroll,
            GatewayProtocol.Names.RouteAnchorEnroll);
        Assert.Equal(BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Released,
            GatewayProtocol.Events.SyncRouteAnchorReleased);
    }

    // =========================================================================
    // 本地状态
    // =========================================================================

    [Fact]
    public async Task Reset_ClearsLocalState()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        Assert.True(anchor.HasActiveAnchor);

        anchor.Reset();

        Assert.Null(anchor.Current);
        Assert.False(anchor.HasActiveAnchor);
        Assert.Null(anchor.TryGetPullCommand());
    }

    [Fact]
    public async Task TryGetPullCommand_NullWhenServerProvidesNoCommandId()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        Assert.Null(anchor.TryGetPullCommand());
    }
}
