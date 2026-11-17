using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Model;
namespace BetterGenshinImpact.GameTask.AutoHoeing;
public partial class AutoHoeingTask
{

    private void ApplyWorldHostConfig(RoomConfig hostConfig)
    {
        AdoptRoutePackagePin(hostConfig.RoutePackageId);
        _config.SyncPointMinDistance = hostConfig.SyncPointMinDistance;
        _config.StartRouteIndex = hostConfig.StartRouteIndex;
        _config.RouteFilterKeywords = hostConfig.RouteFilterKeywords;
        // 线路重试模式关键词房主同步（hoeing-multiplayer-route-retry-mode §0 / EB-v2-4）
        _config.RouteRetryModeKeywords = hostConfig.RouteRetryModeKeywords;
        _config.UseFixedDebugRoutes = hostConfig.UseFixedDebugRoutes;
        _config.FixedDebugRoutePath = hostConfig.FixedDebugRoutePath;
        _config.DebugMode = hostConfig.DebugMode;
        _config.EnableKazuhaSync = hostConfig.EnableKazuhaSync;
        _config.KazuhaSyncWaitSeconds = hostConfig.KazuhaSyncWaitSeconds;
        _config.KazuhaSyncTimeoutSeconds = hostConfig.KazuhaSyncTimeoutSeconds;
        _config.KazuhaWaitSkillCdSeconds = hostConfig.KazuhaWaitSkillCdSeconds;
        _config.KazuhaSecondApproachMaxSteps = hostConfig.KazuhaSecondApproachMaxSteps;
        _config.PartyTimeoutSeconds = hostConfig.PartyTimeoutSeconds;
        _config.MultiWorldEnabled = hostConfig.MultiWorldEnabled;
        _config.MultiWorldCount = hostConfig.MultiWorldCount;
        _config.SelectedBuiltinRoute = hostConfig.SelectedBuiltinRoute;
        _config.FightExtraWaitSeconds = hostConfig.FightExtraWaitSeconds;
        _config.RejoinMaxWaitSeconds = hostConfig.RejoinMaxWaitSeconds;
        // === 集体卡死监测同步（multiplayer-mutual-wait-collective-skip §8.8）===
        _config.EnableMutualWaitCollectiveSkip = hostConfig.EnableMutualWaitCollectiveSkip;
        _config.EnableRouteAnchor = hostConfig.EnableRouteAnchor;
        _config.MutualWaitMinWaitersRatio = hostConfig.MutualWaitMinWaitersRatio;
        _config.MutualWaitStableSeconds = hostConfig.MutualWaitStableSeconds;
        _config.MaxConsecutiveCollectiveSkips = hostConfig.MaxConsecutiveCollectiveSkips;
        // === 快速同步点抢报同步（multiplayer-fast-sync-host-controlled spec FR5）===
        _config.FastSyncPointEnabled = hostConfig.FastSyncPointEnabled;
        _config.FastSyncPathingDistance = FastSyncDecisions.ClampPathingDistance(hostConfig.FastSyncPathingDistance);
        _config.FastSyncTeleportLoadingDelayMs = FastSyncDecisions.ClampTeleportDelay(hostConfig.FastSyncTeleportLoadingDelayMs);
        // === 共享战斗配额结束同步同步（multiplayer-shared-fight-end-quorum-sync spec）===
        _config.SharedFightEndQuorumEnabled = hostConfig.SharedFightEndQuorumEnabled;
        _config.SharedFightEndQuorumRatio = SharedFightEndQuorumDecisions.ClampRatio(hostConfig.SharedFightEndQuorumRatio);
        // === 落后追赶同步（hoeing-lagging-catchup-host-synced-setting spec，成员回填房主下发值）===
        _config.EnableLaggingCatchUp = hostConfig.EnableLaggingCatchUp;
        _config.LagSegmentThreshold = Math.Clamp(hostConfig.LagSegmentThreshold, 1, 3);
        // === 基于经验判断停止锄地同步（multiplayer-hoeing-exp-cap-stop，成员回填房主下发值）===
        _config.EnableExpCapStop = hostConfig.EnableExpCapStop;
        _config.ExpCapDetectAllExp = hostConfig.ExpCapDetectAllExp;
        // === 守护模式同步（hoeing-multiplayer-guard-auto-restart，成员回填房主下发值）===
        _config.HoeingGuardMode = hostConfig.HoeingGuardMode;
        _config.GuardUnexecutedRouteThreshold = Multiplayer.HoeingGuardDecisions.ClampThreshold(hostConfig.GuardUnexecutedRouteThreshold);
        // === 自动更新线路同步（hoeing-autoupdate-routes，成员回填房主下发值）===
        _config.AutoUpdateRoutes = hostConfig.AutoUpdateRoutes;
        _config.RoomWhitelist = hostConfig.RoomWhitelist ?? "";

        // === 执行期参数房主同步（hoeing-multiplayer-sync-execution-params §C2）===
        ApplyHostExecParams(hostConfig);

        // 联机模式：设置战斗超时覆盖值（不修改原始配置）
        PathingConditionConfig.MultiplayerFightTimeoutOverride = hostConfig.FightTimeoutSeconds;
        // multiplayer-hoeing-selectable-fight-strategy §C5: 战斗策略开关纯本地，读本机 _config（不读 hostConfig）
        PathingConditionConfig.MultiplayerUseFixedFightStrategyOverride = _config.MultiplayerUseFixedFightStrategy;

    }

    private List<RouteInfo> PrepareNormalWorldRoutes(List<List<string>> groupTags, bool strict = false)
    {
        RequireMonsterMetadata();
        // 预处理路线
        var pathingDir = PathingDirectory;
        var routes = RouteInfoLoader.LoadRoutes(
            pathingDir, _monsterRepo, _config.IgnoreRate, groupTags[0], strict || _config.MultiplayerEnabled);
        RequireOfficialPathingRoutes(routes);

        // 初始化CD和运行记录
        foreach (var route in routes)
            _cdManager.InitializeRoute(route);

        // 自我优化
        SelfOptimizer.Apply(routes, _config.DisableSelfOptimization, _config.CuriosityFactor);

        // 标记过滤
        var priorityTags = ParseChineseTags(_config.PriorityTags);
        var excludeTags = ParseChineseTags(_config.ExcludeTags);
        if (!_config.PickupMode.Contains("模板匹配") && !excludeTags.Contains("沙暴"))
            excludeTags.Add("沙暴");

        RouteMarker.MarkRoutes(routes, groupTags, priorityTags, excludeTags);

        // 路线选择优化
        var targetElite = Math.Max(0, _config.TargetEliteNum) + 5;
        var targetMonster = Math.Max(0, _config.TargetMonsterNum) + 25;
        _routeSelector.SelectOptimalRoutes(
            routes, _config.EfficiencyIndex, targetElite, targetMonster, _config.SortMode);

        // 分组分配
        RouteGroupAssigner.AssignGroups(routes, groupTags);

        return routes;
    }

    private List<RouteInfo> PrepareTargetWorldRoutes(List<string> targetMonsters, bool strict = false)
    {
        RequireMonsterMetadata();
        var pathingDir = PathingDirectory;
        var fakeGroupTags = Enumerable.Range(0, 10).Select(_ => new List<string>()).ToList();
        var routes = RouteInfoLoader.LoadRoutes(pathingDir, _monsterRepo, _config.IgnoreRate, fakeGroupTags[0], strict || _config.MultiplayerEnabled);
        RequireOfficialPathingRoutes(routes);

        foreach (var route in routes)
            _cdManager.InitializeRoute(route);

        // 逐路线匹配
        foreach (var route in routes)
        {
            var textToSearch = (route.FullPath ?? "") + " " +
                string.Join(" ", route.MonsterInfo.Keys);
            route.Selected = targetMonsters.Any(m => textToSearch.Contains(m));
            route.Group = route.Selected ? 1 : 0;
        }

        return routes;
    }

    private MultiWorldStartPlan? _worldStartPlan;
    private WorldPlanAdmission? _worldPlanAdmission;
    private bool _worldPlanNoWorkConfirmed;
    private bool _worldRoutesUpdateSucceeded;
    private string _routePackageId = "";
    private AutoHoeingRouteUpdater? _routePackageUpdater;
    private AutoHoeingRouteUpdater.Lease? _routePackageLease;
    private readonly SemaphoreSlim _routePackagePreparation = new(1, 1);
    private string _confirmedRoutePackageId = "";
    private bool _routePackageExecutionStarted;
    private bool _routePackageTaskEnded;
    // 测试替身返回实际确认的包 ID；空值或不同 pin 不能授权预判/执行。
    internal Func<string?, bool, CancellationToken, Task<string>>? UpdateHoeingRoutes { get; set; }

    private string PathingDirectory
    {
        get
        {
            var path = _config.MultiplayerEnabled
                ? Path.Combine(_routePackageUpdater?.CurrentDirectory ?? Path.Combine(AutoHoeingRoutePaths.ManagedRoot, "current"), "pathing")
                : Path.Combine(_dataDir, "pathing");
            if (_config.MultiplayerEnabled && !Directory.Exists(path))
                throw new HoeingMissingResourceException("本轮线路包不含普通选线 pathing，不能按空世界跳过：" + path);
            return path;
        }
    }

    private void RequireOfficialPathingRoutes(List<RouteInfo> routes)
    {
        if (_config.MultiplayerEnabled && routes.Count == 0)
            throw new HoeingMissingResourceException("本轮线路包的普通选线目录为空，不能按已完成世界跳过");
    }

    private void RequireMonsterMetadata()
    {
        if (!_config.MultiplayerEnabled || _monsterRepo.GetAll().Count > 0) return;
        var path = Path.Combine(_dataDir, "assets", "monsterInfo.json");
        try
        {
            _monsterRepo.Load(path);
            if (_monsterRepo.GetAll().Count == 0) throw new InvalidDataException("怪物元数据为空");
        }
        catch (Exception ex)
        {
            throw new HoeingMissingResourceException("普通选线缺少有效怪物元数据：" + path + "；" + ex.Message);
        }
    }

    private void AdoptRoutePackagePin(string pin)
    {
        if (!NexusBGI.RoutePackages.RoutePackageArchive.IsValidId(pin)
            || (_routePackageId.Length > 0 && _routePackageId != pin))
            throw new HoeingStartupException("线路整包身份", "房主未提供有效包身份或同轮包身份发生变化");
        _routePackageId = pin;
    }

    private AutoHoeingTask CreateGuardRestartTask() => new(_partyConfig, _settingsOverride, _groupName)
    {
        _isGuardRestartRun = true,
        _routePackageId = _routePackageId,
        UpdateHoeingRoutes = UpdateHoeingRoutes
    };

    private async Task ApplyPinnedHostConfigAsync(RoomConfig hostConfig, CancellationToken ct)
    {
        ApplyWorldHostConfig(hostConfig);
        await RunAutoUpdateRoutesAsync(operationToken: ct);
    }

    // Only set after the server confirmed a partial-party Legacy decision. Network and F2
    // assembly spend the same budget; the latter waits for the actual selected party.
    private int? _legacyPartySize;
    private IReadOnlyList<string>? _manualFrozenParty;
    private IReadOnlyList<string>? RequiredWorldParticipants => _worldStartPlan?.ParticipantUids ?? _manualFrozenParty;

    private void AdoptManualParty(MultiWorldStartPlan decision)
    {
        if (!decision.CurrentPartyFrozen) return;
        var uids = decision.ParticipantUids;
        if (decision.Stage != "Legacy" || uids.Count is < 1 or > 4 || uids.Any(string.IsNullOrWhiteSpace)
            || uids.Distinct(StringComparer.Ordinal).Count() != uids.Count || !uids.Contains(_config.PlayerUid))
            throw new InvalidOperationException("按当前人数开始的成员身份不完整");
        _manualFrozenParty = uids.ToArray();
    }

    private bool _keepWaitingForLateMember;
    private long _partyWaitStarted;
    private int InitialPartySize() => _keepWaitingForLateMember ? _config.ExpectedPlayerCount :
        _legacyPartySize ?? _config.ExpectedPlayerCount;
    private int RemainingPartyWaitSeconds()
        => _legacyPartySize == null ? _config.PartyTimeoutSeconds :
            Math.Max(1, (int)Math.Ceiling(_config.PartyTimeoutSeconds -
                System.Diagnostics.Stopwatch.GetElapsedTime(_partyWaitStarted).TotalSeconds));

    private async Task PublishPartialPartyAsync(CoordinatorClient client, bool manual, CancellationToken ct)
    {
        var admission = await client.BeginWorldPlanAsync(ct, forceLegacy: true, freezeCurrentParty: manual);
        var snapshot = admission?.Snapshot;
        if (snapshot?.Stage != "Legacy" || snapshot.ParticipantUids.Count is < 1 or > 4)
            throw new InvalidOperationException("未获得全队按当前人数开始的决定");
        AdoptManualParty(snapshot);
        _legacyPartySize = snapshot.ParticipantUids.Count;
        // Only a manual skip selects a smaller party early. Automatic fallback still admits
        // the missing member until the original deadline, while the others assemble in game.
        _keepWaitingForLateMember = !manual;
        SkipPartyWait = false; // “立即开始”已用于确定实际名册，不可再跳过游戏入队。
        _logger.LogInformation("[续开重跑] 按当前 {Count} 人逐家检查，剩余组队时间 {Seconds} 秒",
            _legacyPartySize, RemainingPartyWaitSeconds());
    }


    // Preview and real upload share keywords/CD rules; neither writes progress.
    private List<RouteInfo> SelectHostWorldRoutes(List<RouteInfo> routes, bool preview)
    {
        var keywords = RouteKeywordFilterDecisions.ParseKeywords(_config.RouteFilterKeywords);
        var cutoff = DateTime.Now; // 本次选线共用同一时刻；未来解冻的线路不计入本次开队。
        return routes.Where(route =>
        {
            if (RouteKeywordFilterDecisions.ShouldSkip(Path.GetFileNameWithoutExtension(route.FileName), keywords))
                return false;
            var cdRoute = route;
            if (preview || _worldStartPlan != null)
            {
                var (actual, _) = ResolveAndLoadActualVariant(route.FileName, route.FullPath,
                    Path.GetDirectoryName(route.FullPath) ?? "");
                if (actual == null || actual.Positions.Count == 0) throw new InvalidDataException("无法读取实际线路: " + route.FileName);
                // CD was written under the resolved local variant, not the representative.
                cdRoute = new RouteInfo { FileName = actual.FileName };
            }
            return _config.StartRouteIndex > 0 || !_cdManager.IsOnCooldownAt(cdRoute, cutoff);
        }).ToList();
    }

    private (bool Known, int Count) PreviewOwnWorld()
    {
        try
        {
            var account = string.IsNullOrEmpty(_config.AccountName) ? "默认账户" : _config.AccountName;
            if (!_worldRoutesUpdateSucceeded || (_config.MultiplayerEnabled
                && (!NexusBGI.RoutePackages.RoutePackageArchive.IsValidId(_routePackageId)
                    || _confirmedRoutePackageId != _routePackageId))) return (false, 0);
            _cdManager.Load(_dataDir, account);
            if (!_cdManager.LoadSucceeded) return (false, 0);
            if (!_config.UseFixedDebugRoutes)
            {
                var monsterFile = Path.Combine(_dataDir, "assets", "monsterInfo.json");
                if (!File.Exists(monsterFile)) return (false, 0);
                using var monsterJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(monsterFile));
                if (monsterJson.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array || monsterJson.RootElement.GetArrayLength() == 0)
                    return (false, 0);
                _monsterRepo.Load(monsterFile);
                if (_monsterRepo.GetAll().Count == 0) return (false, 0);
            }
            List<RouteInfo> routes;
            if (_config.OperationMode == "启用仅指定怪物模式")
            {
                var targets = ParseChineseTags(_config.TargetMonsters);
                if (targets.Count == 0) return (false, 0);
                routes = PrepareTargetWorldRoutes(targets, strict: true);
                if (routes.Count == 0) return (false, 0);
            }
            else if (_config.UseFixedDebugRoutes)
            {
                if (!string.IsNullOrEmpty(_config.FixedDebugRoutePath) && !Directory.Exists(_config.FixedDebugRoutePath))
                    return (false, 0);
                if (string.IsNullOrWhiteSpace(_config.FixedDebugRoutePath) && !string.IsNullOrWhiteSpace(_config.SelectedBuiltinRoute)
                    && !Directory.Exists(ResolveSelectedRouteDirectory()))
                    return (false, 0); // 正式旧流程可尝试默认包，但不能据默认包的 CD 跳过用户指定的世界。
                routes = LoadRoutesBasedOnConfig();
                if (routes.Count == 0) return (false, 0); // Missing package is not a completed world.
            }
            else
            {
                if (_config.OperationMode != "运行锄地路线") return (false, 0);
                routes = PrepareNormalWorldRoutes(BuildGroupTags(), strict: true);
                if (routes.Count == 0) return (false, 0);
            }
            var selected = routes.Where(r => r.Group == _config.GroupIndex && r.Selected).ToList();
            var available = SelectHostWorldRoutes(selected, preview: true);
            return (true, Math.Max(0, available.Count - Math.Max(0, _config.StartRouteIndex - 1)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[续开重跑] 本机线路无法可靠预判，全队退回逐家检查");
            return (false, 0);
        }
    }

    /// <returns>True only when startup has already joined the chosen world, or confirmed no work.</returns>
    private async Task<bool> TryPrepareWorldStartAsync(CoordinatorClient client, bool isHost)
    {
        if (!client.SupportsWorldPlan) return false;
        if (isHost && _config.SoloDebugMode)
        {
            if (_config.MultiWorldEnabled)
                await client.BeginWorldPlanAsync(_ct, forceLegacy: true);
            return false;
        }
        var timeout = TimeSpan.FromSeconds(Math.Max(30, _config.PartyTimeoutSeconds));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        budget.CancelAfter(timeout);
        var ct = budget.Token;
        WorldPlanAdmission? admission = null;
        try
        {
            if (isHost)
            {
                if (!_config.MultiWorldEnabled) return false;
                bool ordinary = !BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope.IsActive;
                _partyWaitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var limit = Math.Max(1, _config.PartyTimeoutSeconds);
                // Leave the final half (at most 30 seconds) of the original timeout to physically join.
                var rosterSeconds = ordinary && _config.PartyTimeoutAction == 1
                    ? limit - Math.Min(30.0, limit / 2.0) : limit;
                if (ordinary) IsWaitingForParty = true;
                while (client.CurrentPlayerList.Count < _config.ExpectedPlayerCount)
                {
                    ct.ThrowIfCancellationRequested();
                    if (ordinary && (SkipPartyWait || (_config.PartyTimeoutAction == 1 &&
                        System.Diagnostics.Stopwatch.GetElapsedTime(_partyWaitStarted).TotalSeconds >= rosterSeconds)))
                    {
                        await PublishPartialPartyAsync(client, SkipPartyWait, ct);
                        return false;
                    }
                    await Task.Delay(250, ct);
                }
                IsWaitingForParty = false;
                admission = await client.BeginWorldPlanAsync(ct);
            }
            else
            {
                admission = await NegotiateMemberWorldPlanAsync(client, budget);
                if (admission == null) return false;
            }
            if (admission == null) throw new InvalidOperationException("开队预判未获得服务端身份");
            if (admission.Snapshot.Stage == "Legacy") { AdoptManualParty(admission.Snapshot); return false; }
            _worldPlanAdmission = admission;
            _isInitialHost = isHost;
            _firstHostConfig = admission.Snapshot.HostConfig;
            RequireMatchingRoomBatch(_firstHostConfig);
            await ApplyPinnedHostConfigAsync(_firstHostConfig, ct);
            var preview = _config.SoloDebugMode ? (Known: false, Count: 0)
                : admission.Snapshot.CandidateHostUids.Contains(_config.PlayerUid)
                    ? PreviewOwnWorld() : (Known: true, Count: 0);
            var plan = await client.ReportWorldPlanAsync(admission, preview.Known, preview.Count, ct);
            while (plan.Stage == "Planning")
            {
                await Task.Delay(250, ct);
                plan = await client.ReadWorldPlanAsync(admission, ct);
            }
            if (plan.Stage == "Aborted") throw new InvalidOperationException("本次开队预判已中止");
            plan = await client.AckWorldPlanAsync(admission, ct);
            while (plan.Stage == "Prepared")
            {
                await Task.Delay(250, ct);
                plan = await client.ReadWorldPlanAsync(admission, ct);
            }
            if (plan.Stage == "Legacy") { _worldPlanAdmission = null; return false; }
            if (plan.Stage is not ("Committed" or "NoWork"))
                throw new InvalidOperationException("开队预判未共同确认: " + plan.Stage);
            _worldStartPlan = plan;
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _config.PartyTimeoutSeconds) * 3.0));
            _logger.LogInformation("[续开重跑] 原计划 {Original}；剩余世界 {Remaining}",
                string.Join(",", plan.CandidateHostUids), string.Join(",", plan.RemainingHostUids));
            if (plan.Stage == "NoWork")
            {
                // This is a confirmed no-work outcome, not fictitious WorldJoined evidence.
                MultiWorldStartPlan released;
                using (client.EnterWorldPlanTransfer(plan.PlanId, plan.InitialRoomCode))
                    released = await client.LeaveWorldPlanSourceAsync(admission, ct);
                if (released.Stage != "NoWork" || !released.SourceReleased)
                    throw new InvalidOperationException("全空计划未完成全队确认");
                ct.ThrowIfCancellationRequested();
                _worldPlanNoWorkConfirmed = true;
                _completedNormally = true;
                _mainExecutionReturned = true;
                _logger.LogInformation("[续开重跑] 全队确认本次计划无待执行线路");
                return true;
            }
            if (plan.RemainingHostUids[0] == plan.InitialHostUid) return false;

            var target = plan.Players.Single(p => p.PlayerUid == plan.RemainingHostUids[0]);
            using (client.EnterWorldPlanTransfer(plan.PlanId, plan.InitialRoomCode))
            {
                // No one has entered A's game world: network leave only, no F2 or LeaveWorld.
                await client.LeaveWorldPlanSourceAsync(admission, ct);
                await DisposeRoundCoordinatorAsync();
                var result = await SetupNextRoundAsync(0, target, target.PlayerUid == _config.PlayerUid,
                    client, plan.ParticipantUids.Count, BuildGroupTags());
                if (result != RoundSetupOutcome.Proceed)
                    throw new InvalidOperationException("首个待执行世界组队失败");
            }
            await RequireStartupAdmissionAsync(client, ct);
            _worldStateMonitor!.IsPartyPhase = false;
            _multiplayerPartyReady = true;
            lock (AutoHoeingProgress.Sync) AutoHoeingProgress.IsRunning = true;
            return true;
        }
        catch
        {
            if (admission != null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await client.AbortWorldPlanAsync(admission, cleanup.Token); }
                catch (Exception ex) { _logger.LogWarning(ex, "[续开重跑] 中止预判上报失败"); }
            }
            throw;
        }
        finally
        {
            if (isHost) IsWaitingForParty = false;
        }
    }

    private async Task RequireWorldPlanTargetAsync(CoordinatorClient client, CancellationToken ct, int round = 0)
    {
        if (_worldPlanAdmission == null || _worldStartPlan == null || round != 0) return;
        var plan = await client.ConfirmWorldPlanTargetAsync(_worldPlanAdmission, ct);
        if (plan.Stage != "Committed" || plan.RemainingHostUids.Count == 0
            || !string.Equals(client.HostPlayerUid, plan.RemainingHostUids[0], StringComparison.Ordinal))
            throw new InvalidOperationException("首个待执行世界身份已变化或本次开队已撤销");
        ct.ThrowIfCancellationRequested();
    }
}
