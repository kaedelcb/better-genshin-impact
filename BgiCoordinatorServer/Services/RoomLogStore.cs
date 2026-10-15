using Microsoft.Extensions.Logging;

namespace BgiCoordinatorServer.Services;

/// <summary>保留带结构化房间码的近期预览，并将完整记录交给按天日志存储。</summary>
public sealed class RoomLogStore(RoomLogArchive? archive = null, TimeProvider? timeProvider = null)
{
    private const int MaxEntries = 5000;
    private const int MaxMessageLength = 2000;
    private readonly Queue<StoredRoomLog> _entries = new();
    private readonly object _gate = new();

    internal void Append(IEnumerable<string> roomCodes, LogLevel level, string category, string message)
    {
        var codes = roomCodes.Select(NormalizeCode).Where(code => code is not null)
            .Select(code => code!).ToHashSet(StringComparer.Ordinal);
        if (codes.Count == 0) return;

        var timestamp = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        archive?.Append(codes, new RoomServerLogEntry(timestamp, level.ToString(), category, message));
        var entry = new RoomServerLogEntry(timestamp, level.ToString(),
            category.Length > 120 ? category[^120..] : category,
            message.Length > MaxMessageLength ? message[..MaxMessageLength] + "…" : message);
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

public sealed record RoomServerLogEntry(DateTime TimestampUtc, string Level, string Category, string Message);

public sealed class RoomLogProvider(RoomLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RoomLogger(store, categoryName);
    public void Dispose() { }

    private sealed class RoomLogger(RoomLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None;

        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level) || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            try
            {
                var codes = fields
                    .Where(field => IsRoomField(field.Key))
                    .Select(field => field.Value?.ToString() ?? "")
                    .ToArray();
                if (codes.Length == 0) return;
                var message = formatter(state, exception);
                if (exception is not null) message += Environment.NewLine + exception;
                store.Append(codes, level, category, message);
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
