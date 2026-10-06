using System.Text.Json;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed record WorkflowObservationFact(string Instance, string State, IReadOnlyList<WorkflowObservationHit> Hits, string? Reason);
public sealed record WorkflowObservationHit(DateTime At, string Value, string SourceFile, long Offset);

public interface IWorkflowObservationSession : IAsyncDisposable
{
    Task<WorkflowObservationFact> FreezeAsync(CancellationToken ct);
}

public interface IWorkflowObservationSource
{
    Task<IWorkflowObservationSession> ArmAsync(string identity, string keyword, CancellationToken ct);
}

/// <summary>仅订阅现有增量日志；实例与进程纪元固定，历史回填不作为命中。等待不持有此订阅。</summary>
public sealed class WorkflowLogObservationSource(BgiLogTailService tail, Func<BgiEpoch?> epochProvider) : IWorkflowObservationSource
{
    public async Task<IWorkflowObservationSession> ArmAsync(string identity, string keyword, CancellationToken ct)
    {
        var epoch=epochProvider() ?? throw new InvalidOperationException("观察器缺少实际BGI进程纪元");
        if(await tail.DrainBoundaryAsync(ct).ConfigureAwait(false) is not {} path)
            throw new InvalidOperationException("观察日志尚未就绪，未启动叶子");
        return new Session(tail,epochProvider,epoch,identity,keyword,path);
    }

    private sealed class Session : IWorkflowObservationSession
    {
        private readonly BgiLogTailService _tail;
        private readonly Func<BgiEpoch?> _epochProvider;
        private readonly BgiEpoch _epoch;
        private readonly string _id,_keyword;
        private readonly DateTime _armedAt=new(DateTime.Now.Ticks/TimeSpan.TicksPerMillisecond*TimeSpan.TicksPerMillisecond,DateTimeKind.Local);
        private readonly object _gate=new();
        private readonly List<WorkflowObservationHit> _hits=[];
        private string? _logInstance,_gap;
        private bool _closed;
        private WorkflowObservationFact? _frozen;
        public Session(BgiLogTailService tail,Func<BgiEpoch?> epochProvider,BgiEpoch epoch,string id,string keyword,string path)
        {
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
                if(entry.Instance is null) {_gap="日志缺少实例身份，观察未知";return;}
                var match=Regex.Match(entry.Instance,@"^[A-Za-z][A-Za-z0-9]*:S\d+:P(?<pid>\d+):T\d+$");
                if(!match.Success || match.Groups["pid"].Value!=_epoch.ProcessId.ToString())return;
                if(_logInstance is not null && _logInstance!=entry.Instance){_gap="观察期间BGI实例改变";return;}
                _logInstance=entry.Instance;
                if(entry.Message.Contains(_keyword,StringComparison.OrdinalIgnoreCase))
                    _hits.Add(new(entry.Time,entry.Message,entry.SourceFile,entry.FileOffset));
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
                _frozen=new(_id,_gap is null?"frozen":"unknown",_hits.ToArray(),_gap);
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
