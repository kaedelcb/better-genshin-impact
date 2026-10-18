using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class AutoStartupRegistrationTests
{
    private const string UserSid = "S-1-5-21-123456789-234567890-345678901-1001";
    private const string OtherUserSid = "S-1-5-21-123456789-234567890-345678901-1002";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Theory]
    [InlineData(true, " --minimized --no-auto-launch")]
    [InlineData(false, " --no-auto-launch")]
    public void BuildCommand_UsesAbsolutePathAndExpectedArguments(bool minimized, string expectedArguments)
    {
        var relativePath = Path.Combine("bin", "MultiplayerHoeingAssistant.exe");

        var command = AutoStartupRegistration.BuildCommand(relativePath, minimized);

        Assert.Equal($"\"{Path.GetFullPath(relativePath)}\"{expectedArguments}", command);
    }

    [Fact]
    public void BuildLauncherCommand_UsesAnIndependentExecutableSoMainProgramElevationCannotBlockLogon()
    {
        var executable = Path.GetFullPath(Path.Combine("助手 目录", "MultiplayerHoeingAssistant.exe"));
        Assert.Equal($"\"{Path.Combine(Path.GetDirectoryName(executable)!, "NexusBGI.Startup.exe")}\"",
            AutoStartupRegistration.BuildLauncherCommand(executable));
        Assert.DoesNotContain("MultiplayerHoeingAssistant.exe", AutoStartupRegistration.BuildLauncherCommand(executable));
        Assert.Throws<InvalidOperationException>(() => AutoStartupRegistration.BuildLauncherCommand(
            Path.Combine(Path.GetPathRoot(executable)!, new string('a', 250), "MultiplayerHoeingAssistant.exe")));
    }

    [Fact]
    public void HistoricalValueNames_ContainsOnlyTheKnownLegacyAssistantValue()
    {
        Assert.Equal([AutoStartupRegistration.LegacyValueName], AutoStartupRegistration.HistoricalValueNames);
    }

    [Theory]
    [InlineData("managed-file")]
    [InlineData("managed-directory")]
    [InlineData("com-file")]
    [InlineData("com-directory")]
    public void ReadTaskXml_MissingTaskReturnsNullForManagedAndComExceptionMappings(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "managed-file" => new FileNotFoundException("任务不存在"),
            "managed-directory" => new DirectoryNotFoundException("任务目录不存在"),
            "com-file" => new COMException("任务不存在", unchecked((int)0x80070002)),
            "com-directory" => new COMException("任务目录不存在", unchecked((int)0x80070003)),
            _ => throw new ArgumentException(nameof(failureKind))
        };

        Assert.Null(WindowsAutoStartupRegistration.ReadTaskXml(new ThrowingTaskFolder(failure), "missing-task"));
    }

    [Theory]
    [InlineData("managed-access-denied")]
    [InlineData("com-access-denied")]
    [InlineData("other-io")]
    public void ReadTaskXml_DoesNotTreatAccessDeniedOrOtherFailuresAsAMissingTask(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "managed-access-denied" => new UnauthorizedAccessException("禁止读取任务"),
            "com-access-denied" => new COMException("禁止读取任务", unchecked((int)0x80070005)),
            "other-io" => new IOException("其他读取失败"),
            _ => throw new ArgumentException(nameof(failureKind))
        };

        var actual = Assert.ThrowsAny<Exception>(() =>
            WindowsAutoStartupRegistration.ReadTaskXml(new ThrowingTaskFolder(failure), "unreadable-task"));

        Assert.Same(failure, actual);
    }

    [Fact]
    [Trait("Category", "WindowsTaskSchedulerReadOnly")]
    public void ReadTaskXml_NativeWindowsSchedulerMissingTaskReturnsNullWithoutRegisteringAnything()
    {
        Assert.True(OperatingSystem.IsWindows(), "此只读原生回归需要 Windows 任务计划服务。");
        object? service = null;
        object? folder = null;
        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            service = Activator.CreateInstance(schedulerType)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");

            var result = WindowsAutoStartupRegistration.ReadTaskXml(folder,
                "NexusBGI_ReadOnlyMissing_" + Guid.NewGuid().ToString("N"));

            Assert.Null(result);
        }
        finally
        {
            if (folder is not null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
    }

    [Fact]
    public void BuildTaskName_IsStableForOneUserAndSeparateForDifferentUsers()
    {
        var taskName = AutoStartupRegistration.BuildTaskName(UserSid);

        Assert.Equal(taskName, AutoStartupRegistration.BuildTaskName(UserSid));
        Assert.NotEqual(taskName, AutoStartupRegistration.BuildTaskName(OtherUserSid));
        Assert.DoesNotContain('\\', taskName);
        Assert.DoesNotContain('/', taskName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("汐")]
    [InlineData("S-1-5-21-123456789-234567890-345678901-1001\\OtherTask")]
    public void BuildTaskName_RejectsNonSidTaskIdentity(string userSid)
    {
        Assert.ThrowsAny<ArgumentException>(() => AutoStartupRegistration.BuildTaskName(userSid));
    }

    [Fact]
    public void BuildTaskXml_UsesTheSameAccountForLogonTriggerAndElevatedInteractiveProcess()
    {
        var task = ParseTask(minimized: false);
        var trigger = Assert.Single(task.Element(TaskNamespace + "Triggers")!.Elements());
        var principal = Assert.Single(task.Element(TaskNamespace + "Principals")!.Elements());

        Assert.Equal(TaskNamespace + "LogonTrigger", trigger.Name);
        Assert.Equal("false", trigger.Element(TaskNamespace + "Enabled")?.Value);
        Assert.Equal(UserSid, trigger.Element(TaskNamespace + "UserId")?.Value);
        Assert.Equal(UserSid, principal.Element(TaskNamespace + "UserId")?.Value);
        Assert.Equal("InteractiveToken", principal.Element(TaskNamespace + "LogonType")?.Value);
        Assert.Equal("HighestAvailable", principal.Element(TaskNamespace + "RunLevel")?.Value);
        Assert.Equal(AutoStartupRegistration.TaskOwnershipMarker,
            task.Element(TaskNamespace + "RegistrationInfo")?.Element(TaskNamespace + "Source")?.Value);
    }

    [Fact]
    public void BuildTaskXml_StandardAccountUsesItsOwnInteractiveTokenWithoutElevation()
    {
        var task = XDocument.Parse(AutoStartupRegistration.BuildTaskXml(
            Path.GetFullPath(Path.Combine("bin", "MultiplayerHoeingAssistant.exe")), UserSid,
            minimized: false, highestAvailable: false)).Root!;
        var principal = Assert.Single(task.Element(TaskNamespace + "Principals")!.Elements());

        Assert.Equal(UserSid, principal.Element(TaskNamespace + "UserId")?.Value);
        Assert.Equal("InteractiveToken", principal.Element(TaskNamespace + "LogonType")?.Value);
        Assert.Equal("LeastPrivilege", principal.Element(TaskNamespace + "RunLevel")?.Value);
    }

    [Theory]
    [InlineData(false, "--no-auto-launch")]
    [InlineData(true, "--minimized --no-auto-launch")]
    public void BuildTaskXml_PreservesTheExecutableWorkingDirectoryAndWindowPreference(bool minimized,
        string expectedArguments)
    {
        var executablePath = Path.Combine(Path.GetTempPath(), "汐 & BGI", "Tools", "MultiplayerHoeingAssistant.exe");
        var task = XDocument.Parse(AutoStartupRegistration.BuildTaskXml(executablePath, UserSid, minimized)).Root!;
        var action = Assert.Single(task.Element(TaskNamespace + "Actions")!.Elements());

        Assert.Equal(TaskNamespace + "Exec", action.Name);
        Assert.Equal(Path.GetFullPath(executablePath), action.Element(TaskNamespace + "Command")?.Value);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(executablePath)),
            action.Element(TaskNamespace + "WorkingDirectory")?.Value);
        Assert.Equal(expectedArguments, action.Element(TaskNamespace + "Arguments")?.Value);
    }

    [Fact]
    public void BuildTaskXml_AllowsLongRunningAssistantsAcrossRdpSessionsAndOnBattery()
    {
        var settings = ParseTask(minimized: false).Element(TaskNamespace + "Settings")!;

        Assert.Equal("Parallel", settings.Element(TaskNamespace + "MultipleInstancesPolicy")?.Value);
        Assert.Equal("true", settings.Element(TaskNamespace + "AllowStartOnDemand")?.Value);
        Assert.Equal("PT0S", settings.Element(TaskNamespace + "ExecutionTimeLimit")?.Value);
        Assert.Equal("false", settings.Element(TaskNamespace + "DisallowStartIfOnBatteries")?.Value);
        Assert.Equal("false", settings.Element(TaskNamespace + "StopIfGoingOnBatteries")?.Value);
    }

    [Fact]
    public void MatchesTask_AcceptsWindowsOmittingSchemaDefaultsThatPreserveTheRequestedBehavior()
    {
        var task = ParseTask(minimized: false);
        Assert.True(MatchesConstructedTask(task));
        var settings = task.Element(TaskNamespace + "Settings")!;
        foreach (var name in new[] { "Enabled", "AllowStartOnDemand", "RunOnlyIfNetworkAvailable", "RunOnlyIfIdle", "DisallowStartOnRemoteAppSession" })
            settings.Element(TaskNamespace + name)!.Remove();

        Assert.True(MatchesConstructedTask(task));
    }

    [Theory]
    [InlineData("LogonTrigger", "Enabled")]
    [InlineData("Settings", "DisallowStartIfOnBatteries")]
    [InlineData("Settings", "ExecutionTimeLimit")]
    [InlineData("LogonTrigger", "ExecutionTimeLimit")]
    [InlineData("Settings", "MultipleInstancesPolicy")]
    public void MatchesTask_RejectsOmittedValuesWhoseDefaultsWouldRestrictTheAssistant(string parent, string name)
    {
        var task = ParseTask(minimized: false);
        task.Descendants(TaskNamespace + parent).Single().Element(TaskNamespace + name)!.Remove();

        Assert.False(MatchesConstructedTask(task));
    }

    [Theory]
    [InlineData("LogonTrigger", "Enabled", "true")]
    [InlineData("Settings", "AllowStartOnDemand", "false")]
    [InlineData("Settings", "RunOnlyIfNetworkAvailable", "true")]
    [InlineData("Settings", "DisallowStartIfOnBatteries", "true")]
    public void MatchesTask_RejectsExplicitBooleanSettingsThatChangeTheRequestedBehavior(string parent,
        string name, string value)
    {
        var task = ParseTask(minimized: false);
        task.Descendants(TaskNamespace + parent).Single().Element(TaskNamespace + name)!.Value = value;

        Assert.False(MatchesConstructedTask(task));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConfigurationArguments_RoundTripTheRequestedSettingForTheSameAccount(bool enabled, bool minimized)
    {
        var arguments = AutoStartupRegistration.BuildConfigurationArguments(enabled, minimized, UserSid);

        Assert.True(AutoStartupRegistration.TryParseConfigurationArguments(arguments, UserSid,
            out var parsedEnabled, out var parsedMinimized));
        Assert.Equal(enabled, parsedEnabled);
        Assert.Equal(minimized, parsedMinimized);
        Assert.False(AutoStartupRegistration.TryParseConfigurationArguments(arguments, OtherUserSid, out _, out _));
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurationArguments))]
    public void TryParseConfigurationArguments_RejectsMalformedHelperRequests(string[] arguments)
    {
        Assert.False(AutoStartupRegistration.TryParseConfigurationArguments(arguments, UserSid, out _, out _));
    }

    [Theory]
    [InlineData(false, true, true, 0, true)]
    [InlineData(true, false, false, 0, true)]
    [InlineData(true, false, true, 1, false)]
    public void ConfigureAutoStartup_FailedRegistrationPreservesThePreviousSettingAndRefreshesBindings(
        bool previouslyEnabled, bool previouslyMinimized, bool requestedEnabled, int requestedMode,
        bool allowElevation)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "设置入口测试必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var config = new AssistConfig
        {
            AutoLaunchOnBoot = previouslyEnabled,
            AutoLaunchOnBootMinimized = previouslyMinimized
        };
        var viewModel = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, config);
        var requests = new List<(bool Enabled, bool Minimized, bool AllowElevation)>();
        viewModel.AutoStartupApply = (enabled, minimized, elevation) =>
        {
            requests.Add((enabled, minimized, elevation));
            return new AutoStartupRegistrationResult(false, "测试拒绝登记");
        };
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, change) => changedProperties.Add(change.PropertyName);

        var result = viewModel.ConfigureAutoStartup(requestedEnabled, requestedMode, allowElevation);

        Assert.False(result.Success);
        Assert.Equal("测试拒绝登记", result.Message);
        Assert.Equal((requestedEnabled, requestedMode == 1, allowElevation), Assert.Single(requests));
        Assert.Equal(previouslyEnabled, config.AutoLaunchOnBoot);
        Assert.Equal(previouslyMinimized, config.AutoLaunchOnBootMinimized);
        Assert.Equal(previouslyEnabled, viewModel.AutoLaunchOnBoot);
        Assert.Equal(previouslyMinimized ? 1 : 0, viewModel.AutoLaunchOnBootModeIndex);
        Assert.Contains(nameof(MainViewModel.AutoLaunchOnBoot), changedProperties);
        Assert.Contains(nameof(MainViewModel.AutoLaunchOnBootModeIndex), changedProperties);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ConfigureAutoStartup_FailedSaveRestoresPreviousSettingsAndReportsIncompleteRecovery(
        bool configurationRestored, bool registrationRestored)
    {
        var (viewModel, config) = ConfiguredViewModel(enabled: false, minimized: true);
        var requests = new List<(bool Enabled, bool Minimized, bool AllowElevation)>();
        viewModel.AutoStartupApply = (enabled, minimized, elevation) =>
        {
            requests.Add((enabled, minimized, elevation));
            return new AutoStartupRegistrationResult(requests.Count == 1 || registrationRestored,
                "测试登记恢复结果");
        };
        var saves = new List<(bool Enabled, bool Minimized)>();
        viewModel.AutoStartupPersist = () =>
        {
            saves.Add((config.AutoLaunchOnBoot, config.AutoLaunchOnBootMinimized));
            return saves.Count == 1
                ? new AutoStartupRegistrationResult(false, "测试首次保存失败")
                : new AutoStartupRegistrationResult(configurationRestored, "测试配置恢复结果");
        };

        var result = viewModel.ConfigureAutoStartup(enabled: true, modeIndex: 0, allowElevation: false);

        Assert.False(result.Success);
        Assert.Contains("测试首次保存失败", result.Message);
        if (configurationRestored && registrationRestored)
        {
            Assert.Contains("已恢复原来的自启动设置", result.Message);
            Assert.DoesNotContain("实际自启动状态未确认", result.Message);
        }
        else Assert.Contains("实际自启动状态未确认", result.Message);
        Assert.Equal(new[] { (true, false, false), (false, true, false) }, requests);
        Assert.Equal(new[] { (true, false), (false, true) }, saves);
        Assert.False(config.AutoLaunchOnBoot);
        Assert.True(config.AutoLaunchOnBootMinimized);
        Assert.False(viewModel.AutoLaunchOnBoot);
        Assert.Equal(1, viewModel.AutoLaunchOnBootModeIndex);
    }

    [Fact]
    public void ConfigureAutoStartup_SucceedsOnlyAfterRegistrationAndPersistenceAreConfirmed()
    {
        var (viewModel, config) = ConfiguredViewModel(enabled: false, minimized: true);
        var operations = new List<string>();
        viewModel.AutoStartupApply = (enabled, minimized, elevation) =>
        {
            Assert.True(enabled);
            Assert.False(minimized);
            Assert.False(elevation);
            Assert.False(config.AutoLaunchOnBoot);
            Assert.True(config.AutoLaunchOnBootMinimized);
            operations.Add("registration");
            return new AutoStartupRegistrationResult(true, "测试登记成功");
        };
        viewModel.AutoStartupPersist = () =>
        {
            Assert.Equal(new[] { "registration" }, operations);
            Assert.True(config.AutoLaunchOnBoot);
            Assert.False(config.AutoLaunchOnBootMinimized);
            operations.Add("persistence");
            return new AutoStartupRegistrationResult(true, "测试保存确认成功");
        };

        var result = viewModel.ConfigureAutoStartup(enabled: true, modeIndex: 0, allowElevation: false);

        Assert.True(result.Success);
        Assert.Equal(new[] { "registration", "persistence" }, operations);
        Assert.True(viewModel.AutoLaunchOnBoot);
        Assert.Equal(0, viewModel.AutoLaunchOnBootModeIndex);
    }

    public static IEnumerable<object[]> InvalidConfigurationArguments()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { AutoStartupRegistration.ConfigurationArgument, "enable", "popup" }];
        yield return [new[] { "--another-command", "enable", "popup", UserSid }];
        yield return [new[] { AutoStartupRegistration.ConfigurationArgument, "unknown", "popup", UserSid }];
        yield return [new[] { AutoStartupRegistration.ConfigurationArgument, "enable", "unknown", UserSid }];
        yield return [new[] { AutoStartupRegistration.ConfigurationArgument, "enable", "popup", "汐" }];
        yield return [new[] { AutoStartupRegistration.ConfigurationArgument, "enable", "popup", UserSid, "extra" }];
    }

    private static XElement ParseTask(bool minimized) => XDocument.Parse(AutoStartupRegistration.BuildTaskXml(
        Path.GetFullPath(Path.Combine("bin", "MultiplayerHoeingAssistant.exe")), UserSid, minimized)).Root!;

    private static bool MatchesConstructedTask(XElement task) => WindowsAutoStartupRegistration.MatchesTask(
        task.ToString(SaveOptions.DisableFormatting), UserSid,
        Path.GetFullPath(Path.Combine("bin", "MultiplayerHoeingAssistant.exe")), minimized: false,
        highestAvailable: true);

    private static (MainViewModel ViewModel, AssistConfig Config) ConfiguredViewModel(bool enabled, bool minimized)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "设置入口测试必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var config = new AssistConfig { AutoLaunchOnBoot = enabled, AutoLaunchOnBootMinimized = minimized };
        var viewModel = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, config);
        return (viewModel, config);
    }

    public sealed class ThrowingTaskFolder(Exception failure)
    {
        public object GetTask(string taskName) => throw failure;
    }
}
