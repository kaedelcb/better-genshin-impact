using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record AutoStartupRegistrationResult(bool Success, string Message)
{
    internal bool StateUnconfirmed { get; init; }
}

/// <summary>事务配置当前账号的会话登录入口和助手任务；不会运行任务或修改 UAC/兼容性设置。</summary>
internal static class WindowsAutoStartupRegistration
{
    private const int TaskCreate = 2;
    private const int TaskUpdate = 4;
    private const int TaskLogonInteractiveToken = 3;
    private const int ConfigurationFailedExitCode = 1;
    private const int InvalidConfigurationExitCode = 2;
    private const int StateUnconfirmedExitCode = 3;
    private const string OperationMarkerPrefix = "NexusBGI.StartupChange/";

    internal static AutoStartupRegistrationResult Apply(bool enabled, bool minimized, bool allowElevation = true)
    {
        string sid;
        string executablePath;
        bool highestAvailable;
        try
        {
            sid = GetCurrentUserSid();
            executablePath = GetExecutablePath();
            highestAvailable = IsAdministratorAccount();
            if (enabled)
            {
                var launcherPath = AutoStartupRegistration.GetLauncherPath(executablePath);
                if (!File.Exists(launcherPath))
                    return new(false, "助手登录入口缺失，未更改自启动设置。请使用包含 NexusBGI.Startup.exe 的完整助手文件。");
                if (RequiresAdministratorCompatibility(launcherPath))
                    return new(false, "登录入口 NexusBGI.Startup.exe 被强制设为管理员运行，无法由 Windows 登录启动；管理员兼容性设置仅应应用于助手主程序。");
                _ = AutoStartupRegistration.BuildLauncherCommand(executablePath);
            }
            if (enabled && !highestAvailable && RequiresAdministratorCompatibility(executablePath))
                return new(false, "当前 Windows 账号是标准账号，但助手被兼容性设置强制以管理员身份运行，无法在本账号内自动提权。请取消助手的此项兼容性设置，不能改用其他账号的凭据。");
        }
        catch (Exception ex)
        {
            return Failure("无法确认当前账号或助手路径", ex);
        }

        try
        {
            ApplyTransaction(enabled, minimized, sid, executablePath, highestAvailable);
            return Success(enabled, highestAvailable);
        }
        catch (RegistrationRollbackException ex)
        {
            return new(false, "实际登记未确认：设置失败，且本次变更恢复未完成。" + ex.Message)
                { StateUnconfirmed = true };
        }
        catch (Exception ex) when (allowElevation && highestAvailable && IsAccessDenied(ex) && !IsElevated())
        {
            return ApplyWithElevation(enabled, minimized, sid, executablePath, highestAvailable);
        }
        catch (Exception ex)
        {
            return Failure("登录自启动设置未完成", ex);
        }
    }

    internal static bool TryHandleConfigurationArguments(string[] args, out int exitCode)
    {
        exitCode = 0;
        // 出现 helper 标记却不满足严格协议，也必须退出，不能落入普通启动和业务初始化。
        if (!args.Any(arg => string.Equals(arg, AutoStartupRegistration.ConfigurationArgument,
                StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            if (!AutoStartupRegistration.TryParseConfigurationArguments(args, GetCurrentUserSid(),
                    out var enabled, out var minimized))
            {
                exitCode = InvalidConfigurationExitCode;
                return true;
            }

            var result = Apply(enabled, minimized, allowElevation: false);
            exitCode = result.Success ? 0 : result.StateUnconfirmed ? StateUnconfirmedExitCode : ConfigurationFailedExitCode;
            if (!result.Success) Trace.WriteLine(result.Message);
        }
        catch (StartupIdentityMismatchException ex)
        {
            Trace.WriteLine(ex.Message);
            exitCode = InvalidConfigurationExitCode;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("登录自启动配置助手失败：" + ex);
            exitCode = ConfigurationFailedExitCode;
        }
        return true;
    }

    internal sealed record RunValueSnapshot(string Name, object Value, RegistryValueKind Kind);

    internal sealed class RegistrationChange(string? taskXml, IReadOnlyList<RunValueSnapshot> runValues)
    {
        internal string? BeforeTaskXml { get; } = taskXml;
        internal IReadOnlyList<RunValueSnapshot> BeforeRunValues { get; } = runValues;
        internal string OperationMarker { get; } = OperationMarkerPrefix + Guid.NewGuid().ToString("N");
        internal bool TaskMutationAttempted { get; set; }
        internal Dictionary<string, RunValueSnapshot?> RunWritesAttempted { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RegistrationRollbackException(string message, Exception innerException)
        : InvalidOperationException(message, innerException) { }

    private sealed class StartupIdentityMismatchException(string message) : InvalidOperationException(message) { }

    private static void ApplyTransaction(bool enabled, bool minimized, string sid, string executablePath, bool highestAvailable)
    {
        RegistrationChange? change = null;
        try
        {
            change = WithTaskFolder(folder =>
            {
                var taskXml = ReadTaskXml(folder, AutoStartupRegistration.BuildTaskName(sid));
                EnsureTaskOwnership(taskXml, sid);
                return new RegistrationChange(taskXml, ReadOwnedRunValues());
            });
            ApplyTask(enabled, minimized, sid, executablePath, highestAvailable, change);
            ApplyRunValues(change, enabled, executablePath);
        }
        catch (Exception failure)
        {
            if (change is null || (!change.TaskMutationAttempted && change.RunWritesAttempted.Count == 0)) throw;
            var restoreErrors = new List<string>();
            try { RestoreRunValues(change); }
            catch (Exception ex) { restoreErrors.Add("启动项恢复：" + ex.GetBaseException().Message); }
            try { RestoreTask(sid, change); }
            catch (Exception ex) { restoreErrors.Add("计划任务恢复：" + ex.GetBaseException().Message); }
            if (restoreErrors.Count != 0)
                throw new RegistrationRollbackException(failure.GetBaseException().Message + "；" + string.Join("；", restoreErrors), failure);
            throw;
        }
    }

    private static void ApplyTask(bool enabled, bool minimized, string sid, string executablePath, bool highestAvailable,
        RegistrationChange change)
    {
        object? service = null;
        object? folder = null;
        object? registeredTask = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
                ?? throw new InvalidOperationException("Windows 任务计划服务不可用。");
            service = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException("无法连接 Windows 任务计划服务。");
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            var taskName = AutoStartupRegistration.BuildTaskName(sid);
            var existingXml = ReadTaskXml(folder, taskName);
            EnsureTaskOwnership(existingXml, sid);
            if (!SameTaskXml(existingXml, change.BeforeTaskXml))
                throw new InvalidOperationException("登录任务在设置过程中被其他进程改变，已保留当前任务，请重新读取后再设置。");

            if (!enabled)
            {
                if (existingXml is not null)
                {
                    change.TaskMutationAttempted = true;
                    ((dynamic)folder).DeleteTask(taskName, 0);
                }
                if (ReadTaskXml(folder, taskName) is not null)
                    throw new InvalidOperationException("登录任务删除后仍可读到，关闭结果未确认。");
                return;
            }

            // 已符合目标的任务只读回，不重复改写；普通启动不会反复要求管理员授权。
            if (existingXml is not null && MatchesTask(existingXml, sid, executablePath, minimized, highestAvailable)) return;

            var task = ParseTask(AutoStartupRegistration.BuildTaskXml(executablePath, sid, minimized, highestAvailable));
            XNamespace ns = AutoStartupRegistration.TaskXmlNamespace;
            task.Element(ns + "RegistrationInfo")!.Add(new XElement(ns + "Documentation", change.OperationMarker));
            var xml = task.ToString(SaveOptions.DisableFormatting);
            // 首次只能创建，更新只能修改已校验的任务，避免创建竞争时覆盖另一份同名任务。
            change.TaskMutationAttempted = true;
            registeredTask = ((dynamic)folder).RegisterTask(taskName, xml,
                existingXml is null ? TaskCreate : TaskUpdate, sid, null, TaskLogonInteractiveToken, null);
            var actualXml = ReadTaskXml(folder, taskName);
            if (actualXml is null || !MatchesTask(actualXml, sid, executablePath, minimized, highestAvailable))
                throw new InvalidOperationException("登录任务登记后的读回内容不匹配，未确认启用成功。");
        }
        finally
        {
            ReleaseComObject(registeredTask);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    internal static string? ReadTaskXml(object folder, string taskName)
    {
        object? task = null;
        try
        {
            task = ((dynamic)folder).GetTask(taskName);
            return (string)((dynamic)task).Xml;
        }
        // COM 互操作会把缺失任务的 HRESULT 映射为 FileNotFoundException/DirectoryNotFoundException。
        // 仅将任务不存在视为无登记；权限、服务及其他读取失败仍向上传播。
        catch (Exception ex) when (ex.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003))
        {
            return null;
        }
        finally { ReleaseComObject(task); }
    }

    private static T WithTaskFolder<T>(Func<object, T> action)
    {
        object? service = null;
        object? folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(type)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            return action(folder);
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static void EnsureTaskOwnership(string? xml, string sid)
    {
        if (xml is not null && !IsOwnedTask(xml, sid))
            throw new InvalidOperationException("同名计划任务不属于本账号的助手，已保留原任务，不能覆盖或删除。");
    }

    private static bool SameTaskXml(string? first, string? second)
        => first is null || second is null ? first == second : XNode.DeepEquals(ParseTask(first), ParseTask(second));

    private static void RestoreTask(string sid, RegistrationChange change)
    {
        if (!change.TaskMutationAttempted) return;
        WithTaskFolder(folder =>
        {
            var name = AutoStartupRegistration.BuildTaskName(sid);
            var currentXml = ReadTaskXml(folder, name);
            if (SameTaskXml(currentXml, change.BeforeTaskXml)) return true;
            if (currentXml is not null)
            {
                EnsureTaskOwnership(currentXml, sid);
                XNamespace ns = AutoStartupRegistration.TaskXmlNamespace;
                if (ParseTask(currentXml).Element(ns + "RegistrationInfo")?.Element(ns + "Documentation")?.Value != change.OperationMarker)
                    throw new InvalidOperationException("同名任务已被其他进程修改，未覆盖或删除当前任务。");
            }
            if (change.BeforeTaskXml is null)
            {
                if (currentXml is not null) ((dynamic)folder).DeleteTask(name, 0);
            }
            else
            {
                object? restored = null;
                try
                {
                    // 当前缺失时只允许创建，绝不覆盖恢复期间出现的外来同名任务。
                    restored = ((dynamic)folder).RegisterTask(name, change.BeforeTaskXml,
                        currentXml is null ? TaskCreate : TaskUpdate, sid, null, TaskLogonInteractiveToken, null);
                }
                finally { ReleaseComObject(restored); }
            }
            if (!SameTaskXml(ReadTaskXml(folder, name), change.BeforeTaskXml))
                throw new InvalidOperationException("任务恢复后的读回与变更前内容不一致。");
            return true;
        });
    }

    private static bool IsOwnedTask(string xml, string sid)
    {
        var task = ParseTask(xml);
        XNamespace ns = AutoStartupRegistration.TaskXmlNamespace;
        var registration = task.Element(ns + "RegistrationInfo");
        var principals = task.Element(ns + "Principals")?.Elements().ToArray();
        var triggers = task.Element(ns + "Triggers")?.Elements().ToArray();
        return registration?.Element(ns + "Source")?.Value == AutoStartupRegistration.TaskOwnershipMarker
            && registration.Element(ns + "URI")?.Value == "\\" + AutoStartupRegistration.BuildTaskName(sid)
            && principals is { Length: 1 } && principals[0].Name == ns + "Principal"
            && IsSameUser(principals[0].Element(ns + "UserId")?.Value, sid)
            && triggers is { Length: 1 } && triggers[0].Name == ns + "LogonTrigger"
            && IsSameUser(triggers[0].Element(ns + "UserId")?.Value, sid);
    }

    internal static bool MatchesTask(string xml, string sid, string executablePath, bool minimized, bool highestAvailable)
    {
        if (!IsOwnedTask(xml, sid)) return false;
        var task = ParseTask(xml);
        XNamespace ns = AutoStartupRegistration.TaskXmlNamespace;
        var principal = task.Element(ns + "Principals")!.Element(ns + "Principal")!;
        var trigger = task.Element(ns + "Triggers")!.Element(ns + "LogonTrigger")!;
        var settings = task.Element(ns + "Settings");
        var actions = task.Element(ns + "Actions");
        var actionItems = actions?.Elements().ToArray();
        if (settings is null || actionItems is not { Length: 1 } || actionItems[0].Name != ns + "Exec")
            return false;
        var action = actionItems[0];
        return principal.Element(ns + "LogonType")?.Value == "InteractiveToken"
            && principal.Element(ns + "RunLevel")?.Value == (highestAvailable ? "HighestAvailable" : "LeastPrivilege")
            && (string?)actions!.Attribute("Context") == (string?)principal.Attribute("id")
            && IsFalse(trigger.Element(ns + "Enabled"), defaultValue: true)
            && IsUnlimited(trigger.Element(ns + "ExecutionTimeLimit"))
            && trigger.Element(ns + "StartBoundary") is null
            && trigger.Element(ns + "EndBoundary") is null
            && trigger.Element(ns + "Repetition") is null
            && (trigger.Element(ns + "Delay") is null || IsUnlimited(trigger.Element(ns + "Delay")))
            && SamePath(action.Element(ns + "Command")?.Value, executablePath)
            && action.Element(ns + "Arguments")?.Value == AutoStartupRegistration.BuildArguments(minimized)
            && SamePath(action.Element(ns + "WorkingDirectory")?.Value, Path.GetDirectoryName(executablePath)!)
            && settings.Element(ns + "MultipleInstancesPolicy")?.Value == "Parallel"
            && IsTrue(settings.Element(ns + "AllowStartOnDemand"), defaultValue: true)
            && IsFalse(settings.Element(ns + "DisallowStartIfOnBatteries"), defaultValue: true)
            && IsFalse(settings.Element(ns + "StopIfGoingOnBatteries"), defaultValue: true)
            && IsFalse(settings.Element(ns + "RunOnlyIfNetworkAvailable"), defaultValue: false)
            && IsFalse(settings.Element(ns + "RunOnlyIfIdle"), defaultValue: false)
            && IsTrue(settings.Element(ns + "StartWhenAvailable"), defaultValue: false)
            && IsFalse(settings.Element(ns + "AllowHardTerminate"), defaultValue: true)
            && IsFalse(settings.Element(ns + "IdleSettings")?.Element(ns + "StopOnIdleEnd"), defaultValue: true)
            && IsFalse(settings.Element(ns + "DisallowStartOnRemoteAppSession"), defaultValue: false)
            && IsTrue(settings.Element(ns + "Enabled"), defaultValue: true)
            && IsUnlimited(settings.Element(ns + "ExecutionTimeLimit"));
    }

    private static XElement ParseTask(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != XName.Get("Task", AutoStartupRegistration.TaskXmlNamespace))
            throw new InvalidOperationException("同名任务的 XML 无法识别，已保留原任务。");
        return root;
    }

    private static bool IsSameUser(string? userId, string sid)
    {
        if (string.Equals(userId, sid, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(userId)) return false;
        try { return ((SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier))).Value == sid; }
        catch (IdentityNotMappedException) { return false; }
    }

    private static bool SamePath(string? actualPath, string expectedPath)
        => actualPath is not null && Path.IsPathFullyQualified(actualPath)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(actualPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)), StringComparison.OrdinalIgnoreCase);

    // 任务计划程序可省略等于 schema 默认值的节点；显式值必须仍是合法且符合目标的布尔值。
    private static bool IsTrue(XElement? value, bool defaultValue)
        => value is null ? defaultValue : value.Value is "true" or "1";
    private static bool IsFalse(XElement? value, bool defaultValue)
        => value is null ? !defaultValue : value.Value is "false" or "0";
    private static bool IsUnlimited(XElement? value)
        => value is not null && XmlConvert.ToTimeSpan(value.Value) == TimeSpan.Zero;

    private static IReadOnlyList<RunValueSnapshot> ReadOwnedRunValues()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartupRegistration.RunKeyPath, writable: false);
        return ReadOwnedRunValues(key);
    }

    internal static IReadOnlyList<RunValueSnapshot> ReadOwnedRunValues(RegistryKey? key)
        => key is null ? [] : new[] { AutoStartupRegistration.ValueName }.Concat(AutoStartupRegistration.HistoricalValueNames)
            .Select(name => ReadRunValue(key, name)).OfType<RunValueSnapshot>().ToArray();

    private static RunValueSnapshot? ReadRunValue(RegistryKey key, string name)
    {
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : new(name, value, key.GetValueKind(name));
    }

    private static bool SameRunValue(RunValueSnapshot? first, RunValueSnapshot? second)
    {
        if (first is null || second is null) return first is null && second is null;
        if (first.Kind != second.Kind) return false;
        return first.Value switch
        {
            byte[] bytes => second.Value is byte[] other && bytes.SequenceEqual(other),
            string[] strings => second.Value is string[] other && strings.SequenceEqual(other),
            _ => Equals(first.Value, second.Value)
        };
    }

    private static RunValueSnapshot? DesiredRunValue(string name, bool enabled, string executablePath)
        => enabled && name == AutoStartupRegistration.ValueName
            ? new(name, AutoStartupRegistration.BuildLauncherCommand(executablePath), RegistryValueKind.String)
            : null;

    private static void ApplyRunValues(RegistrationChange change, bool enabled, string executablePath)
    {
        using var readKey = Registry.CurrentUser.OpenSubKey(AutoStartupRegistration.RunKeyPath, writable: false);
        if (GetRunMutations(readKey, change, enabled, executablePath).Length == 0) return;
        using var key = Registry.CurrentUser.CreateSubKey(AutoStartupRegistration.RunKeyPath, writable: true);
        ApplyRunValues(key, change, enabled, executablePath);
    }

    private static string[] GetRunMutations(RegistryKey? key, RegistrationChange change, bool enabled, string executablePath)
    {
        var names = new[] { AutoStartupRegistration.ValueName }.Concat(AutoStartupRegistration.HistoricalValueNames).ToArray();
        var before = change.BeforeRunValues.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
            if (!SameRunValue(key is null ? null : ReadRunValue(key, name), before.GetValueOrDefault(name)))
                throw new InvalidOperationException("助手启动项在设置过程中被其他进程改变，未覆盖当前内容。");
        return names.Where(name => !SameRunValue(before.GetValueOrDefault(name),
            DesiredRunValue(name, enabled, executablePath))).ToArray();
    }

    internal static void ApplyRunValues(RegistryKey key, RegistrationChange change, bool enabled, string executablePath)
    {
        var before = change.BeforeRunValues.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in GetRunMutations(key, change, enabled, executablePath))
        {
            if (!SameRunValue(ReadRunValue(key, name), before.GetValueOrDefault(name)))
                throw new InvalidOperationException("助手启动项在设置过程中被其他进程改变，未覆盖当前内容。");
            var desired = DesiredRunValue(name, enabled, executablePath);
            change.RunWritesAttempted.Add(name, desired);
            if (desired is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, desired.Value, desired.Kind);
        }
        VerifyRunState(key, enabled, executablePath);
    }

    private static void VerifyRunState(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartupRegistration.RunKeyPath, writable: false);
        VerifyRunState(key, enabled, executablePath);
    }

    private static void VerifyRunState(RegistryKey? key, bool enabled, string executablePath)
    {
        var actual = ReadOwnedRunValues(key);
        var expected = DesiredRunValue(AutoStartupRegistration.ValueName, enabled, executablePath);
        if (expected is null ? actual.Count != 0 : actual.Count != 1 || !string.Equals(actual[0].Name, expected.Name, StringComparison.OrdinalIgnoreCase)
            || !SameRunValue(actual[0], expected))
            throw new InvalidOperationException("登录入口登记后的值或类型读回不匹配，设置未完成。");
    }

    private static void RestoreRunValues(RegistrationChange change)
    {
        if (change.RunWritesAttempted.Count == 0) return;
        using var key = Registry.CurrentUser.CreateSubKey(AutoStartupRegistration.RunKeyPath, writable: true);
        RestoreRunValues(key, change);
    }

    internal static void RestoreRunValues(RegistryKey key, RegistrationChange change)
    {
        var restoreErrors = new List<string>();
        foreach (var (name, written) in change.RunWritesAttempted)
        {
            try
            {
                var before = change.BeforeRunValues.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase));
                var current = ReadRunValue(key, name);
                if (SameRunValue(current, before)) continue;
                if (!SameRunValue(current, written))
                    throw new InvalidOperationException("助手启动项出现并发修改，未覆盖当前值。");
                if (before is null) key.DeleteValue(name, throwOnMissingValue: false);
                else key.SetValue(name, before.Value, before.Kind);
                if (!SameRunValue(ReadRunValue(key, name), before))
                    throw new InvalidOperationException("启动项恢复后的值或类型与变更前不一致。");
            }
            catch (Exception ex) { restoreErrors.Add(name + "：" + ex.GetBaseException().Message); }
        }
        if (restoreErrors.Count != 0) throw new InvalidOperationException(string.Join("；", restoreErrors));
    }

    private static AutoStartupRegistrationResult ApplyWithElevation(bool enabled, bool minimized,
        string expectedSid, string executablePath, bool highestAvailable)
    {
        var helperStarted = false;
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            };
            foreach (var argument in AutoStartupRegistration.BuildConfigurationArguments(enabled, minimized, expectedSid))
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null) return new(false, "未能启动管理员授权，登录自启动设置未完成。");
            helperStarted = true;
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                if (process.ExitCode == InvalidConfigurationExitCode)
                    return new(false, "管理员授权必须使用当前登录桌面的 Windows 账号，不能改用另一个账号；登录自启动设置未完成。");
                if (process.ExitCode == ConfigurationFailedExitCode)
                    return new(false, "管理员配置未成功，本次登记变更已恢复。请检查当前账号的任务计划服务权限。");
                return new(false, "实际登记未确认：管理员配置助手失败或未能恢复本次变更。") { StateUnconfirmed = true };
            }

            // helper 返回成功后，父进程也重新确认自己的 SID 和任务，不能只采信进程退出码。
            if (GetCurrentUserSid() != expectedSid)
                return new(false, "实际登记未确认：当前 Windows 账号身份变化。") { StateUnconfirmed = true };
            VerifyTaskState(enabled, minimized, expectedSid, executablePath, highestAvailable);
            VerifyRunState(enabled, executablePath);
            return Success(enabled, highestAvailable);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223 && !helperStarted)
        {
            return new(false, "已取消管理员授权，登录自启动设置未完成。");
        }
        catch (Exception ex)
        {
            return new(false, (helperStarted ? "实际登记未确认：" : "管理员配置未完成：") + ex.GetBaseException().Message)
                { StateUnconfirmed = helperStarted };
        }
    }

    private static void VerifyTaskState(bool enabled, bool minimized, string sid, string executablePath, bool highestAvailable)
    {
        object? service = null;
        object? folder = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(type)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            var xml = ReadTaskXml(folder, AutoStartupRegistration.BuildTaskName(sid));
            if (enabled ? xml is null || !MatchesTask(xml, sid, executablePath, minimized, highestAvailable) : xml is not null)
                throw new InvalidOperationException("配置助手返回后，登录任务读回与目标状态不匹配。");
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    private static string GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("无法确认当前助手进程的 Windows 账号 SID。");
        using var process = Process.GetCurrentProcess();
        var sessionAccount = WindowsSessionIdentity.GetUserName(process.SessionId);
        if (string.IsNullOrWhiteSpace(sessionAccount) || sessionAccount.EndsWith('\\'))
            throw new InvalidOperationException("无法确认当前 Windows 会话的登录账号，未更改自启动设置。");
        var sessionSid = ((SecurityIdentifier)new NTAccount(sessionAccount).Translate(typeof(SecurityIdentifier))).Value;
        if (!string.Equals(sid, sessionSid, StringComparison.Ordinal))
            throw new StartupIdentityMismatchException("助手进程账号与当前登录桌面账号不同，未更改自启动设置。请在登录账号下运行助手，不能使用其他管理员账号的凭据。");
        return sid;
    }

    private static string GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(Path.GetFileName(executablePath), "MultiplayerHoeingAssistant.exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executablePath))
            throw new InvalidOperationException("必须从助手自身的可执行文件配置登录自启动。");
        return Path.GetFullPath(executablePath);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsAdministratorAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        // UAC 限制令牌中的管理员 SID 是 deny-only，仍在 Groups 内；IsInRole 只能判断当前是否已提权。
        return identity.Groups?.Any(group => group is SecurityIdentifier groupSid
            && groupSid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) == true;
    }

    private static bool RequiresAdministratorCompatibility(string executablePath)
    {
        const string layersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        using var userKey = Registry.CurrentUser.OpenSubKey(layersPath);
        using var machineKey = Registry.LocalMachine.OpenSubKey(layersPath);
        return HasRunAsAdministrator(userKey?.GetValue(executablePath) as string)
            || HasRunAsAdministrator(machineKey?.GetValue(executablePath) as string);
    }

    private static bool HasRunAsAdministrator(string? flags)
        => flags?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(flag => string.Equals(flag, "RUNASADMIN", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsAccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException or SecurityException
                || current.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070522)
                || current is Win32Exception { NativeErrorCode: 5 or 1314 }) return true;
        }
        return false;
    }

    private static AutoStartupRegistrationResult Failure(string message, Exception exception)
        => new(false, message + "：" + exception.GetBaseException().Message);

    private static AutoStartupRegistrationResult Success(bool enabled, bool highestAvailable)
        => new(true, !enabled ? "已关闭当前账号的登录自启动。"
            : highestAvailable ? "已启用当前账号各登录会话的自启动（支持管理员权限）。"
            : "已启用当前账号各登录会话的普通权限自启动。");

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
