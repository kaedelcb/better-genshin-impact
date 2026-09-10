using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MultiplayerHoeingAssistant.Services;

internal static class WindowsSessionIdentity
{
    public static void VerifyPipeServer(System.IO.Pipes.NamedPipeClientStream pipe, int expectedPid)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (pid != (uint)expectedPid) throw new InvalidOperationException("状态管道服务端 PID 不匹配");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle handle, out uint pid);

    public static string GetUserName(int sessionId)
    {
        var user = Query(sessionId, 5);
        var domain = Query(sessionId, 7);
        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    public static bool Matches(string actual, string requested)
    {
        requested = requested.Trim();
        if (requested.Length == 0) return false;
        return string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase)
            || (!requested.Contains('\\') && string.Equals(actual.Split('\\')[^1], requested, StringComparison.OrdinalIgnoreCase));
    }

    public static long GetLogonTime(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 24, out var buffer, out var bytes))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (bytes < Marshal.SizeOf<SessionInfo>()) throw new InvalidOperationException("会话身份数据不完整");
            var info = Marshal.PtrToStructure<SessionInfo>(buffer);
            if (info.LogonTime <= 0) throw new InvalidOperationException("无法确认目标登录会话");
            return info.LogonTime;
        }
        finally { WTSFreeMemory(buffer); }
    }

    public static int[] GetSessionIds()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new int[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStructure<SessionEntry>(IntPtr.Add(buffer, i * Marshal.SizeOf<SessionEntry>())).Id;
            return result;
        }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfo
    {
        public int State, SessionId;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Station;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string User;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionEntry { public int Id; public IntPtr Station; public int State; }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr buffer, out int count);

    private static string Query(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try { return Marshal.PtrToStringUni(buffer) ?? ""; }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
}
