using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

/// <summary>R4.6 Batch 3b 合同夹具：三新操作严格校验 / 身份扩展 / suppress+expectedUid 读取 / 写操作集 / Skipped 终态守卫。</summary>
internal static class R46PrerequisiteContractRegression
{
    private static void Must(bool value, string message) { if (!value) throw new Exception(message); }

    private static JObject StrictData(string op, params (string Key, object? Value)[] extra)
    {
        var data = new JObject
        {
            ["executionContractVersion"] = 1,
            ["bgiEpoch"] = new JObject
            {
                ["processId"] = JobRegistry.CurrentEpoch.ProcessId,
                ["startTicksUtc"] = JobRegistry.CurrentEpoch.StartTicksUtc,
            },
            ["idempotencyKey"] = Guid.NewGuid().ToString("N"),
            ["expiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            ["workflowRunId"] = Guid.NewGuid().ToString("N"),
            ["nodeId"] = op.Contains("terminal") ? "$flow" : "node-1",
            ["iteration"] = 0,
        };
        foreach (var (k, v) in extra) data[k] = v == null ? JValue.CreateNull() : JToken.FromObject(v);
        return data;
    }

    private static InstanceIpcEnvelope Req(string op, JObject? data) => InstanceIpcEnvelope.Request(op, data);
    private static string? RejectCode(InstanceIpcEnvelope r) => ExecutionRequestContract.Validate(r)?.ErrorCode;

    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        await check("R46-T1 三新操作缺合同版本即拒（capability_required）", async () => {
            foreach (var op in new[] { "ext.prerequisite.account", "ext.prerequisite.redeemCode", "ext.terminal.completionAction" })
            {
                var data = StrictData(op, ("uid", "100000001"), ("action", "closeGame"));
                data.Remove("executionContractVersion");
                Must(RejectCode(Req(op, data)) == "capability_required", op + " must require contract v1");
            }
        });
        await check("R46-T2 缺流程身份即拒（invalid_request）", async () => {
            var data = StrictData("ext.prerequisite.account", ("uid", "100000001"));
            data.Remove("workflowRunId");
            Must(RejectCode(Req("ext.prerequisite.account", data)) == "invalid_request", "identity required");
        });
        await check("R46-T3 前置操作缺 uid 即拒；空 uid 即拒", async () => {
            Must(RejectCode(Req("ext.prerequisite.account", StrictData("ext.prerequisite.account"))) == "invalid_request", "uid required");
            Must(RejectCode(Req("ext.prerequisite.redeemCode", StrictData("ext.prerequisite.redeemCode", ("uid", "  ")))) == "invalid_request", "blank uid rejected");
        });
        await check("R46-T4 收尾动作词表校验（非法即拒，四档合法放行）", async () => {
            Must(RejectCode(Req("ext.terminal.completionAction", StrictData("ext.terminal.completionAction", ("action", "reboot")))) == "invalid_request", "bad action rejected");
            foreach (var action in new[] { "closeGame", "closeSoftware", "closeGameAndSoftware", "shutdown" })
                Must(ExecutionRequestContract.Validate(Req("ext.terminal.completionAction", StrictData("ext.terminal.completionAction", ("action", action)))) == null, action + " must pass");
        });
        await check("R46-T5 合法前置载荷通过校验（account 含 bindingCode 可选）", async () => {
            Must(ExecutionRequestContract.Validate(Req("ext.prerequisite.account", StrictData("ext.prerequisite.account", ("uid", "100000001"), ("bindingCode", "13800138000")))) == null, "account valid");
            Must(ExecutionRequestContract.Validate(Req("ext.prerequisite.redeemCode", StrictData("ext.prerequisite.redeemCode", ("uid", "100000001")))) == null, "redeem valid");
        });
        await check("R46-T6 occurrence/attempt add-only 映射进身份", async () => {
            var data = StrictData("ext.prerequisite.account", ("uid", "100000001"), ("occurrence", 2), ("attempt", 1));
            var id = ExecutionRequestContract.ReadIdentity(data);
            Must(id is { Occurrence: 2, Attempt: 1 }, "occurrence/attempt mapped");
            var legacy = StrictData("ext.prerequisite.account", ("uid", "100000001"));
            Must(ExecutionRequestContract.ReadIdentity(legacy) is { Occurrence: null, Attempt: null }, "legacy null");
        });
        await check("R46-T7 suppress/expectedUid 读取器（缺省 false/null）", async () => {
            var data = StrictData("ext.task.start", ("suppressConfigCompletionAction", true), ("expectedUid", "100000001"));
            Must(ExecutionRequestContract.ReadSuppressConfigCompletionAction(data), "suppress true");
            Must(ExecutionRequestContract.ReadExpectedUid(data) == "100000001", "expectedUid read");
            Must(!ExecutionRequestContract.ReadSuppressConfigCompletionAction(new JObject()), "default false");
            Must(ExecutionRequestContract.ReadExpectedUid(new JObject()) == null, "default null");
        });
        await check("R46-T8 三新操作纳入写操作集", async () => {
            Must(ExternalInterfaceOperations.IsWriteOperation("ext.prerequisite.account"), "account write");
            Must(ExternalInterfaceOperations.IsWriteOperation("ext.prerequisite.redeemCode"), "redeem write");
            Must(ExternalInterfaceOperations.IsWriteOperation("ext.terminal.completionAction"), "terminal write");
        });
        await check("R46-T9 JobKind 尾部追加数值稳定（Prerequisite=6, Terminal=7）", async () => {
            Must((int)JobKind.Prerequisite == 6 && (int)JobKind.Terminal == 7, "tail append stable");
        });
        await check("R46-T10 Skipped 是合法注册表终态（守卫修复回归）", async () => {
            var registry = new JobRegistry(false);
            var job = registry.Submit(JobKind.Solo, "skip-guard", JobSource.Ext, null, null).Job;
            Must(registry.TryMarkRunning(job.JobId), "running");
            Must(registry.TryMarkTerminal(job.JobId, JobState.Skipped, JobErrorCodes.SkippedNormal, null, false), "skipped terminal accepted");
            Must(registry.Query(job.JobId)!.State == JobState.Skipped, "state persisted");
        });
    }
}
