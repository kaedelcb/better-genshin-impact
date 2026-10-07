from pathlib import Path
import hashlib, os
ROOT=Path(__file__).resolve().parents[2]
BASE=Path(__file__).resolve().parent/'path-cutoff-01a1176e/opening'
prepared={}
def edit(name,changes):
    path=ROOT/'MultiplayerHoeingAssistant/Services/TaskCenter'/name
    original=path.read_bytes()
    assert original==(BASE/('original-'+name)).read_bytes(),name+' no longer matches audited source'
    newline='\r\n' if b'\r\n' in original else '\n'
    data=original
    for old,new,count in changes:
        old=old.replace('\n',newline).encode('utf-8');new=new.replace('\n',newline).encode('utf-8')
        assert data.count(old)==count,(name,old[:120],data.count(old),count)
        data=data.replace(old,new)
    assert len(data)>len(original)*.9
    prepared[path]=(original,data)
edit('WorkflowPlanner.cs',[(
'''    public WorkflowNodeOccurrence? FirstOccurrence()
        => _doc.Nodes.Count == 0 ? null : HasPaths
            ? _doc.Nodes.FindIndex(n => Covers(n, 0)) is var first && first >= 0 ? OccurrenceAt(first, 0) with { PathLane = 0 } : null
            : OccurrenceAt(0, 0);''',
'''    public WorkflowNodeOccurrence? FirstOccurrence() => StructuralRoundEntry(0);

    private WorkflowNodeOccurrence? StructuralRoundEntry(int loopIteration)
        => _doc.Nodes.Count == 0 ? null : HasPaths
            ? _doc.Nodes.FindIndex(n => Covers(n, 0)) is var first && first >= 0 ? OccurrenceAt(first, loopIteration) with { PathLane = 0 } : null
            : OccurrenceAt(0, loopIteration);

    internal bool IsStructuralRoundEntry(WorkflowNodeOccurrence occurrence)
        => StructuralRoundEntry(occurrence.LoopIteration) is { } entry
            && entry.NodeId == occurrence.NodeId && entry.Occurrence == occurrence.Occurrence;''',1)])
edit('WorkflowPlan.Path.cs',[(
    'OccurrenceAt(0,checked(current.LoopIteration+1))',
    'StructuralRoundEntry(checked(current.LoopIteration+1))',2)])
edit('WorkflowRunner.cs',[(
'''                if (run.LoopDeadlineAt is { } cutoff && _opt.Clock() >= cutoff)
                {
                    SettleDeadlineWait(run, plan);
                    run.Note = AppendNote(run.Note, "循环绝对截止已到，不再启动后续节点。");
                    ApplyRelocation(run, null); _runs.Update(run); break;
                }
                if (plan.Document.Loop is { Mode: "scheduled" } calendar && (calendar.GetBool("skipAcrossDays") ?? true))
                {
                    if (run.LoopRoundEndsAt is null)
                    {
                        var now = _opt.Clock(); var at = TimeOnly.Parse(calendar.GetString("time")!);
                        var end = new DateTimeOffset(now.Date + at.ToTimeSpan(), now.Offset);
                        run.LoopRoundEndsAt = end <= now ? end.AddDays(1) : end; _runs.Update(run);
                    }
                    if (_opt.Clock() >= run.LoopRoundEndsAt && occurrence.LoopIteration == run.LastScheduledRoundWait)
                    {
                        var expiredRound = occurrence.LoopIteration;
                        do
                        {
                            CommitOutcome(run, plan, occurrence, "skippedFilter", "跨天跳过本轮剩余节点，不补跑");
                            occurrence = Relocate(run, plan);
                        } while (occurrence is not null && occurrence.LoopIteration == expiredRound);
                        run.LoopRoundEndsAt = null; _runs.Update(run); continue;
                    }
                }''',
'''                if (TrySettleLoopDeadline(run, plan)) break;
                if (SkipExpiredScheduledRound(run, plan, occurrence, out var nextRound))
                { occurrence = nextRound; continue; }''',1),(
'''                if (occurrence is { SequenceIndex: 0, LoopIteration: > 0 }
                    && occurrence.LoopIteration != run.LastScheduledRoundWait)''',
'''                if (occurrence.LoopIteration > 0 && plan.IsStructuralRoundEntry(occurrence)
                    && occurrence.LoopIteration != run.LastScheduledRoundWait)''',1),(
'''                // [BO-8] 恢复点或修订重排可能落在已完成出现之前。''',
'''                EnsureScheduledRoundEnd(run, plan);

                // [BO-8] 恢复点或修订重排可能落在已完成出现之前。''',1),(
'''                var scheduleResult = await AwaitNodeScheduleAsync(run, node, occurrence, control, ct).ConfigureAwait(false);
                if (control.PauseRequested) return Pause(run);
                if (!scheduleResult)''',
'''                var scheduleResult = await AwaitNodeScheduleAsync(run, plan, node, occurrence, control, ct).ConfigureAwait(false);
                if (control.PauseRequested) return Pause(run);
                // 等待醒来后重新检查持久截止，不能把到点等待当成新节点执行许可。
                if (TrySettleLoopDeadline(run, plan)) break;
                if (SkipExpiredScheduledRound(run, plan, occurrence, out var afterExpiredRound))
                { occurrence = afterExpiredRound; continue; }
                if (!scheduleResult)''',1),(
'''    private void SettleDeadlineWait(WorkflowRunRecord run, WorkflowPlan plan)''',
'''    private bool TrySettleLoopDeadline(WorkflowRunRecord run, WorkflowPlan plan)
    {
        if (run.LoopDeadlineAt is not { } cutoff || _opt.Clock() < cutoff) return false;
        SettleDeadlineWait(run, plan);
        run.Note = AppendNote(run.Note, "循环绝对截止已到，不再启动后续节点。");
        ApplyRelocation(run, null);
        _runs.Update(run);
        return true;
    }

    private void EnsureScheduledRoundEnd(WorkflowRunRecord run, WorkflowPlan plan)
    {
        if (run.LoopRoundEndsAt is not null || plan.Document.Loop is not { Mode: "scheduled" } calendar
            || !(calendar.GetBool("skipAcrossDays") ?? true)) return;
        var now = _opt.Clock();
        var at = TimeOnly.Parse(calendar.GetString("time")!);
        var end = new DateTimeOffset(now.Date + at.ToTimeSpan(), now.Offset);
        run.LoopRoundEndsAt = end <= now ? end.AddDays(1) : end;
        _runs.Update(run);
    }

    private bool SkipExpiredScheduledRound(WorkflowRunRecord run, WorkflowPlan plan,
        WorkflowNodeOccurrence occurrence, out WorkflowNodeOccurrence? next)
    {
        next = null;
        EnsureScheduledRoundEnd(run, plan);
        if (plan.Document.Loop is not { Mode: "scheduled" } calendar || !(calendar.GetBool("skipAcrossDays") ?? true)
            || run.LoopRoundEndsAt is not { } end || _opt.Clock() < end
            || occurrence.LoopIteration != run.LastScheduledRoundWait) return false;
        var expiredRound = occurrence.LoopIteration;
        next = occurrence;
        do
        {
            CommitOutcome(run, plan, next, "skippedFilter", "跨天跳过本轮剩余节点，不补跑");
            next = Relocate(run, plan);
        } while (next is not null && next.LoopIteration == expiredRound);
        run.LoopRoundEndsAt = null;
        _runs.Update(run);
        return true;
    }

    private void SettleDeadlineWait(WorkflowRunRecord run, WorkflowPlan plan)''',1)])
edit('WorkflowNodeSchedule.cs',[(
'''    private async Task<bool> AwaitNodeScheduleAsync(WorkflowRunRecord run, WorkflowNode node,''',
'''    private DateTimeOffset? NodeScheduleCutoff(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence occurrence)
    {
        var cutoff = run.LoopDeadlineAt;
        if (plan.Document.Loop is { Mode: "scheduled" } loop && (loop.GetBool("skipAcrossDays") ?? true)
            && occurrence.LoopIteration == run.LastScheduledRoundWait && run.LoopRoundEndsAt is { } roundEnd
            && (cutoff is null || roundEnd < cutoff)) cutoff = roundEnd;
        return cutoff;
    }

    private async Task<bool> AwaitNodeScheduleAsync(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNode node,''',1),(
'''        var at = timing.ScheduledAt;
        if (timing.Kind''',
'''        var at = timing.ScheduledAt;
        var cutoff = NodeScheduleCutoff(run, plan, occurrence);
        bool CutoffReached() => cutoff is { } end && _opt.Clock() >= end;
        if (CutoffReached()) return false;
        if (timing.Kind''',1),(
'''            await WaitAsync(run, key, at, control, ct).ConfigureAwait(false);
            if (control.PauseRequested) return false;
        }
        if (timing.Kind''',
'''            var until = cutoff is { } end && end < at ? end : at;
            await WaitAsync(run, key, until, control, ct).ConfigureAwait(false);
            if (control.PauseRequested) return false;
        }
        if (CutoffReached()) return false;
        if (timing.Kind''',1),(
'''        while (_opt.Clock() < timing.WindowEndsAt)
        {
            await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
            if (TaskCenterMechanismPolicy.IsFlexiblyIdle(_opt.FlexibleFactsProvider(), out _)) return true;''',
'''        while (_opt.Clock() < timing.WindowEndsAt && !CutoffReached())
        {
            await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
            if (CutoffReached()) return false;
            if (TaskCenterMechanismPolicy.IsFlexiblyIdle(_opt.FlexibleFactsProvider(), out _)) return !CutoffReached();''',1),(
'''            if (next > timing.WindowEndsAt) next = timing.WindowEndsAt!.Value;
            await WaitAsync''',
'''            if (next > timing.WindowEndsAt) next = timing.WindowEndsAt!.Value;
            if (cutoff is { } end && next > end) next = end;
            await WaitAsync''',1)])
for path,(before,after) in prepared.items():
    assert path.read_bytes()==before
    temp=path.with_name(path.name+'.path-cutoff-repair.tmp')
    with temp.open('xb') as f:f.write(after);f.flush();os.fsync(f.fileno())
    os.replace(temp,path)
    actual=path.read_bytes();assert actual==after
    print(path.name,len(before),len(actual),'BOM',before.startswith(b'\xef\xbb\xbf'),'CRLF',b'\r\n' in before,hashlib.sha256(actual).hexdigest())
