using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    /// <summary>[A4.3] reconcile 循环的本拍唤醒器：job.* 事件到达即 TrySetResult（快速路径），
    /// 帧丢失由 10s 节拍 pull 兜底自愈。批次线程每拍重建，事件线程只 TrySetResult，无锁安全。</summary>
    private volatile TaskCompletionSource? _batchWakeSignal;

    /// <summary>[P1b] 策略收尾恰好一次守卫：已完成收尾的最高批次 generation（单调不减，代序号比对）。
    /// 新一轮 AllReady 不重置——重置会让旧轮次的 10s 恢复定时器在新一轮里重跑收尾。</summary>
    private int _teardownDoneGeneration;

    /// <summary>互斥锁：防止两轮 AllReady 并发执行 OnAllReadyConfirmedInternal（patterns §31）。</summary>
    private int _isAllReadyProcessing;

    /// <summary>
    /// [P1b] 策略收尾恰好一次守卫：批次正常末尾 / 批次内 F11 取消分支 / 10s 恢复定时器三个入口互斥，
    /// 同一轮上线锄地（按批次 generation 代序号比对）只执行一次——恢复定时器由 autoHoeingRunning
    /// 边沿触发，与批次末尾收尾可能同时命中，RunSpecified 策略双执行会重复启动指定任务（实机事故）。
    /// 守卫单调不减、新一轮 AllReady 不重置（重置会让旧轮次迟到的恢复定时器在新一轮里重跑收尾）；
    /// generation ≤ 0 的异常批次不进门，保持原行为直接执行。
    /// </summary>
    private async Task ApplyPolicyTeardownOnceAsync(OnlineHoeingBatch batch, string executedDesc, bool userCancelled)
    {
        lock (_teardownGate)
        {
            if (batch.Generation > 0 && _teardownDoneGeneration >= batch.Generation) return;
            if (batch.Generation > _teardownDoneGeneration) _teardownDoneGeneration = batch.Generation;
        }
        if (_commandExecutor != null)
        {
            await _commandExecutor.ApplyPolicyTeardownAsync(SnapshotOnlineHoeingPolicy(), executedDesc, userCancelled, AddLog);
            // 记录实际执行时间：10s 恢复定时器的无批次键直接路径据此跳过 30s 内的迟到边沿
            lock (_teardownGate) { _lastTeardownUtc = DateTime.UtcNow; }
        }
    }

    private async Task OnAllReadyConfirmedInternal(int generation)
    {
        // 互斥锁：防止两轮 AllReady 并发执行 OnAllReadyConfirmedInternal（patterns §31）
        if (Interlocked.CompareExchange(ref _isAllReadyProcessing, 1, 0) != 0)
        {
            AddLog("[上线探针] OnAllReadyConfirmedInternal 已在执行中，跳过");
            return;
        }
        // [P1] 幂等保护收口进状态机：同一 generation 只处理一次（防止 async void 并发或重复广播）；
        // 通过后状态 Armed → Executing
        if (!_intentLifecycle.TryBeginAllReady(generation))
        {
            _isAllReadyProcessing = 0;
            return;
        }

        // [P1b] 新一轮到达而上一轮批次仍存活：先取消旧批次（CTS.Cancel + 短超时等退出）再启动新批次，
        // 杜绝两轮 AllReady 批次并发交错（原 fire-and-forget 无句柄可寻，旧批次不可取消）
        OnlineHoeingBatch? previous;
        lock (_batchGate) { previous = _activeBatch; }
        if (previous is { IsAlive: true })
        {
            AddLog($"新一轮 AllReady（generation={generation}）到达，取消上一轮未完成批次（generation={previous.Generation}）");
            previous.Cancel();
            if (previous.RunTask != null)
            {
                try
                {
                    await previous.RunTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                    // 超时/取消/异常均继续：旧批次残余会在下一次迭代检查点读到令牌后自行退出
                }
            }
        }
        var batch = new OnlineHoeingBatch(generation);
        lock (_batchGate) { _activeBatch = batch; }

        // 获取绑定的联机配置组列表
        var groupNames = _config?.OnlineHoeingGroupNames ?? [];
        var groupIndex = _config?.OnlineHoeingGroupIndex ?? 0;
        var groupName = (groupIndex >= 0 && groupIndex < groupNames.Count) ? groupNames[groupIndex] : null;

        if (string.IsNullOrEmpty(groupName))
        {
            _isAllReadyProcessing = 0;
            AddLog("未绑定联机锄地配置组，无法启动联机锄地");
            // [P1b] 早退也要复位状态机（Executing → Idle），否则生命周期卡在 Executing 永不回收
            _intentLifecycle.OnBatchFinished(generation, "未绑定联机锄地配置组");
            return;
        }

        // 检查 CommandExecutor 是否可用（依赖 BgiPath 配置）
        if (_commandExecutor == null)
        {
            _isAllReadyProcessing = 0;
            AddLog("CommandExecutor 不可用（BgiPath 未配置），无法通过 IPC 启动 BGI 配置组");
            if (_processMonitor != null)
            {
                // [A4.4] 废弃 --startGroups 命令行直启（对 ext 队列/注册表不可见的黑盒执行，
                // 是"已下发当已完成"事故的温床）。只裸拉起 BGI；执行声明权回归 IPC/reconcile——
                // 请配置 BGI 路径后重新触发，IPC 通道可用时批次循环会自动接管全部绑定组。
                AddLog("尝试裸拉起 BGI（不带执行参数）；配置 BGI 路径启用 IPC 后，批次将由 reconcile 循环重新声明执行");
                _processMonitor.RestartBgi(null);
            }
            else
            {
                AddLog("_processMonitor 也为 null，无法启动 BGI。请在设置页配置 BGI 路径");
            }
            // [P1b] 批次未启动的统一复位出口（Executing → Idle）
            _intentLifecycle.OnBatchFinished(generation, $"CommandExecutor 不可用，AllReady 批次未启动（generation={generation}）");
            _ = ReportStatusAsync();
            return;
        }

        // 先 task.suspend 中断当前任务
        var suspendResult = await _commandExecutor.ExecuteSuspendAsync(groupName);
        if (suspendResult.Status != "success")
        {
            // suspend 失败（典型场景：BGI 未运行，IPC 连接超时）。
            // 不要在此 KillBgi + RestartBgi 单组并 return —— 那样冷启动只执行第一个绑定组就停，
            // 其余绑定组永远不会执行（本 bug 根因）。直接落入下方批次循环：reconcile 路径由对账循环
            // 逐拍重试声明；v2 老路径下首个 start_group 的 Connect 失败会在 CommandExecutor 内
            // 触发裸拉起回退（A4.4：不带执行参数，拉起后本方法内重试 IPC）；
            // 若 BGI 实际在运行而 suspend 仅业务失败，也不会误杀进程。
            AddLog($"task.suspend 未成功（{suspendResult.Message}），继续走批次启动流程（IPC 不通时由批次回退统一裸拉起 BGI 后经 IPC 执行）");
        }

        // 等待 BGI 内部的 CancellationContext 取消状态传播完毕，避免取消令牌残留影响后续 start_group
        // [P1-C 止血] 固定 1500ms 盲等改为轮询 IPC task.status（200ms 间隔、上限 6s）：
        // 确认 BGI 无任务在运行（或中断上下文已就位 hasSuspendedTaskContext=true）后再进入批次 task.start。
        // 超时仅记警告日志后继续，保持原有容错语义。
        // [切片4/切片7] settle 判定已抽为 CommandExecutor.WaitTaskSlotSettledAsync 共享方法
        // （ext slotReleased 事件 + 快照探测 + 200ms×30 轮询兜底），按键抢占路径复用同一实现。
        await _commandExecutor.WaitTaskSlotSettledAsync("[上线探针]", AddLog);

        // [P1b] 依次执行所有绑定的配置组（批次有主句柄：取消语义走 batch.Cts，原共享 bool 已废弃）
        batch.RunTask = Task.Run(async () =>
        {
            try
            {
                // [A4.3] capability 门控：新 BGI（hello 声明 job.registry）走 reconcile 对账循环
                // （pull=ext.job.list 事实源 + job.* 事件唤醒）；老 BGI 无能力位，走原阻塞式
                // for 循环（逐字节保留，单机/旧版零回归）。
                // [fix 2026-09-13] 本批次是否有任何绑定组真正被 BGI 接受启动：
                // 全部未启动（如绑定组在 BGI 侧不存在被业务拒绝）时"锄地完成"前提不成立，
                // 下方 RunSpecified 收尾必须跳过（否则空批次凭空启动指定任务——实机事故）。
                var anyItemStarted = false;
                var extClient = _externalClient;
                if (extClient is { State: BgiExternalLinkState.Ready }
                    && extClient.HasCapability(BgiExternalClient.CapabilityJobRegistry))
                {
                    anyItemStarted = await RunBatchReconcileLoopAsync(batch, extClient, groupNames, generation);
                }
                else
                {
                    for (int i = 0; i < groupNames.Count; i++)
                    {
                        // 取消检查点：外部"用户手动停止"（OnStop）或新一轮 AllReady 顶替都会置令牌
                        if (batch.IsCancellationRequested)
                        {
                            break;
                        }
                        var currentGroup = groupNames[i];
                        var groupType = (_config?.OnlineHoeingGroupTypes?.Count > i)
                            ? _config.OnlineHoeingGroupTypes[i]
                            : "group";
                        var isOneClick = groupType == "onedragon";
                        var startCmd = new RemoteCommand
                        {
                            Cmd = isOneClick ? "start_oneclick" : "start_group",
                            Params = new Dictionary<string, object>
                            {
                                { isOneClick ? "configName" : "groupName", currentGroup },
                                { "startFromIndex", 0 },
                                { "generation", generation },
                                { "batchGroupNames", string.Join(",", groupNames) }
                            }
                        };
                        var startResult = await _commandExecutor.ExecuteAsync(startCmd);
                        if (startResult.Status == "cancelled")
                        {
                            // 区分取消来源：令牌已被外部置位（新一轮 AllReady 顶替 / OnStop 手动停止），
                            // 说明取消不是用户 F11——跳过 userCancelled:true 收尾直接退出，
                            // 否则会把新轮刚建立的中断上下文清掉
                            if (batch.IsCancellationRequested)
                            {
                                AddLog("批次被新轮/手动停止取消，跳过 F11 收尾");
                                break;
                            }
                            batch.Cancel(); // 用户 F11 取消：记入批次令牌，阻止批次末尾再走策略收尾
                            // 用户 F11 取消永远压过配置策略：清上下文，不恢复、不启动指定任务
                            // [P1b] 走恰好一次守卫：10s 恢复定时器若同时命中则跳过
                            await ApplyPolicyTeardownOnceAsync(batch, "联机锄地配置组", userCancelled: true);
                            break;
                        }
                        if (startResult.Status != "success")
                        {
                            AddLog($"启动配置组 \"{currentGroup}\" 失败，跳过");
                            continue;
                        }
                        anyItemStarted = true;
                        if (batch.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                // [P1b] 批次正常结束的统一复位出口（Executing → Idle）
                _intentLifecycle.OnBatchFinished(batch.Generation, $"AllReady 批次执行完毕（generation={generation}）");

                // 执行完所有绑定的配置组后，按"上线锄地策略"处置被中断的原任务
                // （恢复 / 不恢复直接停止 / 不恢复并执行指定任务，收尾逻辑共享 CommandExecutor.ApplyPolicyTeardownAsync）。
                // 此位置在 for 循环全部执行完后，天然覆盖两个场景：
                //   场景A: 绑定配置组是联机锄地（AutoHoeingTask）
                //   场景B: 绑定配置组是普通配置组（如"采集"）
                // 注意：如果配置组已被用户取消（F11），已在 cancelled 分支中按"取消优先"清除中断上下文，
                // 不需要再执行策略收尾（否则会打误导日志/误启动指定任务）
                // [P1b] 走恰好一次守卫：与 10s 恢复定时器互斥（同一代序号只收尾一次）
                if (!batch.IsCancellationRequested)
                {
                    // [fix 2026-09-13] 空批次守卫：全部绑定组都未真正启动（被 BGI 拒绝/不存在）时，
                    // "锄地完成"前提不成立，RunSpecified 收尾（启动指定任务）必须跳过——
                    // 否则定时上线触发后组名失效会凭空启动指定任务（实机事故：空批次后"采集"自动开跑）。
                    // Resume/Stop 不受影响：恢复原任务/清上下文是恢复现场语义，与批次是否空跑无关。
                    // 10s 恢复定时器不会补刀：它由 autoHoeingRunning 边沿触发，空批次从未产生该边沿。
                    if (SnapshotOnlineHoeingPolicy().Policy == TaskConflictPolicy.RunSpecified && !anyItemStarted)
                    {
                        AddLog("[任务冲突策略] 本批次所有绑定组均未启动成功（被 BGI 拒绝/不存在），未实际锄地，"
                               + "跳过「完成后执行指定任务」收尾（防空批次误启动）；请检查绑定的配置组名是否在 BGI 中存在");
                    }
                    else
                    {
                        await ApplyPolicyTeardownOnceAsync(batch, "联机锄地", userCancelled: false);
                    }
                }

                _ = ReportStatusAsync();
            }
            catch (Exception ex)
            {
                AddLog($"依次执行配置组异常: {ex.Message}");
                // [P1b] 批次异常的统一复位出口（Executing → Idle）
                _intentLifecycle.OnBatchFinished(batch.Generation, $"AllReady 批次异常（generation={generation}）");
                _ = ReportStatusAsync();
            }
            finally
            {
                // [P1b] 批次完结后句柄归 null（仅当仍指向本批次）：否则已完结批次残留，
                // 后续手动锄地结束的边沿捕获到旧批次，守卫因 _teardownDoneGeneration 已越代而
                // 直接 return，手动会话的策略收尾被静默吞掉（RunSpecified 下指定任务不启动）
                lock (_batchGate) { if (ReferenceEquals(_activeBatch, batch)) _activeBatch = null; }
            }
        });
        // 注意：互斥锁只覆盖启动窗口（suspend+settle+发起批次），Task.Run 启动后即释放是刻意的——
        // 后台序列可能横跨整个锄地会话，锁全程持有会挡住后续合法轮次；并发第二轮的穿透风险由
        // 服务端"参与者集合消费"（B157，不再残留武装事件幻影触发）+ [P1b] 批次句柄顶替取消兜底。
        _isAllReadyProcessing = 0;
    }

    /// <summary>[A4.3] reconcile 节拍（pull 兜底周期）：job.* 事件帧丢失时 10s 内自愈。</summary>
    private static readonly TimeSpan ReconcileTickInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// [A4.3] 批次 reconcile 对账循环（总计划 §4.5，K8s 控制器风格水平触发）：
    /// 仅当 ext Ready 且对端声明 job.registry 能力时由批次任务调用，替代原阻塞式 for 循环。
    /// 事实源 = ext.job.list 快照 pull（每拍必拉）；job.* 事件只是唤醒快速路径（帧丢失由节拍兜底）。
    /// 推进/取消/终态判定全部集中在 BatchReconcileDecider 纯函数（单测已穷尽），此方法只做
    /// "拉快照 → 决策 → 应用动作（副作用）"三件事。
    /// 收尾语义与旧循环逐条对齐：F11 取消 → AbortUserCancelled → teardown(userCancelled:true)；
    /// 外部取消（新轮顶替/手动停止）→ 令牌退出、不收尾；全部终态确认 → Complete → 由调用方走策略收尾。
    /// 返回值：本批次是否有任何期望项真正被 BGI 接受启动（空批次守卫的输入，fix 2026-09-13）。
    /// </summary>
    private async Task<bool> RunBatchReconcileLoopAsync(
        OnlineHoeingBatch batch, BgiExternalClient ext, IReadOnlyList<string> groupNames, int generation)
    {
        // 期望清单：串行语义（同时至多一项在飞），状态推进由 BatchReconcileDecider 决策
        var items = new List<BatchExpectedItem>(groupNames.Count);
        for (var i = 0; i < groupNames.Count; i++)
        {
            var t = (_config?.OnlineHoeingGroupTypes?.Count > i) ? _config.OnlineHoeingGroupTypes[i] : "group";
            items.Add(new BatchExpectedItem(groupNames[i], t == "onedragon"));
        }
        var batchGroupNames = string.Join(",", groupNames);

        BgiEpoch? epoch = null; // 首个成功快照捕获；之后帧间比对识别 BGI 重启
        var snapshotFailLogged = false;
        AddLog($"[reconcile] 批次启动（generation={generation}，期望 {items.Count} 项；pull=ext.job.list，push=job.* 事件唤醒）");

        while (!batch.IsCancellationRequested)
        {
            // 本拍唤醒器先于拉快照建立：拉取期间到达的 job.* 事件也计入本拍（漏唤醒最多拖到节拍兜底）
            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _batchWakeSignal = wake;

            BgiJobListSnapshot? snapshot = null;
            try
            {
                snapshot = await ext.QueryJobListAsync(batch.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                return false; // 外部取消：不收尾（与旧循环 break 同语义）
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException
                                       or TimeoutException or JsonException)
            {
                snapshot = null; // 通道瞬态失败：下一拍重试，绝不误判
            }

            if (snapshot is null)
            {
                if (!snapshotFailLogged)
                {
                    AddLog("[reconcile] ext.job.list 拉取失败（通道瞬态），节拍重试中");
                    snapshotFailLogged = true;
                }
            }
            else
            {
                if (snapshotFailLogged)
                {
                    AddLog("[reconcile] ext.job.list 拉取恢复");
                    snapshotFailLogged = false;
                }

                var epochMatch = snapshot.Epoch is not null && epoch is not null
                                 && snapshot.Epoch.ProcessId == epoch.ProcessId
                                 && snapshot.Epoch.StartTicksUtc == epoch.StartTicksUtc;
                if (epoch is null)
                {
                    epoch = snapshot.Epoch; // 首拍捕获，不构成纪元变化
                    epochMatch = true;
                }
                else if (!epochMatch)
                {
                    AddLog($"[reconcile] BGI 纪元变化（pid {epoch.ProcessId} → {snapshot.Epoch?.ProcessId}），在飞项退回重对账");
                    epoch = snapshot.Epoch;
                }

                // 观察输入：本批 generation 的作业 ∪ 已附着 jobId 的作业（跨代残留也能终态确认）
                var attachedIds = new HashSet<string>(
                    items.Where(it => it.JobId is not null).Select(it => it.JobId!));
                var jobs = snapshot.Jobs
                    .Where(j => j.JobId is not null
                                && (j.Generation == generation || attachedIds.Contains(j.JobId)))
                    .Select(j => new BatchJobObservation(
                        j.JobId!, j.Name, j.Generation, j.State ?? "unknown", j.WasCancelled, j.ErrorCode))
                    .ToList();

                var actions = BatchReconcileDecider.Decide(items, jobs, epochMatch, generation);
                var finished = false;
                foreach (var action in actions)
                {
                    switch (action)
                    {
                        case BatchReconcileAction.EpochChanged:
                            foreach (var it in items.Where(it => it.State == BatchItemState.Submitted))
                            {
                                it.State = BatchItemState.PendingSubmit;
                                it.JobId = null;
                            }
                            break;
                        case BatchReconcileAction.Attach attach:
                            items[attach.Index].JobId ??= attach.JobId;
                            items[attach.Index].Started = true; // 注册表里存在该作业 = 曾被 BGI 接受启动
                            AddLog($"[reconcile] 按名附着找回作业：「{items[attach.Index].Name}」jobId={attach.JobId}");
                            break;
                        case BatchReconcileAction.Submit submit:
                            await SubmitBatchItemAsync(ext, items[submit.Index], generation, batchGroupNames, batch);
                            break;
                        case BatchReconcileAction.Resubmit resubmit:
                            AddLog($"[reconcile] 「{items[resubmit.Index].Name}」已提交但快照查无此作业"
                                   + $"（jobId={items[resubmit.Index].JobId}），限次重提交");
                            items[resubmit.Index].JobId = null;
                            await SubmitBatchItemAsync(ext, items[resubmit.Index], generation, batchGroupNames, batch);
                            break;
                        case BatchReconcileAction.ConfirmTerminal confirm:
                            items[confirm.Index].State = BatchItemState.TerminalConfirmed;
                            items[confirm.Index].TerminalWasCancelled = confirm.Cancelled;
                            items[confirm.Index].TerminalErrorCode = confirm.ErrorCode;
                            if (!confirm.Cancelled)
                            {
                                AddLog(confirm.ErrorCode is null
                                    ? $"[reconcile] 「{items[confirm.Index].Name}」执行完成"
                                    : $"[reconcile] 「{items[confirm.Index].Name}」执行失败（{confirm.ErrorCode}），继续下一项");
                            }
                            break;
                        case BatchReconcileAction.AbortUserCancelled abort:
                            AddLog($"[reconcile] 「{items[abort.Index].Name}」被用户取消（F11 语义），批次按用户取消收尾");
                            batch.Cancel();
                            // 与旧循环 cancelled 分支同语义：取消压过策略，清上下文，走恰好一次守卫
                            await ApplyPolicyTeardownOnceAsync(batch, "联机锄地配置组", userCancelled: true);
                            finished = true;
                            break;
                        case BatchReconcileAction.Complete:
                            AddLog("[reconcile] 全部期望项终态确认，批次完成");
                            finished = true;
                            break;
                        case BatchReconcileAction.Wait:
                            break;
                    }
                    if (finished)
                    {
                        break;
                    }
                }
                if (finished || batch.IsCancellationRequested)
                {
                    // Complete 收尾：返回是否有项真正启动过（空批次守卫输入）；
                    // 取消/中止路径返回值不被消费（调用方见令牌即跳过收尾）
                    return finished && items.Any(it => it.Started);
                }
            }

            // 等下一拍：job.* 事件唤醒（快速路径）或 10s 节拍超时（兜底）
            try
            {
                await Task.WhenAny(wake.Task, Task.Delay(ReconcileTickInterval, batch.Cts.Token));
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            if (batch.IsCancellationRequested)
            {
                return false; // WhenAny 不会因 Delay 取消而抛，此处统一接外部取消
            }
        }

        return false; // while 条件退出 = 令牌置位，不收尾
    }

    /// <summary>
    /// [A4.3] reconcile 循环的提交副作用（Submit/Resubmit 动作的执行体）：
    /// 入队成功 → 记 jobId（应答缺句柄则留空，下一拍按名附着找回）；业务拒绝/幂等命中 →
    /// 直接记终态（与旧循环"启动失败跳过 / already_executed 按成功"同语义）；通道瞬态失败 →
    /// 保持 Submitted 无 jobId，下一拍附着或限次重提交自愈。一切失败留痕。
    /// </summary>
    private async Task SubmitBatchItemAsync(
        BgiExternalClient ext, BatchExpectedItem item, int generation,
        string batchGroupNames, OnlineHoeingBatch batch)
    {
        item.State = BatchItemState.Submitted;
        item.SubmitAttempts++;
        try
        {
            var submit = await ext.SubmitTaskStartAsync(
                item.IsOneDragon ? null : item.Name,
                item.IsOneDragon ? item.Name : null,
                0, generation, batchGroupNames, batch.Cts.Token);
            if (!submit.Success)
            {
                // 业务拒绝（queue_full 等）：与旧循环"启动失败，跳过"同语义——记失败终态，推进下一项
                AddLog($"[reconcile] 启动「{item.Name}」被 BGI 拒绝（{submit.ErrorCode ?? "unknown"}）："
                       + $"{submit.ErrorMessage ?? "无详情"}，按失败终态跳过");
                item.State = BatchItemState.TerminalConfirmed;
                item.TerminalWasCancelled = false;
                item.TerminalErrorCode = submit.ErrorCode ?? "rejected";
                return;
            }
            if (submit.Status == "already_executed")
            {
                AddLog($"[reconcile] 「{item.Name}」幂等命中 already_executed（generation={generation}），按成功终态确认");
                item.State = BatchItemState.TerminalConfirmed;
                item.TerminalWasCancelled = false;
                item.Started = true; // 幂等命中 = 该 generation 已执行过
                return;
            }
            item.Started = true; // 提交被 BGI 接受（即便应答缺 taskHandle 也算已启动）
            if (!string.IsNullOrEmpty(submit.TaskHandle))
            {
                item.JobId = submit.TaskHandle;
                AddLog($"[reconcile] 「{item.Name}」已下发 jobId={item.JobId} status={submit.Status}");
            }
            else
            {
                AddLog($"[reconcile] 「{item.Name}」下发应答缺 taskHandle，下一拍按名附着找回");
            }
        }
        catch (OperationCanceledException)
        {
            // 外部取消：保持 Submitted，循环顶部统一退出
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                   or TimeoutException or JsonException)
        {
            AddLog($"[reconcile] 「{item.Name}」下发通道瞬态失败：{ex.Message}，下一拍按名附着/限次重提交自愈");
        }
    }
}
