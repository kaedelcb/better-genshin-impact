using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>跨 Windows 用户稳定的机器标识；只传不可逆摘要，不传注册表原值。</summary>
internal static class HostMachineIdentity
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var machineGuid = key?.GetValue("MachineGuid") as string;
            if (string.IsNullOrWhiteSpace(machineGuid)) return "";
            return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes("BetterGI.RemoteCenter.v1:" + machineGuid.Trim())));
        }
        catch { return ""; }
    }
}
