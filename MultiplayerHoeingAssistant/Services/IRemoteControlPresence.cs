namespace MultiplayerHoeingAssistant.Services;

/// <summary>被控端的可见、可过期控制租约；owner 是服务器连接或本机配对会话身份。</summary>
public interface IRemoteControlPresence : IDisposable
{
    Task<bool> ActivateAsync(string owner, string controlId, TimeSpan lifetime, bool renewOnly,
        CancellationToken cancellationToken = default);
    Task DeactivateAsync(string owner, string controlId);
    Task ClearOwnerPrefixAsync(string ownerPrefix);
    Task ClearAsync();
    bool IsActive(string owner, string controlId);
}
