using Microsoft.Win32;
using Serilog;
using System;
using System.IO;
using System.Security;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BetterGenshinImpact.Helpers;

internal static partial class UrlProtocolHelper
{
    public const string ProtocolRootKey = @"HKEY_CLASSES_ROOT\";
    public const string ProtocolUserKey = @"HKEY_CURRENT_USER\" + ProtocolUserSubKey;
    public const string ProtocolUserSubKey = @"Software\Classes\";
    public const string ProtocolName = "BetterGI";

    public static RegistryHive RegistryHive { get; set; } = RegistryHive.CurrentUser;

    public static string ProtocolKeyFull => RegistryHive switch
    {
        RegistryHive.ClassesRoot => ProtocolRootKey,
        RegistryHive.CurrentUser or _ => ProtocolUserKey,
    } + ProtocolName;

    private static RegistryKey? OpenSchemeKey()
    {
        if (RegistryHive == RegistryHive.ClassesRoot)
        {
            using RegistryKey keyRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            RegistryKey? keyScheme = keyRoot.OpenSubKey(ProtocolName);

            return keyScheme;
        }
        else
        {
            using RegistryKey keyUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            RegistryKey? keyScheme = keyUser.OpenSubKey(ProtocolUserSubKey + ProtocolName);

            return keyScheme;
        }
    }

    private static RegistryKey CreateSchemeKey()
    {
        if (RegistryHive == RegistryHive.ClassesRoot)
        {
            using RegistryKey keyRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            RegistryKey keyScheme = keyRoot.CreateSubKey(ProtocolName);

            return keyScheme;
        }
        else
        {
            using RegistryKey keyUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            // Access only our protocol key; requesting write access to the Classes
            // parent can be denied even when this child is writable.
            RegistryKey keyScheme = keyUser.CreateSubKey(ProtocolUserSubKey + ProtocolName);

            return keyScheme;
        }
    }

    public static string GetUrl(string param = null!)
    {
        return $"{ProtocolName}://{param ?? string.Empty}";
    }

    public static bool GetPath(out string path)
    {
        if (Process.GetCurrentProcess().MainModule?.FileName is string fileName)
        {
            path = $"\"{fileName}\" \"%1\"";
            return true;
        }
        path = null!;
        return false;
    }

    public static bool IsRegistered()
    {
        using RegistryKey? keyScheme = OpenSchemeKey();
        return IsRegistered(keyScheme);
    }

    internal static bool IsRegistered(RegistryKey? keyScheme)
    {
        if (keyScheme?.GetValue("URL Protocol") is string)
        {
            using RegistryKey? keyShell = keyScheme.OpenSubKey("shell");
            using RegistryKey? keyOpen = keyShell?.OpenSubKey("open");
            using RegistryKey? keyCommand = keyOpen?.OpenSubKey("command");

            if (keyCommand?.GetValue(string.Empty) is string pathRegistered)
            {
                if (GetPath(out string path))
                {
                    if (pathRegistered == path)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public static string GetVaildPath()
    {
        using RegistryKey? keyScheme = OpenSchemeKey();

        if (keyScheme != null)
        {
            using RegistryKey? keyShell = keyScheme.OpenSubKey("shell");
            using RegistryKey? keyOpen = keyShell?.OpenSubKey("open");
            using RegistryKey? keyCommand = keyOpen?.OpenSubKey("command");

            if (keyCommand?.GetValue(string.Empty) is string pathRegistered)
            {
                Match match = ExecutableRegex().Match(pathRegistered);

                if (match.Success && match.Groups["exe"]?.Value is string exe)
                {
                    return exe;
                }
            }
        }
        return null!;
    }

    public static bool IsVaildProtocol()
    {
        return GetVaildPath() != null;
    }

    public static void Register()
    {
        using RegistryKey keyScheme = CreateSchemeKey();
        Register(keyScheme);
    }

    internal static void Register(RegistryKey keyScheme)
    {
        keyScheme.SetValue(string.Empty, ProtocolName);
        keyScheme.SetValue("URL Protocol", string.Empty);

        if (GetPath(out string path))
        {
            using RegistryKey keyShell = keyScheme.CreateSubKey("shell");
            using RegistryKey keyOpen = keyShell.CreateSubKey("open");
            using RegistryKey keyCommand = keyOpen.CreateSubKey("command");

            keyCommand.SetValue(string.Empty, path);
        }
    }

    public static void Unregister()
    {
        {
            using RegistryKey keyRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64);
            using RegistryKey? keyScheme = keyRoot.OpenSubKey(ProtocolName, true);

            if (keyScheme != null)
            {
                keyScheme.Close();
                keyRoot.DeleteSubKeyTree(ProtocolName);
            }
        }
        {
            using RegistryKey keyUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey keyUserSub = keyUser.OpenSubKey(ProtocolUserSubKey)!;
            using RegistryKey? keyScheme = keyUserSub.OpenSubKey(ProtocolName, true);

            if (keyScheme != null)
            {
                keyScheme.Close();
                keyUserSub.DeleteSubKeyTree(ProtocolName);
            }
        }
    }

    public static Task RegisterAsync() => RegisterAsync(IsRegistered, Register);

    internal static async Task RegisterAsync(Func<bool> isRegistered, Action register)
    {
        try
        {
            await Task.Run(() =>
            {
                if (!isRegistered()) register();
            });
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Protocol association is optional. A restricted registry must not abort
            // the application after its host has already started.
            Log.Warning(ex, "无法注册 {ProtocolName} URL 协议（{RegistryKey}）；程序继续启动，协议链接唤起可能不可用。",
                ProtocolName, ProtocolKeyFull);
        }
    }

    public static void Launch(string param = null!)
    {
        _ = Process.Start(new ProcessStartInfo()
        {
            UseShellExecute = true,
            FileName = GetUrl(param),
        });
    }

    public static async Task LaunchAsync(string param = null!)
    {
        await Task.Run(() =>
        {
            Launch(param);
        });
    }

    [GeneratedRegex("\"(?<exe>[\\s\\S]*?)\" \"%1\"")]
    private static partial Regex ExecutableRegex();
}
