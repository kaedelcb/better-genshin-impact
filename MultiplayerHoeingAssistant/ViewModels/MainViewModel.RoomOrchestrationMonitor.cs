using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    internal sealed record OrchestrationMonitorTarget(string Uid, string InstanceId, string MachineId, string Name)
    {
        public string Label => $"{Name} · {InstanceId[..Math.Min(8, InstanceId.Length)]} · {MachineId[..Math.Min(8, MachineId.Length)]}";
    }

    private sealed record OrchestrationRoute(RemoteCenterTarget Target, string Server, string Room, RoomOrchestrationMonitorLink? Link)
    {
        internal OrchestrationMonitorTarget Identity => new(Target.PlayerUid, Target.ClientInstanceId, Target.MachineId, Target.PlayerName);
        internal bool Matches(OrchestrationMonitorTarget identity) => Target.PlayerUid == identity.Uid
            && Target.ClientInstanceId == identity.InstanceId && Target.MachineId == identity.MachineId;
    }

    private readonly Dictionary<string, RoomOrchestrationMonitorLink> _orchestrationLinks = new(StringComparer.Ordinal);
    private CancellationTokenSource? _orchestrationMonitorLifetime;
    private OrchestrationMonitorTarget? _orchestrationSelected;
    private OrchestrationRoute[] _orchestrationRoutes = [];
    private RemoteCenterTarget[] _orchestrationLocalTargets = [];
    private RemoteOrchestrationSnapshot? _orchestrationRemoteSnapshot;
    private OrchestrationRoute? _orchestrationSnapshotRoute;
    private DateTimeOffset _orchestrationReceivedAt, _orchestrationNextRefresh, _orchestrationNextLocalProbe;
    private string _orchestrationMonitorMessage = "正在查找本人的执行端…";
    private bool _orchestrationMonitorBusy;
    private AssistConfig? _orchestrationMonitorConfig;
    private string _orchestrationPrimaryScope = "";

    internal OrchestrationMonitorTarget? SelectedOrchestrationMonitorTarget => _roomFollowing is { } follow
        ? new(follow.Invitation.Uid, follow.Invitation.InstanceId, follow.Invitation.MachineId, "邀请中的执行端") : _orchestrationSelected;
    internal OrchestrationMonitorTarget[] OrchestrationMonitorTargets => IsRoomFollowing ? [] : _orchestrationRoutes.Select(r => r.Identity)
        .DistinctBy(t => (t.Uid, t.InstanceId, t.MachineId)).ToArray();

    internal void SelectOrchestrationMonitorTarget(OrchestrationMonitorTarget target)
    {
        if (IsRoomFollowing || !IsObserverMode || !OrchestrationMonitorTargets.Contains(target) || SameOrchestrationTarget(target, _orchestrationSelected)) return;
        _orchestrationSelected = target;
        _orchestrationRemoteSnapshot = null;
        _orchestrationSnapshotRoute = null;
        _orchestrationNextRefresh = DateTimeOffset.MinValue;
        _orchestrationMonitorMessage = "正在读取所选执行端的房间配置…";
    }

    private static bool SameOrchestrationTarget(OrchestrationMonitorTarget? a, OrchestrationMonitorTarget? b)
        => a is not null && b is not null && a.Uid == b.Uid && a.InstanceId == b.InstanceId && a.MachineId == b.MachineId;

    internal void StopRoomOrchestrationMonitor()
    {
        var lifetime = _orchestrationMonitorLifetime;
        _orchestrationMonitorLifetime = null;
        lifetime?.Cancel(); lifetime?.Dispose();
        foreach (var link in _orchestrationLinks.Values) _ = DisposeOrchestrationLinkAsync(link);
        _orchestrationLinks.Clear();
        _orchestrationRoutes = []; _orchestrationLocalTargets = [];
        _orchestrationSnapshotRoute = null;
        _orchestrationMonitorConfig = null;
        _orchestrationMonitorBusy = false;
        _orchestrationNextRefresh = _orchestrationNextLocalProbe = DateTimeOffset.MinValue;
        _orchestrationMonitorMessage = "正在重新核对执行端…";
    }

    private static async Task DisposeOrchestrationLinkAsync(RoomOrchestrationMonitorLink link)
    {
        try { await link.DisposeAsync(); } catch { /* independent observer cleanup must not affect the main room */ }
    }

    internal async Task RefreshRoomOrchestrationMonitorAsync()
    {
        // The follow snapshot only grants metadata and view selection. Do not use the old
        // same-UID task graph discovery (including its additional observer room logins) for it.
        if (IsRoomFollowing) return;
        if (_disposing || !IsObserverMode || !IsShowingRoomOrchestration || _config is not { } config
            || string.IsNullOrWhiteSpace(config.PlayerUid)) { StopRoomOrchestrationMonitor(); return; }
        if (!ReferenceEquals(_orchestrationMonitorConfig, config) || _orchestrationSelected is { } selected && selected.Uid != config.PlayerUid)
        {
            StopRoomOrchestrationMonitor();
            if (_orchestrationSelected?.Uid != config.PlayerUid) { _orchestrationSelected = null; _orchestrationRemoteSnapshot = null; }
            _orchestrationMonitorConfig = config;
        }
        var lifetime = _orchestrationMonitorLifetime ??= new();
        if (_orchestrationMonitorBusy) return;
        var ct = lifetime.Token;
        _orchestrationMonitorBusy = true;
        try
        {
            var server = config.ServerUrl;
            var room = RoomCode;
            // Use the actual primary connection/IPC for the current room. Other saved rooms
            // receive independent observer connections authenticated by their own saved password.
            var scope = JsonSerializer.Serialize(new[] { server, room, config.ControlRoomPassword });
            if (_orchestrationPrimaryScope != scope)
            {
                _orchestrationPrimaryScope = scope; _orchestrationLocalTargets = [];
                _orchestrationNextLocalProbe = DateTimeOffset.MinValue;
            }
            var desired = config.RoomProfiles.Where(p => !IsStandaloneMode && !string.IsNullOrWhiteSpace(p.RoomCode)
                    && !string.IsNullOrEmpty(p.Password) && RoomServerAddress.TryNormalize(config.GetRoomServerUrl(p), out _)
                    && !(p.RoomCode == room && RoomServerAddress.Same(config.GetRoomServerUrl(p), server)))
                .GroupBy(p => p.Id, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single()).ToArray();
            foreach (var (id, link) in _orchestrationLinks.ToArray())
            {
                var profile = desired.FirstOrDefault(p => p.Id == id);
                if (profile is not null && link.Matches(profile, config.GetRoomServerUrl(profile), config.PlayerUid, config.BypassSystemProxy)) continue;
                _orchestrationLinks.Remove(id); _ = DisposeOrchestrationLinkAsync(link);
            }
            foreach (var profile in desired)
            {
                if (!_orchestrationLinks.TryGetValue(profile.Id, out var link))
                    _orchestrationLinks[profile.Id] = link = new(profile, config.GetRoomServerUrl(profile), config.PlayerUid, config.PlayerName, config.BypassSystemProxy);
                link.EnsureConnected();
            }
            if (DateTimeOffset.UtcNow >= _orchestrationNextLocalProbe)
            {
                _orchestrationNextLocalProbe = DateTimeOffset.UtcNow.AddSeconds(10);
                var locals = await FindLocalCenterExecutorsAsync();
                if (ct.IsCancellationRequested || !ReferenceEquals(config, _config) || RoomCode != room || !RoomServerAddress.Same(server, config.ServerUrl)) return;
                _orchestrationLocalTargets = locals.ToArray();
            }
            var primary = _orchestrationLocalTargets.Where(t => t.PlayerUid == config.PlayerUid).ToList();
            if (_signalRClient is { IsControlRoomReady: true } client && client.CurrentRoomCode == room && RoomServerAddress.Same(client.CurrentServerUrl, server))
                primary.AddRange(RemoteCenterTargets.Where(t => t.Online && t.PlayerUid == config.PlayerUid && !t.ForceLocal
                    && MatchesCenterTargetScope(BindCenterTargetScope(t, server, room), server, room)));
            _orchestrationRoutes = primary.Select(t => new OrchestrationRoute(FreezeOrchestrationTarget(t, server, room), server, room, null))
                .Concat(_orchestrationLinks.Values.SelectMany(link => link.Targets().Select(t => new OrchestrationRoute(t, link.ServerUrl, link.RoomCode, link))))
                .Where(r => !string.IsNullOrWhiteSpace(r.Target.ClientInstanceId) && !string.IsNullOrWhiteSpace(r.Target.MachineId))
                .DistinctBy(r => (r.Target.PlayerUid, r.Target.ClientInstanceId, r.Target.MachineId, r.Server, r.Room)).ToArray();
            var targets = OrchestrationMonitorTargets;
            if (_orchestrationSelected is null && targets.Length == 1 && _orchestrationLinks.Values.All(l => l.InitialDiscoveryComplete))
                _orchestrationSelected = targets[0];
            if (_orchestrationSelected is not { } chosen)
            {
                _orchestrationMonitorMessage = targets.Length == 0 ? "未找到在线执行端；正在核对已保存房间。"
                    : targets.Length == 1 ? "已找到执行端；可直接选择，其他房间仍在核对。" : "检测到多个本人执行端，请选择一个。";
                return;
            }
            var routes = _orchestrationRoutes.Where(r => r.Matches(chosen) && IsOrchestrationRouteCurrent(r)).ToArray();
            if (routes.Length != 1)
            {
                _orchestrationSnapshotRoute = null;
                _orchestrationMonitorMessage = routes.Length == 0 ? "所选执行端离线或正在切房，等待原实例重新出现。"
                    : "所选实例的登录房间尚未唯一确认，正在核对切换结果。";
                return;
            }
            var route = routes[0];
            if (_orchestrationSnapshotRoute is { } prior && !SameOrchestrationRoute(prior, route)) _orchestrationSnapshotRoute = null;
            if (DateTimeOffset.UtcNow < _orchestrationNextRefresh && _orchestrationSnapshotRoute is not null) return;
            _orchestrationNextRefresh = DateTimeOffset.UtcNow.AddSeconds(3);
            var reply = await SendOrchestrationRequestAsync(route, "room.orchestration.get", null, null, NewOrchestrationRequestId(), ct);
            if (ct.IsCancellationRequested || !SameOrchestrationTarget(chosen, _orchestrationSelected) || !IsOrchestrationRouteCurrent(route)) return;
            if (reply.Status != "ok") { _orchestrationSnapshotRoute = null; _orchestrationMonitorMessage = reply.Message; return; }
            InstallOrchestrationSnapshot(route, ReadOrchestrationReply<RemoteOrchestrationSnapshot>(reply)!);
        }
        catch (Exception ex) { if (!ct.IsCancellationRequested) { _orchestrationSnapshotRoute = null; _orchestrationMonitorMessage = "执行端状态未确认：" + ex.Message; } }
        finally { if (ReferenceEquals(lifetime, _orchestrationMonitorLifetime)) _orchestrationMonitorBusy = false; }
    }

    private RemoteCenterTarget FreezeOrchestrationTarget(RemoteCenterTarget target, string server, string room)
    {
        var frozen = new RemoteCenterTarget { PlayerUid = target.PlayerUid, PlayerName = target.PlayerName,
            ClientInstanceId = target.ClientInstanceId, MachineId = target.MachineId, Online = target.Online, ForceLocal = target.ForceLocal };
        BindCenterTargetScope(frozen, server, room);
        return frozen;
    }

    private static bool SameOrchestrationRoute(OrchestrationRoute a, OrchestrationRoute b)
        => a.Matches(b.Identity) && a.Room == b.Room && RoomServerAddress.Same(a.Server, b.Server) && ReferenceEquals(a.Link, b.Link);

    private bool IsOrchestrationRouteCurrent(OrchestrationRoute route)
    {
        if (_disposing || !IsObserverMode || _config is not { } config || config.PlayerUid != route.Target.PlayerUid) return false;
        if (route.Link is { } link)
            return _orchestrationLinks.TryGetValue(link.ProfileId, out var current) && ReferenceEquals(current, link)
                && config.RoomProfiles.Any(p => link.Matches(p, config.GetRoomServerUrl(p), config.PlayerUid, config.BypassSystemProxy))
                && link.Targets().Any(t => route.Matches(new(t.PlayerUid, t.ClientInstanceId, t.MachineId, t.PlayerName)));
        if (RoomCode != route.Room || !RoomServerAddress.Same(config.ServerUrl, route.Server)) return false;
        if (route.Target.ForceLocal) return _orchestrationLocalTargets.Any(t => t.ClientInstanceId == route.Target.ClientInstanceId && t.MachineId == route.Target.MachineId);
        return _signalRClient is { IsControlRoomReady: true } client && client.CurrentRoomCode == route.Room
            && RoomServerAddress.Same(client.CurrentServerUrl, route.Server) && RemoteCenterTargets.Any(t => t.Online && !t.ForceLocal
                && t.PlayerUid == route.Target.PlayerUid && t.ClientInstanceId == route.Target.ClientInstanceId && t.MachineId == route.Target.MachineId
                && MatchesCenterTargetScope(BindCenterTargetScope(t, route.Server, route.Room), route.Server, route.Room));
    }

    private void InstallOrchestrationSnapshot(OrchestrationRoute route, RemoteOrchestrationSnapshot snapshot)
    {
        if (snapshot.PlayerUid != route.Target.PlayerUid || snapshot.InstanceId != route.Target.ClientInstanceId
            || snapshot.MachineId != route.Target.MachineId || snapshot.Presentation is null)
            throw new InvalidOperationException("执行端身份不匹配，未采用回复。");
        _orchestrationRemoteSnapshot = snapshot; _orchestrationSnapshotRoute = route;
        _orchestrationReceivedAt = DateTimeOffset.UtcNow;
        _orchestrationMonitorMessage = "";
    }

    private bool HasFreshOrchestrationSnapshot => _orchestrationSnapshotRoute is { } route && _orchestrationRemoteSnapshot is not null
        && _orchestrationSelected is { } selected && route.Matches(selected) && IsOrchestrationRouteCurrent(route)
        && DateTimeOffset.UtcNow - _orchestrationReceivedAt < TimeSpan.FromSeconds(12);

    private RoomOrchestrationPresentation GetMonitorRoomOrchestrationPresentation()
    {
        var label = _orchestrationSelected is { } target ? "执行端：" + target.Label : "监控端 · 任务编排";
        if (HasFreshOrchestrationSnapshot && _orchestrationRemoteSnapshot is { } snapshot)
            return snapshot.Presentation with { LoginContext = label + " · " + snapshot.Presentation.LoginContext };
        // Retain only the last confirmed configuration and explicitly mark ALL observed progress
        // as unknown. A disconnected or switching executor is not a waiting/finished executor.
        var stale = _orchestrationRemoteSnapshot;
        var context = label + " · " + (string.IsNullOrEmpty(_orchestrationMonitorMessage) ? "正在重新确认状态" : _orchestrationMonitorMessage);
        var status = new RoomOrchestrationStatus("unknown", "待核对");
        if (stale is null) return new("执行端登录：尚未确认", context, status, [], []);
        var last = "上次确认 " + _orchestrationReceivedAt.ToLocalTime().ToString("HH:mm:ss");
        RoomOrchestrationEdge? StaleEdge(RoomOrchestrationEdge? edge) => edge is null ? null : edge with
        { SwitchStatus = status, PostStatus = edge.PostStatus is null ? null : status, OnlineResult = "", Detail = last + "，当前状态未知" };
        var cards = stale.Presentation.Cards.Select(c => c with { Current = false, CanEdit = false, Status = status, ProgressUnconfirmed = true,
            UpdatedText = last, Detail = "以下为上次读取的配置和任务，当前进度尚未确认。", TasksCaption = "上次读取的任务",
            Tasks = c.Tasks.Select(t => t with { Status = status }).ToArray(), NextTasks = c.NextTasks.Select(t => t with { Status = status }).ToArray(),
            Edge = StaleEdge(c.Edge), PreviousEdge = StaleEdge(c.PreviousEdge) }).ToArray();
        return new("执行端登录：待核对", context + " · " + last, status, [new("unknown", $"待核对 {cards.Length}")], cards);
    }

    private static string NewOrchestrationRequestId() => "room-orchestration-" + Guid.NewGuid().ToString("N");

    private async Task<RemoteCenterReply> SendOrchestrationRequestAsync(OrchestrationRoute route, string operation,
        object? payload, string? revision, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsOrchestrationRouteCurrent(route)) return RemoteCenterReply.Rejected("执行端原房间或实例已变化，请重新读取。");
        if (route.Link is null) return await RequestRemoteCenterAsync(route.Target, operation, payload, revision, id,
            cancellationToken: ct, expectedMachineId: route.Target.MachineId);
        return await route.Link.RequestAsync(route.Target, new RemoteCenterRequest { Operation = operation,
            TargetInstanceId = route.Target.ClientInstanceId, ExpectedMachineId = route.Target.MachineId, ExpectedRevision = revision,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload) }, id, ct);
    }

    private static T? ReadOrchestrationReply<T>(RemoteCenterReply reply)
    {
        if (reply.Status != "ok") throw new InvalidOperationException(reply.Message);
        return reply.PayloadJson is null ? default : JsonSerializer.Deserialize<T>(reply.PayloadJson);
    }

    private async Task<RemoteCenterReply> RequestOrchestrationEditorAsync(OrchestrationRoute route, string operation, object? payload,
        string? revision, bool tracked, CancellationToken ct)
    {
        if (_orchestrationSelected is not { } selected || !route.Matches(selected))
            throw new InvalidOperationException("执行端选择已变化，请重新打开编辑器。");
        var id = NewOrchestrationRequestId();
        try
        {
            var reply = await SendOrchestrationRequestAsync(route, operation, payload, revision, id, ct);
            ct.ThrowIfCancellationRequested();
            if (!tracked) return reply;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(operation == "room.orchestration.save" ? 24 : 190);
            while (reply.Status is "accepted" or "unknown" && DateTimeOffset.UtcNow < deadline && IsOrchestrationRouteCurrent(route))
            {
                await Task.Delay(750, ct);
                var receipt = await SendOrchestrationRequestAsync(route, "operation.get", id, null, NewOrchestrationRequestId(), ct);
                // A missing receipt after a lost reply is not proof the mutation never ran.
                if (receipt.OperationId == id) reply = receipt;
                else if (receipt.Status == "rejected") break;
            }
            if (reply.Status is "accepted" or "unknown")
            {
                var message = "执行端操作结果未确认，请关闭编辑器并重新读取执行端配置。操作编号：" + id;
                if (operation == "room.orchestration.save") throw new RoomOrchestrationUnconfirmedException(message);
                throw new InvalidOperationException(message);
            }
            if (operation == "room.orchestration.save" && reply.Status == "ok" && reply.OperationId != id)
                throw new RoomOrchestrationUnconfirmedException("保存回执与本次操作不匹配，请关闭编辑器重新读取。操作编号：" + id);
            ct.ThrowIfCancellationRequested();
            return reply;
        }
        catch (OperationCanceledException)
        {
            if (tracked && operation != "room.orchestration.save" && IsOrchestrationRouteCurrent(route))
                _ = SendOrchestrationRequestAsync(route, "room.orchestration.cancel", id, null, NewOrchestrationRequestId(), CancellationToken.None);
            throw;
        }
    }

    internal async Task<(RemoteOrchestrationEdit Edit, RoomOrchestrationEditorBackend Backend)> ReadMonitorRoomOrchestrationEditorAsync(string profileId)
    {
        if (IsRoomFollowing || !HasFreshOrchestrationSnapshot || _orchestrationSnapshotRoute is not { } route)
            throw new InvalidOperationException("执行端状态尚未确认或正在只读跟随，请退出跟随后等待重新读取再编辑。");
        var ct = _orchestrationMonitorLifetime?.Token ?? CancellationToken.None;
        var reply = await RequestOrchestrationEditorAsync(route, "room.orchestration.edit", profileId, null, false, ct);
        var edit = ReadOrchestrationReply<RemoteOrchestrationEdit>(reply) ?? throw new InvalidOperationException("执行端未返回编辑配置。");
        if (!IsOrchestrationRouteCurrent(route) || edit.Snapshot.Key.ProfileId != profileId || edit.Snapshot.Key.PlayerUid != route.Target.PlayerUid)
            throw new InvalidOperationException("执行端房间身份已变化，请重新读取。");
        var backend = new RoomOrchestrationEditorBackend(route.Target.Label, edit.HasPeerSnapshot,
            async (type, name, workflow, refresh, token) => ReadOrchestrationReply<CompletionTargetCatalog>(await RequestOrchestrationEditorAsync(route,
                "room.orchestration.catalog", new RemoteOrchestrationCatalogRequest(profileId, type, name, workflow, refresh), null, refresh, token))
                ?? throw new InvalidOperationException("执行端未返回任务目录。"),
            async (refresh, token) => ReadOrchestrationReply<RoomOrchestrationNameCatalog>(await RequestOrchestrationEditorAsync(route,
                "room.orchestration.names", new RemoteOrchestrationCatalogRequest(profileId, "group", "", "", refresh), null, refresh, token)),
            (_, _) => Task.FromResult<string?>(null), // validated again on the authoritative executor at save
            async (bindings, token) => ReadOrchestrationReply<string>(await RequestOrchestrationEditorAsync(route,
                "room.orchestration.layout", new RemoteOrchestrationLayoutRequest(profileId, bindings), null, false, token)),
            async (changes, _, conflict, token) =>
            {
                _orchestrationSnapshotRoute = null;
                var saved = await RequestOrchestrationEditorAsync(route, "room.orchestration.save",
                    new RemoteOrchestrationSaveRequest(profileId, changes, conflict), edit.Revision, true, token);
                if (saved.Status != "ok") throw new InvalidOperationException(saved.Message);
                try
                {
                    var snapshot = ReadOrchestrationReply<RemoteOrchestrationSnapshot>(saved);
                    if (saved.Status == "ok")
                    {
                        if (snapshot is null || string.IsNullOrEmpty(saved.OperationId) || saved.TargetInstanceId != route.Target.ClientInstanceId)
                            throw new InvalidOperationException("执行端保存回复缺少确认身份或配置快照");
                        if (snapshot.PlayerUid != route.Target.PlayerUid || snapshot.InstanceId != route.Target.ClientInstanceId
                            || snapshot.MachineId != route.Target.MachineId)
                            throw new InvalidOperationException("执行端保存回复身份不匹配");
                        if (IsOrchestrationRouteCurrent(route)) InstallOrchestrationSnapshot(route, snapshot);
                    }
                }
                catch (Exception ex) { throw new RoomOrchestrationUnconfirmedException("保存结果待核对，请关闭编辑器重新读取：" + ex.Message); }
                _orchestrationNextRefresh = DateTimeOffset.MinValue;
            });
        return (edit, backend);
    }

    private async Task EditMonitorRoomOrchestrationAsync(string profileId, System.Windows.Window owner)
    {
        var (edit, backend) = await ReadMonitorRoomOrchestrationEditorAsync(profileId);
        var window = new Views.RoomOrchestrationEditorWindow(this, edit.Snapshot, edit.Choices, NewDarkCombo, owner, backend);
        window.ShowDialog();
        _orchestrationNextRefresh = DateTimeOffset.MinValue;
    }
}

internal sealed class RoomOrchestrationUnconfirmedException(string message) : InvalidOperationException(message);

internal sealed record RoomOrchestrationEditorBackend(string ExecutorLabel, bool HasPeerSnapshot,
    Func<string, string, string, bool, CancellationToken, Task<CompletionTargetCatalog>> Catalog,
    Func<bool, CancellationToken, Task<RoomOrchestrationNameCatalog?>> Names,
    Func<TaskConflictPolicySettings, CancellationToken, Task<string?>> ValidateCompletion,
    Func<RoomOrchestrationBinding[]?, CancellationToken, Task<string?>> Layout,
    Func<RoomOrchestrationEditChanges, RoomOrchestrationNameCatalog?, string?, CancellationToken, Task> Save);
