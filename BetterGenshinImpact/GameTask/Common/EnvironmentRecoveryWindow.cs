using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>
/// 一次操作的环境保护预算。探测成功不会重置预算，防止“开图成功、传送失败”循环续期。
/// 只有用户明确继续后才开始新预算；等待期间调用保持未完成，不向上层返回假成功。
/// </summary>
public sealed class EnvironmentRecoveryWindow
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _limit;
    private long? _started;

    public EnvironmentRecoveryWindow(TimeProvider? time = null, TimeSpan? limit = null)
    {
        _time = time ?? TimeProvider.System;
        _limit = limit ?? TimeSpan.FromSeconds(60);
        if (_limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    public async Task WaitAsync(Func<CancellationToken, Task<bool>> probe,
        Func<CancellationToken, Task> waitForUser, CancellationToken ct)
    {
        _started ??= _time.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = _limit - _time.GetElapsedTime(_started.Value);
            if (remaining > TimeSpan.Zero)
            {
                using var timeout = new CancellationTokenSource(remaining, _time);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                try
                {
                    var recovered = await probe(attempt.Token);
                    // 迟到结果不能解除暂停；用户停止优先于成功和自动超时。
                    ct.ThrowIfCancellationRequested();
                    if (!timeout.IsCancellationRequested && recovered) return;
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, attempt.Token);
                    continue;
                }
                catch (Exception) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
                {
                    // BGI 的 Delay 也可能以 NormalEndException 表示令牌取消。
                }
            }

            ct.ThrowIfCancellationRequested();
            await waitForUser(ct);
            ct.ThrowIfCancellationRequested();
            _started = _time.GetTimestamp();
        }
    }
}
