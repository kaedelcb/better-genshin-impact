using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Rename preserves this identity; equal bytes in a different file never prove ownership.</summary>
internal static class ConfigTransferFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong Volume; public ulong Low; public ulong High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdInfo information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    internal static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream.SafeFileHandle);
    }

    internal static string Read(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
            throw new IOException("无法确认迁移文件的物理身份。", new Win32Exception(Marshal.GetLastWin32Error()));
        return $"{info.Volume:X16}:{info.Low:X16}{info.High:X16}";
    }

    internal static string CanonicalDirectory(string directory)
    {
        using var handle = CreateFileW(directory, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("无法确认配置目录身份。", new Win32Exception(Marshal.GetLastWin32Error()));
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity)
            throw new IOException("无法规范化配置目录路径。", new Win32Exception(Marshal.GetLastWin32Error()));
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.TrimEndingDirectorySeparator(path);
    }
}
