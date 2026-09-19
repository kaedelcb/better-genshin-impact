using System;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>R5.2 B2 仲裁接线测试接缝（生产=null 全走实况；夹具注入事实快照/发送结果/时钟）。</summary>
internal sealed class TaskCenterAdmissionSeams
{
    public bool? F11Active { get; set; }
    public bool? Occupied { get; set; }
    public bool? FactsUnknown { get; set; }
    public string? Epoch { get; set; }
    public Func<SubmissionDispatch, Task<SendOutcome>>? SenderOverride { get; set; }
    public Func<DateTimeOffset>? UtcNow { get; set; }
    public AdmissionBarriers? Barriers { get; set; }
    /// <summary>租约所有权 TTL 秒（生产 null=15s 默认；夹具注小值加速接管观察验证——接管期限取自被观察租约，须两端同值）。</summary>
    public int? OwnershipTtlSeconds { get; set; }
}

/// <summary>
/// R5.2 B2（E1）：任务中心宿主流线仲裁门面接线（冻结稿 v8 §0/§2.2/§5 落地）。
/// 布线决策登记（冻结稿 §7.3「实现入档」）：
/// - 门面单例懒组装于首个执行入口；租约目录=运行存储同级 arbitration（配置面唯一权威在助手本机，不碰 BGI User 目录）；
///   EnsureOwnership 每次组装执行（TTL 过期即重获）+ RecoverAfterRestart 每进程一次（幂等）。
/// - E1 候选构造按 §2.2 面板 start 列：scope=bgi:local:{epoch}（epoch 首次构造捕获固定，只比较不重写）、
///   namespace=manual、trigger=manual:panel:{requestIdentity}（I3 占位符门面回填）、resourceRef=flow:{workflowId}、
///   intent=start、排序键全缺省（本阶段适配器缺省冻结）；发送前预建 Planned 运行固定 candidateId→runId 绑定
///   （§3.2 绑定不可改写；Planned 记录无执行副作用，仲裁未受理即终态化清理留痕）。
/// - 事实快照来源：执行占用=BGI 控制面快照 TaskRunning；快照缺失=未知（NeedReconcile 保守拒绝，不解释为空闲）。
/// - F11 独立停止闸门事实源：当前助手无 F11 实时信号（F11 为 BGI 侧用户按键），生产恒 false——
///   BGI 侧 F11 取消效果经「执行占用/回执词/WasCancelled」已有通道覆盖；实时闸门源归后续阶段登记。
/// - E1 接管台账=RunStore 运行记录（AdmissionHooks 文档冻结）：受理持久化=运行记录在册可载；
///   终局回写=驱动观察收尾按 runBinding 反查 Operations 调 MarkOperationTerminal（台账交叉确认=运行记录终态）。
/// - 节点级后继提交（Runner 经边界）与 E2 恢复五路径接线归 B2-γ/B2-β；本批 sender 遇非流程启动形状=Unknown 保守待对账。
/// 会诊登记（DS FLASH B2-α 轮，建议级归后续批次）：
/// - 建议-1 对账/终局交错：MarkAdmissionTerminalIfAny 与门面「受理→台账→关闭」交错窗下，关闭路径对已终局操作幂等断言归 B4；
/// - 建议-2 接线路经就绪判定在发送侧兜底（CreateRunner launch_error），生产恒传 ensureExecutionReady 前置保留观察，B4 收口复核；
/// - 建议-3 NeedPreemptConfirm 下预建运行已取消的 candidateId→runId 绑定再入/迁移处置归 R5.3 抢占语义登记；
/// - 建议-4 ReadCore 内部 IO 异常折 Corrupt 不经锁争用重试（锁文件不存在直读路径），区分「争用可重试/真损坏留痕」归后续打磨。
/// </summary>
public sealed partial class TaskCenterHost
{
    private readonly bool _admissionWired;
    private readonly string? _arbitrationDir;
    private readonly TaskCenterAdmissionSeams? _admissionSeams;
    private string? _runsDirPath;
    private readonly object _admissionInitGate = new();
    private ArbitrationLeaseStore? _admissionStore;
    private ArbitrationAdmissionService? _admission;

    /// <summary>
    /// 门面懒组装（所有权+§6.3 失联接管编排+每进程一次重启恢复；失败留 null 允许下次重试）。
    /// 接管编排=宿主职责（门面不自行接管）：TryAcquire held → LeaseTakeoverObserver 单调观察满 TTL 产出证据 →
    /// 证据获取（锁内复核）；TTL 内重启=等满观察期（单调钟，UTC 拨动不影响）——§6.3 唯一接管依据，不抄近道。
    /// 阶段二观察等待在初始化锁外异步进行（不阻塞 UI/调用线程），并发首调者每轮重查 _admission 避免双双等满 TTL。
    /// </summary>
    private async Task EnsureAdmissionFacadeAsync(CancellationToken ct)
    {
        if (_admission is not null) return;

        // 阶段一（锁内零等待）：组装+首次获取尝试。
        ArbitrationAdmissionService facade;
        ArbitrationLeaseStore store;
        LeaseOpResult acq;
        var ttl = _admissionSeams?.OwnershipTtlSeconds ?? 15;
        var ownerKey = "pid:" + Environment.ProcessId;
        lock (_admissionInitGate)
        {
            if (_admission is not null) return;
            var root = Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
            var dir = _arbitrationDir ?? Path.Combine(root, "arbitration");
            Directory.CreateDirectory(dir); // 首个执行入口才落目录（构造零副作用原则不破）
            var utcNow = _admissionSeams?.UtcNow;
            store = new ArbitrationLeaseStore(dir, utcNow);
            var hooks = new AdmissionHooks
            {
                F11Active = () => CurrentArbitrationFacts().F11Active,
                FactsProvider = CurrentArbitrationFacts,
                BgiEpochProvider = CurrentBgiEpoch,
                Sender = DispatchViaHostAsync,
                Barriers = _admissionSeams?.Barriers,
                TakeoverPersist = entry => Task.FromResult<string?>(
                    entry.RunId is { } rid && _runs.Load(rid) is not null ? null : "run_record_missing"),
                TakeoverTerminalConfirmed = (submissionIdentity, _) =>
                {
                    var read = _admissionStore?.Read();
                    var op = read.File?.Handoff?.Operations?.FirstOrDefault(
                        o => string.Equals(o.SubmissionIdentity, submissionIdentity, StringComparison.Ordinal));
                    return op?.RunBinding is { } rb
                        && _runs.Load(rb)?.State is WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled;
                },
            };
            facade = new ArbitrationAdmissionService(store, hooks, utcNow);
            acq = facade.EnsureOwnership(ownerKey, ttl);
            if (acq.Success)
            {
                _admissionStore = store; // 会诊重要-1：先于恢复赋值——TakeoverTerminalConfirmed/Dispatch 钩子闭包读字段，晚赋值=恒 null（保守失效）
                facade.RecoverAfterRestart(); // 每进程一次（幂等；恢复五路径准入门面侧）
                _admission = facade; // 保持恢复后赋值：并发首调者不得早退复用未完成恢复的实例
                return;
            }

            if (acq.Reason != "held")
                throw new InvalidOperationException("仲裁租约获取失败：" + acq.Reason);
        }

        // 阶段二（锁外有界等待）：held=旧所有者仍在 TTL 内——单调观察满 TTL 取证接管（§6.3 唯一依据）。
        var observer = new LeaseTakeoverObserver(() => _admissionMonotonic());
        var budget = TimeSpan.FromSeconds(ttl * 2 + 5); // 观察+一轮重试余量（心跳前进重观察另计）
        var started = _admissionMonotonic();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); // 宿主退出=放弃初始化（未产生任何副作用），OCE 不吞上抛
            LeaseTakeoverEvidence? evidence = null;
            while (evidence is null)
            {
                ct.ThrowIfCancellationRequested();
                if (_admissionMonotonic() - started > budget)
                    throw new InvalidOperationException("仲裁租约接管观察超时（仍被持有且有界预算耗尽，未产生任何副作用）");
                evidence = observer.Observe(store.Read());
                if (evidence is null) await Task.Delay(50, ct).ConfigureAwait(false);
            }

            lock (_admissionInitGate)
            {
                if (_admission is not null) return; // 并发首调者已接管完成——直接复用
                acq = facade.EnsureOwnership(ownerKey, ttl, evidence);
                if (acq.Success)
                {
                    _admissionStore = store; // 会诊重要-1：先于恢复赋值（同阶段一）
                    facade.RecoverAfterRestart();
                    _admission = facade;
                    return;
                }
            }

            // 观察期间心跳前进/被抢先接管：证据自然失效，重新观察（其他拒绝理由响亮抛出）。
            if (acq.Reason is not ("held" or "heartbeat_advanced"))
                throw new InvalidOperationException("仲裁租约接管获取失败：" + acq.Reason);
        }
    }

    /// <summary>本进程单调钟（§6.3：接管观察用单调时间——UTC 前跳不提前撤权、回拨不续命）。</summary>
    private static readonly System.Diagnostics.Stopwatch _admissionStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private static TimeSpan _admissionMonotonic() => _admissionStopwatch.Elapsed;

    /// <summary>仲裁事实快照（接缝优先；生产=BGI 控制面快照——缺失即未知，不解释为空闲）。</summary>
    private ArbitrationFacts CurrentArbitrationFacts()
    {
        if (_admissionSeams is { } s)
        {
            return new ArbitrationFacts
            {
                F11Active = s.F11Active ?? false,
                ExecutionOccupied = s.Occupied ?? false,
                ExecutionFactsUnknown = s.FactsUnknown ?? false,
            };
        }

        var status = _statusSnapshotProvider?.Invoke();
        return new ArbitrationFacts
        {
            ExecutionOccupied = status?.TaskRunning == true,
            ExecutionFactsUnknown = status is null,
        };
    }

    /// <summary>当前 BGI epoch（只比较不重写；缺失=空串——就绪守卫先行，实践中非空）。</summary>
    private string CurrentBgiEpoch()
    {
        if (_admissionSeams?.Epoch is { } e) return e;
        var se = _clientAccessor()?.ServerEpoch;
        return se is null ? "" : $"{se.ProcessId}:{se.StartTicksUtc}";
    }

    /// <summary>E1 面板启动统一仲裁面入口（冻结稿 §0 链路：无副作用解析→仲裁→锁内校验→落盘→锁外发送→对账→台账→关闭）。</summary>
    private async Task<HostActionResult> SubmitFlowStartViaAdmissionAsync(string workflowId, WorkflowSnapshot snapshot)
    {
        ArbitrationAdmissionService facade;
        try
        {
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            facade = _admission!;
        }
        catch (OperationCanceledException)
        {
            return HostActionResult.Unavailable("任务中心宿主正在退出，仲裁面初始化已取消（未产生任何副作用）");
        }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable("仲裁面初始化失败（未产生任何副作用）：" + ex.Message);
        }

        facade.SweepExpiredRetryWindows();

        // 同流程互斥前置（既有 R4 合同保留于无副作用解析段；跨流程并发由仲裁面全序裁决）
        lock (_gate)
        {
            var sameFlow = _runs.List().Where(r => r.WorkflowId == workflowId).ToList();
            if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                return HostActionResult.Unavailable("该流程已有活动运行（同流程同时只允许一个运行）");
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HostActionResult.Unavailable("该流程存在结果不确定（Unknown）的运行，需先对账再启动");
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return HostActionResult.Unavailable("该流程运行正在启动/驱动中");
        }

        // 发送前固定 candidateId→runId（§3.2：绑定不可改写）。预建 Planned 记录无执行副作用；未受理即终态化清理留痕。
        var run = _runs.CreateRun(workflowId, snapshot.Revision, note: "仲裁受理预备（R5.2 E1：未受理即终态化清理）");
        var request = new AdmissionRequest
        {
            Namespace = "manual",
            Kind = AdmissionKind.Create,
            SourceDetail = "ui:panel:start",
            RunBinding = run.RunId,
            Candidate = new ArbitrationCandidate
            {
                Scope = $"bgi:local:{CurrentBgiEpoch()}",
                Namespace = "manual",
                WorkflowId = workflowId,
                TriggerOccurrenceId = "manual:panel:{requestIdentity}", // I3：占位符由门面身份分配后回填
                ResourceRef = $"flow:{workflowId}",
                Intent = "start",
            },
        };

        AdmissionResult result;
        try
        {
            result = await facade.SubmitAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CleanupRejectedFlowRun(run, "仲裁面异常：" + ex.Message);
            return HostActionResult.Unavailable("仲裁面异常：" + ex.Message);
        }

        if (result.Kind == AdmissionResultKind.Accepted)
            return HostActionResult.Registered($"已受理启动（流程「{snapshot.Document.Name}」，运行 {run.RunId}）");

        // Reconciling=发送已尝试但结果不可考：运行记录不臆断取消（驱动或在飞），留待既有收敛/对账；
        // 其余未受理（确定未发送/未获选/拒绝/闸门）一律终态化清理留痕。
        if (result.Kind != AdmissionResultKind.Reconciling)
            CleanupRejectedFlowRun(run, $"仲裁未受理（{result.Kind}/{result.ReasonCode}）");
        return HostActionResult.Unavailable(result.Kind switch
        {
            AdmissionResultKind.F11Blocked => "F11 独立停止闸门激活（未产生任何租约副作用）",
            AdmissionResultKind.NeedPreemptConfirm => "BGI 执行占用中（需先停止在跑任务；抢占确认归后续阶段）",
            AdmissionResultKind.Reconciling or AdmissionResultKind.NeedReconcile => "执行事实待对账，已保守拒绝：" + result.Detail,
            AdmissionResultKind.RetryableRejected => "提交被确定拒绝（窗口内可重试）：" + result.Detail,
            AdmissionResultKind.TerminalRejected => "提交被终局拒绝：" + result.Detail,
            AdmissionResultKind.NotSelected => "并发仲裁未获选：" + result.Detail,
            _ => $"仲裁拒绝（{result.Kind}/{result.ReasonCode}）：{result.Detail}",
        });
    }

    /// <summary>锁外发送分派（门面回调）：E1 流程启动=驱动已受理运行；其他形状=Unknown 保守待对账（归 B2-β/γ）。</summary>
    private async Task<SendOutcome> DispatchViaHostAsync(SubmissionDispatch d)
    {
        if (_admissionSeams?.SenderOverride is { } over) return await over(d).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(d.Candidate.NodeId) || !d.ResourceRef.StartsWith("flow:", StringComparison.Ordinal))
            return new SendOutcome.Unknown("unsupported_dispatch_shape_b2a");

        // runBinding 反查（消费权威=租约 Operations；不凭内存映射——崩溃窗不丢绑定）
        var read = _admissionStore!.Read();
        var op = read.File?.Handoff?.Operations?.FirstOrDefault(
            o => string.Equals(o.RequestIdentity, d.RequestIdentity, StringComparison.Ordinal));
        if (op?.RunBinding is not { } runId)
            return new SendOutcome.Unknown("run_binding_missing");

        var workflowId = d.Candidate.WorkflowId;
        lock (_gate)
        {
            if (_shutdown) return new SendOutcome.Rejected("host_shutdown", false, "host:shutdown");
            if (CapabilityBlockReason() is { } cap) return new SendOutcome.Rejected("capability_blocked", false, "host:" + cap);
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return new SendOutcome.Rejected("task_running", true, "host:flow_inflight"); // 同流程在飞=可重试拒绝（门面派生窗口）
            _reservedWorkflows.Add(workflowId);
        }

        WorkflowRunner runner;
        try
        {
            runner = CreateRunner(_clientAccessor());
        }
        catch (Exception ex)
        {
            lock (_gate) _reservedWorkflows.Remove(workflowId);
            return new SendOutcome.Rejected("launch_error", false, "host:" + ex.GetType().Name);
        }

        var launch = LaunchDrive(workflowId, runner,
            cts => runner.StartExistingRunAsync(runId, cts.Token),
            $"已受理启动（流程 {workflowId}）", runId);
        // LaunchDrive 返回 Unavailable 的唯一可达路径=关闭竞态册外观察（驱动或在飞——异步入口异常一律入 Task 由观察器收敛）：
        // 受理与否不可考=Unknown 保守待对账（孤儿观察器按既有合同收敛运行记录；不臆断拒绝防双跑）。
        return launch.Status != HostActionStatus.Unavailable
            ? new SendOutcome.Accepted("host:drive_registered", runId)
            : (SendOutcome)new SendOutcome.Unknown("host:orphaned_mid_launch");
    }

    /// <summary>未受理预建运行清理（终态化留痕：历史可见「尝试被拒」，不留 Planned 僵尸）。</summary>
    private void CleanupRejectedFlowRun(WorkflowRunRecord run, string reason)
    {
        try
        {
            run.State = WorkflowRunState.Cancelled;
            run.Note = (run.Note is null ? "" : run.Note + "；") + reason;
            _runs.Update(run);
        }
        catch (Exception ex)
        {
            _log?.Invoke("[任务中心] 未受理运行清理失败：" + ex.Message);
        }
    }

    /// <summary>运行终态→仲裁操作终局回写（按 runBinding 反查 Operations；台账交叉确认经 TakeoverTerminalConfirmed 钩子）。</summary>
    private void MarkAdmissionTerminalIfAny(string? runId)
    {
        if (!_admissionWired || runId is null || _admission is null) return;
        // 受理管线内竞态（夹具实证）：驱动极快时运行先终态，门面尚在「占位→台账→关闭」途中（op=Granted/Sending）。
        // 纪律：不得在本调用路径上同步等待受理收敛——entry.Task 已完成时 ObserveDriveAsync 整体内联于门面流水线程
        // 执行，同步自旋=循环等待（实测 5s 停摆）。整个回写放线程池异步有界重试：管线毫秒级关闭后自会看到 Accepted；
        // 崩溃/未决（Reconciling 等）=保守不回写（恢复路径按既有合同处置）；进程退出前未完成的回写由重启恢复兜底。
        _ = Task.Run(async () =>
        {
            try
            {
                var run = _runs.Load(runId);
                if (run?.State is not (WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled)) return;
                OperationRecord? op = null;
                for (var spin = 0; spin < 500; spin++)
                {
                    OperationRecord? current;
                    try
                    {
                        current = _admissionStore!.Read().File?.Handoff?.Operations?.FirstOrDefault(
                            o => string.Equals(o.RunBinding, runId, StringComparison.Ordinal));
                    }
                    catch (IOException)
                    {
                        // 锁文件（FileShare.None）瞬时争用：与门面自身写入/其他读取碰撞——按「尚未收敛」重试（跨进程互斥合同的调用方义务）。
                        await Task.Delay(20).ConfigureAwait(false);
                        continue;
                    }

                    if (current is null || current.RequestState is OperationRequestState.Reconciling
                        or OperationRequestState.TerminalRejected or OperationRequestState.NotSelected
                        or OperationRequestState.TerminalCompleted) return; // 确定不会进入 Accepted=无需回写
                    if (current.RequestState == OperationRequestState.Accepted) { op = current; break; }
                    await Task.Delay(10).ConfigureAwait(false);
                }

                if (op is null)
                {
                    _log?.Invoke("[任务中心] 仲裁操作终局回写放弃：受理管线未在有界窗口内收敛（保守留待对账）。");
                    return;
                }

                AdmissionResult? r = null;
                for (var attempt = 0; attempt < 5; attempt++) // 锁争用 IOException 有界重试（逻辑拒绝不重试）
                {
                    try
                    {
                        r = _admission.MarkOperationTerminal(op.RequestIdentity, "runstore:" + run!.State);
                        break;
                    }
                    catch (IOException) when (attempt < 4)
                    {
                        await Task.Delay(25).ConfigureAwait(false);
                    }
                }

                if (r is null || r.Kind == AdmissionResultKind.Error)
                    _log?.Invoke($"[任务中心] 仲裁操作终局回写被拒（{r?.ReasonCode ?? "lock_contention"}）：{r?.Detail ?? "锁争用重试耗尽，保守留待对账"}");
            }
            catch (Exception ex)
            {
                _log?.Invoke("[任务中心] 仲裁操作终局回写异常（保守留待对账）：" + ex.Message);
            }
        });
    }
}
