using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private bool _configurationTransfer;
    private int _configurationOperations;
    internal IDisposable TryBeginConfigTransfer()
    {
        lock (_gate)
        {
            var (occupied, unknown) = ReadExternalStartLedgerOccupancy();
            var runs = _runs.ListWithIntegrity("config-transfer");
            if (_shutdown || _configurationTransfer || _configurationOperations != 0 || _drives.Count != 0
                || _reservedWorkflows.Count != 0 || _recoverTask is { IsCompleted: false } || occupied || unknown
                || runs.UnknownFiles.Count > 0 || runs.Records.Any(r => !IsExecutionResetReleased(r)
                    && (ActiveStates.Contains(r.State) || r.State == WorkflowRunState.Unknown)))
                throw new InvalidOperationException("任务中心有活动、等待或结果未确认的执行责任，暂不能迁移设置。");
            _configurationTransfer = true;
            return new ConfigurationLease(this, true);
        }
    }
    private IDisposable? TryBeginConfigurationOperation()
    {
        lock (_gate)
        {
            if (_configurationTransfer) return null;
            _configurationOperations++;
            return new ConfigurationLease(this, false);
        }
    }
    private sealed class ConfigurationLease(TaskCenterHost host, bool transfer) : IDisposable
    {
        private TaskCenterHost? _host = host;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _host, null);
            if (owner is null) return;
            lock (owner._gate)
            {
                if (transfer) owner._configurationTransfer = false;
                else owner._configurationOperations--;
            }
        }
    }
}
