#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.Service.Execution;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing;

public partial class AutoHoeingTask
{
    private bool _standbyOnly;
    private bool _hostWorldRoom;
    private bool _usedStandbyRoom;
    private HostWorldState? _rotationStandbyRoles;
    private bool _standbyAdmissionPending;
    private volatile bool _hostWorldRoutePhase;
    private string[]? _hostWorldRouteNames;
    private string? _hostWorldManifestDigest;
    private bool _worldOwnerIsStandby;
    private Dictionary<RouteInfo, string>? _originalRouteNames;
    private Dictionary<string, RouteInfo>? _standbyOwnerCdRoutes;
    private readonly HashSet<string> _appliedOwnerCdReceipts = new(StringComparer.Ordinal);
    private bool _ownerCdNeedsSave;
    private bool _hostWorldExecutorReachedEnd;

    internal static bool HostWorldModeAllowed(bool multiplayer, bool multiWorld, bool debug, bool soloDebug, bool managed)
        => multiplayer && !debug && !soloDebug && !managed;

    private static bool IsManagedHostWorldInvocation => CoordinatedHoeingScope.IsActive
        || ExecutionScope.Current?.Descriptor.WorkflowRunId != null;

    private bool CanOfferStandby(bool isMember) => _settingsOverride is { Count: > 0 }
        && _config.MultiplayerEnabled && !_config.SoloDebugMode
        && !IsManagedHostWorldInvocation && !string.IsNullOrWhiteSpace(_config.PlayerUid)
        && (isMember || (!_config.DebugMode && _config.ExpectedPlayerCount is >= 2 and <= 4
            && _config.OperationMode is "运行锄地路线" or "启用仅指定怪物模式"));

    private void RequireLocalStandbySetting()
    {
        if (string.IsNullOrWhiteSpace(_config.PlayerUid))
            throw new HoeingStartupException("仅组队，不跟跑", "请填写本机的玩家 UID");
        if (_settingsOverride is not { Count: > 0 } || !_config.MultiplayerEnabled || IsManagedHostWorldInvocation)
            throw new HoeingStartupException("仅组队，不跟跑", "请从本机联机锄地配置组启动；任务中心和协同批次暂不支持");
        if (_config.SoloDebugMode)
            throw new HoeingStartupException("仅组队，不跟跑", "请关闭单人调试模式");
        if (_config.MultiplayerRole != "member") RequireHostWorldModeAllowed();
    }

    private void RequireHostWorldModeAllowed()
    {
        if (!HostWorldModeAllowed(_config.MultiplayerEnabled, _config.MultiWorldEnabled, _config.DebugMode,
                _config.SoloDebugMode, IsManagedHostWorldInvocation))
            throw new HoeingStartupException("仅组队，不跟跑", "请从本机配置组启动正常联机锄地并关闭调试；任务中心和协同批次暂不支持");
        if (_standbyOnly && _config.MultiplayerRole != "member" && (_config.ExpectedPlayerCount is < 2 or > 4
            || string.IsNullOrWhiteSpace(_config.PlayerUid)
            || _config.OperationMode is not ("运行锄地路线" or "启用仅指定怪物模式")))
            throw new HoeingStartupException("仅提供世界", "房主需填写 UID、设置 2–4 人并选择正常路线执行");
    }

    private async Task SealHostWorldPartyAsync(CoordinatorClient client, int actualCount, bool allowCurrentParty)
    {
        // On a partial start the server still requires the exact sealed party to have reported game-world entry.
        var partyCount = actualCount > 0 ? actualCount : client.CurrentRoomPlayerCount;
        if (partyCount < 2 && _standbyOnly)
            throw new HoeingStartupException("仅组队，不跟跑", "未确认至少一人跟跑");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(RemainingPartyWaitSeconds(), 1, 86400)));
        try
        {
            await client.ReportWorldJoinedAsync(budget.Token);
            var state = await PollHostWorldAsync(t => client.ReadOrUpdateHostWorldAsync(t, "start", allowPartial: allowCurrentParty,
                    partyCount: partyCount), s => s.Phase != "waiting", budget.Token);
            ApplyStandbyStartState(client, state);
            if (_hostWorldRoom && state.ExecutorUids.Length + state.StandbyUids!.Length != partyCount)
                throw new HoeingStartupException("仅组队，不跟跑", "已确认的执行及待机名单与实际组队人数不一致");
        }
        catch (OperationCanceledException ex) when (!_ct.IsCancellationRequested)
        {
            throw new HoeingStartupException("仅提供世界", "等待实际成员就绪超时", ex);
        }
    }

    private async Task ReportHostWorldMemberJoinedAsync(CoordinatorClient client)
    {
        _hostWorldRoutePhase = true; // A fast empty report may reach normal room close before this member gets the join receipt.
        using var report = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        report.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.PartyTimeoutSeconds, 1, 86400)));
        try { await client.ReportWorldJoinedAsync(report.Token); }
        catch (Exception ex) when (!_ct.IsCancellationRequested
            && ex is OperationCanceledException or MultiplayerSessionFailedException)
        {
            // Only the same room's sealed roster can reconcile lost confirmation.
            var observed = await client.ReadOrUpdateHostWorldAsync(_ct);
            if (observed.Phase is "waiting" or "regular") throw;
            ApplyStandbyStartState(client, observed);
            return;
        }
        var state = await PollHostWorldAsync(t => client.ReadOrUpdateHostWorldAsync(t), s => s.Phase != "waiting", report.Token);
        ApplyStandbyStartState(client, state);
    }

    private void ApplyStandbyStartState(CoordinatorClient client, HostWorldState state)
    {
        _hostWorldRoom = client.IsHostWorldOnlyRoom || _standbyOnly;
        _hostWorldRoutePhase = _hostWorldRoom;
        if (state.Phase == "aborted")
            throw new HoeingStartupException("仅组队，不跟跑", state.Reason ?? "协作房间已中止");
        if (!_hostWorldRoom && state.Phase != "regular" || _hostWorldRoom && state.StandbyUids is not { Length: > 0 })
            throw new HoeingStartupException("仅组队，不跟跑", "未确认本场执行及待机名单");
        if (_config.MultiWorldEnabled)
        {
            if (_rotationStandbyRoles is { } frozen && !HasSameStandbyRoles(frozen, state))
                throw new HoeingStartupException("换世界", "新房间的待机/执行身份与本场开始时不一致");
            if (_hostWorldRoom) _rotationStandbyRoles ??= state;
        }
        _usedStandbyRoom |= _hostWorldRoom;
        _standbyAdmissionPending = false;
    }

    internal static bool HasSameStandbyRoles(HostWorldState original, HostWorldState next)
        => original.ExecutorUids.ToHashSet(StringComparer.Ordinal).SetEquals(next.ExecutorUids)
            && (original.StandbyUids ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(next.StandbyUids ?? []);

    private void AdoptRotatedRoom(CoordinatorClient client)
    {
        _hostWorldRoom = false;
        _hostWorldRoutePhase = false;
        _standbyAdmissionPending = client.NegotiatesPlayerStandby;
        if ((_standbyOnly || _rotationStandbyRoles != null) && !_standbyAdmissionPending)
            throw new HoeingStartupException("换世界", "新房间未确认本机的待机协商，不进入错误世界");
    }

    private List<RouteInfo> PrepareHostWorldRoutes(List<List<string>> groupTags)
    {
        if (!_cdManager.LoadSucceeded)
            throw new HoeingStartupException("借世界路线", "房主 CD 记录无法读取，禁止按空路线开场");
        if (_config.OperationMode == "启用仅指定怪物模式")
        {
            var tags = ParseChineseTags(_config.TargetMonsters);
            if (tags.Count == 0) throw new HoeingStartupException("借世界路线", "未配置目标怪物");
            return PrepareTargetWorldRoutes(tags, strict: true);
        }
        if (_config.OperationMode != "运行锄地路线")
            throw new HoeingStartupException("借世界路线", "仅支持正常路线执行，不支持调试分配或刷新记录");
        if (!_config.UseFixedDebugRoutes) return PrepareNormalWorldRoutes(groupTags, strict: true);
        // The persisted key is also used for the normal built-in multiplayer route pack.
        var fixedRoutes = LoadRoutesBasedOnConfig();
        HoeingMissingResourceBoundary.RequireFixedRoutes(fixedRoutes.Count, _ct,
            static () => ExecutionScope.Current?.ThrowIfStopped());
        return fixedRoutes;
    }

    private async Task RunHostWorldAsync(string accountName, List<List<string>> groupTags, bool amIHost,
        MultiWorldRoundContext? roundContext = null)
    {
        var client = _coordinatorClientRef!;
        var taskToken = _ct;
        _hostWorldRoutePhase = true;
        _hostWorldExecutorReachedEnd = false;
        _worldOwnerIsStandby = false;
        _originalRouteNames = null;
        _standbyOwnerCdRoutes = null;
        _appliedOwnerCdReceipts.Clear();
        _ownerCdNeedsSave = false;
        Volatile.Write(ref _hostWorldStopReceipt, null);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(taskToken);
        run.CancelAfter(TimeSpan.FromHours(6));
        using var stopAtDeadline = run.Token.Register(() => _linkedStopCts!.Cancel());
        HostWorldState published;
        List<RouteInfo>? prepared = null;
        if (amIHost)
        {
            prepared = PrepareHostWorldRoutes(groupTags);
            // Preparation and selection only. No route engine, position reports, sync or CD write.
            var selected = SelectHostWorldRoutes(prepared.Where(r => r.Group == _config.GroupIndex && r.Selected).ToList(), preview: false);
            var names = selected.Select(r => r.FileName).ToArray();
            if (_standbyOnly) _standbyOwnerCdRoutes = ResolveStandbyOwnerCdRoutes(selected);
            var digest = names.Length == 0 ? null : ComputeHostWorldManifestDigest(selected.Select(r => r.FullPath), _ct);
            if (names.Length > 0 && _config.StartRouteIndex > names.Length)
                throw new HoeingStartupException("借世界路线", "起始路线超出已选路线范围");
            EnsureCooperativeExecutionAllowed();
            using var publication = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            publication.CancelAfter(TimeSpan.FromSeconds(30));
            published = await PollHostWorldAsync(t => client.ReadOrUpdateHostWorldAsync(t, "publish", names, manifestDigest: digest),
                s => s.Phase != "ready", publication.Token);
            if (published.Phase != "aborted" && (published.RouteNames == null || published.ManifestDigest != digest
                || !names.SequenceEqual(published.RouteNames, StringComparer.Ordinal)))
                throw new HoeingStartupException("借世界路线", "服务端未确认同一份路线发布结果");
            _logger.LogInformation("[借世界] 已确认房主发布 {Count} 条路线", names.Length);
            _hostWorldManifestDigest = digest;
        }
        else
        {
            using var publication = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            publication.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.PartyTimeoutSeconds, 90, 600)));
            published = await PollHostWorldAsync(t => client.ReadOrUpdateHostWorldAsync(t),
                s => s.Phase is not ("waiting" or "ready"), publication.Token);
        }
        _worldOwnerIsStandby = published.StandbyUids?.Contains(published.HostUid, StringComparer.Ordinal) == true;
        if (published.Phase == "running" && _worldStateMonitor != null) _worldStateMonitor.IsPartyPhase = false;
        if (_standbyOnly)
        {
            ApplyVerifiedOwnerCd(published);
            _logger.LogInformation("[联机] 本机选择不跟跑，原地等待执行者收尾");
            var terminal = published.IsTerminal ? published : await PollHostWorldTerminalAsync(client, run.Token);
            RecordHostWorldTerminal(terminal);
            return;
        }
        if (published.IsTerminal)
        {
            if (published.Phase == "completed") throw new HoeingStartupException("借世界执行", "本机尚未执行，服务端却返回完工");
            RecordHostWorldTerminal(published);
            return;
        }

        _hostWorldRouteNames = published.RouteNames!;
        _hostWorldManifestDigest = published.ManifestDigest;
        using var monitorEnd = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var watch = ObserveHostWorldTerminalAsync(client, monitorEnd.Token);
        try
        {
            await DeclareKazuhaCapabilityIfPresentAsync(taskToken);
            // Ignore the member's local operation mode (e.g. refresh records) for a frozen, host-selected manifest.
            var localRoutes = prepared ?? (_config.UseFixedDebugRoutes
                ? LoadRoutesBasedOnConfig() : PrepareNormalWorldRoutes(groupTags, strict: true));
            ValidateTeam();
            await ProcessRoutesByGroup(localRoutes, accountName, roundContext);
            EnsureCooperativeExecutionAllowed();
            if (!_hostWorldExecutorReachedEnd)
                throw new HoeingStartupException("借世界执行", "本机未到达完整路线和重跑收尾，不能上报完工");

            // Finish is idempotent. After sending it, only the matching room's terminal state authorizes normal return.
            using var finish = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            finish.CancelAfter(TimeSpan.FromSeconds(30));
            var receipt = await PollHostWorldAsync(t => client.ReadOrUpdateHostWorldAsync(t, "finish"), _ => true, finish.Token);
            var terminal = receipt.IsTerminal ? receipt : await watch.WaitAsync(run.Token);
            RecordHostWorldTerminal(terminal);
        }
        catch (Exception ex) when (ex is OperationCanceledException or MultiplayerSessionFailedException
            && Volatile.Read(ref _hostWorldStopReceipt) is { } stopped)
        {
            RecordHostWorldTerminal(stopped);
        }
        catch (MultiplayerSessionFailedException) when (!taskToken.IsCancellationRequested)
        {
            // The server may close after an experience-cap terminal before the poll receives it.
            var state = await client.ReadOrUpdateHostWorldAsync(taskToken);
            if (state.Phase != "expCap") throw;
            RecordHostWorldTerminal(state);
        }
        finally
        {
            monitorEnd.Cancel();
            try { await watch; }
            catch (OperationCanceledException) when (monitorEnd.IsCancellationRequested) { }
        }
    }

    private async Task<HostWorldState> ObserveHostWorldTerminalAsync(CoordinatorClient client, CancellationToken ct)
    {
        try
        {
            var state = await PollHostWorldTerminalAsync(client, ct);
            if (state.Phase is "aborted" or "expCap")
            {
                // Publish the result before cancelling the execution loop. The caller reconciles the receipt.
                Volatile.Write(ref _hostWorldStopReceipt, state);
                _linkedStopCts!.Cancel();
            }
            return state;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _stopReason ??= $"无法确认借世界房间仍有效：{ex.Message}";
            _linkedStopCts!.Cancel();
            throw;
        }
    }

    // The room owner publishes file identity, not a route vote. This also verifies a lone executor
    // using local fallback, where comparing the executor with itself would otherwise always pass.
    internal static string ComputeHostWorldManifestDigest(IEnumerable<string> fullPaths, CancellationToken ct)
    {
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in fullPaths)
        {
            ct.ThrowIfCancellationRequested();
            using var file = File.OpenRead(path);
            manifest.AppendData(SHA256.HashData(file));
        }
        ct.ThrowIfCancellationRequested();
        return Convert.ToHexString(manifest.GetHashAndReset());
    }

    private Dictionary<string, RouteInfo> ResolveStandbyOwnerCdRoutes(IEnumerable<RouteInfo> routes)
    {
        var result = new Dictionary<string, RouteInfo>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            _ct.ThrowIfCancellationRequested();
            var (actual, _) = ResolveAndLoadActualVariant(route.FileName, route.FullPath, Path.GetDirectoryName(route.FullPath) ?? "");
            if (actual == null || actual.Positions.Count == 0)
                throw new HoeingStartupException("借世界路线", "无法确认房主本地路线身份: " + route.FileName);
            result.Add(route.FileName, new RouteInfo
            {
                FileName = actual.FileName, NormalMonsterCount = route.NormalMonsterCount,
                EliteMonsterCount = route.EliteMonsterCount, Tags = route.Tags, AdjustedTime = route.AdjustedTime,
            });
        }
        return result;
    }

    private void ApplyVerifiedOwnerCd(HostWorldState state)
    {
        if (_standbyOwnerCdRoutes == null || state.RouteCompletions is not { Length: > 0 }) return;
        if (state.ManifestDigest != _hostWorldManifestDigest)
            throw new HoeingStartupException("房主路线 CD", "完成回执与本轮路线清单不匹配");
        foreach (var receipt in state.RouteCompletions)
        {
            if (!_standbyOwnerCdRoutes.TryGetValue(receipt.RouteName, out var route))
                throw new HoeingStartupException("房主路线 CD", "完成回执不属于本轮房主路线");
            if (_appliedOwnerCdReceipts.Contains(receipt.RouteName)) continue;
            _ownerCdNeedsSave |= _cdManager.ApplyVerifiedCompletion(route, receipt.DurationSeconds, receipt.CompletedAtUtc);
            _appliedOwnerCdReceipts.Add(receipt.RouteName);
        }
        if (_ownerCdNeedsSave)
        {
            _cdManager.SaveOrThrow(atomic: true);
            _ownerCdNeedsSave = false;
        }
    }

    private async Task ReportVerifiedStandbyOwnerRouteAsync(RouteInfo route, double durationSeconds)
    {
        if (!_worldOwnerIsStandby) return;
        if (_originalRouteNames == null || !_originalRouteNames.TryGetValue(route, out var name))
            throw new HoeingStartupException("房主路线 CD", "找不到实际完成路线的原始编号");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        var state = await PollHostWorldAsync(t => _coordinatorClientRef!.ReadOrUpdateHostWorldAsync(t, "routeDone",
            manifestDigest: _hostWorldManifestDigest, routeName: name, durationSeconds: durationSeconds),
            s => s.IsTerminal || s.RouteCompletions?.Any(r => r.RouteName == name) == true, budget.Token);
        if (!state.IsTerminal && state.RouteCompletions?.Any(r => r.RouteName == name) != true)
            throw new HoeingStartupException("房主路线 CD", "未确认服务器收到实际完成回执");
    }

    private HostWorldState? _hostWorldStopReceipt;

    private Task<HostWorldState> PollHostWorldTerminalAsync(CoordinatorClient client, CancellationToken ct)
        => PollHostWorldAsync(async t =>
        {
            var state = await client.ReadOrUpdateHostWorldAsync(t);
            ApplyVerifiedOwnerCd(state);
            return state;
        }, s => s.IsTerminal, ct, checkRestriction: true);

    private async Task<HostWorldState> PollHostWorldAsync(Func<CancellationToken, Task<HostWorldState>> read,
        Func<HostWorldState, bool> accept, CancellationToken ct, bool checkRestriction = false)
    {
        const int maxFailures = 3;
        var failures = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (checkRestriction && (_timeChecker.IsInRestrictedPeriod() || _timeChecker.IsApproachingRestriction()))
                throw new HoeingStartupException("借世界收尾", "已接近限制时间，停止本场");
            try
            {
                var state = await read(ct);
                ct.ThrowIfCancellationRequested();
                failures = 0;
                if (accept(state)) return state;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                && (ex is IOException or HttpRequestException or WebSocketException or TimeoutException or OperationCanceledException)
                && ++failures < maxFailures)
            {
                _logger.LogDebug(ex, "[借世界] 原房间请求未确认，将有界重试");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private void RecordHostWorldTerminal(HostWorldState state)
    {
        if (!state.IsSuccess)
            throw new HoeingStartupException("借世界收尾", state.Reason ?? "房间中断，未确认正常结束");
        if (state.Phase == "expCap")
        {
            _expCapStopTriggered = true;
            _stopReason = "执行成员均达经验上限，结束本场锄地";
        }
        if (!_multiWorldRoundLoopEngaged) _completedNormally = true;
        _logger.LogInformation("[借世界] 已确认本场终态 {Phase}", state.Phase);
    }
}
