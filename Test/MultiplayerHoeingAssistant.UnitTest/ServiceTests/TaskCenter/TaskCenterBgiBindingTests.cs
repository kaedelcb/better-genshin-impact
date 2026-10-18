using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>Exercise the real preparation entry without connecting a pipe or starting BetterGI.</summary>
public sealed class TaskCenterBgiBindingTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo EnsureMethod = typeof(MainViewModel)
        .GetMethod("EnsureTaskCenterExecutionReadyAsync", PrivateInstance)!;

    [Fact]
    public async Task MatchingProcessPassesBeforeEitherGateWithoutStartingClient()
    {
        using var process = Process.GetCurrentProcess();
        using var client = ReadyClient(Epoch(process));
        var vm = ViewModel(client, Environment.ProcessPath!);
        var ensureGate = Gate(vm, "_bgiEnsureGate");
        var channelGate = Gate(vm, "_externalChannelGate");
        await ensureGate.WaitAsync();
        await channelGate.WaitAsync();
        try
        {
            Assert.Null(await Ensure(vm).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, ensureGate.CurrentCount);
            Assert.Equal(0, channelGate.CurrentCount);
            Assert.Null(typeof(BgiExternalClient).GetField("_managerTask", PrivateInstance)!.GetValue(client));
        }
        finally
        {
            channelGate.Release();
            ensureGate.Release();
        }
    }

    [Fact]
    public async Task DifferentExistingProgramReportsBothPathsWithoutWaitingForStartup()
    {
        using var process = Process.GetCurrentProcess();
        using var client = ReadyClient(Epoch(process));
        var actual = Environment.ProcessPath!;
        var configured = OtherProgram(actual);
        var vm = ViewModel(client, configured);

        var failure = await Ensure(vm).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(failure);
        Assert.Contains("与配置的程序不一致", failure);
        Assert.Contains(actual, failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(configured, failure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("未发送任务", failure);
    }

    [Fact]
    public async Task MissingProcessReportsReadFailureRatherThanWrongConfiguration()
    {
        using var process = Process.GetCurrentProcess();
        using var client = ReadyClient(new BgiEpoch
        {
            ProcessId = int.MaxValue,
            StartTicksUtc = process.StartTime.ToUniversalTime().Ticks,
        });
        var vm = ViewModel(client, Environment.ProcessPath!);

        var failure = await Ensure(vm).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(failure);
        Assert.Contains("打开 BGI 进程失败", failure);
        Assert.DoesNotContain("与配置的程序不一致", failure);
        Assert.DoesNotContain("配置 BGI", failure);
    }

    [Fact]
    public async Task ChangedStartTimeStillRejectsTheCurrentPid()
    {
        using var process = Process.GetCurrentProcess();
        using var client = ReadyClient(new BgiEpoch
        {
            ProcessId = process.Id,
            StartTicksUtc = process.StartTime.ToUniversalTime().Ticks + 1,
        });
        var vm = ViewModel(client, Environment.ProcessPath!);

        var failure = await Ensure(vm).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(failure);
        Assert.Contains("进程已重启或连接身份已经变化", failure);
        Assert.DoesNotContain("与配置的程序不一致", failure);
    }

    [Fact]
    public async Task PreCancelledRequestWinsEvenWhenMatchingClientIsReady()
    {
        using var process = Process.GetCurrentProcess();
        using var client = ReadyClient(Epoch(process));
        var vm = ViewModel(client, Environment.ProcessPath!);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ensure(vm, cancellation.Token));
    }

    [Fact]
    public void ReadyClientLosingItsEpochDuringReconnectIsOnlyNotReady()
    {
        using var process = Process.GetCurrentProcess();
        using var client = ReadyClient(Epoch(process));
        var vm = ViewModel(client, Environment.ProcessPath!);
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.ServerEpoch))!.SetValue(client, null);

        var inspect = typeof(MainViewModel).GetMethod("InspectTaskCenterClientBinding", PrivateInstance)!;
        var result = ((BgiExternalClient? Client, string? Failure))inspect.Invoke(vm, null)!;

        Assert.Null(result.Client);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task ReadyWrongTargetArrivingDuringGateWaitIsRejectedBeforeProcessStartup()
    {
        using var process = Process.GetCurrentProcess();
        using var client = ReadyClient(Epoch(process));
        var configured = OtherProgram(Environment.ProcessPath!);
        // ObserverMode makes any accidental fall-through stop before process enumeration or startup.
        var vm = ViewModel(null, configured);
        var gate = Gate(vm, "_bgiEnsureGate");
        await gate.WaitAsync();
        Task<string?> pending;
        try
        {
            pending = Ensure(vm);
            Assert.False(pending.IsCompleted);
            Set(vm, "_externalClient", client);
        }
        finally
        {
            gate.Release();
        }

        var failure = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(failure);
        Assert.Contains("与配置的程序不一致", failure);
        Assert.Contains(configured, failure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("观察者模式", failure);
        Assert.Null(typeof(BgiExternalClient).GetField("_managerTask", PrivateInstance)!.GetValue(client));
    }

    [Fact]
    public void DeniedVmReadBreaksOldMainModuleButRealPreparationStillBindsAndRejectsWrongPath()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须在启动 testhost 前设置隔离 NEXUSBGI_DATA_ROOT。");
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(executable));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("Start-Sleep -Seconds 90");
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("无法创建自有权限夹具进程");
        try
        {
            var epoch = Epoch(helper);
            using var client = ReadyClient(epoch);
            var vm = ViewModel(client, executable);
            using var deniedRead = new DenyVmReadScope(helper.Id);
            using var withoutDebugToken = TokenWithoutDebugPrivilege();

            WindowsIdentity.RunImpersonated(withoutDebugToken, () =>
            {
                using var oldLookup = Process.GetProcessById(helper.Id);
                var oldFailure = Assert.Throws<Win32Exception>(() => _ = oldLookup.MainModule?.FileName);
                Assert.Equal(5, oldFailure.NativeErrorCode);

                // Ready fast paths complete synchronously, keeping both checks in this impersonation scope.
                Assert.Null(Ensure(vm).GetAwaiter().GetResult());
                vm.Config!.BgiPath = OtherProgram(executable);
                var wrongTarget = Ensure(vm).GetAwaiter().GetResult();
                Assert.NotNull(wrongTarget);
                Assert.Contains("与配置的程序不一致", wrongTarget);
                Assert.Contains(executable, wrongTarget, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(vm.Config.BgiPath, wrongTarget, StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            // Only this test's own helper is touched; the ACL scope has already restored its original DACL.
            if (!helper.HasExited) helper.Kill();
            Assert.True(helper.WaitForExit(5000), "自有权限夹具进程未退出");
        }
    }

    private static MainViewModel ViewModel(BgiExternalClient? client, string configuredPath)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须在启动 testhost 前设置隔离 NEXUSBGI_DATA_ROOT。");
        var vm = new MainViewModel();
        Set(vm, "_config", new AssistConfig { BgiPath = configuredPath, ObserverMode = true });
        Set(vm, "_externalClient", client);
        return vm;
    }

    private static BgiExternalClient ReadyClient(BgiEpoch epoch)
    {
        var client = new BgiExternalClient(AssistantDataDirectory.Root);
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.ServerEpoch))!.SetValue(client, epoch);
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.State))!.SetValue(client, BgiExternalLinkState.Ready);
        return client;
    }

    private static BgiEpoch Epoch(Process process) => new()
    {
        ProcessId = process.Id,
        StartTicksUtc = process.StartTime.ToUniversalTime().Ticks,
    };

    private static string OtherProgram(string actual)
    {
        var candidate = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(File.Exists(candidate));
        Assert.False(string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase));
        return candidate;
    }

    private static Task<string?> Ensure(MainViewModel vm, CancellationToken cancellation = default)
        => (Task<string?>)EnsureMethod.Invoke(vm, [cancellation])!;

    private static SemaphoreSlim Gate(MainViewModel vm, string name)
        => (SemaphoreSlim)typeof(MainViewModel).GetField(name, PrivateInstance)!.GetValue(vm)!;

    private static void Set(MainViewModel vm, string name, object? value)
        => typeof(MainViewModel).GetField(name, PrivateInstance)!.SetValue(vm, value);

    private static SafeAccessTokenHandle TokenWithoutDebugPrivilege()
    {
        const uint tokenDuplicate = 0x0002, tokenQuery = 0x0008, tokenImpersonate = 0x0004, tokenAdjustPrivileges = 0x0020;
        using var process = Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, tokenDuplicate | tokenQuery, out var original))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (original)
        {
            if (!DuplicateTokenEx(original, tokenQuery | tokenImpersonate | tokenAdjustPrivileges,
                    IntPtr.Zero, 2, 2, out var duplicate))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out var debugPrivilege))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                // Retain ordinary user privileges, including SeChangeNotifyPrivilege for path traversal.
                var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = debugPrivilege, Attributes = 0 };
                if (!AdjustTokenPrivileges(duplicate, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var error = Marshal.GetLastWin32Error();
                // ERROR_NOT_ALL_ASSIGNED is expected when the original token has no debug privilege.
                if (error != 0 && error != 1300) throw new Win32Exception(error);
                return duplicate;
            }
            catch
            {
                duplicate.Dispose();
                throw;
            }
        }
    }

    /// <summary>Preserve and restore only the owned helper's DACL; deny no access besides PROCESS_VM_READ.</summary>
    private sealed class DenyVmReadScope : IDisposable
    {
        private const uint DaclSecurityInformation = 4;
        private readonly SafeProcessHandle _process;
        private IntPtr _originalDescriptor;
        private IntPtr _originalDacl;

        internal DenyVmReadScope(int processId)
        {
            _process = OpenProcess(0x00020000 | 0x00040000, false, (uint)processId); // READ_CONTROL | WRITE_DAC
            if (_process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = GetSecurityInfo(_process, 6, DaclSecurityInformation,
                out _, out _, out _originalDacl, out _, out _originalDescriptor);
            if (error != 0)
            {
                _process.Dispose();
                throw new Win32Exception((int)error);
            }

            try
            {
                var bytes = new byte[GetSecurityDescriptorLength(_originalDescriptor)];
                Marshal.Copy(_originalDescriptor, bytes, 0, bytes.Length);
                var descriptor = new RawSecurityDescriptor(bytes, 0);
                var dacl = descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("夹具进程没有可保留的 DACL");
                using var identity = WindowsIdentity.GetCurrent();
                var sid = identity.User ?? throw new InvalidOperationException("缺少当前用户 SID");
                dacl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x0010, sid, false, null));
                var aclBytes = new byte[dacl.BinaryLength];
                dacl.GetBinaryForm(aclBytes, 0);
                var replacement = Marshal.AllocHGlobal(aclBytes.Length);
                try
                {
                    Marshal.Copy(aclBytes, 0, replacement, aclBytes.Length);
                    error = SetSecurityInfo(_process, 6, DaclSecurityInformation,
                        IntPtr.Zero, IntPtr.Zero, replacement, IntPtr.Zero);
                    if (error != 0) throw new Win32Exception((int)error);
                }
                finally
                {
                    Marshal.FreeHGlobal(replacement);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_originalDescriptor == IntPtr.Zero) return;
            var descriptor = _originalDescriptor;
            _originalDescriptor = IntPtr.Zero;
            try
            {
                var error = SetSecurityInfo(_process, 6, DaclSecurityInformation,
                    IntPtr.Zero, IntPtr.Zero, _originalDacl, IntPtr.Zero);
                if (error != 0) throw new Win32Exception((int)error, "恢复自有夹具进程 DACL 失败");
            }
            finally
            {
                LocalFree(descriptor);
                _process.Dispose();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint desiredAccess,
        IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState, uint length,
        IntPtr previousState, IntPtr returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeProcessHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(SafeProcessHandle handle, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
