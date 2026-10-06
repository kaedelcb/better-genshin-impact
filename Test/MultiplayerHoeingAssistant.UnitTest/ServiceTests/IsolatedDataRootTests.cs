using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class IsolatedDataRootTests
{
    [Fact]
    public void UnspecifiedRoot_PreservesExistingUserLocation()
    {
        var appData = Path.Combine(Path.GetTempPath(), "sample-roaming");
        Assert.Equal(Path.Combine(appData, "NexusBGI"), AssistantDataDirectory.ResolveRoot(null, appData));
        Assert.Equal(Path.Combine(appData, "NexusBGI"), AssistantDataDirectory.ResolveRoot("", appData));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(" ")]
    [InlineData("C:relative")]
    public void InvalidExplicitRoot_RejectsInsteadOfUsingUserLocation(string value)
        => Assert.Throws<ArgumentException>(() => AssistantDataDirectory.ResolveRoot(value, "real-user-roaming"));

    [Fact]
    public void ExplicitRoot_IsTheDirectoryItself_NotAnExtraNexusSubdirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "own-acceptance", "data");
        Assert.Equal(Path.GetFullPath(path), AssistantDataDirectory.ResolveRoot(path, "unused-real-user-roaming"));
    }

    [ExplicitDataRootFact]
    public void ProductionStores_ShareExplicitRoot_AndConfigPersistsWithoutLegacyImport()
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var root = AssistantDataDirectory.Root;
        Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable(AssistantDataDirectory.EnvironmentVariable)!), root);
        Assert.Equal(Path.Combine(root, "flows"), WorkflowStore.DefaultFlowsDir());
        Assert.Equal(Path.Combine(root, "runs"), RunStore.DefaultRunsDir());
        Assert.Equal(Path.Combine(root, "resource-catalog-cache.json"), ResourceCatalogService.DefaultCacheFile());

        var settings = new AssistConfigManager();
        settings.Save(new AssistConfig { ServerUrl = "", StandaloneMode = true, GuardBgi = false,
            AutoLaunchOnBoot = false, AutoLaunchWithBgi = false });
        Assert.True(File.Exists(Path.Combine(root, "assistant-config.json")));
        Assert.True(new AssistConfigManager().Load().StandaloneMode);
        Assert.Equal("", new AssistConfigManager().Load().ServerUrl);
        new StartupFlowStore().Save(new StartupFlowConfig { Enabled = false });
        Assert.True(File.Exists(Path.Combine(root, "startup-flow.json")));
        Assert.False(new StartupFlowStore().Load().Enabled);
        new StartupFlowSchemeStore().SaveAll([]);
        Assert.True(File.Exists(Path.Combine(root, "startup-flow-schemes.json")));
        Assert.Empty(new StartupFlowSchemeStore().Load());
    }
}

public sealed class ExplicitDataRootFactAttribute : FactAttribute
{
    public ExplicitDataRootFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AssistantDataDirectory.EnvironmentVariable)))
            Skip = "Requires an explicitly isolated process data root; never writes the real user store.";
    }
}
