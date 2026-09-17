using System.IO;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Bounded retries for read-only IPC; never retries a start/stop side effect.</summary>
public static class CoordinatedBatchReads
{
    public static async Task<T?> ReadAsync<T>(Func<CancellationToken, Task<T?>> read, CancellationToken ct) where T : class
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await read(ct);
                if (result != null) return result;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException) { }
            if (attempt < 2) await Task.Delay(250, ct);
        }
        return null;
    }
}
