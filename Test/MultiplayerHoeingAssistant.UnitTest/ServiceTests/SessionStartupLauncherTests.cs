using System.Security.Principal;
using System.Xml.Linq;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Startup;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class SessionStartupLauncherTests
{
    private const string UserSid = "S-1-5-21-123456789-234567890-345678901-1001";
    private const string OtherUserSid = "S-1-5-21-123456789-234567890-345678901-1002";
    private static readonly XNamespace TaskNamespace = AutoStartupRegistration.TaskXmlNamespace;
    private static readonly string AssistantDirectory = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "汐 & BGI", "Tools"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_SameAccountInTwoRdpSessionsPassesEachActualSessionWithoutAsSelf(bool minimized)
    {
        var taskXml = BuildTask(minimized).ToString(SaveOptions.DisableFormatting);
        var requests = new List<(object? Parameters, int Flags, int SessionId, string UserSid)>();

        foreach (var sessionId in new[] { 2, 8 })
            SessionStartupLauncher.Start(taskXml, AssistantDirectory, UserSid, sessionId, UserSid,
                (parameters, flags, actualSessionId, sid) => requests.Add((parameters, flags, actualSessionId, sid)));

        Assert.Equal(new[] { 2, 8 }, requests.Select(request => request.SessionId));
        Assert.All(requests, request =>
        {
            Assert.Null(request.Parameters);
            Assert.Equal(12, request.Flags);
            Assert.Equal(0, request.Flags & 1); // TASK_RUN_AS_SELF must not replace the task's elevated token.
            Assert.Equal(UserSid, request.UserSid);
        });
    }

    [Theory]
    [InlineData("LogonTrigger")]
    [InlineData("Principal")]
    public void Start_WindowsNormalizingTheCurrentAccountToAnNtAccountStillPassesItsSid(string accountElement)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User!.Value;
        var task = XDocument.Parse(AutoStartupRegistration.BuildTaskXml(
            Path.Combine(AssistantDirectory, "MultiplayerHoeingAssistant.exe"), currentSid, minimized: false)).Root!;
        task.Descendants(TaskNamespace + accountElement).Single().Element(TaskNamespace + "UserId")!.Value = identity.Name;
        var calls = 0;

        SessionStartupLauncher.Start(task.ToString(SaveOptions.DisableFormatting), AssistantDirectory,
            currentSid, 8, currentSid, (_, flags, sessionId, runSid) =>
            {
                calls++;
                Assert.Equal(12, flags);
                Assert.Equal(8, sessionId);
                Assert.Equal(currentSid, runSid);
            });

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("foreign-account")]
    [InlineData("different-session-owner")]
    [InlineData("session-zero")]
    [InlineData("negative-session")]
    public void Start_UntrustedIdentityOrNonInteractiveSessionNeverInvokesScheduler(string scenario)
    {
        var taskXml = BuildTask().ToString(SaveOptions.DisableFormatting);
        var identitySid = scenario == "foreign-account" ? OtherUserSid : UserSid;
        var sessionUserSid = scenario == "different-session-owner" ? OtherUserSid : identitySid;
        var sessionId = scenario switch { "session-zero" => 0, "negative-session" => -1, _ => 8 };
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => SessionStartupLauncher.Start(taskXml,
            AssistantDirectory, identitySid, sessionId, sessionUserSid, (_, _, _, _) => calls++));

        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("extra-action")]
    [InlineData("non-exec-action")]
    [InlineData("foreign-command")]
    [InlineData("relative-command")]
    [InlineData("injected-arguments")]
    [InlineData("argument-placeholder")]
    [InlineData("foreign-working-directory")]
    [InlineData("foreign-principal")]
    [InlineData("password-logon")]
    [InlineData("invalid-run-level")]
    [InlineData("extra-principal")]
    [InlineData("missing-principal-id")]
    [InlineData("foreign-action-context")]
    [InlineData("enabled-logon-trigger")]
    [InlineData("implicit-enabled-logon-trigger")]
    [InlineData("foreign-logon-trigger")]
    [InlineData("extra-logon-trigger")]
    [InlineData("disabled-task")]
    [InlineData("demand-start-disabled")]
    [InlineData("ignore-new-instance")]
    [InlineData("foreign-task-owner")]
    [InlineData("foreign-task-uri")]
    public void Start_TaskThatCanChangeAccountSessionOrLaunchTargetNeverInvokesScheduler(string mutation)
    {
        var task = BuildTask();
        var registration = task.Element(TaskNamespace + "RegistrationInfo")!;
        var principal = task.Element(TaskNamespace + "Principals")!.Element(TaskNamespace + "Principal")!;
        var actions = task.Element(TaskNamespace + "Actions")!;
        var action = actions.Element(TaskNamespace + "Exec")!;
        var triggers = task.Element(TaskNamespace + "Triggers")!;
        var trigger = triggers.Element(TaskNamespace + "LogonTrigger")!;
        var settings = task.Element(TaskNamespace + "Settings")!;
        switch (mutation)
        {
            case "extra-action": actions.Add(new XElement(action)); break;
            case "non-exec-action": action.Name = TaskNamespace + "ComHandler"; break;
            case "foreign-command":
                action.Element(TaskNamespace + "Command")!.Value = Path.Combine(
                    AssistantDirectory, "Other", "MultiplayerHoeingAssistant.exe");
                break;
            case "relative-command": action.Element(TaskNamespace + "Command")!.Value = "MultiplayerHoeingAssistant.exe"; break;
            case "injected-arguments": action.Element(TaskNamespace + "Arguments")!.Value = "--start-continuous-plan unwanted"; break;
            case "argument-placeholder": action.Element(TaskNamespace + "Arguments")!.Value = "--no-auto-launch $(Arg0)"; break;
            case "foreign-working-directory": action.Element(TaskNamespace + "WorkingDirectory")!.Value = Path.GetTempPath(); break;
            case "foreign-principal": principal.Element(TaskNamespace + "UserId")!.Value = OtherUserSid; break;
            case "password-logon": principal.Element(TaskNamespace + "LogonType")!.Value = "Password"; break;
            case "invalid-run-level": principal.Element(TaskNamespace + "RunLevel")!.Value = "Unrecognized"; break;
            case "extra-principal": principal.Parent!.Add(new XElement(principal)); break;
            case "missing-principal-id": principal.Attribute("id")!.Remove(); break;
            case "foreign-action-context": actions.SetAttributeValue("Context", "AnotherPrincipal"); break;
            case "enabled-logon-trigger": trigger.Element(TaskNamespace + "Enabled")!.Value = "true"; break;
            case "implicit-enabled-logon-trigger": trigger.Element(TaskNamespace + "Enabled")!.Remove(); break;
            case "foreign-logon-trigger": trigger.Element(TaskNamespace + "UserId")!.Value = OtherUserSid; break;
            case "extra-logon-trigger": triggers.Add(new XElement(trigger)); break;
            case "disabled-task": settings.SetElementValue(TaskNamespace + "Enabled", "false"); break;
            case "demand-start-disabled": settings.SetElementValue(TaskNamespace + "AllowStartOnDemand", "false"); break;
            case "ignore-new-instance": settings.SetElementValue(TaskNamespace + "MultipleInstancesPolicy", "IgnoreNew"); break;
            case "foreign-task-owner": registration.Element(TaskNamespace + "Source")!.Value = "UnrelatedApplication"; break;
            case "foreign-task-uri": registration.Element(TaskNamespace + "URI")!.Value = "\\UnrelatedTask"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        var calls = 0;

        Assert.Throws<InvalidOperationException>(() => SessionStartupLauncher.Start(
            task.ToString(SaveOptions.DisableFormatting), AssistantDirectory, UserSid, 8, UserSid,
            (_, _, _, _) => calls++));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Start_WindowsOmittingEnabledDemandStartAndOtherNonRestrictiveDefaultsStillUsesRequestedSession()
    {
        var task = BuildTask();
        var settings = task.Element(TaskNamespace + "Settings")!;
        foreach (var name in new[] { "Enabled", "AllowStartOnDemand", "RunOnlyIfNetworkAvailable",
                     "RunOnlyIfIdle", "DisallowStartOnRemoteAppSession" })
            settings.Element(TaskNamespace + name)?.Remove();
        var calls = 0;

        SessionStartupLauncher.Start(task.ToString(SaveOptions.DisableFormatting), AssistantDirectory,
            UserSid, 8, UserSid, (parameters, flags, sessionId, sid) =>
            {
                calls++;
                Assert.Null(parameters);
                Assert.Equal(12, flags);
                Assert.Equal(8, sessionId);
                Assert.Equal(UserSid, sid);
            });

        Assert.Equal(1, calls);
    }

    private static XElement BuildTask(bool minimized = false) => XDocument.Parse(
        AutoStartupRegistration.BuildTaskXml(Path.Combine(AssistantDirectory, "MultiplayerHoeingAssistant.exe"),
            UserSid, minimized)).Root!;
}
