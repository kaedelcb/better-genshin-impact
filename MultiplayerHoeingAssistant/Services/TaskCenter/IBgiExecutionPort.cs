using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// R5.2 B2-γ 实施前置（设计稿 §12）：执行边界面向 BGI 客户端的最小端口。
///
/// 动机：`BgiExternalClient` 是 sealed 具体类且无注入接缝，而 §12 的组件级验收口径要求
/// 「后继节点经仲裁面**真实**提交 + 断言发送次数」——不起真实 IPC 就无法成立。把边界实际消费的
/// 客户端成员收敛到本端口后，夹具可注入可控实现，生产实现（<see cref="BgiExternalClientPort"/>）
/// 仍然直连客户端、行为不变。
///
/// 纪律：本端口**只做能力/纪元/收发的最薄转发**，不得在此层加入仲裁、重试或状态判定——
/// 判定仍归边界与门面（避免把「谁负责」模糊掉）。
/// </summary>
internal interface IBgiExecutionPort
{
    /// <summary>链路是否 Ready（等价 `client.State == BgiExternalLinkState.Ready`）。</summary>
    bool IsReady { get; }

    /// <summary>能力查询（生产语义含 Ready 前置判定，由实现方保证）。</summary>
    bool HasCapability(string name);

    /// <summary>服务端进程纪元（未连接=null；保留原始对象以便线协议载荷按原样编码 processId/startTicksUtc）。</summary>
    BgiEpoch? ServerEpoch { get; }

    /// <summary>发送一次 ext 命令（不做重试、不做结果解释）。</summary>
    Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct);

    /// <summary>查询作业注册表快照（不确定发送后的对账用）。</summary>
    Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct);

    /// <summary>按作业号查询状态（终态轮询用）。</summary>
    Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct);

    /// <summary>请求取消自有作业（best-effort，应答不代表清理完成）。</summary>
    Task CancelOwnedTaskAsync(string jobId, CancellationToken ct);
}

/// <summary>生产实现：直连 <see cref="BgiExternalClient"/> 的最薄转发（行为与端口化之前逐字等价）。</summary>
internal sealed class BgiExternalClientPort : IBgiExecutionPort
{
    private readonly BgiExternalClient _client;

    public BgiExternalClientPort(BgiExternalClient client)
        => _client = client ?? throw new System.ArgumentNullException(nameof(client));

    public bool IsReady => _client.State == BgiExternalLinkState.Ready;

    public bool HasCapability(string name) => _client.HasCapability(name);

    public BgiEpoch? ServerEpoch => _client.ServerEpoch;

    public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        => _client.SendCommandAsync(operation, payload, null, ct);

    public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
        => _client.QueryJobListAsync(ct);

    public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        => _client.QueryJobStatusAsync(jobId, ct);

    public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct)
        => _client.CancelOwnedTaskAsync(jobId, ct);
}
