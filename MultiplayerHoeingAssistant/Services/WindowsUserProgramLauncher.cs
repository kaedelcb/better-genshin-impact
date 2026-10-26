using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>通过一次性任务进入指定账号的既有桌面；由该会话内的短进程启动程序并返回实际创建结果。</summary>
internal static class WindowsUserProgramLauncher
{
    internal const string HelperArgument = "--startup-user-program";
    private const string Prefix = "NexusBGI_ProgramLaunch_";
    internal sealed record UserSession(int SessionId, string UserSid, string AccountName);
    private sealed record LaunchRequest(string Path, string Arguments);
    private sealed record LaunchReply(bool Success, int ProcessId, string Message);

    internal static UserSession SelectSession(string userSid, string accountName, IReadOnlyList<UserSession> sessions)
    {
        var matches = sessions.Where(s => s.SessionId > 0 && s.UserSid == userSid).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Windows 用户「{accountName}」尚未登录，无法在其桌面启动程序；不会改在当前用户中启动。"),
            _ => throw new InvalidOperationException($"Windows 用户「{accountName}」有多个登录会话，无法仅凭用户名确定桌面；未启动程序。")
        };
    }

    internal static string NormalizeAccountName(string userName)
    {
        var name = userName.Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
            throw new InvalidOperationException("Windows 用户名无效。");
        if (name.StartsWith(@".\", StringComparison.Ordinal))
            return Environment.MachineName + name[1..];
        return name.Contains('\\') || name.Contains('@') ? name : Environment.MachineName + "\\" + name;
    }

    private static UserSession ResolveSession(string userName)
    {
        var accountName = NormalizeAccountName(userName);
        string sid;
        try { sid = ((SecurityIdentifier)new NTAccount(accountName).Translate(typeof(SecurityIdentifier))).Value; }
        catch (IdentityNotMappedException ex)
        {
            throw new InvalidOperationException($"找不到 Windows 用户「{userName.Trim()}」，请填写登录账号名称（可用 计算机名\\用户名）。", ex);
        }
        return SelectSession(sid, accountName, ReadSessions());
    }

    private static List<UserSession> ReadSessions()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 Windows 登录会话。");
        try
        {
            var result = new List<UserSession>();
            var size = Marshal.SizeOf<SessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(IntPtr.Add(buffer, i * size));
                // Active/Connected/Disconnected 均可保有登录桌面；不选择服务、监听或正在注销的会话。
                if (session.SessionId <= 0 || session.State is not (0 or 1 or 4)) continue;
                var user = QuerySessionString(session.SessionId, 5);
                if (string.IsNullOrWhiteSpace(user)) continue;
                var domain = QuerySessionString(session.SessionId, 7);
                var account = string.IsNullOrWhiteSpace(domain) ? new NTAccount(user) : new NTAccount(domain, user);
                var sid = ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
                result.Add(new(session.SessionId, sid, account.Value));
            }
            return result;
        }
        finally { WTSFreeMemory(buffer); }
    }

    private static string QuerySessionString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认 Windows 会话账号。");
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    private static bool SessionHasUser(int sessionId, string expectedSid)
    {
        // 标准账号短入口只查询自己所在的会话，不要求有权读取其他账号的会话信息。
        var user = QuerySessionString(sessionId, 5);
        if (string.IsNullOrWhiteSpace(user)) return false;
        var domain = QuerySessionString(sessionId, 7);
        var account = string.IsNullOrWhiteSpace(domain) ? new NTAccount(user) : new NTAccount(domain, user);
        return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value == expectedSid;
    }

    internal static async Task<string> StartAsync(string path, string arguments, string userName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = ResolveSession(userName);
        using var identity = WindowsIdentity.GetCurrent();
        var callerSid = identity.User ?? throw new InvalidOperationException("无法确认当前 Windows 账号。");
        if (callerSid.Value != target.UserSid && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("跨 Windows 用户启动程序需要管理员权限，请以管理员身份运行助手；未启动程序。");
        var executable = Path.Combine(AppContext.BaseDirectory, "MultiplayerHoeingAssistant.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("缺少同目录的助手程序，无法进入目标用户会话。", executable);
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("程序路径不存在。", path);

        var taskName = Prefix + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(callerSid, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (callerSid.Value != target.UserSid)
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(target.UserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(taskName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        object? scheduler = null, folder = null, definition = null, principal = null, settings = null;
        object? actions = null, action = null, registered = null, running = null;
        var registeredHere = false;
        var requestSent = false;
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            definition = ((dynamic)scheduler).NewTask(0);
            principal = ((dynamic)definition).Principal;
            ((dynamic)principal).UserId = target.UserSid;
            ((dynamic)principal).LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN，不存储密码、不自动登录。
            ((dynamic)principal).RunLevel = 0;
            settings = ((dynamic)definition).Settings;
            ((dynamic)settings).AllowDemandStart = true;
            ((dynamic)settings).DisallowStartIfOnBatteries = false;
            ((dynamic)settings).StopIfGoingOnBatteries = false;
            ((dynamic)settings).ExecutionTimeLimit = "PT0S"; // 不给第三方程序及其子进程附加寿命限制。
            actions = ((dynamic)definition).Actions;
            action = ((dynamic)actions).Create(0);
            ((dynamic)action).Path = executable;
            ((dynamic)action).Arguments = $"{HelperArgument} {taskName} {target.UserSid} {target.SessionId}";
            ((dynamic)action).WorkingDirectory = AppContext.BaseDirectory;
            timeout.Token.ThrowIfCancellationRequested();
            registered = ((dynamic)folder).RegisterTaskDefinition(taskName, definition, 2, target.UserSid, null, 3, null);
            registeredHere = true; // TASK_CREATE，绝不覆盖已存在的任务。
            timeout.Token.ThrowIfCancellationRequested();
            running = ((dynamic)registered).RunEx(null, 0x4 | 0x8, target.SessionId, target.UserSid);
            if (running == null) throw new InvalidOperationException("Windows 未返回启动实例，程序启动结果未确认。");
            await pipe.WaitForConnectionAsync(timeout.Token);
            ValidatePipeClient(pipe, target);
            // 登录/注销可能在登记任务期间发生；发送程序信息前再次确认目标身份和唯一会话。
            if (ResolveSession(userName) != target)
                throw new InvalidOperationException("目标 Windows 登录会话已变化，未发送程序启动请求。");
            timeout.Token.ThrowIfCancellationRequested();
            await WriteMessageAsync(pipe, new LaunchRequest(path, arguments), timeout.Token);
            requestSent = true;
            var reply = await ReadMessageAsync<LaunchReply>(pipe, timeout.Token);
            if (!reply.Success || reply.ProcessId <= 0)
                throw new InvalidOperationException(reply.Message);
            return $"已在 Windows 用户「{target.AccountName}」会话 {target.SessionId} 启动程序（PID {reply.ProcessId}）：{path}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("等待目标 Windows 用户启动回执超时，结果未确认；不会在当前用户中重试。");
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            throw new InvalidOperationException("Windows 拒绝跨用户启动，请确认助手具有管理员权限、目标用户有权访问助手和程序目录；未回退当前用户。", ex);
        }
        finally
        {
            // 请求发出后不能把“停止任务”当作撤回启动，以免杀掉已由用户要求启动的第三方程序。
            if (!requestSent && running != null)
                try { ((dynamic)running).Stop(0); } catch (COMException) { }
            if (registeredHere)
                try { ((dynamic)folder!).DeleteTask(taskName, 0); }
                catch (COMException ex) { Trace.WriteLine($"清理一次性启动任务 {taskName} 失败：{ex.Message}"); }
            foreach (var value in new[] { running, registered, action, actions, settings, principal, definition, folder, scheduler })
                if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    private static void ValidatePipeClient(NamedPipeServerStream pipe, UserSession target)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认目标用户启动进程。");
        using var process = Process.GetProcessById(checked((int)processId));
        if (process.SessionId != target.SessionId)
            throw new InvalidOperationException("启动进程不在目标 Windows 会话，拒绝发送程序信息。");
        using var processHandle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (processHandle.IsInvalid || !OpenProcessToken(processHandle, 0x8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认目标启动进程的账号。");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            if (identity.User?.Value != target.UserSid)
                throw new InvalidOperationException("启动进程的 Windows 账号不匹配，拒绝发送程序信息。");
    }

    internal static bool IsHelperInvocation(string[] args)
        => args.Any(arg => string.Equals(arg, HelperArgument, StringComparison.OrdinalIgnoreCase));

    internal static async Task<int> RunHelperAsync(string[] args)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            if (args.Length != 4 || args[0] != HelperArgument || !args[1].StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(args[1][Prefix.Length..], "N", out _)
                || identity.User?.Value != args[2] || !int.TryParse(args[3], out var sessionId)
                || sessionId <= 0 || process.SessionId != sessionId
                || !SessionHasUser(sessionId, args[2]))
                return 2;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(timeout.Token);
            var request = await ReadMessageAsync<LaunchRequest>(pipe, timeout.Token);
            LaunchReply reply;
            try
            {
                if (!Path.IsPathFullyQualified(request.Path) || !File.Exists(request.Path))
                    throw new InvalidOperationException("目标用户无法访问程序路径，未启动程序。");
                // 只由已确认身份的会话内进程启动，保留程序路径、工作目录和原始参数。
                using var child = Process.Start(new ProcessStartInfo
                {
                    FileName = request.Path,
                    Arguments = request.Arguments,
                    WorkingDirectory = Path.GetDirectoryName(request.Path)!,
                    UseShellExecute = true
                });
                reply = child == null
                    ? new(false, 0, "Windows 已处理启动请求，但未返回新进程，程序实际启动结果未确认。")
                    : new(true, child.Id, "已创建程序进程");
            }
            catch (Exception ex) { reply = new(false, 0, "目标用户启动程序失败：" + ex.Message); }
            await WriteMessageAsync(pipe, reply, timeout.Token);
            return reply.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Trace.WriteLine("目标用户程序启动入口失败：" + ex.Message);
            return 1;
        }
    }

    private static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > 128 * 1024) throw new InvalidOperationException("程序启动参数过长。");
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<T> ReadMessageAsync<T>(Stream stream, CancellationToken ct)
    {
        var length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is <= 0 or > 128 * 1024) throw new InvalidOperationException("程序启动回执格式无效。");
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidOperationException("程序启动回执为空。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionInfo { public int SessionId; public IntPtr StationName; public int State; }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, int access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
}
