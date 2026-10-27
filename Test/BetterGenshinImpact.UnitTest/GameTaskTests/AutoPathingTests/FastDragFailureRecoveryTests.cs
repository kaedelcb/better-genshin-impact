using System;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class FastDragFailureRecoveryTests
{
    private static readonly Rect RecognizedRect = new(4495, 3017, 989, 563);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 1)]
    public void FailedDragCannotEarlyStopEvenAfterLoopAdvances(int iteration, int failures)
    {
        Assert.False(TpTaskFastDrag.CanEarlyStopAfterDrag(true, iteration, failures));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 5)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 5)]
    public void WithoutFailedDragExistingEarlyStopEligibilityIsUnchanged(bool enabled, int iteration)
    {
        Assert.Equal(enabled && iteration > 0, TpTaskFastDrag.CanEarlyStopAfterDrag(enabled, iteration, 0));
    }

    [Fact]
    public void SuccessfulRedragRestoresEarlyStopEligibility()
    {
        Assert.False(TpTaskFastDrag.CanEarlyStopAfterDrag(true, 1, 1));
        Assert.True(TpTaskFastDrag.CanEarlyStopAfterDrag(true, 2, 0));
    }

    [Fact]
    public void FirstRecognitionSuccessUsesExactlyOneCaptureAndNoExtraWait()
    {
        int captures = 0, waits = 0;
        var result = TpTaskFastDrag.RecognizeRestoredMapRect(
            () => { captures++; return RecognizedRect; }, () => waits++, CancellationToken.None);

        Assert.Equal(RecognizedRect, result);
        Assert.Equal(1, captures);
        Assert.Equal(0, waits);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EmptyThenValidRecognitionRetriesOnlyUntilSuccess(int emptyFrames)
    {
        int captures = 0, waits = 0;
        var result = TpTaskFastDrag.RecognizeRestoredMapRect(
            () => ++captures <= emptyFrames ? default : RecognizedRect,
            () => waits++, CancellationToken.None);

        Assert.Equal(RecognizedRect, result);
        Assert.Equal(emptyFrames + 1, captures);
        Assert.Equal(emptyFrames, waits);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    public void InvalidFramesStopAfterThreeAttemptsInsteadOfReturningStaleRect(int width, int height)
    {
        int captures = 0, waits = 0;
        Assert.Throws<InvalidOperationException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => { captures++; return new Rect(100, 200, width, height); },
            () => waits++, CancellationToken.None));

        Assert.Equal(3, captures);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void RecognitionExceptionCanRecoverOnNextFreshFrame()
    {
        int captures = 0, waits = 0;
        var result = TpTaskFastDrag.RecognizeRestoredMapRect(
            () => ++captures == 1 ? throw new InvalidOperationException("frame unavailable") : RecognizedRect,
            () => waits++, CancellationToken.None);

        Assert.Equal(RecognizedRect, result);
        Assert.Equal(2, captures);
        Assert.Equal(1, waits);
    }

    [Fact]
    public void PersistentRecognitionExceptionIsRetainedAsFailure()
    {
        int captures = 0, waits = 0;
        var original = new InvalidOperationException("matching failed");
        var error = Assert.Throws<InvalidOperationException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => { captures++; throw original; }, () => waits++, CancellationToken.None));

        Assert.Same(original, error.InnerException);
        Assert.Equal(3, captures);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void AlreadyCancelledDoesNotCaptureOrWait()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int captures = 0, waits = 0;
        Assert.Throws<OperationCanceledException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => { captures++; return RecognizedRect; }, () => waits++, cancellation.Token));
        Assert.Equal(0, captures);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void CancellationDuringWaitStopsBeforeAnotherCapture()
    {
        using var cancellation = new CancellationTokenSource();
        int captures = 0, waits = 0;
        Assert.Throws<OperationCanceledException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => { captures++; return default; },
            () => { waits++; cancellation.Cancel(); }, cancellation.Token));
        Assert.Equal(1, captures);
        Assert.Equal(1, waits);
    }

    [Fact]
    public void CancellationDuringSuccessfulRecognitionDoesNotReturnSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        int waits = 0;
        Assert.Throws<OperationCanceledException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => { cancellation.Cancel(); return RecognizedRect; }, () => waits++, cancellation.Token));
        Assert.Equal(0, waits);
    }

    [Fact]
    public void RecognitionCancellationIsNotRetried()
    {
        var original = new OperationCanceledException();
        int waits = 0;
        var error = Assert.Throws<OperationCanceledException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => throw original, () => waits++, CancellationToken.None));
        Assert.Same(original, error);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void NormalTaskStopIsNotRetried()
    {
        var original = new NormalEndException("stop");
        int waits = 0;
        var error = Assert.Throws<NormalEndException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => throw original, () => waits++, CancellationToken.None));
        Assert.Same(original, error);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void WaitFailurePropagatesWithoutAnotherRecognition()
    {
        var original = new InvalidOperationException("map not ready");
        int captures = 0;
        var error = Assert.Throws<InvalidOperationException>(() => TpTaskFastDrag.RecognizeRestoredMapRect(
            () => { captures++; return default; }, () => throw original, CancellationToken.None));
        Assert.Same(original, error);
        Assert.Equal(1, captures);
    }
}
