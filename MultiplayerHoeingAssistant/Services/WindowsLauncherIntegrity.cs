using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>与构建入口保持相同规则：仅移除产品启动文件意外继承的 Low 标签，不修改目录或用户数据。</summary>
internal static class WindowsLauncherIntegrity
{
    private const uint LabelSecurityInformation = 0x10;
    private const string LowIntegritySid = "S-1-16-4096";

    internal static bool IsProductLauncher(string installationRoot, string path)
    {
        var relative = Path.GetRelativePath(installationRoot, path).Replace('\\', '/');
        return KnownLauncherPaths().Any(candidate => relative.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> KnownLauncherPaths()
    {
        yield return "BetterGI.exe";
        foreach (var tools in new[] { "Tools", "Tool" })
        foreach (var assistant in new[] { "MultiplayerHoeingAssistant", "MHA" })
        foreach (var name in new[] { "MultiplayerHoeingAssistant.exe", "NexusBGI.Startup.exe" })
            yield return $"{tools}/{assistant}/{name}";
    }

    internal static void EnsureExistingLaunchersSafe(string installationRoot)
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var relative in KnownLauncherPaths())
        {
            var path = Path.Combine(installationRoot, relative);
            if (File.Exists(path) && ReadLabel(path) == LowIntegritySid)
                throw new IOException("启动文件仍为 Low 完整性，先前故障尚未修复：" + path);
        }
    }

    internal static string ToNativePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    internal static bool NormalizeUpdatedLauncher(string installationRoot, string path)
    {
        if (!OperatingSystem.IsWindows() || !IsProductLauncher(installationRoot, path)) return false;
        path = Path.GetFullPath(path);
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (ReadLabel(path, out descriptor, out var sacl) != LowIntegritySid) return false;
            // SetNamedSecurityInfo 会重新继承目录的 Low 标签；按构建入口使用单文件安全描述符写回。
            Marshal.WriteInt16(sacl, 4, 0);
            if (!SetFileSecurity(ToNativePath(path), LabelSecurityInformation, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ReadLabel(path) is not null)
                throw new IOException("启动文件仍带有强制完整性标签：" + path);
            SHChangeNotify(0x800, 0x1005, path, IntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            throw new IOException("启动文件完整性检查失败，不能自动重启：" + path, ex);
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    internal static string? ReadLabel(string path)
    {
        IntPtr descriptor = IntPtr.Zero;
        try { return ReadLabel(Path.GetFullPath(path), out descriptor, out _); }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    private static string? ReadLabel(string path, out IntPtr descriptor, out IntPtr sacl)
    {
        var error = GetNamedSecurityInfo(ToNativePath(path), 1, LabelSecurityInformation,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out sacl, out descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        if (sacl == IntPtr.Zero) return null;
        var count = (ushort)Marshal.ReadInt16(sacl, 4);
        for (uint index = 0; index < count; index++)
        {
            if (!GetAce(sacl, index, out var ace)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Marshal.ReadByte(ace) != 0x11) continue;
            if (!ConvertSidToStringSid(IntPtr.Add(ace, 8), out var text)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(text); }
            finally { LocalFree(text); }
        }
        return null;
    }

    [DllImport("advapi32.dll", EntryPoint = "GetNamedSecurityInfoW", CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfo(string path, uint type, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
    [DllImport("advapi32.dll", EntryPoint = "ConvertSidToStringSidW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);
    [DllImport("advapi32.dll", EntryPoint = "SetFileSecurityW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string path, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr unused);
}
