using Microsoft.Extensions.Logging;

namespace BgiCoordinatorServer.Services;

/// <summary>保留带结构化房间码的近期预览，并将完整记录交给按天日志存储。</summary>
public sealed class RoomLogStore(RoomLogArchive? archive = null, TimeProvider? timeProvider = null)
{
    private const int MaxEntries = 5000;
    private const int MaxMessageLength = 2000;
    private readonly Queue<StoredRoomLog> _entries = new();
    private readonly object _gate = new();

    internal void Append(IEnumerable<string> roomCodes, LogLevel level, string category, string message,
        string? gameSessionId = null)
    {
        var codes = roomCodes.Select(NormalizeCode).Where(code => code is not null)
            .Select(code => code!).ToHashSet(StringComparer.Ordinal);
        if (codes.Count == 0) return;

        var timestamp = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        archive?.Append(codes, new RoomServerLogEntry(timestamp, level.ToString(), category, message, gameSessionId));
        var entry = new RoomServerLogEntry(timestamp, level.ToString(),
            category.Length > 120 ? category[^120..] : category,
            message.Length > MaxMessageLength ? message[..MaxMessageLength] + "…" : message, gameSessionId);
        lock (_gate)
        {
            _entries.Enqueue(new StoredRoomLog(codes, entry));
            while (_entries.Count > MaxEntries) _entries.Dequeue();
        }
    }

    public IReadOnlyList<RoomServerLogEntry> Read(string roomCode, int limit = 200)
    {
        var code = NormalizeCode(roomCode);
        if (code is null) return [];
        limit = Math.Clamp(limit, 1, 200);
        lock (_gate)
        {
            return _entries.Reverse()
                .Where(item => item.RoomCodes.Contains(code))
                .Take(limit)
                .Select(item => item.Entry)
                .ToList();
        }
    }

    public static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var code = value.StartsWith("CTRL_", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;
        if (code.Length != 6 || code.Any(character => !char.IsAsciiLetterOrDigit(character))) return null;
        return code.ToUpperInvariant();
    }

    private sealed record StoredRoomLog(HashSet<string> RoomCodes, RoomServerLogEntry Entry);
}

public sealed record RoomServerLogEntry(DateTime TimestampUtc, string Level, string Category, string Message,
    string? GameSessionId = null);

public sealed class RoomLogProvider(RoomLogStore store, RoomManager? manager = null) : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    public ILogger CreateLogger(string categoryName) => new RoomLogger(this, store, categoryName, manager);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    private sealed class RoomLogger(RoomLogProvider provider, RoomLogStore store, string category, RoomManager? manager) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            try
            {
                var fields = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
                var fieldCodes = fields
                    .Where(field => IsRoomField(field.Key))
                    .Select(field => field.Value?.ToString() ?? "")
                    .ToArray();
                RoomLogScopeSnapshot? scope = null;
                provider._scopes.ForEachScope((value, _) =>
                {
                    if (value is IRoomLogScope roomScope)
                        scope = exception is null ? roomScope.Resolve() : roomScope.ResolveFailure();
                }, (object?)null);
                // Hub 调用只信服务端已登记的身份；信封/参数里的 RoomCode 不能向其它房间注入记录。
                var codes = scope?.RoomCodes ?? fieldCodes;
                if (codes.Count == 0) return;
                var sessionId = scope?.GameSessionId;
                if (scope is null && manager is not null)
                {
                    var sessions = codes.Select(RoomLogStore.NormalizeCode).Where(code => code is not null)
                        .Select(code => manager.GetRoom(code!)?.SessionId).Where(id => id is not null).Distinct().ToArray();
                    if (sessions.Length == 1) sessionId = sessions[0];
                }
                var message = formatter(state, exception);
                if (exception is not null) message += Environment.NewLine + exception;
                store.Append(codes, level, category, message, sessionId);
            }
            catch
            {
                // 日志旁路绝不影响原业务调用。
            }
        }

        private static bool IsRoomField(string key) =>
            key.Equals("RoomCode", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Code", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Room", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Group", StringComparison.OrdinalIgnoreCase);
    }
}
