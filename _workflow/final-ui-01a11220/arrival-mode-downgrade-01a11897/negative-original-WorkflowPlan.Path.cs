using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class WorkflowPlan
{
    public bool HasPaths => _doc.Nodes.Any(n => n.Path is not null || n.Kind.StartsWith("control.", StringComparison.Ordinal) || n.Strategies.Any(s=>s.Kind=="flow.route"));

    private int LaneCount => _doc.ExtensionData?.TryGetValue("scheduleLanes",out var lanes)==true && lanes.ValueKind==JsonValueKind.Array ? Math.Max(1,lanes.GetArrayLength()) : 1;
    private static int Lane(WorkflowNode n)=>n.ExtensionData?.TryGetValue("scheduleLane",out var lane)==true && lane.TryGetInt32(out var value) ? value : 0;
    private static int Span(WorkflowNode n)=>n.ExtensionData?.TryGetValue("scheduleSpan",out var span)==true && span.TryGetInt32(out var value) ? value : 1;
    private static bool Covers(WorkflowNode n,int lane)=>lane>=Lane(n) && lane<Lane(n)+Span(n);
    internal string PathLayoutKey=>_doc.ExtensionData?.TryGetValue("scheduleLanes",out var lanes)==true && lanes.ValueKind==JsonValueKind.Array
        ? JsonSerializer.Serialize(lanes.EnumerateArray().Select(l=>l.GetString()).ToArray()) : "[\"主车道\"]";
    internal bool ValidatePathCursor(WorkflowRunRecord run,bool bindNew=false)
    {
        JsonElement saved=default;var found=run.ExtensionData?.TryGetValue("pathLayout",out saved)==true;
        if(!HasPaths)
        {
            if(found)throw new InvalidOperationException("已绑定的路径运行不能改为顺序流程；保留原定义和游标，请停止旧运行后按新定义启动。");
            return false;
        }
        var key=PathLayoutKey;
        if(found && (saved.ValueKind!=JsonValueKind.String || saved.GetString()!=key))
            throw new InvalidOperationException("运行所绑定的车道布局已改变；保留原游标，请还原布局后恢复，或停止旧运行后按新布局启动。");
        if(!found && (run.NodeOutcomes.Count>0 || run.CurrentSubmission is not null))
            throw new InvalidOperationException("旧路径运行缺少车道绑定，不能猜测恢复去向；保留原运行，请核对后显式新建运行。");
        if(run.Cursor is {} cursor && (!TryLocate(cursor.NodeId,cursor.Occurrence,cursor.LoopIteration,out var at)
            || cursor.PathLane is {} lane && (lane<0 || lane>=LaneCount || !Covers(NodeAt(at),lane))))
            throw new InvalidOperationException("待执行节点或到达车道在新修订中已失效；保留原游标，不推进到另一条路径。");
        if(!found && bindNew){run.ExtensionData ??=new();run.ExtensionData["pathLayout"]=JsonSerializer.SerializeToElement(key);return true;}
        return false;
    }
    private bool LaneTarget(string target,out int lane,out TimeOnly? time)
    {
        lane=-1;time=null;var parts=target.Split(':');
        if(parts.Length==2 && parts[0]=="lane" && int.TryParse(parts[1],out lane))return lane>=0 && lane<LaneCount;
        if(parts.Length==4 && parts[0]=="time" && int.TryParse(parts[1],out lane) && TimeOnly.TryParseExact(parts[2]+":"+parts[3],"HH:mm",out var at))
        {time=at;return lane>=0 && lane<LaneCount;}
        return false;
    }

    private IEnumerable<string> ValidatePaths()
    {
        if (!HasPaths) yield break;
        if(_doc.ExtensionData?.TryGetValue("scheduleLanes",out var definitions)==true
            && (definitions.ValueKind!=JsonValueKind.Array || definitions.EnumerateArray().Any(e=>e.ValueKind!=JsonValueKind.String)))
            yield return "车道定义须为名称数组，未知形状不降级默认主车道";
        if(!_doc.Nodes.Any(n=>Covers(n,0)))yield return "流程没有主车道入口，禁止按空链成功处理";
        if (_doc.Nodes.Any(n=>string.IsNullOrWhiteSpace(n.NodeId)) || _doc.Nodes.GroupBy(n=>n.NodeId).Any(g=>g.Count()>1))
            yield return "流程路径要求每个节点具有唯一非空身份";
        foreach (var n in _doc.Nodes)
        {
            if(Lane(n)<0 || Lane(n)>=LaneCount || Span(n)<1 || Lane(n)+Span(n)>LaneCount)
                yield return $"节点 {n.NodeId} 的车道覆盖范围无效";
            if (n.Path is not null && !n.Strategies.Any(s=>s.Kind=="flow.route") && !n.Kind.StartsWith("control.",StringComparison.Ordinal))
                yield return $"节点 {n.NodeId} 缺少 flow.route 执行类型标记，禁止旧引擎静默顺序执行";
            if(n.Kind=="control.condition")
            {
                if(n.Path?.Yes is null || n.Path.No is null || !ValidCondition(n.Path.Condition))
                    yield return $"判断节点 {n.NodeId} 需要有效条件及明确的是/否去向";
            }
            foreach(var target in new[]{n.Path?.Next,n.Path?.Yes,n.Path?.No}.Where(t=>t is not null && t!="$end"))
                if(_doc.Nodes.Count(x=>x.NodeId==target)!=1 && !LaneTarget(target!,out _,out _)) yield return $"节点 {n.NodeId} 的去向 {target} 不存在或有歧义";
            if(n.Kind=="control.end" && n.Path is {Next: not null} && n.Path.Next!="$end")
                yield return "结束流程节点不能拥有后续执行去向";
            if(n.Kind.StartsWith("control.",StringComparison.Ordinal) && n.Strategies.Any(s=>s.Kind.StartsWith("prerequisite.",StringComparison.Ordinal)))
                yield return "判断和结束节点不能挂载资源前置副作用";
            if(n.Kind.StartsWith("control.",StringComparison.Ordinal) && n.Strategies.Any(s=>s.Kind=="condition.weekdays"))
                yield return "判断节点请使用判断条件编辑星期，不叠加资源过滤策略";
        }
    }

    private static bool ValidCondition(WorkflowPathCondition? c) => c?.Kind switch
    {
        "constant" => c.Value is not null,
        "weekdays" => c.Days is not null && c.Days.All(d=>new[]{"周一","周二","周三","周四","周五","周六","周日"}.Contains(d)),
        "timeWindow" => TimeOnly.TryParseExact(c.From,"HH:mm",out _) && TimeOnly.TryParseExact(c.Until,"HH:mm",out _),
        "observation" => !string.IsNullOrWhiteSpace(c.SourceNodeId),
        _ => false
    };

    public bool EvaluatePathCondition(WorkflowNode n,DateTimeOffset now,WorkflowRunRecord? run=null)
    {
        var c=n.Path?.Condition;
        if(!ValidCondition(c)) throw new InvalidOperationException("条件未知或参数无效，不能当作否分支");
        if(c!.Kind=="constant") return c.Value!.Value;
        if(c.Kind=="observation") return WorkflowObservation.Evaluate(run,c.SourceNodeId!);
        if(c.Kind=="weekdays") return WorkflowWeekdayFilter.Matches(new WorkflowStrategy{Kind="condition.weekdays",Params=new(){["days"]=JsonSerializer.SerializeToElement(c.Days)}},now);
        var from=TimeOnly.ParseExact(c.From!,"HH:mm");var until=TimeOnly.ParseExact(c.Until!,"HH:mm");var time=TimeOnly.FromDateTime(now.DateTime);
        return from<=until ? time>=from && time<until : time>=from || time<until;
    }

    private WorkflowNodeOccurrence? PathSuccessor(WorkflowNodeOccurrence current,string? result)
    {
        var n=NodeAt(current);
        if(n.Kind=="control.end") return null;
        var target=n.Kind=="control.condition"
            ? result switch {"branchYes"=>n.Path?.Yes,"branchNo"=>n.Path?.No,"skippedUser" or "skippedFilter"=>n.Path?.Next ?? "$end",_=>throw new InvalidOperationException("判断结果尚未持久化，禁止猜测去向")}
            : n.Path?.Next;
        if(target=="$end") return _doc.Loop is null ? null : StructuralRoundEntry(checked(current.LoopIteration+1));
        var outputLane=current.PathLane ?? Lane(n);
        int index;
        if(target is not null && LaneTarget(target,out var targetLane,out var targetTime))
        {
            outputLane=targetLane;
            index=_doc.Nodes.FindIndex(x=>Covers(x,targetLane)
                && (targetTime is {} at ? TimeOnly.TryParseExact(x.Strategies.LastOrDefault(s=>s.Kind=="schedule.time")?.GetString("time"),"HH:mm",out var time) && time>=at : _doc.Nodes.IndexOf(x)>current.SequenceIndex));
            if(index<0)return null;
        }
        else if(target is null)
        {
            index=_doc.Nodes.FindIndex(current.SequenceIndex+1,x=>Covers(x,outputLane));
            if(index<0)index=_doc.Nodes.Count;
        }
        else
        {
            index=_doc.Nodes.FindIndex(x=>x.NodeId==target);
            if(index>=0 && !Covers(_doc.Nodes[index],outputLane))outputLane=Lane(_doc.Nodes[index]);
        }
        if(index<0) throw new InvalidOperationException("流程去向已失效，禁止降级顺序执行");
        if(index>=_doc.Nodes.Count) return _doc.Loop is null ? null : StructuralRoundEntry(checked(current.LoopIteration+1));
        // 回边产生新的路径轮次，前向同轮不重复。与旧提交/前置/停驻身份同构，恢复仍用已落盘轮次。
        return OccurrenceAt(index,index<=current.SequenceIndex ? checked(current.LoopIteration+1) : current.LoopIteration) with {PathLane=outputLane};
    }
}
