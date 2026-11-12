using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>真实 Windows 标签验证：更新产物不能因继承 Low 而在重启后失去配置/IPC 权限；不启动任何产品。</summary>
public sealed class WindowsLauncherIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "launcher-integrity-" + Guid.NewGuid().ToString("N"));
    public WindowsLauncherIntegrityTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        Directory.CreateDirectory(_root);
        output.WriteLine($"ValidationHostIntegrity={ReadCurrentProcessIntegrity()}; Executable={Environment.ProcessPath}");
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("BetterGI.exe")]
    [InlineData("Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe")]
    [InlineData("Tool/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe")]
    [InlineData("Tools/MultiplayerHoeingAssistant/NexusBGI.Startup.exe")]
    [InlineData("Tools/MHA/MultiplayerHoeingAssistant.exe")]
    public void KnownLauncher_RemovesLowLabelWithoutChangingContentOrDirectory(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "isolated launcher fixture");
        SetLabel(path, "S:(ML;;NW;;;LW)");
        var directoryLabel = WindowsLauncherIntegrity.ReadLabel(Path.GetDirectoryName(path)!);
        Assert.Equal("S-1-16-4096", WindowsLauncherIntegrity.ReadLabel(path));

        Assert.True(WindowsLauncherIntegrity.NormalizeUpdatedLauncher(_root, path));
        Assert.Null(WindowsLauncherIntegrity.ReadLabel(path));
        Assert.Equal(directoryLabel, WindowsLauncherIntegrity.ReadLabel(Path.GetDirectoryName(path)!));
        Assert.Equal("isolated launcher fixture", File.ReadAllText(path));
        Assert.False(WindowsLauncherIntegrity.NormalizeUpdatedLauncher(_root, path));
    }

    [Theory]
    [InlineData("User/BetterGI.exe")]
    [InlineData("_update_backup/BetterGI.exe")]
    [InlineData("Other/MultiplayerHoeingAssistant.exe")]
    [InlineData("Tools/MultiplayerHoeingAssistant/third-party.exe")]
    public void NonProductPaths_KeepTheirLabels(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "unchanged");
        SetLabel(path, "S:(ML;;NW;;;LW)");
        Assert.False(WindowsLauncherIntegrity.NormalizeUpdatedLauncher(_root, path));
        Assert.Equal("S-1-16-4096", WindowsLauncherIntegrity.ReadLabel(path));
    }

    [Fact]
    public void ExplicitMediumLabel_IsPreserved()
    {
        var path = Path.Combine(_root, "BetterGI.exe");
        File.WriteAllText(path, "unchanged");
        SetLabel(path, "S:(ML;;NW;;;ME)");
        Assert.False(WindowsLauncherIntegrity.NormalizeUpdatedLauncher(_root, path));
        Assert.Equal("S-1-16-8192", WindowsLauncherIntegrity.ReadLabel(path));
    }

    [Fact]
    public void RealPackageApply_NormalizesInheritedLowBeforeReportingSuccess()
    {
        // 原状态文件/解压内容测试无法发现此故障，必须让真实解压在有继承标签的目录中新建 exe。
        SetLabel(_root, "S:(ML;OICI;NW;;;LW)");
        var user = Path.Combine(_root, "User");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "config.json"), "preserve-user");
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z");
        var result = BgiPackageUpdateService.Apply(fixture, _root, ["User"], [], null);
        Assert.True(result.Success, result.Error);
        Assert.Empty(result.SkippedFiles);
        Assert.Null(WindowsLauncherIntegrity.ReadLabel(Path.Combine(_root, "BetterGI.exe")));
        Assert.Equal("S-1-16-4096", WindowsLauncherIntegrity.ReadLabel(_root));
        Assert.Equal("S-1-16-4096", WindowsLauncherIntegrity.ReadLabel(Path.Combine(user, "config.json")));
        Assert.Equal("preserve-user", File.ReadAllText(Path.Combine(user, "config.json")));
    }

    [Fact]
    public void FailureAfterWritingLauncher_ReportsThatAutomaticRecoveryMustStayBlocked()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z");
        var result = BgiPackageUpdateService.Apply(fixture, _root, ["User"], [],
            new FailAfterLauncherWritten(Path.Combine(_root, "BetterGI.exe")));
        Assert.False(result.Success);
        Assert.True(result.RestartBlocked);
    }

    [Fact]
    public void FailedFirstLauncherWrite_DoesNotAllowAutomaticRecovery()
    {
        var executable = Path.Combine(_root, "BetterGI.exe");
        File.WriteAllText(executable, "original");
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z");
        using var held = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = BgiPackageUpdateService.Apply(fixture, _root, ["User"], [], null);
        Assert.NotEmpty(result.SkippedFiles);
        Assert.True(result.RestartBlocked);
        Assert.Equal("original", File.ReadAllText(executable));
    }

    private sealed class FailAfterLauncherWritten(string launcher) : IProgress<string>
    {
        public void Report(string value)
        {
            if (File.Exists(launcher)) throw new IOException("isolated failure before launcher verification");
        }
    }

    private static string ReadCurrentProcessIntegrity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.AccessToken.DangerousGetHandle();
        GetTokenInformation(token, 25, IntPtr.Zero, 0, out var size);
        if (size <= 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var data = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 25, data, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value;
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr data, int size, out int needed);

    private static void SetLabel(string path, string sddl)
    {
        path = WindowsLauncherIntegrity.ToNativePath(path);
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!SetFileSecurity(path, 0x10, descriptor)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string path, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
