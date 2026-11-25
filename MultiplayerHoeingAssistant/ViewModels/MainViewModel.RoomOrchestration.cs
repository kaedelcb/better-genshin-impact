using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

internal sealed record RoomOrchestrationBinding(string Name, string Type)
{
    public string Label => RoomOrchestrationPresentation.TaskType(Type) + " · " + Name;
    public override string ToString() => Label;

    internal static RoomOrchestrationBinding[] From(IReadOnlyList<string>? names, IReadOnlyList<string>? types)
        => (names ?? []).Select((name, i) => new RoomOrchestrationBinding(name,
            types is null or { Count: 0 } ? "group" : types.Count > i ? types[i] : "")).ToArray();
}

internal sealed record RoomOrchestrationEditSnapshot(RoomOrchestrationRoomKey Key, string Name, string BgiPath,
    string[] Names, string[] Types, int ExpectedPlayers, string Schedule, bool ScheduleRequiresConfirmation,
    bool ScheduleSuppressed, TaskConflictPolicySettings Completion, Dictionary<string, string> QuickCommands);

/// <summary>Null means that the editor did not change the corresponding field. An empty schedule explicitly disables it.</summary>
internal sealed record RoomOrchestrationEditChanges(RoomOrchestrationBinding[]? Bindings = null,
    int? ExpectedPlayers = null, string? Schedule = null, TaskConflictPolicySettings? Completion = null,
    IReadOnlyDictionary<string, string>? QuickCommands = null)
{
    internal bool IsEmpty => Bindings is null && ExpectedPlayers is null && Schedule is null
        && Completion is null && QuickCommands is not { Count: > 0 };
}

internal sealed record RoomOrchestrationNameCatalog(IReadOnlyList<string> Groups, IReadOnlyList<string> OneClicks);

public partial class MainViewModel
{
    // Startup only: the app already read the configuration before constructing its window. Do not
    // publish this as the runtime _config (connection, timers and commands have not initialized yet).
    private AssistConfig? _startupRoomViewConfig;
    private AssistConfig? RoomViewConfig => _config ?? _startupRoomViewConfig;
    private string _roomViewPreferenceMessage = "";
    public string RoomViewPreferenceMessage => _roomViewPreferenceMessage;
    public bool CanSelectRoomOrchestration => !_disposing && _config is not null;
    public bool IsShowingRoomOrchestration => RoomViewConfig is { ShowRoomOrchestration: true };
    public string RoomOrchestrationEntryText => IsShowingRoomOrchestration ? "返回成员" : "任务编排";
    internal bool CanEditRoomOrchestration => !_disposing && _config is { ObserverMode: false }
        && _configManager is not null && CanEditConfiguration;

    internal void PrimeStartupRoomView(AssistConfig config)
    {
        if (_config is not null) return;
        _startupRoomViewConfig = config;
        NotifyRoomViewProperties();
    }

    // Also called by the normal role-change notifications. A role fallback does not write the preference.
    private void NotifyRoomOrchestrationModeProperties()
    {
        if (_config is not null) _startupRoomViewConfig = null;
        OnPropertyChanged(nameof(CanSelectRoomOrchestration));
        OnPropertyChanged(nameof(IsShowingRoomOrchestration));
        OnPropertyChanged(nameof(RoomOrchestrationEntryText));
        OnPropertyChanged(nameof(CanOperateDisplayedRoom));
        OnPropertyChanged(nameof(IsDisplayingAllRoomsView));
        OnPropertyChanged(nameof(IsHeaderViewingReadOnlyRoom));
        OnPropertyChanged(nameof(HeaderViewingRoomName));
        OnPropertyChanged(nameof(HeaderViewingRoomFullName));
        OnPropertyChanged(nameof(HeaderViewingRoomToolTip));
    }

    internal void SelectRoomOrchestration(bool persist = true)
    {
        if (!CanSelectRoomOrchestration || _config is null) return;
        CurrentPage = AppPage.Home;
        SetRoomViewPreference(true, null, persist);
        NotifyRoomViewProperties();
        // The graph reads local profiles and recorded state; opening it does not change the
        // previously viewed room or the desired spectator connections.
    }

    private void SetRoomViewPreference(bool? show, string? viewedId, bool persist)
    {
        if (_config is not { } current) return;
        var changed = show.HasValue && current.ShowRoomOrchestration != show.Value
            || viewedId is not null && current.ViewedRoomProfileId != viewedId;
        if (!changed && _roomViewPreferenceMessage.Length == 0) return;
        var priorShow = current.ShowRoomOrchestration;
        var priorView = current.ViewedRoomProfileId;
        _roomViewPreferenceMessage = "";
        if (persist)
        {
            try
            {
                if (_configManager is null || !TryEnterConfigTransferOperation("room_view_preference", out var transfer))
                    throw new InvalidOperationException("配置暂不可写");
                using (transfer)
                using (_configManager.AcquireWriteGate())
                {
                    // Only the two presentation fields belong to this action. In particular, do
                    // not publish the last in-memory room credentials over a newer on-disk save.
                    var candidate = ReadOrchestrationConfig(_configManager, out var expectedRaw);
                    if (candidate.ObserverMode != current.ObserverMode)
                        throw new InvalidOperationException("当前运行角色已变化，请重新打开页面");
                    if (viewedId is not null && viewedId != AssistConfig.AllRoomsViewId
                        && candidate.RoomProfiles.Count(p => p.Id == viewedId) != 1)
                        throw new InvalidOperationException("查看的房间档案已变化");
                    if (show.HasValue) candidate.ShowRoomOrchestration = show.Value;
                    if (viewedId is not null) candidate.ViewedRoomProfileId = viewedId;
                    if (!_configManager.TrySave(candidate, expectedRaw)) throw new IOException("配置写入失败或已发生其他更新");
                    var saved = ReadOrchestrationConfig(_configManager);
                    if (saved.ShowRoomOrchestration != candidate.ShowRoomOrchestration
                        || saved.ViewedRoomProfileId != candidate.ViewedRoomProfileId)
                        throw new IOException("未读回相同页面记忆");
                    // Restore the preference in memory before releasing the on-disk writer gate.
                    current.ShowRoomOrchestration = show ?? priorShow;
                    current.ViewedRoomProfileId = viewedId ?? priorView;
                }
            }
            catch (Exception ex)
            {
                _roomViewPreferenceMessage = "本次查看已切换，但页面选择未记住；重新打开助手可能恢复旧页面。";
                AddLog(_roomViewPreferenceMessage + "（" + ex.Message + "）");
            }
        }
        // Keep the user's present view even on a save failure. No writer from this path changes
        // actual login or task state, and the UI explicitly reports unconfirmed persistence.
        current.ShowRoomOrchestration = show ?? priorShow;
        current.ViewedRoomProfileId = viewedId ?? priorView;
        OnPropertyChanged(nameof(RoomViewPreferenceMessage));
    }

    private static AssistConfig ReadOrchestrationConfig(AssistConfigManager manager) => ReadOrchestrationConfig(manager, out _);

    private static AssistConfig ReadOrchestrationConfig(AssistConfigManager manager, out string raw)
    {
        raw = manager.ReadRawJson();
        var config = JsonSerializer.Deserialize<AssistConfig>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("未能读取助手配置");
        config.EnsureRoomProfiles();
        config.FillLegacyRoomServerUrls();
        return config;
    }

    private static AssistantRoomProfile OrchestrationProfile(AssistConfig config, string profileId)
    {
        var matches = config.RoomProfiles.Where(p => p is not null && p.Id == profileId).ToArray();
        return matches.Length == 1 && !string.IsNullOrWhiteSpace(profileId) ? matches[0]
            : throw new InvalidOperationException("房间档案已移除或身份不唯一，请重新打开编辑器。");
    }

    private static RoomOrchestrationRoomKey GetEditRoomKey(AssistConfig config, AssistantRoomProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.RoomCode) || string.IsNullOrWhiteSpace(config.PlayerUid)
            || !RoomServerAddress.TryNormalize(profile.Id == config.ActiveRoomProfileId
                ? config.ServerUrl : config.GetRoomServerUrl(profile), out var server))
            throw new InvalidOperationException("房间或本机执行身份尚未确认，请先核对房间档案。");
        return new(profile.Id, server, profile.RoomCode, config.PlayerUid);
    }

    private static RoomOrchestrationEditSnapshot CaptureRoomFields(AssistConfig config, string profileId, bool suppressed)
    {
        var profile = OrchestrationProfile(config, profileId);
        var active = config.ActiveRoomProfileId == profile.Id;
        if (active && config.EffectiveRoomCode != profile.RoomCode)
            throw new InvalidOperationException("当前活动档案身份不一致，请重新核对房间。");
        return new(GetEditRoomKey(config, profile), profile.Name, config.BgiPath,
            (active ? config.OnlineHoeingGroupNames : profile.OnlineHoeingGroupNames)?.ToArray() ?? [],
            (active ? config.OnlineHoeingGroupTypes : profile.OnlineHoeingGroupTypes)?.ToArray() ?? [],
            active ? config.ExpectedHoeingPlayers : profile.ExpectedHoeingPlayers,
            (active ? config.ScheduledOnlineTime : profile.ScheduledOnlineTime) ?? "",
            active ? config.ScheduledOnlineRequiresConfirmation : profile.ScheduledOnlineRequiresConfirmation,
            suppressed, CompletionPolicyFields.SnapshotForProfile(config, profileId),
            new Dictionary<string, string>((active ? config.QuickCommands : profile.QuickCommands) ?? new()));
    }

    private bool IsScheduleSuppressed(AssistConfig config, string profileId) => profileId == config.ActiveRoomProfileId
        ? _intentLifecycle.ManuallyClearedOnline
        : _roomScheduleSuppressed.GetValueOrDefault(profileId,
            !App.LaunchIntent.AllowsAutomaticStartup || string.IsNullOrEmpty(OrchestrationProfile(config, profileId).ScheduledOnlineTime));

    internal RoomOrchestrationEditSnapshot ReadRoomOrchestrationEdit(string profileId)
    {
        if (!CanEditRoomOrchestration || _config is not { } config)
            throw new InvalidOperationException("仅本机执行端可以编辑房间任务配置，且设置迁移须已结束。");
        return CaptureRoomFields(config, profileId, IsScheduleSuppressed(config, profileId));
    }

    internal async Task EditRoomOrchestrationAsync(string profileId, Window owner)
    {
        try
        {
            if (IsObserverMode)
            {
                await EditMonitorRoomOrchestrationAsync(profileId, owner);
                return;
            }
            var snapshot = ReadRoomOrchestrationEdit(profileId);
            var choices = RoomSwitchCompletion.Choices(_config!, profileId);
            var window = new RoomOrchestrationEditorWindow(this, snapshot, choices, NewDarkCombo, owner);
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "编辑房间配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await Task.CompletedTask;
    }

    internal Task<CompletionTargetCatalog> LoadOrchestrationCompletionCatalogAsync(string type, string name, string workflowId,
        bool explicitRefresh, CancellationToken ct)
    {
        if (!CanEditRoomOrchestration) return Task.FromResult(new CompletionTargetCatalog([], null, "本机执行端配置暂不可用。"));
        return explicitRefresh ? RefreshCompletionCatalogAsync(type, name, workflowId, ct)
            : GetCompletionTargetCatalogService().LoadAsync(type, name, workflowId, ct);
    }

    // The name-only read deliberately uses the same live catalog provider as the completion panel.
    internal async Task<RoomOrchestrationNameCatalog?> LoadRoomOrchestrationNamesAsync(bool explicitRefresh, CancellationToken ct)
    {
        if (!CanEditRoomOrchestration) return null;
        var catalog = explicitRefresh ? await RefreshCompletionCatalogAsync("group", "", "", ct)
            : await GetCompletionTargetCatalogService().RefreshAsync("group", "", "", ct);
        ct.ThrowIfCancellationRequested();
        return NamesFromCatalog(catalog);
    }

    internal static RoomOrchestrationNameCatalog? NamesFromCatalog(CompletionTargetCatalog? catalog)
        => catalog is { ConfigGroups: not null, OneClickConfigs: not null } &&
           catalog.ConfigGroups.All(n => !string.IsNullOrWhiteSpace(n)) && catalog.OneClickConfigs.All(n => !string.IsNullOrWhiteSpace(n))
            ? new(catalog.ConfigGroups.ToArray(), catalog.OneClickConfigs.ToArray()) : null;

    internal async Task<string?> ValidateRoomOrchestrationCompletionAsync(TaskConflictPolicySettings settings, CancellationToken ct)
    {
        if (!CanEditRoomOrchestration) return "仅本机执行端可以保存完成动作。";
        if (!settings.HasSpecifiedTask) return null;
        return await GetCompletionTargetCatalogService().ValidateAsync(settings.RoomSwitch?.RunAfterSwitch == true
            ? settings.PostSwitchTask() : settings, ct);
    }

    internal bool HasRoomOrchestrationPeerSnapshot(string profileId) => TryReadRoomOrchestrationPeers(profileId, out _);

    private sealed record RoomPeerBinding(string Name, bool Ready, IReadOnlyList<string> Names, IReadOnlyList<string> Types);

    private string? ConfirmedOrchestrationProfileId(AssistConfig? config)
    {
        if (config?.ActiveRoomProfile is not { } profile || IsStandaloneMode || GetAssistantRoomIdentity() is null
            || !RoomServerAddress.Same(config.GetRoomServerUrl(profile), config.ServerUrl) || profile.RoomCode != RoomCode)
            return null;
        return profile.Id;
    }

    private bool TryReadRoomOrchestrationPeers(string profileId, out IReadOnlyList<RoomPeerBinding> bindings)
    {
        bindings = [];
        if (_config is not { } config) return false;
        var profile = config.RoomProfiles.SingleOrDefault(p => p.Id == profileId);
        if (profile is null) return false;
        if (profileId == config.ActiveRoomProfileId)
        {
            if (ConfirmedOrchestrationProfileId(config) != profileId || _appliedRoomMemberRevision <= 0) return false;
            bindings = Members.Where(m => m.Online && m.PlayerUid != config.PlayerUid && m.BgiStatus != "observer")
                .Select(m => new RoomPeerBinding(m.PlayerName, m.OnlineReady, m.OnlineHoeingGroupNames ?? [], m.OnlineHoeingGroupTypes ?? [])).ToArray();
            return true;
        }
        if (!_roomSpectators.TryGetValue(profileId, out var session) || !session.IsConnected
            || session.RoomCode != profile.RoomCode || session.Password != profile.Password
            || !RoomServerAddress.Same(session.ServerUrl, config.GetRoomServerUrl(profile))) return false;
        var players = session.GetPlayersSnapshot();
        if (players.Count == 0) return false;
        bindings = players.Where(p => p.Online && p.PlayerUid != config.PlayerUid && p.BgiStatus != "observer")
            .Select(p => new RoomPeerBinding(p.PlayerName, p.OnlineReady, p.OnlineHoeingGroupNames ?? [], p.OnlineHoeingGroupTypes ?? [])).ToArray();
        return true;
    }

    internal string? GetRoomOrchestrationLayoutConflict(string profileId, RoomOrchestrationBinding[]? bindings)
    {
        if (bindings is null || !TryReadRoomOrchestrationPeers(profileId, out var peers)) return null;
        var expected = OnlineHoeingBindingValidator.Validate(bindings.Select(b => b.Name).ToArray(), bindings.Select(b => b.Type).ToArray());
        foreach (var peer in peers)
        {
            if (peer.Names.Count == 0)
            {
                if (peer.Ready) return $"成员「{peer.Name}」已上线但尚无绑定。";
                continue;
            }
            var conflict = OnlineHoeingBindingValidator.CompareLayout(expected,
                OnlineHoeingBindingValidator.Validate(peer.Names, peer.Types), $"成员「{peer.Name}」");
            if (conflict is not null) return conflict;
        }
        return null;
    }

    /// <summary>Commit only changed fields against their original values. Unrelated updates made
    /// while the modeless IPC reads or the modal editor were open are not overwritten.</summary>
    internal void CommitRoomOrchestrationEdit(RoomOrchestrationEditSnapshot opened, RoomOrchestrationEditChanges changes,
        RoomOrchestrationNameCatalog? catalog, string? acceptedLayoutConflict = null, bool remoteCenterOwnsRoomEffect = false)
    {
        var writeAttempted = false;
        try { CommitCore(); }
        catch (Exception ex) when (remoteCenterOwnsRoomEffect && !writeAttempted)
        { throw new RoomOrchestrationSaveRejectedException(ex.Message); }

        void CommitCore()
        {
            var profileId = opened.Key.ProfileId;
            if (!CanEditRoomOrchestration || _config is not { } current || _configManager is not { } manager)
                throw new InvalidOperationException("本机执行模式或配置已变化，请重新打开编辑器。");
            if (changes.IsEmpty) return;
            if (!TryEnterConfigTransferOperation("room_orchestration_edit", out var transfer))
                throw new InvalidOperationException(ConfigTransferBlockedMessage);
            using var transferOperation = transfer;
            var scheduleChanged = changes.Schedule is not null;
            bool active;
            var now = DateTime.Now;
            DateTime? fireDate = scheduleChanged && TimeSpan.TryParseExact(changes.Schedule, @"hh\:mm", CultureInfo.InvariantCulture, out var scheduled)
                && now >= now.Date.Add(scheduled) ? now.Date : null;
            lock (_assistantRoomEffectsGate)
            lock (_batchGate)
            {
                // Do not take a room effect for the whole dialog. At the final short synchronous
                // boundary, ensure no other room writer is in progress and no switch can commit.
                // Batch admission also reads the active completion policy under _batchGate; it must
                // see either the old fields or the fully committed new fields, never a mixture.
                if (!ReferenceEquals(current, _config) || _assistantRoomEffects != (remoteCenterOwnsRoomEffect ? 1 : 0)
                    || RoomSwitchHasPendingRequest || RoomSwitchBlocksRoomOperations)
                    throw new InvalidOperationException("正在更新房间或核对切换结果，请稍后重试保存。");
                using var roomEffect = TryEnterAssistantRoomEffect() ?? throw new InvalidOperationException("房间状态尚未确认。");
                using var writing = manager.AcquireWriteGate();
                var disk = ReadOrchestrationConfig(manager, out var expectedRaw);
                if (current.ObserverMode || disk.ObserverMode || disk.ActiveRoomProfileId != current.ActiveRoomProfileId)
                    throw new InvalidOperationException("本机执行模式或活动档案已变化，请重新打开编辑器。");
                var currentFields = CaptureRoomFields(current, profileId, IsScheduleSuppressed(current, profileId));
                var diskFields = CaptureRoomFields(disk, profileId, currentFields.ScheduleSuppressed);
                ValidateChangedFields(opened, currentFields, changes);
                ValidateChangedFields(opened, diskFields, changes);
                active = current.ActiveRoomProfileId == profileId;
                if (active && changes.ExpectedPlayers.HasValue && (AssistantRoomHasBatch() || _intentLifecycle.IsOnlineReady))
                    throw new InvalidOperationException("当前房间正在等待开锄或执行任务，请等本轮结束再修改开锄人数。");
                ValidateOrchestrationChanges(changes, catalog);
                var conflict = GetRoomOrchestrationLayoutConflict(profileId, changes.Bindings);
                if (conflict is not null && conflict != acceptedLayoutConflict)
                    throw new InvalidOperationException("该房间成员的绑定结构已变化，请重新确认：" + conflict);
                if (changes.Completion is { } completion)
                {
                    if (CompletionPolicyFields.ValidateRoomSwitch(current, profileId, completion) is { } error)
                        throw new InvalidOperationException(error);
                    if (CompletionPolicyFields.ValidateRoomSwitch(disk, profileId, completion) is { } diskError)
                        throw new InvalidOperationException(diskError);
                }

                ApplyChangedRoomFields(disk, profileId, changes, fireDate);
                writeAttempted = true;
                if (!manager.TrySave(disk, expectedRaw))
                {
                    writeAttempted = false;
                    throw new IOException("房间配置保存失败或已发生其他更新，未修改本机运行设置。");
                }
                var saved = ReadOrchestrationConfig(manager);
                if (JsonSerializer.Serialize(saved) != JsonSerializer.Serialize(disk))
                    throw new IOException("房间配置写入结果未确认，请重新打开编辑器核对。");

                ApplyChangedRoomFields(current, profileId, changes, fireDate);
                if (scheduleChanged)
                {
                    _roomScheduleSuppressed[profileId] = false;
                    if (active)
                    {
                        _lastScheduledFireDate = fireDate ?? DateTime.MinValue;
                        _intentLifecycle.NoteScheduledOnlineConfigured();
                    }
                }
            }
            // Neither refreshing the graph nor editing a non-active profile sends a remote command.
            // Active runtime fields are already persisted and mirrored in its profile at this point.
            NotifyRoomProfiles();
            if (active)
            {
                if (Members.FirstOrDefault(m => m.IsSelf) is { } self)
                {
                    self.OnlineHoeingGroupNames = [.. current.OnlineHoeingGroupNames];
                    self.OnlineHoeingGroupTypes = [.. current.OnlineHoeingGroupTypes];
                    self.ExpectedHoeingPlayers = current.ExpectedHoeingPlayers;
                    self.ScheduledOnlineTime = current.ScheduledOnlineTime;
                    self.QuickCommands = new(current.QuickCommands);
                    self.HoeingCompletionAction = CoordinatedCompletionAction.FromSettings(CompletionPolicyFields.Snapshot(current));
                    self.NotifyAllPropertiesChanged();
                }
                if (scheduleChanged)
                {
                    OnPropertyChanged(nameof(ConfigurationScheduledOnlineMessage));
                    OnPropertyChanged(nameof(ConfigurationTransferStatusMessage));
                    StartOnlineScheduler();
                }
                ObserveOnlineTask(ReportStatusAsync(), "更新当前房间任务配置");
            }
            AddLog($"已保存「{OrchestrationProfile(current, profileId).Name}」任务配置（已受理联机批次仍使用冻结任务）。");
        }
    }

    private static void ValidateChangedFields(RoomOrchestrationEditSnapshot opened, RoomOrchestrationEditSnapshot latest,
        RoomOrchestrationEditChanges changes)
    {
        if (opened.Key != latest.Key || ((changes.Bindings is not null || changes.Completion?.HasSpecifiedTask == true
            || changes.QuickCommands is { Count: > 0 }) && opened.BgiPath != latest.BgiPath))
            throw new InvalidOperationException("房间身份或本机任务目录已变化，请重新打开编辑器。");
        if (changes.Bindings is not null && (!opened.Names.SequenceEqual(latest.Names) || !opened.Types.SequenceEqual(latest.Types))
            || changes.ExpectedPlayers.HasValue && opened.ExpectedPlayers != latest.ExpectedPlayers
            || changes.Schedule is not null && (opened.Schedule != latest.Schedule
                || opened.ScheduleRequiresConfirmation != latest.ScheduleRequiresConfirmation || opened.ScheduleSuppressed != latest.ScheduleSuppressed)
            || changes.Completion is not null && !SameCompletion(opened.Completion, latest.Completion)
            || changes.QuickCommands is { } quick && quick.Keys.Any(k => opened.QuickCommands.GetValueOrDefault(k, "") != latest.QuickCommands.GetValueOrDefault(k, "")))
            throw new InvalidOperationException("本房间的相关配置已由另一路更新，本次未覆盖，请重新打开编辑器核对。");
    }

    private static bool SameCompletion(TaskConflictPolicySettings a, TaskConflictPolicySettings b)
    {
        var other = CompletionPolicyFields.Parameters(b);
        return CompletionPolicyFields.Parameters(a).All(p => other.TryGetValue(p.Key, out var value) && Equals(p.Value, value));
    }

    private static void ValidateOrchestrationChanges(RoomOrchestrationEditChanges changes, RoomOrchestrationNameCatalog? catalog)
    {
        if (changes.ExpectedPlayers is < 1 or > 4) throw new InvalidOperationException("预期开锄人数必须为 1–4 人。");
        if (changes.Schedule is { Length: > 0 } time && !TimeSpan.TryParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException("定时上线时间必须为 HH:mm，或留空禁用。");
        if (changes.Bindings is { } bindings)
        {
            if (catalog is null) throw new InvalidOperationException("修改联机绑定前，请刷新本机 BGI 任务目录。");
            var validation = OnlineHoeingBindingValidator.Validate(bindings.Select(b => b.Name).ToArray(),
                bindings.Select(b => b.Type).ToArray(), catalog.Groups, catalog.OneClicks, checkAvailability: true);
            if (!validation.IsValid) throw new InvalidOperationException(string.Join("\n", validation.Errors));
        }
        if (changes.QuickCommands is { } quick)
        {
            foreach (var (key, value) in quick)
            {
                if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("快捷指令名称无效。");
                if (value.Length == 0) continue;
                var binding = ParseQuickCommandBinding(value);
                if (catalog is null || binding is null || !(binding.Type == "onedragon" ? catalog.OneClicks : catalog.Groups).Contains(binding.Name, StringComparer.Ordinal))
                    throw new InvalidOperationException($"快捷指令「{key}」目标不在本机实时任务目录中，请重新选择。");
            }
        }
        if (changes.Completion is { HasSpecifiedTask: true } completion && CompletionTargetCatalogService.ValidateShape(
            completion.RoomSwitch?.RunAfterSwitch == true ? completion.PostSwitchTask() : completion) is { } shapeError)
            throw new InvalidOperationException(shapeError);
    }

    private static void ApplyChangedRoomFields(AssistConfig config, string profileId, RoomOrchestrationEditChanges changes, DateTime? fireDate)
    {
        var profile = OrchestrationProfile(config, profileId);
        var active = config.ActiveRoomProfileId == profileId;
        if (changes.Bindings is { } bindings)
        {
            profile.OnlineHoeingGroupNames = bindings.Select(b => b.Name).ToList();
            profile.OnlineHoeingGroupTypes = bindings.Select(b => b.Type).ToList();
            profile.OnlineHoeingGroupIndex = 0;
            if (active)
            {
                config.OnlineHoeingGroupNames = [.. profile.OnlineHoeingGroupNames];
                config.OnlineHoeingGroupTypes = [.. profile.OnlineHoeingGroupTypes];
                config.OnlineHoeingGroupIndex = 0;
            }
        }
        if (changes.ExpectedPlayers is { } players)
        {
            profile.ExpectedHoeingPlayers = players;
            if (active) config.ExpectedHoeingPlayers = players;
        }
        if (changes.Schedule is { } time)
        {
            profile.ScheduledOnlineTime = time;
            profile.ScheduledOnlineLastFireDate = fireDate;
            profile.ScheduledOnlineRequiresConfirmation = false;
            if (active)
            {
                config.ScheduledOnlineTime = time;
                config.ScheduledOnlineLastFireDate = fireDate;
                config.ScheduledOnlineRequiresConfirmation = false;
            }
        }
        if (changes.QuickCommands is { Count: > 0 } quick)
        {
            var target = new Dictionary<string, string>((active ? config.QuickCommands : profile.QuickCommands) ?? new());
            foreach (var (key, value) in quick) target[key] = value;
            profile.QuickCommands = target;
            if (active) config.QuickCommands = new(target);
        }
        if (changes.Completion is { } completion) CompletionPolicyFields.ApplyForProfile(config, profileId, completion);
    }

    internal RoomOrchestrationPresentation GetRoomOrchestrationPresentation()
    {
        var config = RoomViewConfig;
        if (config is null) return RoomOrchestrationPresentation.Empty;
        if (config.ObserverMode) return GetMonitorRoomOrchestrationPresentation();
        var confirmed = ReferenceEquals(config, _config) ? ConfirmedOrchestrationProfileId(config) : null;
        string? serverOnline = null;
        if (confirmed is not null && _signalRClient is { IsControlRoomReady: true } client
            && _roomMemberEpoch == client.DesktopControlContext && _appliedRoomMemberRevision > 0
            && _intentLifecycle.PendingOnlineReportGen <= 0
            && Members.FirstOrDefault(m => m.IsSelf && m.Online && m.ClientInstanceId == config.ClientInstanceId) is { OnlineReady: true })
            serverOnline = $"已上线等队友（{Members.Count(m => m.Online && m.OnlineReady)}/{config.ExpectedHoeingPlayers}）";
        else if (confirmed is not null && _intentLifecycle.IsOnlineReady)
            serverOnline = "上线待确认";
        return RoomOrchestrationPresentation.Create(config, confirmed, serverOnline, RoomSwitchStatus,
            CanEditRoomOrchestration, profile => GetRoomOrchestrationRun(profile), profile => GetRoomOrchestrationTransition(profile));
    }
}
