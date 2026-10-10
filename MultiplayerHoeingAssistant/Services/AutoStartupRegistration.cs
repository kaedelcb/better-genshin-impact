using System.IO;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Windows 用户级开机启动登记的稳定名称和命令构造。</summary>
internal static class AutoStartupRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "NexusBGI";
    internal const string LegacyValueName = "MultiplayerHoeingAssistant";

    internal static IReadOnlyList<string> HistoricalValueNames { get; } = [LegacyValueName];

    internal static string BuildCommand(string executablePath, bool minimized)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("助手可执行文件路径不能为空。", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath);
        var arguments = minimized ? " --minimized --no-auto-launch" : " --no-auto-launch";
        return $"\"{fullPath}\"{arguments}";
    }
}
