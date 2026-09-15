#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
using BetterGenshinImpact.Shared.CooperativeRerun;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;

public sealed class CooperativeRerunSession : IAsyncDisposable
{
    private readonly CoordinatorClient _client;
    private readonly IReadOnlyList<CooperativeRoutePlan> _plans;
    private readonly string _scope;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private readonly string _resumeToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
    private readonly CancellationTokenSource _stop = new();
    private Task _pollTask;
    private readonly object _gate = new();
    private RerunSnapshot _snapshot = new();
    private readonly SemaphoreSlim _rpcGate = new(1, 1);
    /// <summary>在途的死亡标记任务。NormalDone 之前必须排空，否则标记可能晚于计划冻结而丢失。</summary>
    private readonly List<Task> _pendingMarks = new();
    private static readonly Microsoft.Extensions.Logging.ILogger Logger =
        BetterGenshinImpact.App.GetLogger<CooperativeRerunSession>();

    private long _lastRevision;
    private bool _aborted;
    private CooperativeRerunSession(CoordinatorClient client, IReadOnlyList<CooperativeRoutePlan> plans, string scope)
    {
        _client=client; _plans=plans; _scope=scope; _pollTask=Task.CompletedTask;
    }
    public RerunSnapshot Snapshot { get { lock(_gate) return Clone(_snapshot); } }
    private static RerunSnapshot Clone(RerunSnapshot s) => new() { SessionId=s.SessionId, Scope=s.Scope, Stage=s.Stage, Revision=s.Revision, PlanHash=s.PlanHash, Participants=s.Participants.ToList(), Plan=s.Plan.ToList(), CurrentPlanIndex=s.CurrentPlanIndex, CurrentRouteId=s.CurrentRouteId, Marks=s.Marks.Select(m=>new RerunMarkEvent { Sequence=m.Sequence, PlayerUid=m.PlayerUid, RouteId=m.RouteId, PointId=m.PointId, IsReplay=m.IsReplay }).ToList(), ResolvedPoints=s.ResolvedPoints.ToList(), Outcomes=new Dictionary<string,RerunRouteOutcome>(s.Outcomes), Reason=s.Reason };
    public string SessionId => _sessionId;
    public static async Task<CooperativeRerunSession> StartAsync(CoordinatorClient client, IReadOnlyList<CooperativeRoutePlan> plans, string scope, CancellationToken ct)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (!client.SupportsCooperativeRerun) throw new NotSupportedException("Server capability hoeing.rerun.v1 is required; cooperative rerun will not fallback.");
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("World-round scope is required.", nameof(scope));
        var s=new CooperativeRerunSession(client, plans ?? Array.Empty<CooperativeRoutePlan>(), scope);
        client.CooperativeSession=s;
        try { await s.SendAsync(RerunProtocol.Enroll, routes:s._plans.Select(x=>x.Manifest).ToList(), ct:ct); s._pollTask=s.PollLoopAsync(); return s; }
        catch { client.CooperativeSession=null; await s.DisposeAsync(); throw; }
    }
    public CooperativeRouteContext CreateContext(CooperativeRoutePlan plan, bool isReplay) => plan == null ? throw new ArgumentNullException(nameof(plan)) : new(this, plan, isReplay);
    internal async Task<RerunSnapshot> SendAsync(string operation, CooperativeRoutePlan? plan=null, string pointId="", bool replay=false, int through=-1, bool skipRoute=false, RerunRouteOutcome outcome=RerunRouteOutcome.None, string reason="", List<RerunRouteManifest>? routes=null, CancellationToken ct=default)
    {
        if (_aborted && operation != RerunProtocol.Poll) throw new OperationCanceledException("Cooperative rerun session aborted.");
        await _rpcGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        var operationId = Guid.NewGuid().ToString("N");
        var req=new RerunRequest { Operation=operation, OperationId=operationId, SessionId=operation == RerunProtocol.Enroll ? "" : _sessionId, Scope=_scope, ResumeToken=_resumeToken, PlanHash=Snapshot.PlanHash, Routes=routes ?? new(), RouteId=plan?.Manifest.RouteId ?? "", PointId=pointId, IsReplay=replay, ThroughSegment=through, SkipRoute=skipRoute, Outcome=outcome, Reason=reason };
        var delay=100;
        Exception? last=null;
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while(DateTime.UtcNow<deadline && !ct.IsCancellationRequested) { try { var snap=await _client.SendCooperativeRerunAsync(req,ct); if(operation==RerunProtocol.Enroll && !string.IsNullOrEmpty(snap.SessionId)) _sessionId=snap.SessionId; Update(snap); return snap; } catch(Exception ex) when(ex is not OperationCanceledException) { last=ex;
            // 服务端协议拒绝是确定性错误（阶段/计划哈希/线路/未解析/能力/身份），重试同一载荷永远不会成功，
            // 只会白占 20s RPC 门并把真正错误码埋进超时文本。立即失败，保留错误码供上层定位。
            if (ex is BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway.GatewayErrorException gw
                && gw.Code.StartsWith("rerun_", StringComparison.Ordinal))
                throw;
            await Task.Delay(delay,ct); delay=Math.Min(delay*2,1000); } }
        // 取消必须按取消语义上抛：调用方按异常类型区分"用户停止/时间禁区"（Cancelled）与"线路失败"（Failed），
        // 若这里一律抛 TimeoutException，取消会被误分类成失败并污染停止原因与日志。
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"Cooperative rerun {operation} did not receive an acknowledgement.",last);
        }
        finally { _rpcGate.Release(); }
    }
    private void Update(RerunSnapshot snapshot) { lock(_gate) { if(snapshot.SessionId!=_sessionId || snapshot.Scope!=_scope || snapshot.Revision<_lastRevision) return; _lastRevision=snapshot.Revision; _snapshot=snapshot; } }
    private async Task PollLoopAsync()
    { try { while(!_stop.IsCancellationRequested && !_aborted) { try { await SendAsync(RerunProtocol.Poll,ct:_stop.Token); } catch(OperationCanceledException) { break; } catch { } await Task.Delay(750,_stop.Token); } } catch(OperationCanceledException) { } }
    public async Task<RerunSnapshot> CompleteNormalAsync(CancellationToken ct)
    {
        // 先排空在途死亡标记，再宣告正常轮结束：服务端在最后一名成员 NormalDone 时冻结计划，
        // 迟到标记会被拒（rerun_normal_done），导致"该重跑的线路"无声丢失。
        await DrainPendingMarksAsync(ct);
        var snap=await SendAsync(RerunProtocol.NormalDone,ct:ct);
        if(snap.Stage==RerunStage.Completed) return snap;
        // PlanHash 只在"固定全员都提交 NormalDone"那一刻由服务端冻结。非末位成员的 NormalDone 响应
        // 仍处于 Normal（hash 为空），此时直接发 Prepare 必然被 rerun_plan_hash 拒绝。
        // 因此先等到 Preparing（快照已带回冻结 hash），再用该 hash 发 Prepare。
        await WaitForStageAsync(RerunStage.Preparing,ct);
        await SendAsync(RerunProtocol.Prepare,ct:ct);
        return await WaitForStageAsync(RerunStage.Running,ct);
    }
    internal async Task<RerunSnapshot> WaitForStageAsync(RerunStage stage,CancellationToken ct)
    { while(true) { var s=Snapshot; if(s.Stage==RerunStage.Aborted) throw new InvalidOperationException(s.Reason); if(s.Stage==RerunStage.Completed || s.Stage==stage) return s; await Task.Delay(500,ct); await SendAsync(RerunProtocol.Poll,ct:ct); } }
    public async Task CompleteRouteAsync(CooperativeRouteContext context,RerunRouteOutcome outcome,CancellationToken ct)
    {
        // 服务端强制：Incomplete 也必须能验证"每个非战斗规范点都已到达或被豁免"，
        // 否则以 rerun_unresolved 拒绝——而该拒绝会让整轮中止，与"记不完整、其余计划项继续"的
        // 契约目标相反。线路提前结束（切队伍失败/校验失败/中断跳段等）时本机确实没有逐点豁免记录，
        // 因此在提交不完整终态前补一次覆盖性豁免，把"确实没走到"如实登记下来。
        if (outcome == RerunRouteOutcome.Incomplete && !context.HadBypass)
        {
            var unresolved = context.UnresolvedNonFightPoints();
            if (unresolved.Count > 0)
            {
                Logger.LogWarning("[共同重跑] 不完整终态存在 {N} 个未到达规范点，先提交覆盖性豁免: {Points}",
                    unresolved.Count, string.Join(",", unresolved));
                await context.ReportBypassAsync(throughSegment: -1, skipRoute: true, ct);
            }
        }

        // 提交前自检：Incomplete 必须带非空原因；Completed 不接受任何 bypass。
        // 快速失败好过被服务端拒绝后白等 20s（并把整轮拖停）。
        var reason = context.FailureReason ?? "";
        if (outcome == RerunRouteOutcome.Incomplete && string.IsNullOrWhiteSpace(reason))
            reason = "重跑线路未完整执行（客户端未提供原因）";
        if (outcome == RerunRouteOutcome.Completed && context.HadBypass)
            throw new InvalidOperationException("重跑线路存在已豁免的未到达点，终态不能是 Completed");
        await SendAsync(RerunProtocol.RouteDone,context.Plan, outcome:outcome, replay:context.IsReplay, reason:reason,ct:ct);
        await WaitForCurrentRouteAsync(context.RouteId,ct);
    }
    public async Task<RerunSnapshot> WaitForCurrentRouteAsync(string routeId,CancellationToken ct)
    { while(true) { var s=Snapshot; if(s.Stage==RerunStage.Aborted)throw new InvalidOperationException(s.Reason); if(s.Stage is RerunStage.Finishing or RerunStage.Completed || !string.Equals(s.CurrentRouteId,routeId,StringComparison.Ordinal))return s; await Task.Delay(500,ct); await SendAsync(RerunProtocol.Poll,ct:ct); } }
    public async Task FinishAsync(CancellationToken ct)
    {
        var s=Snapshot;
        if(s.Stage==RerunStage.Completed)return;
        if(s.Stage==RerunStage.Aborted)throw new InvalidOperationException(s.Reason);
        if(s.Stage!=RerunStage.Finishing)s=await WaitForStageAsync(RerunStage.Finishing,ct);
        if(s.Stage==RerunStage.Completed)return;
        await SendAsync(RerunProtocol.Finish,ct:ct);
    }
    public async Task AbortAsync(string reason)
    { if(_aborted)return; if(!_client.IsConnected || string.IsNullOrEmpty(_snapshot.SessionId)) { _aborted=true; return; } using var c=new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { await SendAsync(RerunProtocol.Abort,reason:reason,ct:c.Token); } catch { } finally { _aborted=true; } }
    internal void QueueDeathMark(CooperativeRouteContext c,string pointId)
    {
        Task task;
        try
        {
            task = SendAsync(RerunProtocol.Mark,c.Plan,pointId,replay:c.IsReplay,reason:"death",ct:_stop.Token);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[共同重跑] 死亡标记未能发送，该线路可能不会进入重跑计划: {Point}", pointId);
            return;
        }
        lock (_gate) _pendingMarks.Add(task);
        // 失败必须可见：静默丢弃会让"该重跑的线路"无声地不进计划。
        _ = task.ContinueWith(t => Logger.LogWarning(t.Exception,
                "[共同重跑] 死亡标记发送失败，该线路可能不会进入重跑计划: {Point}", pointId),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// 排空在途死亡标记。正常轮结束（NormalDone）之前必须调用：契约要求 normalDone 代表
    /// "本端已停止产生正常轮事件且已排空发送"，否则迟到标记会落在计划冻结之后而被服务端拒绝。
    /// </summary>
    internal async Task DrainPendingMarksAsync(CancellationToken ct)
    {
        Task[] pending;
        lock (_gate) { pending = _pendingMarks.ToArray(); _pendingMarks.Clear(); }
        if (pending.Length == 0) return;
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已经逐条告警；这里不阻断收口，避免单条标记失败把整轮拖停。
            Logger.LogWarning(ex, "[共同重跑] 排空死亡标记存在失败项（已逐条告警），继续收口正常轮");
        }
    }
    internal Task<RerunSnapshot> ArriveAsync(CooperativeRouteContext c,string p,CancellationToken ct)=>SendAsync(RerunProtocol.Arrive,c.Plan,p,c.IsReplay,ct:ct);
    internal Task<RerunSnapshot> FastReportAsync(CooperativeRouteContext c,string p,CancellationToken ct) => ArriveAsync(c,p,ct);
    internal Task<RerunSnapshot> RecoverAsync(CooperativeRouteContext c,string reason,CancellationToken ct)=>SendAsync(RerunProtocol.Recover,c.Plan,replay:c.IsReplay,reason:reason,ct:ct);
    internal Task<RerunSnapshot> BypassAsync(CooperativeRouteContext c,int segment,bool skip,CancellationToken ct)=>SendAsync(RerunProtocol.Bypass,c.Plan,replay:c.IsReplay,through:segment,skipRoute:skip,ct:ct);
    public async ValueTask DisposeAsync(){ _stop.Cancel(); try{await _pollTask;}catch{} if(!_aborted) await AbortAsync("client disposed"); if(ReferenceEquals(_client.CooperativeSession,this))_client.CooperativeSession=null; _stop.Dispose(); }
}
