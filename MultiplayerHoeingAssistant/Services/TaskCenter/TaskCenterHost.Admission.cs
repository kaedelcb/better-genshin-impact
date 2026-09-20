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
    /// <summary>夹具接缝：进入「§6.3 接管观察」阶段时回调（用于把并发首调者的目标交错固定下来，
    /// 证明后继观察者确实与先行接管者重叠，而不是靠调度运气）。</summary>
    public Action? OnTakeoverObservationEntered { get; set; }
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
/// - B2-β（E2 恢复五路径接线，已落地）：ResumeRunAsync/RegisterResumeHandoff 接线后一律经门面 AdmitRecoveryAsync
///   （恢复专用准入边界：不排序不产候选、恢复意图持久化后发送、保留原票据与 RestorePending 责任）；sender 恢复分支
///   DispatchResumeViaHostAsync 按 runBinding 反查台账驱动 ResumeAsync；恢复拒绝不动原运行记录。
///   确认取消后重跑=走 E1 全边界（已接线）；Unknown 待对账=既有拒绝引导（无副作用）；启动移交登记=台账无执行意图（resume 模式经本边界）。
///   登记：恢复操作不走 RetryAsync（轮次排序违反「恢复不排序」——可重试拒绝由入口新操作重新发起）；A6 票据恢复状态机消费侧归 R5.3。
/// - 节点级后继提交（Runner 经边界）归 B2-γ；本批 sender 遇其他非流程启动/恢复形状=Unknown 保守待对账。
/// 会诊登记（DS FLASH B2-α 轮，建议级归后续批次）：
/// - 建议-1 对账/终局交错：MarkAdmissionTerminalIfAny 与门面「受理→台账→关闭」交错窗下，关闭路径对已终局操作幂等断言归 B4；
/// - 建议-2 接线路经就绪判定在发送侧兜底（CreateRunner launch_error），生产恒传 ensureExecutionReady 前置保留观察，B4 收口复核；
/// - 建议-3 NeedPreemptConfirm 下预建运行已取消的 candidateId→runId 绑定再入/迁移处置归 R5.3 抢占语义登记；
/// - 建议-4 ReadCore 内部 IO 异常折 Corrupt 不经锁争用重试（锁文件不存在直读路径），区分「争用可重试/真损坏留痕」归后续打磨。
/// </summary>
public sealed partial class TaskCenterHost
{
    private readonly bool _admissionWired;
    /// <summary>
    /// B2-γ 第 3 步「路径启用」独立门（§12.3 施工阻断：第 3 步尚不得启用相关路径）。
    /// `_admissionWired` 只表示 E1/E2 入口已接线；**节点后继提交改道必须另开此门**——生产构造恒不传
    /// （＝false，节点提交保持 R4 直通）；仅当 G1/G2/G4/G4a/G5/G6/G7/G8/G9/G10 逐条闭环并通过 §12.3 交错验收后，
    /// 才允许由后续批次在生产构造显式打开。代码就绪 ≠ 路径启用。
    /// </summary>
    private readonly bool _successorAdmissionWired;

    /// <summary>夹具接缝：第 3 步路径门当前是否生效（＝E1/E2 已接线 **且** 第 3 步显式启用）。</summary>
    internal bool SuccessorAdmissionWiredForTest => _admissionWired && _successorAdmissionWired;
    private readonly string? _arbitrationDir;
    private readonly TaskCenterAdmissionSeams? _admissionSeams;
    private string? _runsDirPath;
    private readonly object _admissionInitGate = new();
    private ArbitrationLeaseStore? _admissionStore;
    private ArbitrationAdmissionService? _admission;
    private string? _admissionLeaseId; // 获取时捕获的所有者身份（心跳只续本进程租约——绝不为他人续命，会诊 建议-1）
    private string? _admissionOwnerEpoch;

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
                _admissionLeaseId = acq.Lease!.LeaseId;
                _admissionOwnerEpoch = ownerKey;
                facade.RecoverAfterRestart(); // 每进程一次（幂等；恢复五路径准入门面侧）
                _admission = facade; // 保持恢复后赋值：并发首调者不得早退复用未完成恢复的实例
                StartAdmissionLeaseHeartbeat(ttl); // 所有者存续期续期（TTL 到期=一切写入被拒）
                return;
            }

            if (acq.Reason != "held")
                throw new InvalidOperationException("仲裁租约获取失败：" + acq.Reason);
        }

        // 阶段二（锁外有界等待）：held=旧所有者仍在 TTL 内——单调观察满 TTL 取证接管（§6.3 唯一依据）。
        var observer = new LeaseTakeoverObserver(() => _admissionMonotonic());
        var budget = TimeSpan.FromSeconds(ttl * 2 + 5); // 观察+一轮重试余量（心跳前进重观察另计）
        var started = _admissionMonotonic();
        _admissionSeams?.OnTakeoverObservationEntered?.Invoke(); // 夹具接缝：固定并发首调者的交错（生产 null=空操作）
        while (true)
        {
            ct.ThrowIfCancellationRequested(); // 宿主退出=放弃初始化（未产生任何副作用），OCE 不吞上抛
            LeaseTakeoverEvidence? evidence = null;
            while (evidence is null)
            {
                ct.ThrowIfCancellationRequested();
                // 会诊阻断项处置：并发首调者复用检查必须早于「接管证据成熟」——旧租约仍被持有时两个首调者同时进入
                // 本阶段，先接管者随即开始心跳，后继者观察到的 heartbeatSeq 持续推进=证据永不成形，原实现会空转到
                // 预算耗尽后响亮失败（共享门面其实早已可用）。故每轮等待都先复用已完成的门面。
                lock (_admissionInitGate)
                {
                    if (_admission is not null) return; // 并发首调者已接管完成——直接复用
                }
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
                    _admissionLeaseId = acq.Lease!.LeaseId;
                    _admissionOwnerEpoch = ownerKey;
                    facade.RecoverAfterRestart();
                    _admission = facade;
                    StartAdmissionLeaseHeartbeat(ttl);
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

    /// <summary>
    /// 优雅退出释放租约（会诊 建议-1 处置：缩短重启接管观察窗——释放保留 Handoff/Diag 段，未决事实不清空，§6.2 更替继承）；
    /// 释放失败/所有权已更替=仅留痕（留待 TTL 接管路径兜底，绝不强制）。
    /// </summary>
    private void ReleaseAdmissionLeaseOnShutdown()
    {
        if (_admissionStore is not { } store || _admissionLeaseId is not { } lid || _admissionOwnerEpoch is not { } oe) return;
        try
        {
            var read = store.Read();
            if (read.File?.Lease is not { } lease || !string.Equals(lease.LeaseId, lid, StringComparison.Ordinal)) return; // 已更替=无需释放
            var rel = store.TryRelease(lid, oe, read.File.Revision);
            if (!rel.Success)
                _log?.Invoke("[任务中心] 仲裁租约退出释放被拒（" + rel.Reason + "）——留待 TTL 接管路径。");
        }
        catch (Exception ex)
        {
            _log?.Invoke("[任务中心] 仲裁租约退出释放异常（留待 TTL 接管路径）：" + ex.Message);
        }
    }

    /// <summary>
    /// 租约心跳续期循环（B2-β 实证缺口：所有者存续期间必须续期——TTL 到期后一切变更被锁内 TTL 校验拒 lease_stale_generation，
    /// §6.1 过期不得续期复活=只能重新接管观察等满 TTL，生产不可接受）。TTL/3 节奏（下限 200ms）锁内续期；
    /// 续期被拒=所有权已更替/丢失——后续提交自然响亮拒绝（不借旧身份写入）；宿主退出随 _shutdownCts 取消。
    /// </summary>
    private void StartAdmissionLeaseHeartbeat(int ttlSeconds)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(200, ttlSeconds * 1000 / 3));
        var ct = _shutdownCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                    var read = _admissionStore?.Read(); // 锁内读当前文件（修订在文件级）
                    var lease = read?.File?.Lease;
                    if (lease is null || read!.File is null) continue;
                    // 会诊 建议-1 处置：只续「本进程获取时捕获」的租约身份——所有权已更替=停止心跳（绝不为他人续命）；
                    // 后续提交经锁内身份/TTL 校验自然响亮拒绝，重取走 §6.3 接管观察路径。
                    if (!string.Equals(lease.LeaseId, _admissionLeaseId, StringComparison.Ordinal)
                        || !string.Equals(lease.OwnerEpoch, _admissionOwnerEpoch, StringComparison.Ordinal))
                    {
                        _log?.Invoke("[任务中心] 仲裁租约所有权已更替（本进程停止续期；后续提交将响亮拒绝或经接管重取）。");
                        return;
                    }

                    // 用最新修订变体：原「Read() 取修订 → TryRenew(捕获修订)」在同胞写入并发下会因修订漂移被误判
                    // lease_stale_generation；续期误判累积到 TTL 即失去租约（会诊重要项）。
                    var renew = _admissionStore!.TryRenewLatest(_admissionLeaseId!, _admissionOwnerEpoch!);
                    if (!renew.Success)
                        _log?.Invoke("[任务中心] 仲裁租约续期被拒（" + renew.Reason + "）——后续提交将响亮拒绝。");
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { /* 锁争用：下一周期再试（单次操作持锁极短） */ }
                catch (Exception ex)
                {
                    _log?.Invoke("[任务中心] 仲裁租约续期异常（下一周期再试）：" + ex.Message);
                }
            }
        });
    }

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

        // §7.1-1 冻结合同「F11 判定先于租约获取」：本预检必须早于 EnsureAdmissionFacadeAsync——门面组装会执行
        // EnsureOwnership/RecoverAfterRestart/心跳与重试窗口扫描（均属租约/责任副作用）。预建 Planned 运行属 RunStore
        // 侧、非租约副作用，且 B2-α 已签署「F11 阻断也终态化清理留痕」，故本预检置于其后。
        // 门面内锁内复核保留（双保险，竞态窗口内激活同样阻断占位与发送）。
        if (CurrentArbitrationFacts().F11Active)
        {
            CleanupRejectedFlowRun(run, "F11 独立停止闸门激活（租约零副作用）");
            return HostActionResult.Unavailable("F11 独立停止闸门激活（未发生租约副作用）");
        }

        ArbitrationAdmissionService facade;
        try
        {
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
            facade = _admission!;
        }
        catch (OperationCanceledException)
        {
            CleanupRejectedFlowRun(run, "仲裁面初始化已取消（宿主退出，未发送）");
            return HostActionResult.Unavailable("任务中心宿主正在退出，仲裁面初始化已取消（未产生租约副作用）");
        }
        catch (Exception ex)
        {
            CleanupRejectedFlowRun(run, "仲裁面初始化失败（未发送）：" + ex.Message);
            return HostActionResult.Unavailable("仲裁面初始化失败（未产生租约副作用）：" + ex.Message);
        }

        // 会诊 P2 处置：预建 Planned 运行已存在，此后任何异常都必须终态化清理，否则 Planned 僵尸会持续占用
        // 同流程活动态检查、把后续启动一路拒绝到重启恢复为止。
        try
        {
            facade.SweepExpiredRetryWindows();
        }
        catch (Exception ex)
        {
            var cleaned = CleanupRejectedFlowRun(run, "仲裁面维护扫描异常（未发送）：" + ex.Message);
            return HostActionResult.Unavailable(cleaned
                ? "仲裁面维护扫描异常（未产生发送）：" + ex.Message
                : "仲裁面维护扫描异常且预建运行清理失败（未产生发送，该流程在重启对账前不可再启动）：" + ex.Message);
        }

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
        if (string.Equals(d.Intent, "resume", StringComparison.Ordinal))
            return await DispatchResumeViaHostAsync(d).ConfigureAwait(false); // E2 恢复专用（B2-β）
        // R5.2 B2-γ 第 3 步（§13.10 A2）：**节点执行操作**走后继专用分派（候选带节点出现身份）；
        // 非节点且非流程启动的未知形状仍按 B2-α 口径保守 Unknown。
        if (!string.IsNullOrEmpty(d.Candidate.NodeId))
            return await DispatchSuccessorViaHostAsync(d).ConfigureAwait(false);
        if (!d.ResourceRef.StartsWith("flow:", StringComparison.Ordinal))
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

    /// <summary>
    /// 锁外发送分派（E2 恢复，B2-β）：runBinding 反查租约 Operations（消费权威）→运行台账核对→LaunchDrive ResumeAsync。
    /// 受理=驱动登记（与 E1 同口径）；LaunchDrive Unavailable=关闭竞态册外观察（驱动或在飞）→Unknown 不臆断拒绝。
    /// </summary>
    private async Task<SendOutcome> DispatchResumeViaHostAsync(SubmissionDispatch d)
    {
        var read = _admissionStore!.Read();
        var op = read.File?.Handoff?.Operations?.FirstOrDefault(
            o => string.Equals(o.RequestIdentity, d.RequestIdentity, StringComparison.Ordinal));
        if (op?.RunBinding is not { } runId)
            return new SendOutcome.Unknown("run_binding_missing");
        var run = _runs.Load(runId);
        if (run is null)
            return new SendOutcome.Rejected("run_record_missing", false, "host:runstore"); // 台账缺失=本地权威确定拒绝
        if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused))
            return new SendOutcome.Rejected("run_state_changed", false, "host:runstore"); // 准入→发送窗口状态已变（本地权威）

        var workflowId = run.WorkflowId!;
        lock (_gate)
        {
            if (_shutdown) return new SendOutcome.Rejected("host_shutdown", false, "host:shutdown");
            if (CapabilityBlockReason() is { } cap) return new SendOutcome.Rejected("capability_blocked", false, "host:" + cap);
            // 会诊 P1 处置：E2 前置互斥检查在门面登记之前且不预留，与本次预留之间存在窗口——窗口内同流程其他运行
            // 可能已进入活动态（如另一 Interrupted 运行先行恢复后转 Paused/Unknown）。故在预留临界区内对台账原子再复核：
            // 目标仍可恢复 + 同流程无其他活动态/Unknown，任何一项不成立即响亮拒绝、不预留、不驱动。
            var fresh = _runs.Load(runId);
            if (fresh is null) return new SendOutcome.Rejected("run_record_missing", false, "host:runstore");
            if (fresh.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused))
                return new SendOutcome.Rejected("run_state_changed", false, "host:runstore"); // 准入→发送窗口状态已变（本地权威）
            var siblings = _runs.List().Where(r => r.WorkflowId == workflowId && r.RunId != runId).ToList();
            if (siblings.Any(r => ActiveStates.Contains(r.State)))
                return new SendOutcome.Rejected("same_flow_active_run", false, "host:flow_conflict");
            if (siblings.Any(r => r.State == WorkflowRunState.Unknown))
                return new SendOutcome.Rejected("same_flow_unknown_run", false, "host:flow_conflict");
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return new SendOutcome.Rejected("task_running", true, "host:flow_inflight"); // 同流程在飞=可重试拒绝
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

        await Task.CompletedTask.ConfigureAwait(false); // 发送本体为同步登记（与 E1 分支同构，保留异步签名）
        var launch = LaunchDrive(workflowId, runner,
            cts => runner.ResumeAsync(runId, cts.Token),
            $"已受理恢复（运行 {runId}，游标身份重定位）", runId);
        return launch.Status != HostActionStatus.Unavailable
            ? new SendOutcome.Accepted("host:drive_registered", runId)
            : (SendOutcome)new SendOutcome.Unknown("host:orphaned_mid_launch");
    }

    /// <summary>
    /// E2 恢复统一准入入口（冻结稿 §5.1：恢复专用准入边界——不排序不产候选；面板恢复/启动移交 resume 共用）。
    /// 恢复拒绝不动原运行记录（保持 Interrupted/Paused 可再恢复——无预建记录需清理，与 E1 预建清理镜像对称）。
    /// </summary>
    private async Task<HostActionResult> SubmitResumeViaAdmissionAsync(WorkflowRunRecord run, string sourceDetail)
    {
        // §7.1-1 同口径：恢复入口同样先判 F11，再组装门面（理由见启动入口注释）。
        if (CurrentArbitrationFacts().F11Active)
            return HostActionResult.Unavailable("F11 独立停止闸门激活（未发生租约副作用）");

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

        // 同流程互斥前置（无副作用解析段；恢复目标自身排除——与旧临界区同口径）
        lock (_gate)
        {
            var sameFlow = _runs.List().Where(r => r.WorkflowId == run.WorkflowId && r.RunId != run.RunId).ToList();
            if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                return HostActionResult.Unavailable("该流程已有其他活动运行（同流程同时只允许一个运行）");
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HostActionResult.Unavailable("该流程存在结果不确定（Unknown）的其他运行，需先对账再恢复");
            if (_reservedWorkflows.Contains(run.WorkflowId!) || _drives.ContainsKey(run.WorkflowId!))
                return HostActionResult.Unavailable("该流程已有运行正在驱动（禁止双驱动）");
        }

        var result = await facade.AdmitRecoveryAsync(new RecoveryAdmissionRequest
        {
            SourceDetail = sourceDetail,
            RunId = run.RunId!,
            WorkflowId = run.WorkflowId!,
            RestoreBranch = run.State == WorkflowRunState.Paused ? "paused-continue" : "interrupted-relocate",
            Scope = $"bgi:local:{CurrentBgiEpoch()}",
        }).ConfigureAwait(false);

        if (result.Kind == AdmissionResultKind.Accepted)
            return HostActionResult.Registered($"已受理恢复（运行 {run.RunId}，游标身份重定位）");
        return HostActionResult.Unavailable(result.Kind switch
        {
            AdmissionResultKind.F11Blocked => "F11 独立停止闸门激活（未产生任何租约副作用）",
            AdmissionResultKind.RetryableRejected when result.ReasonCode == "execution_occupied"
                => "BGI 执行占用中，恢复未发送（占用解除后可重新恢复）",
            AdmissionResultKind.RetryableRejected => "恢复被确定拒绝（窗口内可重试）：" + result.Detail,
            AdmissionResultKind.Reconciling or AdmissionResultKind.NeedReconcile => "执行事实待对账，恢复已保守停驻：" + result.Detail,
            AdmissionResultKind.TerminalRejected => "恢复被终局拒绝：" + result.Detail,
            _ => $"恢复准入拒绝（{result.Kind}/{result.ReasonCode}）：{result.Detail}",
        });
    }

    /// <summary>未受理预建运行清理（终态化留痕：历史可见「尝试被拒」，不留 Planned 僵尸）。</summary>
    /// <summary>
    /// 未受理预建运行终态化清理（留痕）。返回是否清理成功——清理失败（磁盘写失败）会留下 Planned 记录并
    /// 持续占用同流程活动态检查，属必须上抛的后果，故不再无条件吞掉（会诊四轮 P2）。
    /// </summary>
    private bool CleanupRejectedFlowRun(WorkflowRunRecord run, string reason)
    {
        try
        {
            run.State = WorkflowRunState.Cancelled;
            run.Note = (run.Note is null ? "" : run.Note + "；") + reason;
            _runs.Update(run);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                _log?.Invoke("[任务中心] 未受理运行清理失败（将阻塞同流程后续启动，需重启对账）：" + ex.Message);
            }
            catch
            {
                // 日志异常不得掩盖清理失败的事实。
            }

            return false;
        }
    }

    /// <summary>
    /// R5.2 B2-γ 第 3 步（§13.10 A）：节点提交的后继结果回传槽。**按完整发送身份**（submissionIdentity）存取，
    /// 不得用业务身份做键（同 attempt 可有多 sendSeq）；装饰器据此返回 Runner 所需的 `BoundarySubmitResult`。
    /// </summary>
    /// <summary>
    /// **节点后继的进程内不可变请求快照**（§13.10 A1/A2）：入队时按 Runner 当时提交的请求**冻结**，
    /// 随获选排队项传到 Sender。**节点经 JSON 往返深拷贝**——不得把可变 `WorkflowNode` 引用当作冻结快照
    /// （排队期间流程定义可能被编辑）。
    /// </summary>
    internal sealed record SuccessorContext(
        WorkflowNode Node, WorkflowNodeOccurrence Occurrence, bool Suppress, int Attempt, string ExpectedSubmissionKey);

    /// <summary>把节点冻结成不可变副本（JSON 往返；失败＝抛错，由调用方保守拒绝，不降级为「用原引用」）。</summary>
    private static WorkflowNode FreezeNode(WorkflowNode node)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(node);
        return System.Text.Json.JsonSerializer.Deserialize<WorkflowNode>(json)
               ?? throw new InvalidOperationException("节点冻结失败（反序列化为空）。");
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BoundarySubmitResult> _successorSendResults = new();

    /// <summary>
    /// 后继提交准入（§5.2／§13.10 A）：门面先占位，Sender 内再准备与发送；本方法只负责**准入**与**结果映射**。
    /// </summary>
    internal async Task<BoundarySubmitResult> SubmitSuccessorViaAdmissionAsync(WorkflowSubmitRequest request, CancellationToken ct)
    {
        var run = request.Run;
        var occ = request.Occurrence;
        var sub = run.CurrentSubmission;
        // 引擎纪律：意图先行（Runner 已落盘）；身份不符＝可证实未发送
        if (sub is null || sub.Intent != SubmitIntentState.IntentRecorded
            || sub.NodeId != occ.NodeId || sub.Occurrence != occ.Occurrence || sub.LoopIteration != occ.LoopIteration)
            return BoundarySubmitResult.Rejected("提交意图缺失或身份不符（未发送）");
        // §7.1-1：F11 判定先于租约获取
        if (CurrentArbitrationFacts().F11Active)
            return BoundarySubmitResult.Rejected("F11 独立停止闸门激活（未发生租约副作用；未发送）");

        ArbitrationAdmissionService facade;
        try { await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false); facade = _admission!; }
        catch (OperationCanceledException) { return BoundarySubmitResult.UnknownWith("宿主退出，仲裁面初始化取消（未发送，待对账）"); }
        catch (Exception ex) { return BoundarySubmitResult.UnknownWith("仲裁面初始化失败（未发送，待对账）：" + ex.GetType().Name); }

        // 授权来源：该 runBinding 已登记操作的固定 Scope（I-1：继承，不重读当前 epoch）
        var scope = TryGetAdmissionScope(run.RunId!);
        if (scope is null)
        {
            // [纠正·2026-09-21] 原「缺 Scope 时退回直通发送」的临时旁路被会诊否决（ASTRA high 阻断项 1）：
            // 那会凭空建立一条**不受仲裁许可约束的发送入口**（绕过占位/Pending/Submission 冲突与固定授权纪元校验），
            // 与 AMD-1-5 第三条「缺固定 Scope/绑定＝不签发、不发送」直接冲突。
            // 正确处置＝响亮拒绝并留痕（可证实未发送）。缺口本体（启动移交来源未登记固定 Scope/绑定）归
            // §13.11 G4a，须先按 AMD-1-5 的四类来源规则在移交受理处落定来源记录，再启用本路径。
            try
            {
                // 会诊复审：日志委托是外部注入的诊断面，其异常不得改变本方法的结构化返回值。
                _log?.Invoke("[任务中心] 后继提交缺少已登记仲裁授权（运行 " + run.RunId + " 无固定 Scope/绑定）——"
                             + "按 AMD-1-5 第三条拒绝签发（未发送）；缺口见设计稿 §13.11 G4a。");
            }
            catch (Exception)
            {
                // 诊断失败不改变结论：仍按 AMD-1-5 第三条拒绝签发。
            }
            return BoundarySubmitResult.Rejected(
                "无已登记仲裁授权（缺固定 Scope/绑定，按 AMD-1-5 第三条不签发不发送）");
        }

        AdmissionResult result;
        try
        {
            result = await facade.SubmitAsync(new AdmissionRequest
            {
                Namespace = "successor",
                Kind = AdmissionKind.Create,
                SourceDetail = "runner:successor",
                RunBinding = run.RunId,
                // §13.10 A1/A2：入队即冻结「Runner 当时提交的请求」（节点深拷贝＋完整出现身份＋提交选项＋预期提交键），
                // 随获选排队项传到 Sender；Sender 不得再从当前流程定义重建。
                ProcessLocalContext = new SuccessorContext(
                    FreezeNode(request.Node), request.Occurrence, request.SuppressConfigCompletionAction,
                    sub.Attempt, sub.Key),
                CursorRef = run.Cursor is { } cur ? $"{cur.NodeId}#{cur.Occurrence}#{cur.LoopIteration}" : null,
                Candidate = new ArbitrationCandidate
                {
                    Scope = scope,
                    Namespace = "successor",
                    WorkflowId = run.WorkflowId,
                    TriggerOccurrenceId = "successor:" + run.RunId,
                    RunId = run.RunId,
                    NodeId = occ.NodeId,
                    Occurrence = occ.Occurrence,
                    LoopIteration = occ.LoopIteration,
                    Attempt = sub.Attempt,
                    ResourceRef = "node:" + occ.NodeId,
                    Intent = "start",
                },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return BoundarySubmitResult.UnknownWith("仲裁面异常（结果待对账）：" + ex.GetType().Name);
        }

        if (result.Kind == AdmissionResultKind.Accepted)
        {
            // 按**完整发送身份**取回 sender 内真实发送结果（§13.10 A2：**非破坏性读取**——合并调用者共享同一结果，
            // 不得因首个读取者 `TryRemove` 而让其余调用者降级为 Unknown）。取不到＝不猜成功，保守待对账。
            if (result.SubmissionIdentity is { } sid && _successorSendResults.TryGetValue(sid, out var sent)) return sent;
            return BoundarySubmitResult.UnknownWith("仲裁已受理但未取得发送回执（不猜成功，待对账）");
        }

        return MapAdmissionResultToBoundary(result);
    }

    /// <summary>
    /// §13.10 A3 三态映射（internal static＝可直接夹具断言）。
    /// [纠正·2026-09-21] 会诊阻断（ASTRA high 阻断项 2）：三态必须按「**结果确定性**」映射，不得按「可否重试」
    /// 映射，也不得用 `_ => Rejected` 兜底——否则「sender 已 Accepted、随后关闭/接管抛异常」会被
    /// `ProcessRoundAsync` 收敛成 `Error`，再被本层**反转为确定拒绝**（事实反转）。
    /// 判据：门面给出**确定结论** → Rejected（本层只转发门面结论，**不**自行断言「一定没发送」）；
    /// **事实不可考**（含 Error）→ Unknown（不猜成功不猜失败）。
    /// 原因码与明细原样保留，供对账与诊断。
    /// </summary>
    internal static BoundarySubmitResult MapAdmissionResultToBoundary(AdmissionResult result)
    {
        return result.Kind switch
        {
            AdmissionResultKind.TerminalRejected or AdmissionResultKind.RetryableRejected
                or AdmissionResultKind.NotSelected or AdmissionResultKind.F11Blocked
                or AdmissionResultKind.NeedPreemptConfirm
                => BoundarySubmitResult.Rejected(
                    "仲裁确定未受理（" + result.Kind + "/" + result.ReasonCode + "）：" + result.Detail),
            AdmissionResultKind.NeedReconcile or AdmissionResultKind.Reconciling
                => BoundarySubmitResult.UnknownWith(
                    "仲裁待对账（事实不可考，不猜成功也不猜失败）：" + result.Kind + "/" + result.ReasonCode
                    + "：" + result.Detail),
            _ => BoundarySubmitResult.UnknownWith(
                "仲裁面事实不可考（" + result.Kind + "/" + result.ReasonCode + "）：" + result.Detail
                + "（不臆断未发送，待对账）"),
        };
    }

    /// <summary>该 runBinding 已登记操作的固定 `Scope`（I-1 继承；查不到＝无授权，不得读当前 epoch 补造）。</summary>
    private string? TryGetAdmissionScope(string runId)
    {
        var read = _admissionStore?.Read();
        return read?.File?.Handoff?.Operations?
            .Where(o => string.Equals(o.RunBinding, runId, StringComparison.Ordinal))
            .OrderByDescending(o => o.UpdatedAtUtc)
            .Select(o => o.Candidate?.Scope)
            .FirstOrDefault(s => !string.IsNullOrEmpty(s));
    }

    /// <summary>
    /// 后继发送分派（门面 Sender 回调，§13.10 A2/A3）：runBinding 反查 run → 由请求重建出现身份与节点 →
    /// 发送段内 `PrepareSubmit + SendPreparedAsync`（准入已在门面完成）→ 结果按完整发送身份暂存并映射三态。
    /// </summary>
    private async Task<SendOutcome> DispatchSuccessorViaHostAsync(SubmissionDispatch d)
    {
        var read = _admissionStore!.Read();
        var op = read.File?.Handoff?.Operations?.FirstOrDefault(
            o => string.Equals(o.RequestIdentity, d.RequestIdentity, StringComparison.Ordinal));
        if (op?.RunBinding is not { } runId) return new SendOutcome.Unknown("run_binding_missing");
        var run = _runs.Load(runId);
        if (run is null) return new SendOutcome.Rejected("run_record_missing", false, "host:runstore");
        var c = _clientAccessor();
        if (c is null) return new SendOutcome.Rejected("bgi_client_missing", false, "host:client");

        // §13.10 A1/A2/A2′：**只消费获选排队项携带的不可变请求快照**——
        // 不得 LoadSnapshot 重建节点（排队期间流程定义可变）、不得写死 occurrence 参数、不得丢弃提交选项。
        // 上下文缺失（续用/重试/重启后未携带）= 响亮拒绝，绝不静默重建后发送。
        if (d.ProcessLocalContext is not SuccessorContext ctx)
            return new SendOutcome.Rejected("successor_context_missing", false, "host:context");
        var occ = ctx.Occurrence;
        var node = ctx.Node;
        // 出现身份与候选必须一致（防上下文与候选错配）
        if (!string.Equals(occ.NodeId, d.Candidate.NodeId, StringComparison.Ordinal)
            || occ.Occurrence != d.Candidate.Occurrence || occ.LoopIteration != d.Candidate.LoopIteration)
            return new SendOutcome.Rejected("successor_context_mismatch", false, "host:context");
        // §13.10 A1（[补·2026-09-21] 会诊要求）：**消费**冻结的 attempt/key——排队期间若当前提交已换身份
        // （新 attempt / 新提交键），旧授权不得被准备成新提交，一律响亮拒绝（确定未发送）。
        if (run.CurrentSubmission is not { } liveSub
            || liveSub.Attempt != ctx.Attempt
            || !string.Equals(liveSub.Key, ctx.ExpectedSubmissionKey, StringComparison.Ordinal))
            return new SendOutcome.Rejected("successor_submission_identity_changed", false, "host:context");

        var inner = new BgiWorkflowExecutionBoundary(c, _runs);
        var prepared = inner.PrepareSubmit(new WorkflowSubmitRequest(run, occ, node, ctx.Suppress),
            authorizedEpoch: d.TargetEpoch); // 授权纪元＝门面本轮授权值（A2：不在 sender 内重读当前 epoch）
        if (prepared.Rejection is { } rej)
            return rej.Uncertain ? new SendOutcome.Unknown("boundary_precheck_uncertain") : new SendOutcome.Rejected("boundary_precheck_rejected", false, "host:boundary");

        var sent = await inner.SendPreparedAsync(prepared, CancellationToken.None).ConfigureAwait(false);
        if (d.SubmissionIdentity.Length > 0) _successorSendResults[d.SubmissionIdentity] = sent;
        return sent.Accepted
            ? new SendOutcome.Accepted("host:successor_sent", runId)
            : sent.Uncertain
                ? (SendOutcome)new SendOutcome.Unknown("host:successor_uncertain")
                : new SendOutcome.Rejected("host:successor_rejected", false, "host:boundary");
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
                // B2-β（恢复接入后）：同一 runBinding 可有多笔操作（启动 op + 恢复 op 共享同一运行事实）——
                // 运行终态=绑定该运行的全部已受理操作一同终局；有操作仍在过渡态（受理管线未关闭）则等有界窗口。
                List<OperationRecord>? accepted = null;
                for (var spin = 0; spin < 500; spin++)
                {
                    List<OperationRecord> current;
                    try
                    {
                        current = _admissionStore!.Read().File?.Handoff?.Operations?
                            .Where(o => string.Equals(o.RunBinding, runId, StringComparison.Ordinal)).ToList() ?? [];
                    }
                    catch (IOException)
                    {
                        // 锁文件（FileShare.None）瞬时争用：与门面自身写入/其他读取碰撞——按「尚未收敛」重试（跨进程互斥合同的调用方义务）。
                        await Task.Delay(20).ConfigureAwait(false);
                        continue;
                    }

                    if (current.Count == 0) return; // 无映射=零副作用
                    if (current.All(o => o.RequestState is OperationRequestState.Reconciling
                            or OperationRequestState.TerminalRejected or OperationRequestState.NotSelected
                            or OperationRequestState.TerminalCompleted)) return; // 确定不会进入 Accepted=无需回写
                    var ready = current.Where(o => o.RequestState == OperationRequestState.Accepted).ToList();
                    var transitioning = current.Any(o => o.RequestState is OperationRequestState.Queued
                        or OperationRequestState.InRound or OperationRequestState.Granted or OperationRequestState.Sending); // RetryableRejected=已定拒绝（不再转入 Accepted），不阻塞同胞回写
                    if (ready.Count > 0 && !transitioning) { accepted = ready; break; }
                    await Task.Delay(10).ConfigureAwait(false);
                }

                if (accepted is null)
                {
                    _log?.Invoke("[任务中心] 仲裁操作终局回写放弃：受理管线未在有界窗口内收敛（保守留待对账）。");
                    return;
                }

                foreach (var op in accepted)
                {
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
            }
            catch (Exception ex)
            {
                _log?.Invoke("[任务中心] 仲裁操作终局回写异常（保守留待对账）：" + ex.Message);
            }
        });
    }
}
