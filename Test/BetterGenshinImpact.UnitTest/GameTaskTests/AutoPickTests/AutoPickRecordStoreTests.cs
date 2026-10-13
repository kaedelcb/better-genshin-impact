using BetterGenshinImpact.GameTask.AutoPick;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPickTests;

[Collection("GlobalUserRoot")]
public sealed class AutoPickRecordStoreTests
{
    [Fact]
    public void JsDrain_ConsumesDeliveryQueueButKeepsRecentBagFullEvidence()
    {
        AutoPickRecordStore.Drain();
        var suffix = Guid.NewGuid().ToString("N");
        var bagName = "获得物品-" + suffix;
        var interactName = "交互名称-" + suffix;
        AutoPickRecordStore.Record(bagName, interactName);

        var delivered = Assert.Single(AutoPickRecordStore.Drain());
        Assert.Equal(bagName, delivered.Name);
        Assert.Equal(interactName, delivered.InteractName);
        Assert.Empty(AutoPickRecordStore.Drain());

        var recent = Assert.Single(AutoPickRecordStore.PeekRecent(TimeSpan.FromMinutes(1)),
            record => record.InteractName == interactName);
        Assert.Equal(delivered, recent);
    }

    [Fact]
    public void RecordLimit_KeepsTheLatestHundredInBothConsumers()
    {
        AutoPickRecordStore.Drain();
        var suffix = Guid.NewGuid().ToString("N");
        for (var i = 0; i <= 100; i++)
            AutoPickRecordStore.Record($"物品-{suffix}-{i}", $"交互-{suffix}-{i}");

        var delivered = AutoPickRecordStore.Drain();
        Assert.Equal(100, delivered.Length);
        Assert.Equal($"交互-{suffix}-1", delivered[0].InteractName);
        Assert.Equal($"交互-{suffix}-100", delivered[^1].InteractName);

        var recent = AutoPickRecordStore.PeekRecent(TimeSpan.FromMinutes(1))
            .Where(record => record.InteractName.StartsWith($"交互-{suffix}-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(delivered, recent);
        Assert.Empty(AutoPickRecordStore.Drain());
    }
}
