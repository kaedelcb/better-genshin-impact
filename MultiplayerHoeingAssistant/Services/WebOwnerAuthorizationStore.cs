using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>远程网页的「本人」凭据。授权码只在助手界面显示，一次有效且 5 分钟过期；
/// 网页凭授权码换取长期凭据，执行端只保存凭据的 SHA-256，原文只存在于那个浏览器里。
/// 房间密码人人知道，所以远程修改本机数据必须出示这里签发的凭据。</summary>
internal sealed class WebOwnerAuthorizationStore
{
    internal sealed record Browser(string Id, string Label, string SecretHash, DateTimeOffset CreatedAt, DateTimeOffset LastUsedAt);
    private sealed record PendingCode(string Code, DateTimeOffset Expires);
    private sealed class FileModel { public int Version { get; set; } = 1; public List<Browser> Browsers { get; set; } = []; }

    internal const int MaxBrowsers = 20;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private PendingCode? _pending;
    private int _failedAttempts;
    private List<Browser>? _cache;

    internal WebOwnerAuthorizationStore(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path ?? Path.Combine(AssistantDataDirectory.Root, "web-owner-browsers.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal event Action? Changed;

    /// <summary>生成新的授权码；旧码立即作废。返回 8 位数字，分两段显示便于抄写。</summary>
    internal (string Code, DateTimeOffset Expires) CreateCode()
    {
        lock (_gate)
        {
            var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
            _pending = new PendingCode(code, _clock().Add(CodeLifetime));
            _failedAttempts = 0;
            return (code, _pending.Expires);
        }
    }

    internal void CancelCode() { lock (_gate) _pending = null; }

    internal DateTimeOffset? PendingCodeExpires
    {
        get { lock (_gate) return _pending is { } p && p.Expires > _clock() ? p.Expires : null; }
    }

    /// <summary>用授权码换取凭据。连续输错 5 次作废当前码，防止在 5 分钟内被穷举。</summary>
    internal (string BrowserId, string Secret) Redeem(string code, string label)
    {
        lock (_gate)
        {
            if (_pending is not { } pending || pending.Expires <= _clock())
            { _pending = null; throw new InvalidOperationException("授权码不存在或已过期，请在助手里重新生成"); }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(pending.Code),
                    Encoding.ASCII.GetBytes((code ?? "").Replace(" ", "").Replace("-", ""))))
            {
                if (++_failedAttempts >= 5) _pending = null;
                throw new UnauthorizedAccessException(_pending is null
                    ? "授权码错误次数过多，已作废，请在助手里重新生成" : "授权码不正确");
            }
            _pending = null;
            var browsers = Load();
            if (browsers.Count >= MaxBrowsers)
                throw new InvalidOperationException($"已授权的浏览器达到 {MaxBrowsers} 个，请先在助手里撤销不用的");
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var id = "wb-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var now = _clock();
            var clean = string.IsNullOrWhiteSpace(label) ? "网页浏览器" : label.Trim();
            if (clean.Length > 40) clean = clean[..40];
            browsers.Add(new Browser(id, clean, Hash(secret), now, now));
            Save(browsers);
            Changed?.Invoke();
            return (id, secret);
        }
    }

    /// <summary>核对网页出示的凭据；仅在验证通过时刷新最近使用时间（最多每分钟写一次）。</summary>
    internal bool Verify(string? browserId, string? secret)
    {
        if (string.IsNullOrEmpty(browserId) || string.IsNullOrEmpty(secret) || secret.Length != 64) return false;
        lock (_gate)
        {
            List<Browser> browsers;
            try { browsers = Load(); }
            catch { return false; }
            var index = browsers.FindIndex(b => b.Id == browserId);
            if (index < 0) return false;
            var expected = Convert.FromHexString(browsers[index].SecretHash);
            if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.ASCII.GetBytes(secret))))
                return false;
            var now = _clock();
            if (now - browsers[index].LastUsedAt > TimeSpan.FromMinutes(1))
            {
                browsers[index] = browsers[index] with { LastUsedAt = now };
                try { Save(browsers); } catch { /* 使用时间只用于展示，写入失败不影响本次授权 */ }
            }
            return true;
        }
    }

    internal IReadOnlyList<Browser> List() { lock (_gate) return Load().ToArray(); }

    internal bool Revoke(string browserId)
    {
        lock (_gate)
        {
            var browsers = Load();
            if (browsers.RemoveAll(b => b.Id == browserId) == 0) return false;
            Save(browsers);
        }
        Changed?.Invoke();
        return true;
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));

    private List<Browser> Load()
    {
        if (_cache is not null) return [.. _cache];
        if (!File.Exists(_path)) return (_cache = []).ToList();
        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("网页授权记录为空");
        _cache = model.Browsers.Where(b => !string.IsNullOrEmpty(b.Id) && b.SecretHash?.Length == 64).ToList();
        return [.. _cache];
    }

    private void Save(List<Browser> browsers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(new FileModel { Browsers = browsers }, new JsonSerializerOptions { WriteIndented = true });
        if (!LocalPeerSyncService.WriteFileAtomic(_path, json))
            throw new IOException("网页授权记录写入未确认");
        _cache = [.. browsers];
    }
}
