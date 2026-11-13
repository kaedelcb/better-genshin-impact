using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>仆人自动 EQ 的跨普攻段确认与重试间隔；收契退避不阻止红血 Q。</summary>
internal sealed class ArlecchinoAutoEqState
{
    private DateTime? _redSince;
    private DateTime? _emptySince;
    private DateTime _lastObservation;
    private DateTime _nextBurstAttempt;
    private DateTime _nextCollectionAttempt;

    public bool IsRedBloodConfirmed { get; private set; }
    public bool IsEmptyBondConfirmed { get; private set; }

    public void Observe(bool redBlood, bool hasBond, DateTime now)
    {
        // 长时间没有读屏时重新确认，不能沿用上一场景的采样。
        if (now - _lastObservation > TimeSpan.FromSeconds(2))
        {
            _redSince = null;
            _emptySince = null;
        }

        _lastObservation = now;
        _redSince = redBlood ? _redSince ?? now : null;
        _emptySince = !hasBond ? _emptySince ?? now : null;
        IsRedBloodConfirmed = _redSince.HasValue && now - _redSince.Value >= TimeSpan.FromMilliseconds(100);
        IsEmptyBondConfirmed = _emptySince.HasValue && now - _emptySince.Value >= TimeSpan.FromMilliseconds(100);
    }

    public bool ShouldTryBurst(DateTime now, double skillCd, int skillCdForQ, bool allowEmptyBond)
        => now >= _nextBurstAttempt && (IsRedBloodConfirmed ||
            (allowEmptyBond && IsEmptyBondConfirmed && ArlecchinoAutoEqDecisions.ShouldReleaseQByCd(skillCd, skillCdForQ)));

    public bool CanCollect(DateTime now)
        => IsEmptyBondConfirmed && now >= _nextCollectionAttempt;

    public void BurstAttempted(DateTime now)
    {
        _nextBurstAttempt = now.AddMilliseconds(500);
        _emptySince = null;
        IsEmptyBondConfirmed = false;
    }

    public void CollectionFinished(DateTime now)
    {
        _nextCollectionAttempt = now.AddSeconds(2);
        _emptySince = null;
        IsEmptyBondConfirmed = false;
    }
}
