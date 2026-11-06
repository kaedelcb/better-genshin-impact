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
        _config.MultiWorldResumeEnabled = hostConfig.MultiWorldResumeEnabled;
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
        // 预处理路线
        var pathingDir = Path.Combine(_dataDir, "pathing");
        var routes = RouteInfoLoader.LoadRoutes(
            pathingDir, _monsterRepo, _config.IgnoreRate, groupTags[0], strict);

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
        var pathingDir = Path.Combine(_dataDir, "pathing");
        var fakeGroupTags = Enumerable.Range(0, 10).Select(_ => new List<string>()).ToList();
        var routes = RouteInfoLoader.LoadRoutes(pathingDir, _monsterRepo, _config.IgnoreRate, fakeGroupTags[0], strict);

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
    private bool _worldRoutesUpdateSucceeded = true;

    // Preview and real upload share keywords/CD rules; neither writes progress.
    private List<RouteInfo> SelectHostWorldRoutes(List<RouteInfo> routes, bool preview)
    {
        var keywords = RouteKeywordFilterDecisions.ParseKeywords(_config.RouteFilterKeywords);
        var cutoff = preview ? DateTime.Now.AddSeconds(Math.Max(300, _config.PartyTimeoutSeconds * 3.0 + 120)) : DateTime.Now;
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
            if (_config.AutoUpdateRoutes && !_worldRoutesUpdateSucceeded) return (false, 0);
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
            if (_config.MultiWorldEnabled && _config.MultiWorldResumeEnabled)
                await client.BeginWorldPlanAsync(_ct, forceLegacy: true);
            return false;
        }
        var timeout = TimeSpan.FromSeconds(Math.Max(30, _config.PartyTimeoutSeconds));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        budget.CancelAfter(timeout);
        var ct = budget.Token;
        WorldPlanAdmission? admission = null;
        bool beginAttempted = false;
        try
        {
            if (isHost)
            {
                if (!_config.MultiWorldEnabled || !_config.MultiWorldResumeEnabled) return false;
                if (!BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope.IsActive) IsWaitingForParty = true;
                while (client.CurrentPlayerList.Count < _config.ExpectedPlayerCount)
                {
                    ct.ThrowIfCancellationRequested();
                    if (SkipPartyWait && !BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope.IsActive)
                    {
                        beginAttempted = true;
                        admission = await client.BeginWorldPlanAsync(ct, forceLegacy: true);
                        return false;
                    }
                    // Leave enough of the ordinary party timeout for the old current-members choice.
                    if (budget.IsCancellationRequested) break;
                    await Task.Delay(250, ct);
                }
                beginAttempted = true;
                admission = await client.BeginWorldPlanAsync(ct);
            }
            else
            {
                // Config exchange precedes HostReady: no game-world request may start here.
                RoomConfig? hostConfig;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    hostConfig = await client.GetRoomConfigAsync();
                    if (hostConfig == null) await Task.Delay(250, ct);
                } while (hostConfig == null);
                if (!hostConfig.MultiWorldEnabled || !hostConfig.MultiWorldResumeEnabled) return false;
                budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, hostConfig.PartyTimeoutSeconds)));
                RequireMatchingRoomBatch(hostConfig);
                var needsUpdate = hostConfig.AutoUpdateRoutes && !_config.AutoUpdateRoutes;
                ApplyWorldHostConfig(hostConfig);
                if (needsUpdate) await RunAutoUpdateRoutesAsync();
                _firstHostConfig = hostConfig;
                while (admission == null)
                {
                    admission = await client.AcquireWorldPlanAsync(ct);
                    if (admission == null) await Task.Delay(250, ct);
                }
            }
            if (admission == null) throw new InvalidOperationException("开队预判未获得服务端身份");
            if (admission.Snapshot.Stage == "Legacy") return false;
            _worldPlanAdmission = admission;
            _isInitialHost = isHost;
            _firstHostConfig = admission.Snapshot.HostConfig;
            RequireMatchingRoomBatch(_firstHostConfig);
            ApplyWorldHostConfig(_firstHostConfig);
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
        catch (OperationCanceledException) when (!_ct.IsCancellationRequested && admission == null && !beginAttempted && isHost
            && _config.PartyTimeoutAction == 1
            && !BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope.IsActive)
        {
            using var fallback = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            fallback.CancelAfter(TimeSpan.FromSeconds(10));
            await client.BeginWorldPlanAsync(fallback.Token, forceLegacy: true);
            return false;
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
