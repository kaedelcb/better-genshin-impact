using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    /// <summary>全停止先撤销驱动并耐久封住续跑；未知执行事实保留，不依赖在线能力查询。</summary>
    internal async Task<IReadOnlyList<string>> StopAllRunsForUserAsync()
    {
        DriveEntry[] drives;
        lock (_gate) drives = _drives.Values.ToArray();
        var cancel = Task.WhenAll(drives.Select(d => CancelIsolatedAsync(d.Cts)));
        var failures = new List<string>();
        try
        {
            foreach (var run in ListActiveRuns())
            {
                try
                {
                    _runs.UpdateMergingIf(run.RunId, latest =>
                    {
                        if (latest.StopRequested) return false;
                        latest.StopRequested = true;
                        return true;
                    }, out var latest);
                    if (latest?.StopRequested != true) failures.Add("流程停止未落盘：" + run.RunId);
                    var drive = drives.FirstOrDefault(d => d.WorkflowId == run.WorkflowId);
                    drive?.Runner.RequestAction(run.RunId, WorkflowRunAction.Stop);
                }
                catch (Exception ex) { failures.Add("流程停止记录失败：" + ex.Message); }
            }
        }
        catch (Exception ex) { failures.Add("活动流程读取失败：" + ex.Message); }
        if (await Task.WhenAny(cancel, Task.Delay(2000)).ConfigureAwait(false) != cancel)
            failures.Add("流程取消回调尚未退出，停止意图已保留");
        NotifyStateChanged();
        return failures;
    }
}
