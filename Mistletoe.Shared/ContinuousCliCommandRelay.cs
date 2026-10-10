using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Mistletoe.Shared;

/// <summary>
/// BGI 与槲寄生助手之间的本机连续计划命令桥。
/// 仅传递一次性启动意图；真正的计划解析、受理、执行和终态仍由助手 TaskCenterHost 负责。
/// </summary>
public sealed record ContinuousCliCommand(string? PlanName, DateTimeOffset IssuedAtUtc, int SourceProcessId);

public static class ContinuousCliCommandRelay
{
    public const string CommandArgument = "--start-continuous-plan";
    private const string CommandName = "startContinuousOneDragon";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private static string ScopeKey
    {
        get
        {
            string sid;
            try { sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
            catch { sid = Environment.UserName; }
            var safe = new string(sid.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
            return safe + "_s" + Process.GetCurrentProcess().SessionId;
        }
    }

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI");

    private static string CommandPath => Path.Combine(Root, "continuous-cli-command-" + ScopeKey + ".json");

    private static string EventName => "NexusBGI_ContinuousCli_" + ScopeKey;

    public static EventWaitHandle OpenSignal()
        => new(false, EventResetMode.AutoReset, EventName, out _);

    public static bool Publish(string? planName, out string error)
    {
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(Root);
            var command = new ContinuousCliCommand(
                string.IsNullOrWhiteSpace(planName) ? null : planName.Trim(),
                DateTimeOffset.UtcNow,
                Environment.ProcessId);
            var json = JsonSerializer.Serialize(new
            {
                command = CommandName,
                planName = command.PlanName,
                issuedAtUtc = command.IssuedAtUtc,
                sourceProcessId = command.SourceProcessId,
            });
            var temp = CommandPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, CommandPath, overwrite: true);
            using var signal = OpenSignal();
            signal.Set();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try
            {
                if (File.Exists(CommandPath)) File.Delete(CommandPath);
            }
            catch { }
            return false;
        }
    }

    public static bool TryConsume(out ContinuousCliCommand? command)
    {
        command = null;
        try
        {
            if (!File.Exists(CommandPath)) return false;
            var json = File.ReadAllText(CommandPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var action)
                || !string.Equals(action.GetString(), CommandName, StringComparison.Ordinal))
                return false;
            if (!root.TryGetProperty("issuedAtUtc", out var issued)
                || !issued.TryGetDateTimeOffset(out var issuedAt)
                || DateTimeOffset.UtcNow - issuedAt > MaxAge
                || issuedAt - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1))
                return false;
            var plan = root.TryGetProperty("planName", out var planValue)
                && planValue.ValueKind == JsonValueKind.String
                ? planValue.GetString()
                : null;
            var pid = root.TryGetProperty("sourceProcessId", out var pidValue)
                && pidValue.TryGetInt32(out var parsedPid) ? parsedPid : 0;
            command = new ContinuousCliCommand(
                string.IsNullOrWhiteSpace(plan) ? null : plan.Trim(), issuedAt, pid);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(CommandPath)) File.Delete(CommandPath); }
            catch { }
        }
    }
}
