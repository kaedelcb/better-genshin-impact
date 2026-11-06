using System.Text.Json;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed record WorkflowObservationFact(string Instance, string State, IReadOnlyList<WorkflowObservationHit> Hits, string? Reason,
    string? CoveredSourceInstance = null, string? CoveredSourcePath = null, string? CoveredEpoch = null);
public sealed record WorkflowObservationHit(DateTime At, string Value, string SourceFile, long Offset);

public interface IWorkflowObservationSession : IAsyncDisposable
{
    Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct);
}

public interface IWorkflowObservationSource
{
    Task<IWorkflowObservationSession> ArmAsync(string identity, string keyword, CancellationToken ct);
}

public interface IWorkflowLiveObservationSource : IWorkflowObservationSource
{
    Task<IWorkflowObservationSession> ArmLiveAsync(string identity, string keyword, Action<WorkflowObservationHit> hit, CancellationToken ct);
    Task<IWorkflowObservationSession> ArmLiveContinuationAsync(string identity, string keyword, Action<WorkflowObservationHit> hit,
        string? sourceInstance, string? sourcePath, string? epoch, CancellationToken ct)
        => ArmLiveAsync(identity, keyword, hit, ct);
}

/// <summary>仅订阅现有增量日志；实例与进程纪元固定，历史回填不作为命中。等待不持有此订阅。</summary>
public sealed class WorkflowLogObservationSource(BgiLogTailService tail, Func<BgiEpoch?> epochProvider) : IWorkflowLiveObservationSource
{
    public async Task<IWorkflowObservationSession> ArmAsync(string identity, string keyword, CancellationToken ct)
        => await ArmCoreAsync(identity, keyword, null, ct).ConfigureAwait(false);

    public Task<IWorkflowObservationSession> ArmLiveAsync(string identity, string keyword, Action<WorkflowObservationHit> hit, CancellationToken ct)
        => ArmCoreAsync(identity, keyword, hit, ct);

    public Task<IWorkflowObservationSession> ArmLiveContinuationAsync(string identity, string keyword, Action<WorkflowObservationHit> hit,
        string? sourceInstance, string? sourcePath, string? epoch, CancellationToken ct)
        => ArmCoreAsync(identity, keyword, hit, ct, sourceInstance, sourcePath, epoch);

    private async Task<IWorkflowObservationSession> ArmCoreAsync(string identity, string keyword, Action<WorkflowObservationHit>? hit, CancellationToken ct,
        string? sourceInstance = null, string? sourcePath = null, string? sourceEpoch = null)
    {
        var epoch=epochProvider() ?? throw new InvalidOperationException("观察器缺少实际BGI进程纪元");
        if (sourceEpoch is not null && sourceEpoch != $"{epoch.ProcessId}:{epoch.StartTicksUtc}")
            throw new WorkflowObservationUnknownException("原任务日志的BGI纪元已经改变，不能借同PID日志猜测续接");
        if(await tail.DrainBoundaryAsync(ct).ConfigureAwait(false) is not {} path)
            throw new InvalidOperationException("观察日志尚未就绪，未启动叶子");
        var continuing = sourceInstance is not null && sourceEpoch == $"{epoch.ProcessId}:{epoch.StartTicksUtc}"
            && string.Equals(sourcePath, path, StringComparison.OrdinalIgnoreCase) ? sourceInstance : null;
        return new Session(tail,epochProvider,epoch,identity,keyword,path,hit,continuing);
    }

    private sealed class Session : IWorkflowObservationSession
    {
        private readonly BgiLogTailService _tail;
        private readonly Func<BgiEpoch?> _epochProvider;
        private readonly BgiEpoch _epoch;
        private readonly string _id,_keyword;
        private readonly string _sourcePath;
        private readonly DateTime _armedAt=new(DateTime.Now.Ticks/TimeSpan.TicksPerMillisecond*TimeSpan.TicksPerMillisecond,DateTimeKind.Local);
        private readonly object _gate=new();
        private readonly List<WorkflowObservationHit> _hits=[];
        private readonly Action<WorkflowObservationHit>? _onHit;
        private string? _logInstance,_gap;
        private bool _closed;
        private WorkflowObservationFact? _frozen;
        public Session(BgiLogTailService tail,Func<BgiEpoch?> epochProvider,BgiEpoch epoch,string id,string keyword,string path,Action<WorkflowObservationHit>? hit,string? continuedInstance = null)
        {
            _onHit=hit;
            _sourcePath=path; _logInstance=continuedInstance;
            _tail=tail;_epochProvider=epochProvider;_epoch=epoch;_id=id;_keyword=keyword;
            _tail.EntryReceived+=Entry;_tail.TargetFileChanged+=TargetChanged;
            if(_tail.CurrentFilePath!=path)_gap="观察就绪期间日志来源改变";
        }
        private void TargetChanged(string? path) { lock(_gate) if(!_closed)_gap="观察期间日志文件切换或丢失，不能判未命中"; }
        private void Entry(LogEntry entry)
        {
            lock(_gate)
            {
                if(_closed || entry.Time<_armedAt)return;
                var actualEpoch = _epochProvider();
                if (actualEpoch?.ProcessId != _epoch.ProcessId || actualEpoch.StartTicksUtc != _epoch.StartTicksUtc)
                { _gap = "观察期间BGI纪元改变，未执行日志回调"; return; }
                if(entry.Instance is null) {_gap="日志缺少实例身份，观察未知";return;}
                var match=Regex.Match(entry.Instance,@"^[A-Za-z][A-Za-z0-9]*:S\d+:P(?<pid>\d+):T\d+$");
                if(!match.Success || match.Groups["pid"].Value!=_epoch.ProcessId.ToString())return;
                if(_logInstance is not null && _logInstance!=entry.Instance){_gap="观察期间BGI实例改变";return;}
                _logInstance=entry.Instance;
                if(entry.Message.Contains(_keyword,StringComparison.OrdinalIgnoreCase))
                {
                    var hit = new WorkflowObservationHit(entry.Time,entry.Message,entry.SourceFile,entry.FileOffset);
                    _hits.Add(hit);
                    _onHit?.Invoke(hit);
                }
            }
        }
        public async Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct)
        {
            lock(_gate)if(_frozen is not null)return _frozen;
            try { await _tail.DrainBoundaryAsync(ct).ConfigureAwait(false); }
            catch(Exception ex) { lock(_gate)_gap="日志终态排空未确认："+ex.GetType().Name; }
            lock(_gate)
            {
                var current=_epochProvider();
                if(current?.ProcessId!=_epoch.ProcessId || current.StartTicksUtc!=_epoch.StartTicksUtc)_gap="观察期间BGI纪元改变";
                if(_logInstance is null)_gap ??="未确认本BGI实例的增量日志覆盖，不能判未命中";
                _frozen=new(_id,_gap is null?"frozen":"unknown",_hits.ToArray(),_gap,
                    _gap is null ? _logInstance : null, _gap is null ? _sourcePath : null,
                    _gap is null ? $"{_epoch.ProcessId}:{_epoch.StartTicksUtc}" : null);
                _closed=true;
            }
            // 事实冻结后才撤订阅；之后的迟到事件不再改变事实。
            _tail.EntryReceived-=Entry;_tail.TargetFileChanged-=TargetChanged;
            return _frozen;
        }
        public ValueTask DisposeAsync()
        {
            lock(_gate)_closed=true;
            _tail.EntryReceived-=Entry;_tail.TargetFileChanged-=TargetChanged;
            return ValueTask.CompletedTask;
        }
    }
}

public static class WorkflowObservation
{
    internal static string Key(string nodeId,int occurrence,int loop,int attempt)=>$"observation:{nodeId}:{occurrence}:{loop}:{attempt}";
    internal static bool Evaluate(WorkflowRunRecord? run,string source)
    {
        var outcome=run?.NodeOutcomes.LastOrDefault(o=>o.NodeId==source);
        if(run is null || outcome is null || outcome.Result!="succeeded" || string.IsNullOrEmpty(outcome.SubmissionKey))
            throw new WorkflowObservationUnknownException("宿主观察结果缺少已完成的出现身份，不能按未命中路由");
        // 事实键绑定该次提交（而非只按节点名取最后的任意观察）。
        if(run.ExtensionData?.TryGetValue("observationSubmission:"+outcome.SubmissionKey,out var saved)!=true
            || saved.Deserialize<WorkflowObservationFact>() is not {State:"frozen"} fact)
            throw new WorkflowObservationUnknownException("伴随观察丢失/重启或未冻结，Unknown不当未命中");
        return fact.Hits.Count>0;
    }
}

public sealed class WorkflowObservationUnknownException(string reason) : Exception(reason);
