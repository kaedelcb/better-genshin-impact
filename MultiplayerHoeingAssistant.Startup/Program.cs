using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace MultiplayerHoeingAssistant.Startup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var sessionId = -1;
        try
        {
            using var process = Process.GetCurrentProcess();
            sessionId = process.SessionId;
            if (args.Length != 0)
                throw new InvalidOperationException("登录启动器不接受命令行参数。");
            if (sessionId <= 0)
                throw new InvalidOperationException("登录启动器没有有效的交互会话。");
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value
                ?? throw new InvalidOperationException("无法确认登录启动器的 Windows 账号。");
            var sessionUserSid = CurrentSessionUserSid(sessionId);
            RunRegisteredTask(sid, sessionId, sessionUserSid);
            return 0;
        }
        catch (Exception ex)
        {
            WriteFailure(sessionId, ex);
            return 1;
        }
    }

    private static void RunRegisteredTask(string sid, int sessionId, string sessionUserSid)
    {
        object? scheduler = null;
        object? folder = null;
        object? task = null;
        object? runningTask = null;
        try
        {
            var executablePath = Path.Combine(AppContext.BaseDirectory, "MultiplayerHoeingAssistant.exe");
            if (!File.Exists(executablePath))
                throw new FileNotFoundException("登录启动器同目录中缺少助手程序。", executablePath);
            var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            scheduler = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException("无法连接 Windows 任务计划程序。");
            ((dynamic)scheduler).Connect();
            folder = ((dynamic)scheduler).GetFolder("\\");
            task = ((dynamic)folder).GetTask(SessionStartupLauncher.BuildTaskName(sid));
            if (!(bool)((dynamic)task).Enabled)
                throw new InvalidOperationException("助手登录启动任务已禁用。");
            string xml = ((dynamic)task).Xml;
            SessionStartupLauncher.Start(xml, AppContext.BaseDirectory, sid, sessionId, sessionUserSid,
                (parameters, flags, targetSessionId, targetSid) =>
                {
                    runningTask = ((dynamic)task).RunEx(parameters, flags, targetSessionId, targetSid);
                    if (runningTask == null)
                        throw new InvalidOperationException("任务计划程序未返回助手启动实例。");
                });
        }
        finally
        {
            ReleaseComObject(runningTask);
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(scheduler);
        }
    }

    private static string CurrentSessionUserSid(int sessionId)
    {
        var user = QuerySessionString(sessionId, 5); // WTSUserName
        var domain = QuerySessionString(sessionId, 7); // WTSDomainName
        if (string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("当前 Windows 会话没有登录账号。");
        var account = string.IsNullOrWhiteSpace(domain) ? new NTAccount(user) : new NTAccount(domain, user);
        return ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
    }

    private static string QuerySessionString(int sessionId, int infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buffer, out var bytesReturned))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法确认当前 Windows 登录会话。");
            if (buffer == IntPtr.Zero || bytesReturned < sizeof(char))
                return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                WTSFreeMemory(buffer);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    private static void WriteFailure(int sessionId, Exception exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusBGI");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"login-startup-session-{sessionId}.log");
            if (File.Exists(path) && new FileInfo(path).Length > 32 * 1024)
                File.Move(path, Path.ChangeExtension(path, ".previous.log"), overwrite: true);
            var message = $"{exception.GetType().Name}: {exception.Message}".Replace('\r', ' ').Replace('\n', ' ');
            if (message.Length > 2048)
                message = message[..2048];
            File.AppendAllText(path,
                $"{DateTimeOffset.Now:O} session={sessionId} HRESULT=0x{exception.HResult:X8} {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // 登录路径不可弹窗或再次提权；日志失败仍通过非零退出码报告失败。
        }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass,
        out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
