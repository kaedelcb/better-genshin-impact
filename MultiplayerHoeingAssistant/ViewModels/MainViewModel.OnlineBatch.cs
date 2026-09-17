using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{

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
        // Online batches are finalized only by the all-member authority, never by a per-task falling edge.
        if (!batch.CoordinatedSucceeded && !userCancelled) return;
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
                    await previous.RunTask.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (TimeoutException)
                {
                    _isAllReadyProcessing = 0;
                    _intentLifecycle.OnBatchFinished(generation, "旧批次清理未完成，拒绝新批次");
                    NotifyBatchLoud("联机批次未启动", "旧批次尚未退出清理，禁止启动新批次；请检查 BGI 状态。");
                    return;
                }
                catch (Exception ex)
                {
                    AddLog("[全队批次] 旧批次已结束: " + ex.Message);
                }
            }
        }
        var batch = new OnlineHoeingBatch(generation);
        lock (_batchGate) { _activeBatch = batch; }

        // 获取绑定的联机配置组列表
        var groupNames = _config?.OnlineHoeingGroupNames?.ToList() ?? [];
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

        // [fix 2026-09-13] 冷启动快速通道：同会话 BGI 进程确定不在（IsBgiRunning 实时枚举，
        // 仅看本进程会话）时，suspend/settle 没有对象可等——suspend 烧满 3s 连接超时、
        // settle 兜底循环烧满 30×(1s+0.2s)≈36s，然后才轮到裸拉起，触发到 BGI 启动白等 ~40s。
        // 进程不存在 = 没有任务可中断、没有槽位可等释放，直接落入批次循环由裸拉起回退接管。
        // 误判风险可控：进程在但管道暂不可达（启动中/忙）时 IsBgiRunning 仍为 true，走原路径。
        if (_processMonitor?.IsBgiRunning == false)
        {
            AddLog("[上线探针] 同会话 BGI 未运行，跳过 suspend/settle 等待，直接走裸拉起批次流程");
        }
        else
        {
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
        // [切片4/切片7] settle 判定已抽为 CommandExecutor.WaitTaskSlotSettledAsync 共享方法
        // （ext slotReleased 事件 + 快照探测 + 200ms×30 轮询兜底），按键抢占路径复用同一实现。
        // [A6 状态确认 ADR-2026-09-16] 超时后复核槽位仍忙 = 旧任务可能卡死：中止本批启动尝试并
        // 响亮告警，不再静默进入批次 task.start（事故教训：每组一行执行失败然后无人知晓）。
        if (!await _commandExecutor.WaitTaskSlotSettledAsync("[上线探针]", AddLog))
        {
            _isAllReadyProcessing = 0;
            NotifyBatchLoud("联机锄地批次中止",
                "BGI 任务槽位在 suspend 后超时未释放（旧任务可能卡死），本批锄地未下发；请检查 BGI 状态（必要时手动停止 BGI 任务后重新触发）");
            _intentLifecycle.OnBatchFinished(generation, $"settle 复核槽位仍忙，批次启动中止（generation={generation}）");
            _ = ReportStatusAsync();
            return;
        }
        }

        // [P1b] 依次执行所有绑定的配置组（批次有主句柄：取消语义走 batch.Cts，原共享 bool 已废弃）
        batch.RunTask = Task.Run(async () =>
        {
            try
            {
                var anyItemStarted = await RunCoordinatedBatchAsync(batch, groupNames);
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
                        await _commandExecutor.ExecuteResumeAsync(cancel: true);
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

    /// <summary>[A6] 抢占链响亮告警收口（ADR-2026-09-16）：用户可见日志 + 托盘气泡。
    /// 批次侧任一段最终失败（settle 中止 / 重试预算耗尽 / F11 冷却跳过）都必须用户可见，
    /// 不允许"每组一行执行失败然后无人知晓"。</summary>
    private void NotifyBatchLoud(string title, string message)
    {
        AddLog($"[告警] {message}");
        try
        {
            (System.Windows.Application.Current as App)?.ShowTrayBalloon(title, message);
        }
        catch
        {
            // 托盘不可用时静默（日志已保底）
        }
    }
}
