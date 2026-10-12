using BetterGenshinImpact.Service.OneDragon;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class UidClipboardCopyTests
{
    [Fact]
    public async Task FirstClickMissed_WaitsForHoverAndRetriesBeforeLeavingMenu()
    {
        var steps = new List<string>();
        var clicks = 0;
        var readsAfterSecondClick = 0;
        var result = await UidClipboardCopy.TryCopyAsync(
            () => steps.Add("move"),
            (attempt, _) => { clicks++; steps.Add("click" + attempt); return Task.CompletedTask; },
            () => clicks < 2 || ++readsAfterSecondClick < 2 ? "" : "171234545",
            () => true, CancellationToken.None,
            (_, _) => { steps.Add("wait"); return Task.CompletedTask; });

        Assert.Equal("171234545", result.Text);
        Assert.Equal(2, result.ClicksSent);
        Assert.False(result.FocusLost);
        Assert.Equal(["move", "wait", "click1"], steps.Take(3));
        Assert.Equal(2, steps.Count(s => s == "move"));
    }

    [Fact]
    public async Task FocusLostAfterMove_DoesNotClickOtherWindow()
    {
        var focusChecks = 0;
        var clicks = 0;
        var result = await UidClipboardCopy.TryCopyAsync(
            () => { }, (_, _) => { clicks++; return Task.CompletedTask; }, () => "", () => ++focusChecks == 1,
            CancellationToken.None, (_, _) => Task.CompletedTask);

        Assert.True(result.FocusLost);
        Assert.Equal(0, result.ClicksSent);
        Assert.Equal(0, clicks);
    }

    [Fact]
    public async Task EmptyClipboardAfterBothClicks_ReportsNoCopy()
    {
        var clicks = 0;
        var result = await UidClipboardCopy.TryCopyAsync(
            () => { }, (_, _) => { clicks++; return Task.CompletedTask; }, () => "", () => true,
            CancellationToken.None, (_, _) => Task.CompletedTask);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(2, result.ClicksSent);
        Assert.Equal(2, clicks);
    }
}
