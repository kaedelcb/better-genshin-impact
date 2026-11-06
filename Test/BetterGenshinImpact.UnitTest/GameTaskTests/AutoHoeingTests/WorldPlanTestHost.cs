using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Instance;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Explicit opt-in for tests that construct production services using App.GetLogger.
/// Supplies an isolated, unstarted instance context before App's host is built; it never
/// opens a pipe, forwards activation to a running BetterGI, starts a host, or elevates.
/// </summary>
internal static class WorldPlanTestHost
{
    [ModuleInitializer]
    internal static void InitializeIsolatedInstance()
    {
        if (Environment.GetEnvironmentVariable("BGI_UNIT_TEST_ISOLATED_INSTANCE") != "1") return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The isolated BetterGI service tests require Windows.");

        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Run this explicit service-test host from an already elevated shell; the fixture does not elevate.");
        }

        if (InstanceBootstrap.Current is not null)
            throw new InvalidOperationException("The isolated test context must be installed before BetterGI instance initialization.");

        var runId = $"p{Environment.ProcessId}-{Guid.NewGuid():N}";
        var isolatedUser = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BetterGI-unit-test-" + runId, "User"));
        Directory.CreateDirectory(isolatedUser);

        // Preserve the package/Assets base. Only User reads and writes are redirected.
        var userGate = typeof(Global).GetField("UserRootGate", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(Global).FullName, "UserRootGate");
        var userRoot = typeof(Global).GetField("_userRoot", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(Global).FullName, "_userRoot");
        lock (userGate.GetValue(null)!) userRoot.SetValue(null, isolatedUser);

        var context = new InstanceContext(BetterGiInstanceType.ChildSession,
            "BetterGI.unit-tests." + runId, rootSessionId: null);
        var constructor = typeof(InstanceBootstrap).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Length == 3
                && candidate.GetParameters()[0].ParameterType == typeof(InstanceContext));
        var bootstrap = (InstanceBootstrap)constructor.Invoke(new object?[] { context, null, null });
        var current = typeof(InstanceBootstrap).GetProperty(nameof(InstanceBootstrap.Current), BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMemberException(typeof(InstanceBootstrap).FullName, nameof(InstanceBootstrap.Current));
        current.SetValue(null, bootstrap);

        var config = typeof(ConfigService).GetField("<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(ConfigService).FullName, "<Config>k__BackingField");
        if (config.GetValue(null) == null) config.SetValue(null, new AllConfig());
    }
}
