using System.IO;
using System.Security.Principal;
using System.Xml.Linq;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>本助手拥有的启动项名称，以及不访问系统状态的任务和参数构造。</summary>
internal static class AutoStartupRegistration
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "NexusBGI";
    internal const string LauncherFileName = "NexusBGI.Startup.exe";
    internal const string LegacyValueName = "MultiplayerHoeingAssistant";
    internal const string ConfigurationArgument = "--configure-login-startup";
    internal const string TaskOwnershipMarker = "NexusBGI.LoginStartup/v1";
    internal const string TaskXmlNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static IReadOnlyList<string> HistoricalValueNames { get; } = [LegacyValueName];

    internal static string BuildCommand(string executablePath, bool minimized)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            throw new ArgumentException("助手可执行文件路径不能为空。", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath);
        return $"\"{fullPath}\" {BuildArguments(minimized)}";
    }

    internal static string GetLauncherPath(string executablePath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executablePath))!, LauncherFileName);

    internal static string BuildLauncherCommand(string executablePath)
    {
        // Run 启动的是普通权限入口；管理员权限只由同账号的计划任务授予。
        var command = $"\"{GetLauncherPath(executablePath)}\"";
        if (command.Length > 260)
            throw new InvalidOperationException("助手路径过长，超出 Windows 登录启动项的命令长度限制。");
        return command;
    }

    internal static string BuildArguments(bool minimized)
        => minimized ? "--minimized --no-auto-launch" : "--no-auto-launch";

    internal static string BuildTaskName(string userSid)
        => "NexusBGI_Startup_" + NormalizeSid(userSid);

    internal static string BuildTaskXml(string executablePath, string userSid, bool minimized, bool highestAvailable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("助手可执行文件必须使用完整路径。", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath);
        var sid = NormalizeSid(userSid);
        var workingDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("助手可执行文件目录无效。", nameof(executablePath));
        XNamespace ns = TaskXmlNamespace;
        XElement Element(string name, object value) => new(ns + name, value);

        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.3"),
            new XElement(ns + "RegistrationInfo",
                Element("URI", "\\" + BuildTaskName(sid)),
                Element("Source", TaskOwnershipMarker),
                Element("Author", sid),
                Element("Description", "NexusBGI 助手：由登录入口指定当前 Windows 会话启动。")),
            new XElement(ns + "Triggers",
                new XElement(ns + "LogonTrigger",
                    // 只保留账号归属；登录入口通过 RunEx 显式指定会话，避免同账号串会话。
                    Element("Enabled", false),
                    Element("ExecutionTimeLimit", "PT0S"),
                    Element("UserId", sid))),
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                    Element("UserId", sid),
                    Element("LogonType", "InteractiveToken"),
                    Element("RunLevel", highestAvailable ? "HighestAvailable" : "LeastPrivilege"))),
            new XElement(ns + "Settings",
                Element("MultipleInstancesPolicy", "Parallel"),
                Element("AllowStartOnDemand", true),
                Element("DisallowStartIfOnBatteries", false),
                Element("StopIfGoingOnBatteries", false),
                Element("AllowHardTerminate", false),
                Element("StartWhenAvailable", true),
                Element("RunOnlyIfNetworkAvailable", false),
                Element("IdleSettings", new XElement(ns + "StopOnIdleEnd", false)),
                Element("RunOnlyIfIdle", false),
                Element("Enabled", true),
                Element("Hidden", false),
                Element("ExecutionTimeLimit", "PT0S"),
                Element("DisallowStartOnRemoteAppSession", false)),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"),
                new XElement(ns + "Exec",
                    Element("Command", fullPath),
                    Element("Arguments", BuildArguments(minimized)),
                    Element("WorkingDirectory", workingDirectory)))))
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static string[] BuildConfigurationArguments(bool enabled, bool minimized, string expectedUserSid)
        => [ConfigurationArgument, enabled ? "enable" : "disable", minimized ? "minimized" : "popup",
            NormalizeSid(expectedUserSid)];

    internal static bool TryParseConfigurationArguments(string[] args, string currentUserSid,
        out bool enabled, out bool minimized)
    {
        enabled = false;
        minimized = false;
        if (args.Length != 4 || args[0] != ConfigurationArgument
            || args[1] is not ("enable" or "disable") || args[2] is not ("minimized" or "popup"))
            return false;
        try
        {
            if (!string.Equals(NormalizeSid(args[3]), NormalizeSid(currentUserSid), StringComparison.Ordinal))
                return false;
        }
        catch (ArgumentException) { return false; }

        enabled = args[1] == "enable";
        minimized = args[2] == "minimized";
        return true;
    }

    private static string NormalizeSid(string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        return new SecurityIdentifier(userSid).Value;
    }
}
