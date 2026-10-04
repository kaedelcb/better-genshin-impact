namespace MultiplayerHoeingAssistant.Services;

internal readonly record struct SessionLifetime(int SessionId, long LogonTime);

/// <summary>助手运行期间保留序号槽位；退出的会话不压缩序号，新会话只追加。</summary>
internal sealed class StartupSessionBindings
{
    private readonly object _gate = new();
    private readonly List<SessionLifetime> _slots = [];

    public SessionLifetime Resolve(int order, IReadOnlyList<SessionLifetime> orderedSessions)
    {
        lock (_gate)
        {
            foreach (var session in orderedSessions)
                if (!_slots.Contains(session)) _slots.Add(session);
            if (order < 1 || order > _slots.Count)
                throw new InvalidOperationException("目标启动序号尚不存在，状态未知");
            return _slots[order - 1];
        }
    }
}
