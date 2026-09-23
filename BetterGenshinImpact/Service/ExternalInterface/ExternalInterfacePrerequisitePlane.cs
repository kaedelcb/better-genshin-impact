using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.OneDragon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// R4.6 E1'/B3 前置/收尾操作面（ext.prerequisite.account / ext.prerequisite.redeemCode / ext.terminal.completionAction）。
/// 三操作作业化：经 BgiTaskCoordinator 入队（受理回执 = taskHandle；统一注册表 JobKind.Prerequisite/Terminal；
/// 状态经 ext.job.status 对账；取消经 ext.task.cancel），执行段在持有执行权（ExecutionScope + 任务槽位）期间
/// 完成全部副作用。严格合同 v1 由 ExecutionRequestContract 强制（缺版本/缺身份/缺参数均在副作用前拒绝）。
/// 能力协商：ExternalInterfaceProtocol capabilities 四新位（缺省即不支持）。
/// </summary>
internal static class ExternalInterfacePrerequisitePlane
{
    // 静态类不能作 GetLogger<T> 类型参数（CS0718）——经 ILoggerFactory 取命名 logger
    private static readonly ILogger _logger = App.ServiceProvider.GetService<ILoggerFactory>()!.CreateLogger(typeof(ExternalInterfacePrerequisitePlane).FullName!);

    /// <summary>派发入口（ExternalInterfaceCommandPlane 调用）。合同校验 → 入队 → 受理回执。</summary>
    public static InstanceIpcEnvelope Dispatch(InstanceIpcEnvelope request)
    {
        if (ExecutionRequestContract.Validate(request) is { } invalid)
            return invalid;
        var identity = ExecutionRequestContract.ReadIdentity(request.Data)!;
        var data = request.Data ?? new JObject();
        var submission = new BgiTaskCoordinator.TaskSubmission(0, null, null, 0,
            (handle, token) => RunOpAsync(request.Operation, data, identity, handle, token))
        {
            IdempotencyKey = InstanceIpcProtocol.GetStringOrNull(data, "idempotencyKey"),
            Identity = identity,
            RegistryKind = request.Operation == ExternalInterfaceOperations.TerminalCompletionAction
                ? JobKind.Terminal : JobKind.Prerequisite,
            RegistryName = request.Operation[ExternalInterfaceOperations.Prefix.Length..],
            ProjectRegistryOutcome = true,
        };
        var result = BgiTaskCoordinator.Instance.Submit(submission);
        return result.Status switch
        {
            // E3'/B3：受理回执 = accepted（句柄对账）；执行结果走注册表终态，发送 ≠ 完成
            BgiTaskCoordinator.SubmitStatus.Queued or BgiTaskCoordinator.SubmitStatus.Adopted =>
                InstanceIpcEnvelope.Response(request, new { status = "accepted", taskHandle = result.TaskHandle.ToString("N") }),
            BgiTaskCoordinator.SubmitStatus.AlreadyExecuted =>
                InstanceIpcEnvelope.Response(request, new { status = "already_executed" }),
            BgiTaskCoordinator.SubmitStatus.QueueFull =>
                InstanceIpcEnvelope.Failure(request, "queue_full", "任务队列已满，前置/收尾操作未受理"),
            _ => InstanceIpcEnvelope.Failure(request, "service_unavailable", "任务协调器不可用"),
        };
    }

    /// <summary>
    /// 作业化执行体（B3）：认领协调器登记的 Queued 作业 → 取得执行权（ExecutionScope + 任务槽位）→
    /// 受控执行（取消/失败/成功分别表达，受控错误码）→ 终态登记。返回 true = 执行中被取消。
    /// </summary>
    private static async Task<bool> RunOpAsync(string operation, JObject data, JobExecutionIdentity identity,
        Guid handle, CancellationToken token)
    {
        var isTerminal = operation == ExternalInterfaceOperations.TerminalCompletionAction;
        var kind = isTerminal ? JobKind.Terminal : JobKind.Prerequisite;
        var name = operation[ExternalInterfaceOperations.Prefix.Length..];
        var registry = JobRegistry.Instance;
        // A2.4 纪律：协调器入队时已建 Queued（handle==jobId 别名）；缺失则退化新建
        if (registry.Query(handle) == null)
            registry.Submit(kind, name, JobSource.Ext, null,
                InstanceIpcProtocol.GetStringOrNull(data, "idempotencyKey"), jobId: handle, identity: identity);

        ExecutionScope scope;
        try
        {
            scope = ExecutionScope.Start(new JobDescriptor(kind, name, JobSource.Ext, JobId: handle,
                WorkflowRunId: identity.WorkflowRunId, NodeId: identity.NodeId, Iteration: identity.Iteration,
                Occurrence: identity.Occurrence, Attempt: identity.Attempt));
        }
        catch (InvalidOperationException ex)
        {
            registry.TryMarkTerminal(handle, JobState.Rejected, JobErrorCodes.TaskBusy, ex.Message, false);
            return false;
        }
        using (scope)
        {
            var hasLock = await TaskControl.TaskSemaphore.WaitAsync(0);
            if (!hasLock)
            {
                registry.TryMarkTerminal(handle, JobState.Rejected, JobErrorCodes.TaskBusy, "任务槽位被占用，未执行", false);
                return false;
            }
            try
            {
                registry.TryMarkRunning(handle);
                string? failureCode = null;
                string? failureMessage = null;
                string? successDetail = null;
                var cancelled = false;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, scope.Token);
                using var reg = token.Register(() => scope.Cancel());
                try
                {
                    successDetail = operation switch
                    {
                        ExternalInterfaceOperations.PrerequisiteAccount => await RunAccountBodyAsync(data, linked.Token),
                        ExternalInterfaceOperations.PrerequisiteRedeemCode => await RunRedeemBodyAsync(data, linked.Token),
                        _ => await RunTerminalBodyAsync(data, handle, linked.Token),
                    };
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (AccountMismatchException ex) { failureCode = JobErrorCodes.AccountMismatch; failureMessage = ex.Message; }
                catch (PrerequisiteFailedException ex) { failureCode = ex.Code; failureMessage = ex.Message; }
                catch (Exception ex) { failureCode = JobErrorCodes.TaskStartFailed; failureMessage = ex.GetBaseException().Message; }

                if (cancelled)
                {
                    // OCE 不证明切号／兑换／收尾副作用未发生；保留原编号待核查。
                    registry.TryMarkUnknown(handle, "result_unknown", "cancelled_effect_unconfirmed");
                    return false;
                }
                if (failureCode != null)
                {
                    _logger.LogWarning("[{Op}] 作业失败：{Code} {Message}", name, failureCode, failureMessage);
                    if (failureCode is JobErrorCodes.PrerequisiteOutcomeUnknown or JobErrorCodes.TaskStartFailed)
                        registry.TryMarkUnknown(handle, failureCode, failureMessage);
                    else
                        registry.TryMarkTerminal(handle, JobState.Failed, failureCode, failureMessage, false);
                    return false;
                }
                if (isTerminal)
                {
                    // 动作返回不等于独立效果证明，尤其软件退出／关机不能由自身确认。
                    registry.TryMarkUnknown(handle, "result_unknown", successDetail);
                    return false;
                }
                registry.TryMarkTerminal(handle, JobState.Succeeded, null, successDetail, false);
                return false;
            }
            finally
            {
                TaskControl.TaskSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// prerequisite.account（D8/E2-5/E2-9）：切号 → UID 后置严格复验，两段在持有执行权期间连续完成。
    /// 账号上下文 = 请求载荷快照（I5 修订：uid/bindingCode 来自流程策略参数，本会话冻结，不读可变配置）。
    /// </summary>
    private static async Task<string?> RunAccountBodyAsync(JObject data, CancellationToken ct)
    {
        var uid = InstanceIpcProtocol.GetStringOrNull(data, "uid")!;
        var bindingCode = InstanceIpcProtocol.GetStringOrNull(data, "bindingCode");
        var capability = new OneDragonAccountCapability(); // I6：每操作新实例，临时状态天然隔离
        var switched = await capability.SwitchAccountAsync(uid, bindingCode, ct);
        if (!switched)
            // I4：切号结果不明（可能已切换但未进主界面）——不自动重复副作用，按受控 unknown 失败处置
            throw new PrerequisiteFailedException(JobErrorCodes.PrerequisiteOutcomeUnknown, "切号完成确认失败（结果不明，不自动重试）");
        var verify = await capability.VerifyUidStrictAsync(uid, ct);
        if (!verify.Matched)
            throw new AccountMismatchException(verify.Reason ?? "切号后 UID 后置验证不符");
        _logger.LogInformation("[prerequisite.account] 切号+UID 后置验证完成：{Uid}", Helpers.SensitiveTextMask.MaskUid(uid));
        return "switched_and_verified";
    }

    /// <summary>
    /// prerequisite.redeemCode（D8/E2-4'/E2-7）：自身副作用前先验当前账号（仅 redeem 策略节点也受保护）→
    /// 节点准备月卡检查（幂等安全）→ 严格兑换（真实结果/取消传播/失败不写当日标记）。
    /// </summary>
    private static async Task<string?> RunRedeemBodyAsync(JObject data, CancellationToken ct)
    {
        var uid = InstanceIpcProtocol.GetStringOrNull(data, "uid")!;
        var capability = new OneDragonAccountCapability();
        var verify = await capability.VerifyUidStrictAsync(uid, ct);
        if (!verify.Matched)
            throw new AccountMismatchException("兑换前账号验证不符：" + verify.Reason);
        await new BlessingOfTheWelkinMoonTask().Start(ct);
        var result = await capability.CheckAndRedeemCodeStrictAsync(uid, ct);
        if (result.Status == "failed")
            throw new PrerequisiteFailedException(JobErrorCodes.PrerequisiteFailed, result.Reason ?? "兑换失败");
        if (result.Status is not ("disabled" or "alreadyCheckedToday" or "noNewCodes" or "redeemed"))
            throw new PrerequisiteFailedException(JobErrorCodes.PrerequisiteOutcomeUnknown,
                "兑换结果未列入可确认词表，不自动重试：" + result.Status);
        // disabled / alreadyCheckedToday / noNewCodes / redeemed 均为真实结果放行语义，明细随终态 message 回执
        return $"{result.Status}:{result.SubmittedCount}/{result.CandidateCount}";
    }

    /// <summary>
    /// terminal.completionAction（E3'/B8）：动作前校验（无其他活动作业，迟到收尾不误伤新任务；epoch 已由合同把关）→
    /// 不可逆提交点（破坏性动作先预登 Succeeded 再执行，进程自杀类允许 unknown，禁止补发）。
    /// </summary>
    private static async Task<string?> RunTerminalBodyAsync(JObject data, Guid handle, CancellationToken ct)
    {
        var action = InstanceIpcProtocol.GetStringOrNull(data, "action")!;
        if (JobRegistry.Instance.Snapshot().Any(j => !j.IsTerminal && j.JobId != handle))
            throw new PrerequisiteFailedException(JobErrorCodes.TerminalConflict, "存在其他活动作业，收尾拒绝执行");

        ct.ThrowIfCancellationRequested();
        // 当前没有可核查的耐久提交／效果证明；动作前只能记非终态意图。
        JobRegistry.Instance.TryMarkUnknown(handle, "result_unknown", "effect_unconfirmed:" + action);

        _logger.LogInformation("[terminal.completionAction] 执行收尾动作：{Action}", action);
        switch (action)
        {
            case "closeGame":
                SystemControl.CloseGame();
                break;
            case "closeSoftware":
                await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                break;
            case "closeGameAndSoftware":
                SystemControl.CloseGame();
                await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
                break;
            case "shutdown":
                SystemControl.CloseGame();
                SystemControl.Shutdown();
                break;
        }
        return "executed:" + action;
    }
}
