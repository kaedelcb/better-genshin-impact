using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 路径身份比较辅助：把现存前缀解析为 Windows 最终规范化路径，再附回尚不存在的尾段。
/// **用途限定**：演练隔离/运行实例同一性校验；无法解析时调用方必须拒绝，不得退回字符串猜测。
/// </summary>
internal static class PathIdentity
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private enum PathProbe
    {
        Exists,
        Missing,
        Unknown,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    /// <summary>
    /// 解析最终路径用于比较；返回 false 表示原始形态非法、前缀访问失败、句柄失败或命名空间不可比较。
    /// </summary>
    internal static bool TryCanonicalizeForComparison(string? path, out string canonical)
        => TryCanonicalizeForComparison(path, File.GetAttributes, OpenHandle, GetFinalPath, out canonical);

    /// <summary>生产共用核心；测试通过注入探测委托覆盖访问拒绝/句柄失败/最终路径失败。</summary>
    internal static bool TryCanonicalizeForComparison(
        string? path,
        Func<string, FileAttributes> readAttributes,
        Func<string, SafeFileHandle> openHandle,
        Func<SafeFileHandle, string?> getFinalPath,
        out string canonical)
    {
        canonical = "";
        if (!TryNormalizeLocalDriveAbsolute(path, out var full))
            return false;

        try
        {
            var current = full;
            var missingTail = new List<string>();
            FileAttributes attributes;
            while (true)
            {
                var probe = ProbePath(current, readAttributes, out var currentAttributes);
                if (probe == PathProbe.Unknown)
                    return false;
                if (probe == PathProbe.Exists)
                {
                    attributes = currentAttributes;
                    break;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    return false;
                missingTail.Insert(0, Path.GetFileName(current));
                current = parent;
            }

            if (!attributes.HasFlag(FileAttributes.Directory) && missingTail.Count > 0)
                return false;

            using var handle = openHandle(current);
            if (handle.IsInvalid)
                return false;
            var rawFinal = getFinalPath(handle);
            if (string.IsNullOrWhiteSpace(rawFinal))
                return false;

            // Volume GUID / UNC / 未知设备命名空间不能与本机盘符统一证明为同一身份。
            if (rawFinal.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                || rawFinal.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
                || rawFinal.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
                && !rawFinal.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
                return false;

            var final = StripDevicePrefix(rawFinal);
            if (!IsLocalDriveAbsolute(final))
                return false;
            foreach (var segment in missingTail)
                final = Path.Combine(final, segment);
            if (!IsLocalDriveAbsolute(final))
                return false;
            canonical = Path.GetFullPath(final);
            return true;
        }
        catch
        {
            canonical = "";
            return false;
        }
    }

    /// <summary>
    /// 原始输入白名单：仅本地盘符绝对路径，或显式 `\\?\C:\...` 本地扩展路径；
    /// 在首次文件系统访问前拒绝相对路径、盘符相对、UNC、设备/卷命名空间、ADS 等。
    /// </summary>
    internal static bool TryNormalizeLocalDriveAbsolute(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            return false;

        var candidate = path;
        if (candidate.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[4..];
            if (!IsDriveRooted(candidate))
                return false;
        }
        else if (candidate.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
                 || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        else if (!IsDriveRooted(candidate))
        {
            return false;
        }

        if (!HasSafeSegmentSyntax(candidate))
            return false;

        try
        {
            normalized = Path.GetFullPath(candidate);
            return IsLocalDriveAbsolute(normalized);
        }
        catch
        {
            normalized = "";
            return false;
        }
    }

    private static PathProbe ProbePath(string path, Func<string, FileAttributes> readAttributes,
        out FileAttributes attributes)
    {
        try
        {
            attributes = readAttributes(path);
            return PathProbe.Exists;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return PathProbe.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return PathProbe.Missing;
        }
        catch
        {
            // UnauthorizedAccess/IO/设备错误一律按无法证明处理；不得冒充不存在。
            attributes = default;
            return PathProbe.Unknown;
        }
    }

    private static SafeFileHandle OpenHandle(string path)
        => CreateFileW(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(4096);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
    }

    private static string StripDevicePrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];
        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }

    private static bool IsDriveRooted(string path)
        => path.Length >= 3
           && char.IsLetter(path[0])
           && path[1] == ':'
           && (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar)
           && path.IndexOf(':', 2) < 0;

    private static bool IsLocalDriveAbsolute(string path)
        => IsDriveRooted(path)
           && Path.IsPathFullyQualified(path);

    private static bool HasSafeSegmentSyntax(string path)
    {
        if (path.IndexOfAny(['*', '?', '<', '>', '|', '"']) >= 0)
            return false;
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length > 0 && (segment.StartsWith(' ') || segment.EndsWith(' ') || segment.EndsWith('.')))
                return false;
            if (IsReservedDeviceName(segment))
                return false;
        }
        return true;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var name = segment.Split('.')[0].TrimEnd(' ');
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (name[3] is >= '1' and <= '9' || name[3] is '¹' or '²' or '³'))
            return true;
        return false;
    }
}
