using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services.NewArchitecture;

/// <summary>
/// 封装对本地 BGI 的 IPC 命令执行。
/// </summary>
public class BgiCommandService
{
    private readonly IpcClient _ipcClient;
    private readonly BgiProcessMonitor _processMonitor;

    public BgiCommandService(IpcClient ipcClient, BgiProcessMonitor processMonitor)
    {
        _ipcClient = ipcClient;
        _processMonitor = processMonitor;
    }

    public async Task<bool> EnsureBgiRunningAsync(CancellationToken ct = default)
    {
        if (_processMonitor.IsBgiRunning) return true;
        _processMonitor.RestartBgi();
        // 简单轮询等待 BGI 启动
        for (int i = 0; i < 30; i++)
        {
            if (_processMonitor.IsBgiRunning) return true;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }

    public async Task<CommandResult> ExecuteOnlineGroupsAsync(List<string> groupNames, List<string> groupTypes)
    {
        var results = new List<CommandResult>();
        for (int i = 0; i < groupNames.Count; i++)
        {
            var groupName = groupNames[i];
            var groupType = i < groupTypes.Count ? groupTypes[i] : "group";

            var payloadDict = new Dictionary<string, object>
            {
                [groupType == "oneclick" ? "configName" : "groupName"] = groupName
            };
            var request = new IpcRequest
            {
                OpCode = "task.start",
                Payload = JsonSerializer.Serialize(payloadDict)
            };

            var response = await _ipcClient.SendCommandAsync(request);
            results.Add(new CommandResult
            {
                Status = response.Success ? "success" : "failed",
                Message = response.ErrorMessage ?? ""
            });
        }

        var failed = results.FirstOrDefault(r => r.Status != "success");
        return failed ?? new CommandResult { Status = "success", Message = $"执行了 {groupNames.Count} 个配置" };
    }
}

public class CommandResult
{
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}
