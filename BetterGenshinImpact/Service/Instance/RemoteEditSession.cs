using System;
using BetterGenshinImpact.View.Windows;
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

    // 当前会话目标（editing 期间有效）：用于识别"同目标同组的重复开单"（ext+v2 双发/用户连点/网络重试）
    private static string? _targetUid;
    private static string? _groupName;

    // 保存结果（saved 状态下供 config.remote_editor_result 读取）
    private static string? _scriptGroupConfigJson;
    private static string? _soloTaskName;
    private static string? _soloTaskSettingsJson;

    // 当前会话的编辑窗口引用（editing 期间有效）：助手主动中止（config.abort_remote_editor）时强制关窗。
    private static RemoteConfigEditWindow? _window;

    /// <summary>日志类别标记（静态类无法直接作为 GetLogger 泛型实参）。</summary>
    private sealed class LogTag { }

    /// <summary>当前会话状态：idle / editing / saved / cancelled。</summary>
    public static string State
    {
        get { lock (Sync) { return _state; } }
    }

    /// <summary>
    /// 尝试开启一个新会话。已有进行中（editing）的会话时返回 false；
    /// 旧会话超过 <see cref="SessionTimeout"/> 未完结时先强制回收再放行。
    /// targetName/targetUid/groupName/packageJson 仅作入参契约保留，会话不存储（编辑窗口自行解析 packageJson）。
    ///
    /// [实机修复 2026-09-04] saved/cancelled 尸体会话直接回收放行：尸体意味着上一个助手流程已死
    /// （助手侧 RemoteConfigEditService 有单会话锁，不会一边轮询取结果一边开新会话），
    /// 否则尸体将占坑最长 15 分钟、期间一切新会话都被拒——"远程编辑时好时坏"的根因。
    /// 代价：尸体里的 saved 结果若未被取走会丢失（但原子流程已死，结果本也回传不出去）。
    /// </summary>
    public static bool TryBegin(string targetName, string targetUid, string groupName, string packageJson)
    {
        lock (Sync)
        {
            ExpireStaleLocked();

            if (_state is "saved" or "cancelled")
            {
                Logger.LogWarning(
                    "[远程配置编辑] 回收未消费的 {State} 尸体会话（上次编辑结果可能未回传成功），放行新会话",
                    _state);
                ResetLocked();
            }

            if (_state != "idle")
            {
                return false;
            }

            _scriptGroupConfigJson = null;
            _soloTaskName = null;
            _soloTaskSettingsJson = null;
            _targetUid = targetUid;
            _groupName = groupName;
            _state = "editing";
            _lastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// 是否存在与 (targetUid, groupName) 相同的进行中（editing）会话。
    /// 用于把"同一次编辑的重复开单"（ext 通道执行成功但响应丢失后 v2 兜底重发、用户连点、网络重试）
    /// 识别为幂等命中而非冲突——调用方应对此返回 editing 而非"已有进行中的会话"。
    /// </summary>
    public static bool IsSameInFlightSession(string targetUid, string groupName)
    {
        lock (Sync)
        {
            ExpireStaleLocked();
            return _state == "editing"
                && string.Equals(_targetUid, targetUid, StringComparison.Ordinal)
                && string.Equals(_groupName, groupName, StringComparison.Ordinal);
        }
    }

    /// <summary>占用详情（TryBegin 拒绝时随响应返回，便于助手侧诊断与展示）。</summary>
    public static (string State, string? TargetUid, string? GroupName, double AgeSeconds) GetOccupyingInfo()
    {
        lock (Sync)
        {
            var age = _lastActivityUtc == DateTime.MinValue
                ? 0
                : (DateTime.UtcNow - _lastActivityUtc).TotalSeconds;
            return (_state, _targetUid, _groupName, age);
        }
    }

    /// <summary>登记当前会话的编辑窗口（窗口创建成功后由 handler 在 Dispatcher 上调用；窗口关闭自动解除登记）。</summary>
    public static void RegisterWindow(RemoteConfigEditWindow window)
    {
        lock (Sync)
        {
            _window = window;
        }
        window.Closed += (_, _) =>
        {
            lock (Sync)
            {
                if (ReferenceEquals(_window, window))
                {
                    _window = null;
                }
            }
        };
    }

    /// <summary>
    /// 助手主动中止（config.abort_remote_editor）：任意非 idle 会话强制复位为 idle，
    /// editing 会话若窗口仍开着则强制关闭（不弹未保存确认、不标 cancelled——会话直接消失）。
    /// 返回是否有会话被中止。幂等：idle 时返回 false，不报错。
    /// </summary>
    public static bool AbortActiveSession(string reason)
    {
        RemoteConfigEditWindow? window;
        string state;
        lock (Sync)
        {
            if (_state == "idle")
            {
                return false;
            }
            state = _state;
            window = _window;
            ResetLocked();
        }

        Logger.LogInformation("[远程配置编辑] 会话被助手主动中止（原状态 {State}）：{Reason}", state, reason);

        if (window != null)
        {
            try
            {
                // BeginInvoke：不阻塞 IPC 线程；窗口已析构/ Dispatcher 关闭时静默放弃（状态已复位）。
                window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        window.ForceCloseFromAbort();
                    }
                    catch
                    {
                        // 窗口可能已关闭/析构，状态已复位即可
                    }
                }));
            }
            catch
            {
                // Dispatcher 已关闭等极端情况，状态已复位即可
            }
        }
        return true;
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
                _soloTaskSettingsJson,
                _targetUid,
                _groupName);

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
        _targetUid = null;
        _groupName = null;
        _scriptGroupConfigJson = null;
        _soloTaskName = null;
        _soloTaskSettingsJson = null;
        _window = null;
    }
}

/// <summary>config.remote_editor_result 的一次性快照。[2026-09-05] 追加 TargetUid/GroupName：助手探测接管时核对归属，防劫持别的僵尸窗。</summary>
internal sealed record RemoteEditSessionSnapshot(
    string State,
    string? ScriptGroupConfigJson,
    string? SoloTaskName,
    string? SoloTaskSettingsJson,
    string? TargetUid,
    string? GroupName);
