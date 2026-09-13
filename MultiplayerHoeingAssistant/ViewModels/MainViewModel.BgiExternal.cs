using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    /// <summary>[切片1] ext.event 事件通道客户端（BgiExternalClient SDK）；null = 尚未建立/已降级。</summary>
    private BgiExternalClient? _externalClient;

    /// <summary>[切片1] 事件通道探测退避：Legacy（老 BGI）或暂时连不上时，到此时间点之前不再探测。</summary>
    private DateTime _externalNextProbeUtc = DateTime.MinValue;
    /// <summary>[切片4] 事件驱动维护的 ext.task.status 快照（SDK 基线/跳号/事件触发刷新产物）；null = 尚未取得。</summary>
    private string? _latestExtStatusJson;

    /// <summary>
    /// [切片1/4] 确保 ext.* 通道接管 BGI 状态同步：切片1 只订阅 online.triggered；
    /// 切片4 起订阅全部已知事件——task.status 轮询改为"事件触发 SDK 快照刷新 + 快照缓存"驱动，
    /// 通道不可用时所有读取点回退 v2 IpcClient 轮询（兜底路径逐字节保留）。
    /// 返回 true = 事件通道活跃（Ready 且订阅已恢复）；false = 降级走原有 v2 轮询路径。
    /// 老 BGI（ext.hello 不支持）→ Legacy 静默降级、1 分钟后再探测，全程无报错。
    /// 在状态轮询 Timer 线程调用；事件回调跑在 SDK 读线程，两者都是线程池后台线程（同级）。
    /// </summary>
    private async Task<bool> TryEstablishExternalChannelAsync()
    {
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

                var client = new BgiExternalClient();
                var state = await client.StartAsync();
                if (state == BgiExternalLinkState.Ready)
                {
                    client.EventReceived += OnBgiExternalEvent;
                    client.StatusSnapshotUpdated += OnBgiStatusSnapshotUpdated;
                    client.ConnectionStateChanged += OnBgiExternalConnectionStateChanged;
                    _externalClient = client;
                }
                else
                {
                    // Legacy（老 BGI）或暂时连不上：退避后由下一轮轮询再探测，本轮走 v2 轮询
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

            return _externalClient.IsEventChannelActive;
        }
        catch
        {
            // 事件通道故障不阻塞主流程：本轮降级 v2 轮询，下轮再试
            return false;
        }
    }

    /// <summary>[切片4] ext 连接状态机变更日志（验收②：Degraded→重连→Ready 全流程可观测）。</summary>
    private void OnBgiExternalConnectionStateChanged(BgiExternalConnectionState state)
    {
        try
        {
            AddLog($"[ext] BGI 外部接口通道状态 → {state}");
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
            _latestExtStatusJson = snapshot.DataJson;

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

            // [A4.3] job.* 事件 = reconcile 循环的唤醒快速路径（事实源仍是 ext.job.list pull，
            // 帧丢失/乱序由 10s 节拍兜底；job.heartbeat 仅作存活信号，不唤醒）
            if (evt.Name is BgiExternalEventNames.JobQueued
                or BgiExternalEventNames.JobStarted
                or BgiExternalEventNames.JobCompleted
                or BgiExternalEventNames.JobFailed
                or BgiExternalEventNames.JobCancelled)
            {
                _batchWakeSignal?.TrySetResult();
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
            using var ipc = new IpcClient();
            await ipc.ConnectAsync(connectTimeoutMs);
            return await ipc.SendCommandAsync(new IpcRequest { OpCode = v2OpCode, Payload = payloadJson });
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
