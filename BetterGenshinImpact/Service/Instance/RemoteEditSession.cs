using System;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Instance;

/// <summary>
/// 远程配置组编辑会话状态管理（remote-config-group-edit 契约 §2.2 / §2.3）。
/// 单会话：同一时间只允许一个远程编辑会话；IPC handler 在非 UI 线程读写本类，全部状态访问加锁。
/// 数据全程只在内存（临时 ScriptGroup 对象 / JSON 字符串），绝不写本机 User\ScriptGroup 目录。
/// 防卡死：任何会话超过 <see cref="SessionTimeout"/> 未完结（助手中途放弃等），
/// 在 TryBegin / SnapshotAndConsumeIfDone 入口强制复位为 idle 并记日志。
/// </summary>
internal static class RemoteEditSession
{
    private static readonly object Sync = new();
    private static readonly ILogger Logger = App.GetLogger<LogTag>();

    /// <summary>会话超过 15 分钟未完结视为僵尸会话，强制回收。</summary>
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);

    private static string _state = "idle";

    /// <summary>最近一次状态变更时间（TryBegin / MarkSaved / MarkCancelled 时刷新），用于僵尸会话回收。</summary>
    private static DateTime _lastActivityUtc = DateTime.MinValue;

    // 保存结果（saved 状态下供 config.remote_editor_result 读取）
    private static string? _scriptGroupConfigJson;
    private static string? _soloTaskName;
    private static string? _soloTaskSettingsJson;

    /// <summary>日志类别标记（静态类无法直接作为 GetLogger 泛型实参）。</summary>
    private sealed class LogTag { }

    /// <summary>当前会话状态：idle / editing / saved / cancelled。</summary>
    public static string State
    {
        get { lock (Sync) { return _state; } }
    }

    /// <summary>
    /// 尝试开启一个新会话。已有进行中（editing）或未消费（saved/cancelled）的会话时返回 false；
    /// 旧会话超过 <see cref="SessionTimeout"/> 未完结时先强制回收再放行。
    /// targetName/targetUid/groupName/packageJson 仅作入参契约保留，会话不存储（编辑窗口自行解析 packageJson）。
    /// </summary>
    public static bool TryBegin(string targetName, string targetUid, string groupName, string packageJson)
    {
        lock (Sync)
        {
            ExpireStaleLocked();

            if (_state != "idle")
            {
                return false;
            }

            _scriptGroupConfigJson = null;
            _soloTaskName = null;
            _soloTaskSettingsJson = null;
            _state = "editing";
            _lastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>编辑窗口保存并回传：记录结果并进入 saved 状态（等待助手轮询取走）。</summary>
    public static void MarkSaved(string? scriptGroupConfigJson, string? soloTaskName, string? soloTaskSettingsJson)
    {
        lock (Sync)
        {
            if (_state != "editing")
            {
                return;
            }

            _scriptGroupConfigJson = scriptGroupConfigJson;
            _soloTaskName = soloTaskName;
            _soloTaskSettingsJson = soloTaskSettingsJson;
            _state = "saved";
            _lastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>编辑窗口关闭/放弃：进入 cancelled 状态（等待助手轮询取走）。</summary>
    public static void MarkCancelled()
    {
        lock (Sync)
        {
            if (_state != "editing")
            {
                return;
            }

            _state = "cancelled";
            _lastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>开启会话失败（窗口弹出异常等）时立即回滚到 idle，不留下未消费的 cancelled。</summary>
    public static void AbortToIdle()
    {
        lock (Sync)
        {
            ResetLocked();
        }
    }

    /// <summary>
    /// 供 config.remote_editor_result 轮询读取。saved / cancelled 状态读取后会话关闭、状态回 idle；
    /// 超过 <see cref="SessionTimeout"/> 未完结的会话先强制回收（返回 idle）。
    /// </summary>
    public static RemoteEditSessionSnapshot SnapshotAndConsumeIfDone()
    {
        lock (Sync)
        {
            ExpireStaleLocked();

            var snapshot = new RemoteEditSessionSnapshot(
                _state,
                _scriptGroupConfigJson,
                _soloTaskName,
                _soloTaskSettingsJson);

            if (_state is "saved" or "cancelled")
            {
                ResetLocked();
            }

            return snapshot;
        }
    }

    /// <summary>僵尸会话回收：当前会话超过 15 分钟未完结 → 强制复位为 idle 并记日志。调用方须持有 Sync 锁。</summary>
    private static void ExpireStaleLocked()
    {
        if (_state == "idle" || DateTime.UtcNow - _lastActivityUtc <= SessionTimeout)
        {
            return;
        }

        Logger.LogWarning(
            "[远程配置编辑] 会话处于 {State} 状态超过 {Timeout} 分钟未完结（助手可能已放弃），强制复位为 idle",
            _state, SessionTimeout.TotalMinutes);
        ResetLocked();
    }

    private static void ResetLocked()
    {
        _state = "idle";
        _lastActivityUtc = DateTime.MinValue;
        _scriptGroupConfigJson = null;
        _soloTaskName = null;
        _soloTaskSettingsJson = null;
    }
}

/// <summary>config.remote_editor_result 的一次性快照。</summary>
internal sealed record RemoteEditSessionSnapshot(
    string State,
    string? ScriptGroupConfigJson,
    string? SoloTaskName,
    string? SoloTaskSettingsJson);
