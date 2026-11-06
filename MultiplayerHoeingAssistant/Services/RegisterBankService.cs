using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>两个中心和所有流程共用的10位状态。读取/判断不写盘，归零由独立时钟执行。</summary>
public sealed class RegisterBankService : IDisposable
{
    private sealed class State
    {
        public State() { }
        public int Version { get; set; } = 1;
        public List<RegisterEntry> Entries { get; set; } = Enumerable.Range(1, RegisterBank.Count)
            .Select(i => new RegisterEntry { Number = i }).ToList();
    }

    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;
    private readonly System.Threading.Timer? _timer;
    private bool _disposed;
    private string? _lastError;
    private int _tickActive;

    public RegisterBankService(string path, Func<DateTimeOffset>? now = null,
        bool startTimer = true, Action<string>? log = null)
    {
        _path = Path.GetFullPath(path);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        Tick(); // 重启后先兑现已到期的重置，不给流程读到过期标记。
        if (startTimer) _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    public RegisterBankSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var state = Load();
                ResetDue(state); // 只投影到新快照；即使时钟写入暂被占用，已到期的位也按0读取。
                return new RegisterBankSnapshot { Available = true, Entries = state.Entries };
            }
            catch (Exception ex)
            { return new RegisterBankSnapshot { Error = "寄存器状态未确认：" + ex.Message }; }
        }
    }

    public void Set(RegisterWriteSettings settings, CancellationToken ct = default)
    {
        RegisterBank.ValidateSelection(settings.Values);
        Mutate(state =>
        {
            var changed = false;
            var now = _now();
            for (var i = 0; i < RegisterBank.Count; i++)
            {
                if (settings.Values[i] is not { } value || state.Entries[i].Value == value) continue;
                var entry = state.Entries[i];
                entry.Value = value;
                entry.ChangedAtUtc = now;
                entry.NextResetAtUtc = value == 1 ? NextReset(entry, now) : null;
                changed = true;
            }
            return changed;
        }, ct);
    }

    public (bool Passed, string Description) Evaluate(RegisterConditionSettings settings)
    {
        RegisterBank.ValidateSelection(settings.Values);
        if (settings.Match is not ("all" or "any")) throw new ArgumentException("寄存器条件关系无效。");
        var snapshot = GetSnapshot();
        if (!snapshot.Available) throw new InvalidDataException(snapshot.Error);
        var selected = snapshot.Entries.Where((_, i) => settings.Values[i] is not null).ToArray();
        bool Matches(RegisterEntry entry) => entry.Value == settings.Values[entry.Number - 1];
        var passed = settings.Match == "all" ? selected.All(Matches) : selected.Any(Matches);
        return (passed, string.Join(settings.Match == "all" ? " 且 " : " 或 ", selected.Select(e =>
            $"{e.Label} = {settings.Values[e.Number - 1]}（当前{e.Value}）")));
    }

    public RegisterBankSnapshot Configure(RegisterConfigurationChange change)
    {
        ValidateConfiguration(change.Name, change.ResetMode, change.ResetHour, change.ResetMinute, change.DelaySeconds);
        if (change.Number is < 1 or > RegisterBank.Count) throw new ArgumentException("寄存器位号无效。");
        Mutate(state =>
        {
            var entry = state.Entries[change.Number - 1];
            if (entry.SettingsRevision != change.ExpectedSettingsRevision)
                throw new InvalidOperationException("此寄存器设置已被其他操作修改，请重新打开后编辑。");
            var scheduleChanged = entry.ResetMode != change.ResetMode || entry.ResetHour != change.ResetHour
                || entry.ResetMinute != change.ResetMinute || entry.DelaySeconds != change.DelaySeconds;
            entry.Name = change.Name.Trim();
            entry.ResetMode = change.ResetMode;
            entry.ResetHour = change.ResetHour;
            entry.ResetMinute = change.ResetMinute;
            entry.DelaySeconds = change.DelaySeconds;
            entry.SettingsRevision = Guid.NewGuid().ToString("N");
            // 改名不延长计时；改变延时长度仍以原来的实际状态变化时刻为起点。
            if (scheduleChanged)
                entry.NextResetAtUtc = entry.Value == 1 ? NextReset(entry, entry.ChangedAtUtc ?? _now(), _now()) : null;
            ResetDue(state);
            return true;
        });
        return GetSnapshot();
    }

    public void ProcessDueResets()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!File.Exists(_path)) return;
            var now = _now();
            if (!Load().Entries.Any(e => e.Value == 1 && e.NextResetAtUtc <= now)) return;
        }
        // 普通时钟读取不占配置写锁；真的到期后才取得许可并重新核对最新数据。
        Mutate(ResetDue);
    }

    private bool ResetDue(State state)
    {
        var changed = false;
        var now = _now();
        foreach (var entry in state.Entries.Where(e => e.Value == 1 && e.NextResetAtUtc <= now))
        {
            entry.Value = 0;
            entry.ChangedAtUtc = entry.NextResetAtUtc;
            entry.NextResetAtUtc = null;
            changed = true;
        }
        return changed;
    }

    private DateTimeOffset? NextReset(RegisterEntry entry, DateTimeOffset changedAt, DateTimeOffset? configuredAt = null)
    {
        if (entry.ResetMode == RegisterBank.Never) return null;
        if (entry.ResetMode == RegisterBank.AfterChange) return changedAt.AddSeconds(entry.DelaySeconds);
        var localNow = (configuredAt ?? changedAt).ToLocalTime();
        var due = DateTime.SpecifyKind(localNow.Date.AddHours(entry.ResetHour).AddMinutes(entry.ResetMinute), DateTimeKind.Unspecified);
        DateTimeOffset Offset(DateTime time) => new(time, TimeZoneInfo.Local.GetUtcOffset(time));
        if (Offset(due) <= localNow) due = due.AddDays(1);
        while (TimeZoneInfo.Local.IsInvalidTime(due)) due = due.AddMinutes(1);
        return Offset(due).ToUniversalTime();
    }

    private void Mutate(Func<State, bool> update, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // 本服务内的日志触发器串行写入，避免正常并发命中互相拒绝。
        lock (_writeGate)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            // 与其他配置写者使用同一目录锁；外部争用立即失败，不等待或抢锁。
            using var writing = ConfigFileWriteGate.Acquire(_path);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var state = Load();
                ct.ThrowIfCancellationRequested();
                var expired = ResetDue(state);
                var updated = update(state);
                if (!expired && !updated) return;
                ct.ThrowIfCancellationRequested();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
                var temporary = Path.Combine(Path.GetDirectoryName(_path)!, $".registers-{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    File.Move(temporary, _path, true);
                    if (!File.ReadAllBytes(_path).AsSpan().SequenceEqual(bytes))
                        throw new IOException("寄存器保存后读回不一致，结果未确认。");
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }

    private State Load()
    {
        if (!File.Exists(_path)) return new State();
        var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(_path), Json)
            ?? throw new InvalidDataException("寄存器文件为空，原文件已保留。");
        if (state.Version != 1 || state.Entries is null || state.Entries.Count != RegisterBank.Count)
            throw new InvalidDataException("寄存器文件版本或位数无效，原文件已保留。");
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            if (entry is null || entry.Number != i + 1 || entry.Value is not (0 or 1)
                || string.IsNullOrEmpty(entry.SettingsRevision)) throw new InvalidDataException("寄存器数据无效，原文件已保留。");
            ValidateConfiguration(entry.Name, entry.ResetMode, entry.ResetHour, entry.ResetMinute, entry.DelaySeconds);
            if (entry.Value == 1 && entry.ResetMode != RegisterBank.Never && entry.NextResetAtUtc is null)
                throw new InvalidDataException("寄存器自动重置时间缺失，原文件已保留。");
        }
        return state;
    }

    private static void ValidateConfiguration(string? name, string mode, int hour, int minute, int delay)
    {
        if (name is null || name.Length > 40 || name.Any(char.IsControl)) throw new ArgumentException("寄存器名称限40字，不能包含控制字符。");
        if (mode is not (RegisterBank.Never or RegisterBank.Daily or RegisterBank.AfterChange)) throw new ArgumentException("寄存器重置规则无效。");
        if (hour is < 0 or > 23 || minute is < 0 or > 59) throw new ArgumentException("重置时刻无效。");
        if (delay is < 0 or > RegisterBank.MaxDelaySeconds || mode == RegisterBank.AfterChange && delay == 0)
            throw new ArgumentException("延时重置时间须大于0且不超过31天。");
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _tickActive, 1) != 0) return;
        try { ProcessDueResets(); _lastError = null; }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (_lastError == ex.Message) return;
            _lastError = ex.Message;
            try { _log?.Invoke("[寄存器] 自动重置未完成，将重试：" + ex.Message); }
            catch (Exception logError) { System.Diagnostics.Debug.WriteLine(logError); }
        }
        finally { Volatile.Write(ref _tickActive, 0); }
    }

    public void Dispose()
    {
        lock (_writeGate)
            lock (_gate) { _disposed = true; _timer?.Dispose(); }
    }
}
