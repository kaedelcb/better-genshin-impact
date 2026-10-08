using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    /// <summary>[切片1] ext.event 事件通道客户端（BgiExternalClient SDK）；null = 尚未建立/已降级。</summary>
    private BgiExternalClient? _externalClient;

    /// <summary>R4.8 Batch D：任务中心进程内宿主字段（惰性创建；R4.10 终审复核：退出路径锁内捕获时脱离字段引用，随后锁外 best-effort 关闭）。</summary>
    private TaskCenterHost? _taskCenterHost;

    /// <summary>R4.10 终审（重要3）：宿主惰性初始化锁——并发首访与退出置空同锁，保证单例且退出后不再重建。</summary>
    private readonly object _taskCenterHostGate = new();

    /// <summary>R4.8 二轮（阻断4）：退出收敛标志——AddLog 在此期间跳过 UI 派发（Shutdown 的宿主日志回调不得与退出路径互等）。</summary>
    private volatile bool _disposing;

    /// <summary>
    /// R4.8 Batch D：任务中心进程内宿主（槲寄生 Tab1 面板与 R4.9 启动移交共用同一实例）。
    /// 惰性创建；宿主与 Store 构造零文件副作用（目录推迟到首次写入才创建，二轮 重要2）。
    /// 配置面与运行存储在 %APPDATA%/NexusBGI 下（Default* 路径），不碰 BGI User 目录。
    /// </summary>
    public TaskCenterHost TaskCenterHost
    {
        get
        {
            // R4.10 终审（重要3）：并发首访经锁保证单例；退出收敛已开始则拒绝重建
            //（退出期调用方拿已关闭宿主会立即响亮拒绝，而重建会产生未经收敛的新实例）。
            lock (_taskCenterHostGate)
            {
                if (_taskCenterHost is null && _disposing)
                    throw new InvalidOperationException("任务中心宿主已随助手退出收敛，不再创建");
                EnsureSamePackageBgiPath();
                var host = _taskCenterHost ??= new TaskCenterHost(
                    WorkflowStore.DefaultFlowsDir(), RunStore.DefaultRunsDir(), ResourceCatalogService.DefaultCacheFile(),
                    GetBoundTaskCenterClient, () => IsExecutorMode, () => LatestLocalStatus, AddLog, // R4.9 §6.2+I2 能力守卫 + 三轮 B1 快照提供方必传（生产无测试接缝）
                    EnsureTaskCenterExecutionReadyAsync); // 2026-09-20：执行入口环境确保（BGI 未运行自动拉起+有界等待通道就绪）
                // R5.8 §21.4：注入**真实 BGI User 配置根来源**（迁移演练隔离校验用）；未配置/无法解析 ⇒ null ⇒ 演练**保守拒绝**。
                host.UserConfigRootProvider ??= ResolveBgiUserConfigRoot;
                if (IsExecutorMode) host.EnsureLegacyCompatibility(TryResolveBgiUserConfigRoot(Config?.BgiPath));
                return host;
            }
        }
    }

    /// <summary>The shipped pair uses its own BGI directory, rather than a stale path from an older assistant.</summary>
    private void EnsureSamePackageBgiPath()
    {
        if (_config == null) return;
        var localBgi = ResolveSamePackageBgiPath(AppContext.BaseDirectory);
        if (localBgi is null) return;
        var isolated = OneDragonMigration.Core.InstallationPipeScope.IsIsolatedPackage(Path.GetDirectoryName(localBgi)!);
        if (!isolated && !string.IsNullOrWhiteSpace(_config.BgiPath) && File.Exists(_config.BgiPath)) return;
        if (string.Equals(Path.GetFullPath(localBgi), _config.BgiPath, StringComparison.OrdinalIgnoreCase)) return;
        _config.BgiPath = Path.GetFullPath(localBgi);
        _configManager?.Save(_config);
        AddLog("[任务中心] 已自动关联同套 BetterGI，已有配置和资源将直接读取。");
    }

    /// <summary>Epoch plus actual image path binds resources and execution to the configured installation.</summary>
    private BgiExternalClient? GetBoundTaskCenterClient()
    {
        var client = _externalClient;
        if (client is not { State: BgiExternalLinkState.Ready } || client.ServerEpoch is not { } epoch) return null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(epoch.ProcessId);
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            if (process.StartTime.ToUniversalTime().Ticks != epoch.StartTicksUtc ||
                process.SessionId != currentProcess.SessionId ||
                !PathIdentity.TryCanonicalizeForComparison(process.MainModule?.FileName ?? "", out var actual) ||
                !PathIdentity.TryCanonicalizeForComparison(Config?.BgiPath ?? "", out var configured) ||
                !string.Equals(actual, configured, StringComparison.OrdinalIgnoreCase)) return null;
            return client;
        }
        catch { return null; }
    }

    /// <summary>
    /// **BGI User 配置根**（迁移演练隔离校验用）：取**生产启动 BGI 所用的同一配置来源**
    /// `BgiPath` 所在目录下的 `User`（与 BGI `AppContext.BaseDirectory/User` 约定对齐）。
    /// **未配置、文件不存在、User 不存在/非目录、路径非法、含重解析点或无法解析 ⇒ `null`** ⇒
    /// 宿主演练入口**保守拒绝**（不写演练产物、不改配置；诊断日志除外）。本方法只证明“配置的可执行文件路径可派生
    /// 一个已存在的 User 目录”，**不单独证明**没有工作目录/别名导致的实际根差异；
    /// 此类环境无法证明时返回 `null`，并在 R5.8 保留真实环境门禁。
    /// </summary>
    private string? ResolveBgiUserConfigRoot()
    {
        if (!BgiProcessMonitor.TryGetCurrentSessionBgiProcessesStrict(out var processes, out _))
            return null;
        try
        {
            var images = processes.Select(p => p.MainModule?.FileName).ToArray();
            return ResolveBgiUserConfigRootCore(Config?.BgiPath, images);
        }
        catch
        {
            return null;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>生产判定核心：先证明运行实例同一性，再解析已存在的 User 根。</summary>
    internal static string? ResolveBgiUserConfigRootCore(string? configuredBgiPath,
        IReadOnlyList<string?> runningImagePaths)
        => ResolveBgiUserConfigRootCore(configuredBgiPath, runningImagePaths, enumerationComplete: true);

    /// <summary>严格枚举结果的核心判定；枚举不完整时无条件拒绝，不得把“读不到”当“没有实例”。</summary>
    internal static string? ResolveBgiUserConfigRootCore(string? configuredBgiPath,
        IReadOnlyList<string?> runningImagePaths, bool enumerationComplete)
        => enumerationComplete
            && MatchesRunningBgiImagePaths(configuredBgiPath, runningImagePaths)
            ? TryResolveBgiUserConfigRoot(configuredBgiPath)
            : null;

    /// <summary>带枚举来源接缝的核心；来源抛错 ⇒ `null`（保守拒绝）。</summary>
    internal static string? ResolveBgiUserConfigRootCore(string? configuredBgiPath,
        Func<IReadOnlyList<string?>> runningImagePathsProvider)
    {
        try
        {
            return ResolveBgiUserConfigRootCore(configuredBgiPath, runningImagePathsProvider(), enumerationComplete: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前会话 BGI 实例与配置路径同一性：无实例=按配置目标；多实例/无法读取=保守拒绝。</summary>
    private static bool MatchesRunningBgiImagePaths(string? configuredBgiPath,
        IReadOnlyList<string?> runningImagePaths)
    {
        // 先做配置原值白名单，避免在配置非法时仍去探测运行映像。
        if (string.IsNullOrWhiteSpace(configuredBgiPath)
            || !PathIdentity.TryNormalizeLocalDriveAbsolute(configuredBgiPath, out _))
            return false;
        if (runningImagePaths.Count == 0)
            return true;
        if (runningImagePaths.Count != 1)
            return false;

        var imagePath = runningImagePaths[0];
        if (string.IsNullOrWhiteSpace(imagePath))
            return false;
        if (!PathIdentity.TryCanonicalizeForComparison(imagePath, out var actual)
            || !PathIdentity.TryCanonicalizeForComparison(configuredBgiPath, out var configured))
            return false;

        actual = actual.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        configured = configured.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(actual, configured, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>生产解析核心（internal 供夹具直接验证；不得用替身测试替代）。</summary>
    internal static string? TryResolveBgiUserConfigRoot(string? configuredBgiPath)
    {
        try
        {
            // 先校验原始配置值，再做任何规范化；否则首尾控制字符可能被 Trim 静默吞掉。
            if (string.IsNullOrWhiteSpace(configuredBgiPath) || configuredBgiPath.Any(char.IsControl))
                return null;

            if (!PathIdentity.TryNormalizeLocalDriveAbsolute(configuredBgiPath, out var fullBgiPath))
                return null;
            if (!File.Exists(fullBgiPath))
                return null;
            // 符号链接/junction 会使“配置路径目录”与 BGI 进程实际 BaseDirectory 可能不一致；
            // 无法证明时保守返回 null，由宿主拒绝演练。
            if (File.GetAttributes(fullBgiPath).HasFlag(FileAttributes.ReparsePoint))
                return null;

            var dir = Path.GetDirectoryName(fullBgiPath);
            if (string.IsNullOrWhiteSpace(dir) || MigrationSwitchTransaction.HasReparsePoint(dir))
                return null;

            var userRoot = OneDragonMigration.Core.InstallationPipeScope.ResolveUserRoot(dir);
            if (!Directory.Exists(userRoot))
                return null;
            if (MigrationSwitchTransaction.HasReparsePoint(userRoot))
                return null;
            return Path.GetFullPath(userRoot);
        }
        catch
        {
            // 路径解析/属性读取异常不向宿主抛出：返回 null，宿主按“真实 User 根未知”结构化拒绝。
            return null;
        }
    }
    /// <summary>[切片1] 事件通道探测退避：Legacy（老 BGI）或暂时连不上时，到此时间点之前不再探测。</summary>
    private DateTime _externalNextProbeUtc = DateTime.MinValue;
    /// <summary>[切片4] 事件驱动维护的 ext.task.status 快照（SDK 基线/跳号/事件触发刷新产物）；null = 尚未取得。</summary>
    private string? _latestExtStatusJson;
    private readonly object _latestExtStatusGate = new();
    private DateTimeOffset? _latestExtStatusObservedAtUtc;

    /// <summary>
    /// [切片1/4] 确保 ext.* 通道接管 BGI 状态同步：切片1 只订阅 online.triggered；
    /// 切片4 起订阅全部已知事件——task.status 轮询改为"事件触发 SDK 快照刷新 + 快照缓存"驱动，
    /// 通道不可用时所有读取点回退 v2 IpcClient 轮询（兜底路径逐字节保留）。
    /// 返回 true = 事件通道活跃（Ready 且订阅已恢复）；false = 降级走原有 v2 轮询路径。
    /// 老 BGI（ext.hello 不支持）→ Legacy 静默降级、1 分钟后再探测，全程无报错。
    /// 在状态轮询 Timer 线程调用；事件回调跑在 SDK 读线程，两者都是线程池后台线程（同级）。
    /// </summary>
    /// <summary>ext 通道建立串行门（2026-09-20：任务中心「环境确保」会主动触发探测，
    /// 与状态轮询线程并发时不得各自创建客户端实例——同一把信号量收编两侧调用）。</summary>
    private readonly SemaphoreSlim _externalChannelGate = new(1, 1);

    /// <summary>环境确保串行门（会诊 重要2：并发执行请求共享同一把确保门，进入后先复核就绪——
    /// 防止「检查进程→启动」竞态下双路 RestartBgi；BGI 侧单实例锁是兜底，不作为主防线）。</summary>
    private readonly SemaphoreSlim _bgiEnsureGate = new(1, 1);

    /// <summary>本轮探测结果（会诊二轮 重要3：逐轮刷新，绝非粘滞——每次真实探测入口先清空、各出口按本轮结果赋值；
    /// Core 失败分支会释放客户端不赋 _externalClient，ensure 等待循环靠本字段区分本轮 Legacy（响亮拒绝）与 Down（继续等））。</summary>
    private BgiExternalLinkState? _lastProbeState;

    /// <summary>单轮探测结果（会诊三轮 重要1：门内读取、随返回值与调用方绑定——
    /// 杜绝「ensure 探测 → 放门 → 轮询线程覆写 → ensure 读残留」的跨调用竞态）。BudgetExhausted=排队/取门复核耗尽统一预算。</summary>
    private readonly record struct ProbeOutcome(bool Active, BgiExternalLinkState? ProbeState, bool BudgetExhausted);

    /// <summary>既有轮询路径（状态 Timer 线程）：无外部预算与取消——无限等门、不强制穿透退避，行为逐字节不变。</summary>
    private async Task<bool> TryEstablishExternalChannelAsync()
        => (await TryProbeExternalChannelAsync(null, false, CancellationToken.None).ConfigureAwait(false)).Active;

    /// <summary>统一探测入口（会诊三轮 重要1/2 处置）：
    /// - 通道门排队计入调用方预算（deadlineUtc null=无预算），取门后、开探测前复核期限——预算耗尽绝不开启新一轮 SDK 探测；
    /// - forceProbe（仅任务中心显式执行请求）在门内清零退避——与探测同人同门，无锁外覆写竞态；
    /// - 本轮结果在门内读取随返回值绑定调用方；Core 内 SDK 调用不可中途中断，由其自身连接超时兜底（诚实边界），返回后复核取消。</summary>
    private async Task<ProbeOutcome> TryProbeExternalChannelAsync(DateTime? deadlineUtc, bool forceProbe, CancellationToken ct)
    {
        // 会诊四轮 阻断修复：预算耗尽判定仅限有预算调用——InfiniteTimeSpan(-1ms) 会被 <= Zero 误判耗尽（轮询路径永禁）
        var remaining = deadlineUtc is { } d ? d - DateTime.UtcNow : Timeout.InfiniteTimeSpan;
        if (deadlineUtc is not null && remaining <= TimeSpan.Zero)
            return new ProbeOutcome(false, null, true);
        if (!await _externalChannelGate.WaitAsync(remaining, ct).ConfigureAwait(false))
            return new ProbeOutcome(false, null, true);
        try
        {
            if (deadlineUtc is { } d2 && d2 - DateTime.UtcNow <= TimeSpan.Zero)
                return new ProbeOutcome(false, null, true); // 取门后复核：预算耗尽不开新探测
            if (forceProbe)
                _externalNextProbeUtc = DateTime.MinValue; // 门内强制：显式执行请求不吃 1 分钟退避
            var active = await TryEstablishExternalChannelCoreAsync().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new ProbeOutcome(active, _lastProbeState, false);
        }
        finally
        {
            _externalChannelGate.Release();
        }
    }

    /// <summary>
    /// 任务中心执行环境确保（2026-09-20 修复：任务中心启动时 BGI 未运行不会被拉起，启动被「BGI 离线」直接拒绝）。
    /// 仅由任务中心执行入口（Start/Resume/启动移交）在就绪检查前调用；纯查询/监控路径不触达（监控端零副作用合同不破）。
    /// 顺序：已 Ready 直接通过 → 本会话无 BGI 进程则 RestartBgi 拉起（复用成员卡片「启动BGI」同款语义：解除崩溃守护
    /// 「有意关闭」豁免+冷启动宽限期）→ 清零 1 分钟探测退避，有界等待 ext 通道 Ready（BGI 客户端自带 5s 重连循环）。
    /// 返回 null=就绪；非 null=响亮拒绝原因（未发送任何任务）。
    /// </summary>
    private async Task<string?> EnsureTaskCenterExecutionReadyAsync(CancellationToken ct)
    {
        if (GetBoundTaskCenterClient() != null)
            return null;

        EnsureSamePackageBgiPath();
        if (_externalClient?.State == BgiExternalLinkState.Ready && GetBoundTaskCenterClient() == null)
            return "当前连接的BGI不是这套程序，请从同套目录打开BetterGI；未发送任务";

        // 统一预算从进入即计时（会诊二轮 重要4：排队等待计入同一 150s 预算，并发请求不累积超时）
        var deadline = DateTime.UtcNow.AddSeconds(150);
        // 并发收编（会诊 重要2）：共享一把确保门；拿到门后先复核——前者可能就绪了，本请求零等待直通
        var gateBudget = deadline - DateTime.UtcNow;
        if (gateBudget <= TimeSpan.Zero
            || !await _bgiEnsureGate.WaitAsync(gateBudget, ct).ConfigureAwait(false))
            return "等待执行环境确保排队超时（前序请求的就绪等待未在预算内完成；未发送任何任务）";
        try
        {
            if (GetBoundTaskCenterClient() != null)
                return null;
            if (_config?.ObserverMode == true)
                return "观察者模式不提供本地执行通道（BGI 未连接，未启动；未发送任何任务）";

            ct.ThrowIfCancellationRequested(); // 取消先于拉起副作用（会诊 阻断2：退出/链取消后不得再启动进程）
            if (BgiProcessMonitor.GetCurrentSessionBgiProcesses().Length == 0)
            {
                AddLog("[任务中心] BGI 未运行，正在启动（任务中心启动属「要求 BGI 运行」动作，解除有意关闭豁免）");
                if (_processMonitor is null || !_processMonitor.RestartBgi())
                    return "BGI 未运行且启动调用失败（请检查助手配置中的 BGI 路径；未发送任何任务）";
            }

            // 有界等待（沿用进入时统一预算）+逐轮取消；单轮探测自身有 SDK 连接超时，预算是轮次级上限而非严格硬顶（诚实声明）
            var announced = false;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                BgiExternalLinkState? probeState;
                try
                {
                    var probe = await TryProbeExternalChannelAsync(deadline, true, ct).ConfigureAwait(false);
                    if (probe.BudgetExhausted) break; // 预算耗尽（含通道门排队/取门复核）→ 落超时结论，绝不开新探测
                    probeState = probe.ProbeState;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    probeState = null; // 单轮探测失败不终止等待（BGI 冷启动中管道未就绪是常态），下一轮再试；取消不吞
                }

                if (GetBoundTaskCenterClient() != null)
                {
                    AddLog("[任务中心] BGI 外部接口通道已就绪，继续执行");
                    return null;
                }
                if (probeState == BgiExternalLinkState.Legacy)
                    return "对端 BGI 版本过旧（不支持 ext 外部接口），请升级 BGI 后重试（未发送任何任务）";

                if (!announced)
                {
                    announced = true;
                    AddLog("[任务中心] 等待 BGI 外部接口通道就绪（冷启动最长约 150 秒）…");
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            return "BGI 已启动但外部接口通道在 150 秒内未就绪（BGI 可能仍在初始化或启动失败；未发送任何任务）";
        }
        finally
        {
            _bgiEnsureGate.Release();
        }
    }
    private async Task<bool> TryEstablishExternalChannelCoreAsync()
    {
        _lastProbeState = null; // 逐轮刷新：入口清空，出口按本轮结果赋值（杜绝历史 Legacy 残留误判）
        if (_config?.ObserverMode == true)
        {
            return false;
        }

        try
        {
            if (_externalClient == null)
            {
                if (DateTime.UtcNow < _externalNextProbeUtc)
                {
                    return false;
                }

                EnsureSamePackageBgiPath();
                var client = new BgiExternalClient(ResolveConfiguredBgiDirectory());
                var state = await client.StartAsync();
                if (state == BgiExternalLinkState.Ready)
                {
                    _lastProbeState = BgiExternalLinkState.Ready;
                    client.EventReceived += OnBgiExternalEvent;
                    client.StatusSnapshotUpdated += OnBgiStatusSnapshotUpdated;
                    client.ConnectionStateChanged += OnBgiExternalConnectionStateChanged;
                    _externalClient = client;
                    _ = RefreshTaskCenterCatalogAfterConnectionAsync();
                }
                else
                {
                    // Legacy（老 BGI）或暂时连不上：退避后由下一轮轮询再探测，本轮走 v2 轮询
                    _lastProbeState = state; // 探测结果留痕（客户端随即释放，ensure 循环靠它区分 Legacy/Down）
                    client.Dispose();
                    _externalNextProbeUtc = DateTime.UtcNow.AddMinutes(1);
                    return false;
                }
            }

            // 连接内断线重连后订阅会失效（订阅挂在 BGI 侧会话上），补订；
            // SDK 恢复订阅时自动携带 lastKnownRevision 续传缺失事件并拉基线快照校准
            if (_externalClient is { State: BgiExternalLinkState.Ready } readyClient
                && !readyClient.IsEventChannelActive)
            {
                await readyClient.SubscribeAsync([]);
            }

            if (_externalClient.State == BgiExternalLinkState.Ready)
                _lastProbeState = BgiExternalLinkState.Ready;
            return _externalClient.IsEventChannelActive;
        }
        catch
        {
            _lastProbeState = null; // 异常=本轮结果不可考，不留残留
            // 事件通道故障不阻塞主流程：本轮降级 v2 轮询，下轮再试
            return false;
        }
    }

    /// <summary>[切片4] ext 连接状态机变更日志（验收②：Degraded→重连→Ready 全流程可观测）。</summary>
    private void OnBgiExternalConnectionStateChanged(BgiExternalConnectionState state)
    {
        try
        {
            if (state != BgiExternalConnectionState.Ready)
            {
                lock (_latestExtStatusGate)
                {
                    _latestExtStatusJson = null;
                    _latestExtStatusObservedAtUtc = null;
                }
            }
            AddLog($"[ext] BGI 外部接口通道状态 → {state}");
            if (state == BgiExternalConnectionState.Ready) _ = RefreshTaskCenterCatalogAfterConnectionAsync();
        }
        catch
        {
            // 日志失败不影响连接管理
        }
    }

    /// <summary>
    /// [切片4] SDK 快照更新通知：缓存最新 ext.task.status 快照（ReportStatusAsync 直接取用，
    /// 不再周期轮询 task.status），并做 onlineGeneration 边沿检测——与 v2 轮询路径的 P0-B
    /// 基线同步语义逐条一致（快照校准场景补报断线窗口内错过的上线事件）。
    /// </summary>
    private void OnBgiStatusSnapshotUpdated(BgiExternalStatusSnapshot snapshot)
    {
        try
        {
            lock (_latestExtStatusGate)
            {
                _latestExtStatusJson = snapshot.DataJson;
                _latestExtStatusObservedAtUtc = DateTimeOffset.UtcNow;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(snapshot.DataJson);
            if (!doc.RootElement.TryGetProperty("onlineGeneration", out var ogEl)
                || ogEl.ValueKind != System.Text.Json.JsonValueKind.Number
                || !ogEl.TryGetInt32(out var gen))
            {
                return;
            }

            // [B157] 首见边沿新鲜度校验用：BGI task.status 聚合的 onlineTriggeredAt（UTC ISO）
            DateTime? triggeredAt = null;
            if (doc.RootElement.TryGetProperty("onlineTriggeredAt", out var taEl)
                && taEl.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTime.TryParse(taEl.GetString(), out var taVal))
            {
                triggeredAt = taVal.Kind == DateTimeKind.Utc ? taVal : taVal.ToUniversalTime();
            }
            // [P1] 首见判定/新鲜度校验/基线同步/双来源对齐/防抖全部收编进状态机
            ApplyOnlineGenerationEdge(gen, triggeredAt);
        }
        catch
        {
            // 快照解析失败不影响主流程
        }
    }

    /// <summary>
    /// [切片1/4] ext.event 事件回调（SDK 读线程）。online.triggered 直接消费（语义与 v2 轮询
    /// 边沿检测一致）；切片4 起 task.*/hoeing.* 事件触发 SDK 快照刷新（事件驱动替代 10s
    /// task.status 轮询），快照由 OnBgiStatusSnapshotUpdated 应用。
    /// </summary>
    private void OnBgiExternalEvent(BgiExternalEvent evt)
    {
        try
        {
            if (evt.Name == BgiExternalEventNames.OnlineTriggered)
            {
                if (!evt.Payload.TryGetProperty("generation", out var genEl)
                    || !genEl.TryGetInt32(out var gen))
                {
                    return;
                }

                ApplyOnlineGenerationEdge(gen);
                return;
            }

            // 任务/锄地状态事件：触发一次快照刷新（SDK 内部 300ms 节流 + 在飞去重）
            if (evt.Name is BgiExternalEventNames.TaskStarted
                or BgiExternalEventNames.TaskStopped
                or BgiExternalEventNames.TaskProgress
                or BgiExternalEventNames.HoeingProgress
                or BgiExternalEventNames.TaskSuspended
                or BgiExternalEventNames.TaskResumed)
            {
                _ = _externalClient?.RefreshStatusSnapshotAsync($"event:{evt.Name}");
            }
        }
        catch
        {
            // 事件处理失败不影响读循环与主流程
        }
    }

    /// <summary>
    /// [切片4] ext 优先的 BGI IPC 发送（查询/轻操作迁移点共用）：ext 通道 Ready 且操作有 ext 映射时
    /// 走 BgiExternalClient 长连接；否则（老 BGI/未连接/无映射/通道瞬态失败）回退 v2 IpcClient 短连接。
    /// 返回 null = 两条路径都不可用（调用方按原有容错语义处理）。v2 旧路径代码保留不删（老 BGI 降级用）。
    /// </summary>
    private async Task<IpcResponse?> SendBgiIpcPreferredAsync(string v2OpCode, string? payloadJson, int connectTimeoutMs = 2000)
    {
        var ext = _externalClient;
        if (ext is { State: BgiExternalLinkState.Ready }
            && BgiExternalClient.TryMapToExtOperation(v2OpCode, out var extOp))
        {
            try
            {
                var extResp = await ext.SendCommandAsync(
                    extOp,
                    payloadJson is null ? null : JsonSerializer.Deserialize<JsonElement>(payloadJson),
                    TimeSpan.FromMilliseconds(Math.Max(connectTimeoutMs, 2000)));
                if (v2OpCode == "task.status" && extResp.Success
                    && !IpcClient.IsTaskStatusFromRemoteProcess(extResp.Data,
                        ext.ServerEpoch?.ProcessId, ext.ServerEpoch?.StartTicksUtc))
                {
                    return new IpcResponse
                    {
                        Success = false,
                        ErrorCode = "status_identity_mismatch",
                        ErrorMessage = "ext task.status 的 bgiEpoch 与已验证 Ping/Hello 进程身份不一致"
                    };
                }
                return new IpcResponse
                {
                    Success = extResp.Success,
                    Data = extResp.Data,
                    ErrorMessage = extResp.ErrorMessage,
                    ErrorCode = extResp.ErrorCode,
                };
            }
            catch
            {
                // ext 通道瞬态失败 → 落回 v2 短连接
            }
        }

        try
        {
            using var ipc = CreateConfiguredIpcClient();
            await ipc.ConnectAsync(connectTimeoutMs);
            if (v2OpCode == "task.status" && !ipc.IsSessionTrusted)
            {
                return new IpcResponse
                {
                    Success = false,
                    ErrorCode = "ipc_identity_unverified",
                    ErrorMessage = "v2 task.status 的 Ping 身份未能确认，拒绝采信"
                };
            }
            var ipcResponse = await ipc.SendCommandAsync(new IpcRequest { OpCode = v2OpCode, Payload = payloadJson });
            if (v2OpCode == "task.status" && ipcResponse.Success
                && !IpcClient.IsTaskStatusFromRemoteProcess(ipcResponse.Data,
                    ipc.RemoteProcessId, ipc.RemoteProcessStartTicksUtc))
            {
                return new IpcResponse
                {
                    Success = false,
                    ErrorCode = "status_identity_mismatch",
                    ErrorMessage = "v2 task.status 的 bgiEpoch 与已验证 Ping 进程身份不一致"
                };
            }
            return ipcResponse;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>[切片4] ext 通道可信状态同步：SDK 的 ext.hello 已校验同会话（跨会话在 SDK 侧被拒），Ready 即可信。</summary>
    private void UpdateExtSessionTrust()
    {
        if (!IsIpcSessionUntrusted) return;
        IsIpcSessionUntrusted = false;
        AddLog("[IPC] 管道对端已确认为本会话的 BGI 实例（ext.hello 校验），任务状态恢复采信");
    }
}
