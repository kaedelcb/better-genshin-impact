#nullable enable

using BetterGenshinImpact.Core.Script.Dependence;
using Xunit;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class PickupWaypointScriptLogTests
{
    [Theory]
    [InlineData("交互或拾取：\"游医\"", "游医")]
    [InlineData("交互或拾取：  “调查” ", "调查")]
    [InlineData("交互或拾取：冒险家", "冒险家")]
    [InlineData("调试：交互或拾取：\"游医\"", null)]
    [InlineData("交互或拾取：\"游医\"\n其他行", null)]
    [InlineData("交互或拾取：\"\"", null)]
    [InlineData(null, null)]
    public void ParsesOnlySinglePickupLogMessages(string? message, string? expected)
    {
        Assert.Equal(expected, Log.GetPickupItemName(message));
    }
}
