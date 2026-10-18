using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace MultiplayerHoeingAssistant.Startup;

/// <summary>只允许在调用者自己的交互会话中运行本目录拥有的助手任务。</summary>
internal static class SessionStartupLauncher
{
    internal const string TaskOwnershipMarker = "NexusBGI.LoginStartup/v1";
    internal const string TaskXmlNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    internal const int RunFlags = 0x4 | 0x8; // TASK_RUN_USE_SESSION_ID | TASK_RUN_USER_SID
    private static readonly XNamespace Ns = TaskXmlNamespace;

    internal static string BuildTaskName(string userSid)
        => "NexusBGI_Startup_" + NormalizeSid(userSid);

    /// <summary>RunEx 是唯一执行接缝；校验失败时不会调用它，也不会选择其他会话。</summary>
    internal static void Start(string taskXml, string baseDirectory, string identitySid, int sessionId,
        string sessionUserSid, Action<object?, int, int, string> runEx)
    {
        ArgumentNullException.ThrowIfNull(runEx);
        var sid = NormalizeSid(identitySid);
        if (sessionId <= 0)
            throw new InvalidOperationException("登录启动器必须运行于有效的交互会话。");
        if (!string.Equals(sid, NormalizeSid(sessionUserSid), StringComparison.Ordinal))
            throw new InvalidOperationException("当前登录会话账号与启动器进程账号不一致，拒绝启动助手。");

        ValidateTask(taskXml, baseDirectory, sid);
        runEx(null, RunFlags, sessionId, sid);
    }

    private static void ValidateTask(string taskXml, string baseDirectory, string userSid)
    {
        if (!Path.IsPathFullyQualified(baseDirectory))
            throw new InvalidOperationException("登录启动器目录必须使用完整路径。");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var expectedCommand = Path.Combine(directory, "MultiplayerHoeingAssistant.exe");
        using var reader = XmlReader.Create(new StringReader(taskXml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 256 * 1024
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != Ns + "Task")
            throw new InvalidOperationException("登录启动任务 XML 的根节点无效。");

        var registration = Single(root, "RegistrationInfo");
        Require(Value(registration, "Source") == TaskOwnershipMarker, "登录启动任务不属于本助手。");
        Require(Value(registration, "URI") == "\\" + BuildTaskName(userSid), "登录启动任务名称不匹配。");

        var principals = Single(root, "Principals");
        Require(principals.Elements().Count() == 1, "登录启动任务必须只有一个账号。");
        var principal = Single(principals, "Principal");
        var principalId = (string?)principal.Attribute("id");
        Require(!string.IsNullOrWhiteSpace(principalId), "登录启动任务账号标识缺失。");
        Require(IsSameUser(Value(principal, "UserId"), userSid), "登录启动任务账号不匹配。");
        Require(Value(principal, "LogonType") == "InteractiveToken", "登录启动任务必须使用交互账号令牌。");
        Require(Value(principal, "RunLevel") is "HighestAvailable" or "LeastPrivilege", "登录启动任务权限级别无效。");

        // 登录入口由当前会话的 HKCU Run 触发，禁止让调度器再按 SID 任意选择会话。
        var triggers = Single(root, "Triggers");
        Require(triggers.Elements().Count() == 1, "登录启动任务触发器不匹配。");
        var trigger = Single(triggers, "LogonTrigger");
        Require(IsSameUser(Value(trigger, "UserId"), userSid), "登录启动任务触发账号不匹配。");
        Require(!BooleanValue(trigger, "Enabled", true), "登录启动任务的账号登录触发器必须已禁用。");

        var settings = Single(root, "Settings");
        Require(Value(settings, "MultipleInstancesPolicy") == "Parallel", "登录启动任务必须允许各会话并行启动。");
        Require(BooleanValue(settings, "Enabled", true), "登录启动任务已禁用。");
        Require(BooleanValue(settings, "AllowStartOnDemand", true), "登录启动任务不允许按需启动。");

        var actions = Single(root, "Actions");
        Require((string?)actions.Attribute("Context") == principalId, "登录启动任务执行账号不匹配。");
        Require(actions.Elements().Count() == 1, "登录启动任务必须只有一个执行动作。");
        var exec = Single(actions, "Exec");
        Require(exec.Elements().Count() == 3, "登录启动任务执行参数结构不匹配。");
        Require(PathEquals(Value(exec, "Command"), expectedCommand), "登录启动任务指向了其他程序。");
        Require(PathEquals(Value(exec, "WorkingDirectory"), directory), "登录启动任务工作目录不匹配。");
        Require(Value(exec, "Arguments") is "--no-auto-launch" or "--minimized --no-auto-launch",
            "登录启动任务包含不允许的参数。");
    }

    private static XElement Single(XElement parent, string name)
    {
        var children = parent.Elements(Ns + name).ToArray();
        if (children.Length != 1)
            throw new InvalidOperationException($"登录启动任务的 {name} 节点缺失或重复。");
        return children[0];
    }

    private static string Value(XElement parent, string name)
    {
        var element = Single(parent, name);
        Require(!element.HasElements, $"登录启动任务的 {name} 值无效。");
        return element.Value;
    }

    private static bool BooleanValue(XElement parent, string name, bool defaultValue)
    {
        if (!parent.Elements(Ns + name).Any())
            return defaultValue;
        try { return XmlConvert.ToBoolean(Value(parent, name)); }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"登录启动任务的 {name} 值无效。", ex);
        }
    }

    private static bool PathEquals(string actual, string expected)
        => Path.IsPathFullyQualified(actual)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameUser(string userId, string sid)
    {
        if (string.Equals(userId, sid, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(userId))
            return false;
        // 任务计划程序会把触发器中的 SID 保存为 DOMAIN\\user；以解析后的 SID 核账号。
        try { return ((SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier))).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
    }

    private static string NormalizeSid(string sid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        return new SecurityIdentifier(sid).Value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
