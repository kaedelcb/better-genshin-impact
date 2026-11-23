using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Execution;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoOnline;

/// <summary>每次配置组调用独有的房间动作链；只有该调用的联机让位恢复能续用它。</summary>
internal sealed class ConfigurationGroupRoomActions : IDisposable
{
    private static readonly AsyncLocal<Step?> CurrentStep = new();
    private static readonly object RecoveryGate = new();
    // 原生联机接管同一时刻只保存一个被让位的现场。该附属内存状态不进入配置或恢复快照。
    private static Recovery? _suspended;

    private sealed record Key(Guid RootRunId, string GroupName, string? DragonTaskId, string? NodeId,
        int? Iteration, int? Occurrence);
    private sealed record Recovery(Key Key, long StopVersion, Chain Chain);
    private sealed record Step(ConfigurationGroupRoomActions Owner, string Name, int Row, int Repeat);
    private sealed class Chain
    {
        public Guid RunId { get; } = Guid.NewGuid();
        public bool Started { get; set; }
        public string? Interruption { get; set; }
        public Guid? PeerId { get; set; }
        // 组内行的各次重复按顺序执行。只保留每行最后一个已确认收件的重复序号。
        public Dictionary<int, int> ReceivedThrough { get; } = new();
    }

    private readonly ExecutionScope _owner;
    private readonly Key _key;
    private readonly Chain _chain;
    private readonly int _firstSwitchRow;

    private ConfigurationGroupRoomActions(ExecutionScope owner, Key key, Chain chain, int firstSwitchRow)
    {
        _owner = owner;
        _key = key;
        _chain = chain;
        _firstSwitchRow = firstSwitchRow;
    }

    public bool RoutesOnlineThroughMailbox(int row) => _chain.Started || (_chain.Interruption is not null && row >= _firstSwitchRow);

    public static ConfigurationGroupRoomActions Begin(ExecutionScope owner, string groupName, int firstSwitchRow, bool isRecovery)
    {
        var descriptor = owner.Descriptor;
        var key = new Key(owner.RunId, groupName, owner.Checkpoint?.OneDragonTaskId,
            descriptor.NodeId, descriptor.Iteration, descriptor.Occurrence);
        lock (RecoveryGate)
        {
            if (_suspended is { } suspended)
            {
                if (suspended.StopVersion != owner.StopVersion)
                    _suspended = null;
                else if (descriptor.Source == JobSource.Resume && key == suspended.Key)
                {
                    _suspended = null;
                    return new(owner, key, suspended.Chain, firstSwitchRow);
                }
            }
        }
        return new(owner, key, new Chain
        {
            Interruption = isRecovery ? "原配置组房间动作的收件状态无法确认；不重复切房或改用普通上线。" : null
        }, firstSwitchRow);
    }

    public IDisposable EnterStep(string name, int row, int repeat)
    {
        var previous = CurrentStep.Value;
        CurrentStep.Value = new(this, name, row, repeat);
        return new StepBinding(previous);
    }

    private sealed class StepBinding(Step? previous) : IDisposable
    {
        public void Dispose() => CurrentStep.Value = previous;
    }

    public static Task SwitchAsync(string? targetRoomName, CancellationToken ct)
        => CurrentStep.Value is { Name: SwitchAssistantRoomTask.TaskName } step
            ? step.Owner.SendAsync(step, "switch", targetRoomName, ct)
            : Task.FromException(new RoomActionHandoffException("切换助手房间须在实际配置组中运行，不能作为其他配置组的优先插入项目发出。"));

    public static Task? TrySendOnlineAsync(CancellationToken ct)
        => CurrentStep.Value is { Name: NotifyOnlineTask.TaskName } step && step.Owner.RoutesOnlineThroughMailbox(step.Row)
            ? step.Owner.SendAsync(step, "online", null, ct) : null;

    private async Task SendAsync(Step step, string kind, string? targetRoomName, CancellationToken ct)
    {
        _chain.Started = true;
        if (_chain.ReceivedThrough.TryGetValue(step.Row, out var received) && step.Repeat <= received)
        {
            App.GetLogger<ConfigurationGroupRoomActions>().LogInformation("房间动作已由本机助手收件；恢复时不重发。");
            return;
        }
        if (_chain.Interruption is { } interruption)
            throw new RoomActionHandoffException(interruption);

        try
        {
            if (kind == "switch" && string.IsNullOrWhiteSpace(targetRoomName))
                throw new RoomActionHandoffException("切换助手房间需要填写目标房间名称。");
            ct.ThrowIfCancellationRequested();
            _owner.ThrowIfStopped();
            var signal = new RoomActionMailbox.Signal(Guid.NewGuid(), _chain.RunId, kind, targetRoomName, _owner.StopVersion);
            var delivery = RoomActionMailbox.Post(signal, _chain.PeerId, ct);
            var outcome = await RoomActionMailbox.WaitAsync(delivery).ConfigureAwait(false);
            if (!outcome.Accepted || outcome.PeerId is not { } peerId)
            {
                ct.ThrowIfCancellationRequested();
                _owner.ThrowIfStopped();
                throw new RoomActionHandoffException(outcome.Error ?? "无法确认本机助手的收件会话。");
            }

            _chain.PeerId = peerId;
            _chain.ReceivedThrough[step.Row] = step.Repeat;
            App.GetLogger<ConfigurationGroupRoomActions>().LogInformation("本机助手已确认收件；房间请求由助手继续处理。");
        }
        catch (RoomActionHandoffException ex)
        {
            _chain.Interruption = ex.Message;
            throw;
        }
        catch (OperationCanceledException)
        {
            _chain.Interruption = "停止或让位前未确认房间信号收件；原房间动作链已中断，不会重发。";
            App.GetLogger<ConfigurationGroupRoomActions>().LogWarning("{Reason}", _chain.Interruption);
            throw;
        }
    }

    public void Dispose()
    {
        if (_owner.Result != TaskRunResult.Preempted || _owner.StopVersion != ExecutionScope.StopVersionNow)
            return;
        lock (RecoveryGate)
            _suspended = new(_key, _owner.StopVersion, _chain);
    }
}

internal sealed class RoomActionHandoffException(string message) : InvalidOperationException(message);
