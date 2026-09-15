#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.Shared.CooperativeRerun;
namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
public sealed class CooperativeRouteContext
{
 private static readonly Microsoft.Extensions.Logging.ILogger Logger =
  BetterGenshinImpact.App.GetLogger<CooperativeRouteContext>();
 private readonly CooperativeRerunSession _session; private readonly HashSet<string> _completed=new(StringComparer.Ordinal); private readonly HashSet<string> _arrivals=new(StringComparer.Ordinal); private readonly HashSet<string> _fastReported=new(StringComparer.Ordinal); private readonly object _gate=new();
 public CooperativeRoutePlan Plan{get;} public bool IsReplay{get;} public string SessionId=>_session.SessionId; public string RouteId=>Plan.Manifest.RouteId; public bool HadIncompleteExecution{get;private set;} public string? FailureReason{get;private set;}
 /// <summary>本线路是否已提交过“明确豁免某范围”。服务端 Completed 不接受 bypass，故它强制终态为 Incomplete。</summary>
 public bool HadBypass{get;private set;}
 /// <summary>
 /// 本阶段是否已被服务端中止（成员掉线租约到期 / 任一方失败或取消 / 经验上限）。
 /// 供执行器在游戏动作循环中同步判定：中止后不得继续跑完剩余路点。
 /// </summary>
 public bool IsAborted => _session.Snapshot.Stage == RerunStage.Aborted;
 internal CooperativeRouteContext(CooperativeRerunSession session,CooperativeRoutePlan plan,bool replay){_session=session;Plan=plan;IsReplay=replay;}
 public string? GetFightPointId(int seg,int wp)=>Plan.FightPointIds.TryGetValue(seg*10000+wp,out var id)?id:null;
 public string BuildFightKey(int seg,int wp)=>$"{RouteId}/{GetFightPointId(seg,wp) ?? $"fight:{seg}:{wp}"}";
 public bool ShouldSkipFight(string pointId){var s=_session.Snapshot; if(s.SessionId!=SessionId||s.Stage!=(IsReplay?RerunStage.Running:RerunStage.Normal))return false; lock(_gate)return !_completed.Contains(pointId)&&s.Marks.Any(x=>x.IsReplay==IsReplay&&x.RouteId==RouteId&&x.PointId==pointId);}
 public void CompleteFight(string pointId){lock(_gate)_completed.Add(pointId);}
 public void MarkIncomplete(string reason){HadIncompleteExecution=true;FailureReason=reason;}
 /// <summary>
 /// 该点是否为计划内的**规范战斗检查点**。只有规范点才能作为死亡标记上报：
 /// 共享战斗用的复合键（RouteId/point）不是检查点身份，服务端会以 rerun_mark 拒绝，
 /// 且永远无法与队友的跳过判据匹配，因此必须在这里拦下而不是发出去。
 /// </summary>
 public bool IsCanonicalFightPoint(string? pointId)
  => !string.IsNullOrEmpty(pointId) && Plan.FightPointIds.ContainsValue(pointId!);
 public void ReportDeath(string? pointId)
 {
  if (!IsCanonicalFightPoint(pointId))
  {
   Logger.LogWarning("[共同重跑] 当前战斗点没有规范检查点身份，跳过死亡标记（该线路可能不会进入重跑计划）: {Point}", pointId ?? "<null>");
   return;
  }
  _session.QueueDeathMark(this, pointId!);
 }
 /// <summary>本机是否已就该点提交过到达（本地记录，用于终态前的覆盖性豁免自检）。</summary>
 public bool HasArrivedAt(string pointId){lock(_gate)return _arrivals.Contains(pointId);}
 /// <summary>计划中属于本线路、但本机从未提交到达的**非战斗**规范点（服务端要求它们到达或被豁免）。</summary>
 public IReadOnlyList<string> UnresolvedNonFightPoints()
  => Plan.Manifest.Checkpoints.Where(p => p.Kind != "fight" && !HasArrivedAt(p.Id)).Select(p => p.Id).ToList();
 public Task WaitAsync(string pointId,CancellationToken ct)=>WaitCore(pointId,ct);
 /// <summary>单点等待上界。远大于正常跨端等待（队员迟到、传送/回点耗时），只用于把"永久挂死"变成显式失败。</summary>
 private static readonly TimeSpan PointWaitBound=TimeSpan.FromMinutes(8);
 private async Task WaitCore(string pointId,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  var _waitStartedUtc=DateTime.UtcNow;
  bool send; lock(_gate)send=_arrivals.Add(pointId);

  if(send)
  {
   try { await _session.ArriveAsync(this,pointId,ct); }
   catch { lock(_gate)_arrivals.Remove(pointId); throw; }
  }
  while(true)
  {
   ct.ThrowIfCancellationRequested();
   var s=_session.Snapshot;
   if(s.Stage==RerunStage.Aborted)throw new InvalidOperationException(s.Reason);
   if(s.SessionId!=SessionId||s.Stage!=RerunStage.Running||s.CurrentRouteId!=RouteId)throw new InvalidOperationException("Cooperative route is no longer active.");
   if(s.ResolvedPoints.Contains(pointId))return;
   // 有界等待：服务端的成员租约只有在"收到请求"时才评估，而本端轮询会不断续租，
   // 因此一旦出现未预料的规范点分歧，纯等待可以无限持续（只能人工停任务）。
   // 这里给出一个远大于正常跨端等待的上界，超时按"本线路未完整"失败并让阶段统一中止——
   // 宁可显式中止，也不要静默挂死。
   if(DateTime.UtcNow-_waitStartedUtc>PointWaitBound)
    throw new TimeoutException($"Cooperative point wait exceeded {PointWaitBound.TotalMinutes:F0} minutes: {pointId}");
   await Task.Delay(500,ct);
  }
 }
 public bool IsFastReported(string pointId){lock(_gate)return _fastReported.Contains(pointId);}
 public Task<RerunSnapshot> WaitForCurrentRouteAsync(CancellationToken ct)=>_session.WaitForCurrentRouteAsync(RouteId,ct);
 public async Task FastReportAsync(string pointId,CancellationToken ct){ct.ThrowIfCancellationRequested();lock(_gate){if(!_fastReported.Add(pointId))return;}try{await _session.FastReportAsync(this,pointId,ct);}catch{lock(_gate)_fastReported.Remove(pointId);throw;}}
 /// <summary>
 /// 上报“本机正在恢复”。只有重跑阶段（服务端 Running）才允许该操作；正常轮服务端只接受 Mark，
 /// 若在正常轮发送 Recover 会被 rerun_stage 拒绝（旧实现会白等 20s，未包裹调用点还会把线路判失败）。
 /// 因此正常轮只做本地记录，不产生协议调用——正常轮的异常语义由 Mark + 后续轮末重跑承担。
 /// </summary>
 public Task ReportRecoveryAsync(string reason,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  if(!IsReplay)return Task.CompletedTask;
  return _session.RecoverAsync(this,reason,ct);
 }
 /// <summary>
 /// 上报“明确豁免某范围”。同样只在重跑阶段有意义；并且一旦有豁免，本线路终态必须是不完整
 /// （服务端 Completed 要求所有非战斗规范点真实到达，不接受 bypass），故这里同步登记 Incomplete。
 /// </summary>
 public Task ReportBypassAsync(int throughSegment,bool skipRoute,CancellationToken ct)
 {
  ct.ThrowIfCancellationRequested();
  HadBypass=true;
  MarkIncomplete(skipRoute ? "重跑期跳整条线路（含未到达点豁免）" : $"重跑期跳段至段 {throughSegment}（含未到达点豁免）");
  if(!IsReplay)return Task.CompletedTask;
  return _session.BypassAsync(this,throughSegment,skipRoute,ct);
 }
}
