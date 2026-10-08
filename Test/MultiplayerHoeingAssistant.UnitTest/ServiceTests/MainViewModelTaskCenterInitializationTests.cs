using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using OneDragonMigration.Core;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class MainViewModelTaskCenterInitializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "paired-startup-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Tools", "MultiplayerHoeingAssistant")]
    [InlineData("Tool", "MultiplayerHoeingAssistant")]
    [InlineData("Tools", "MHA")]
    public void InstalledAssistantUsesTheMarkedBgiPackageRoot(string tools, string assistant)
    {
        var installed = Path.Combine(_root, tools, assistant);
        Directory.CreateDirectory(installed);
        var image = Path.Combine(_root, "BetterGI.exe"); File.WriteAllText(image, "owned fixture");
        File.WriteAllText(Path.Combine(_root, InstallationPipeScope.MarkerFile),
            "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}");
        var resolved = MainViewModel.ResolveSamePackageBgiPath(installed);
        Assert.Equal(image, resolved);
        using var client = new IpcClient(Path.GetDirectoryName(resolved)!);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Assert.Equal(InstallationPipeScope.ResolveRootPipe(sid, _root), client.GetPipeName());
        Assert.Contains(".install-", client.GetPipeName());
    }

    [Fact]
    public void MissingPairedExecutableIsReportedInsteadOfUsingAnotherInstallation()
    {
        var installed = Path.Combine(_root, "Tools", "MultiplayerHoeingAssistant"); Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(_root, InstallationPipeScope.MarkerFile),
            "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}");
        Assert.Throws<FileNotFoundException>(() => MainViewModel.ResolveSamePackageBgiPath(installed));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
