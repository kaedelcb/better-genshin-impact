using System.IO;
using MultiplayerHoeingAssistant.Services;
using OneDragonMigration.Core;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    /// <summary>Runs after the final assistant configuration is loaded, before any BGI monitor or status timer.</summary>
    private void InitializeTaskCenterCompatibility()
    {
        EnsureSamePackageBgiPath();
        if (IsExecutorMode) _ = TaskCenterHost;
    }

    private IpcClient CreateConfiguredIpcClient() => new(ResolveConfiguredBgiDirectory());

    private string ResolveConfiguredBgiDirectory()
    {
        if (PathIdentity.TryNormalizeLocalDriveAbsolute(Config?.BgiPath ?? "", out var configured))
            return Path.GetDirectoryName(configured)!;
        var paired = ResolveSamePackageBgiPath(AppContext.BaseDirectory);
        return paired is null ? AppContext.BaseDirectory : Path.GetDirectoryName(paired)!;
    }

    /// <summary>Supports the actual paired layout: BGI root plus Tools/MultiplayerHoeingAssistant.</summary>
    internal static string? ResolveSamePackageBgiPath(string assistantDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(assistantDirectory));
        var direct = Path.Combine(current.FullName, "BetterGI.exe");
        if (InstallationPipeScope.IsIsolatedPackage(current.FullName))
            return File.Exists(direct) ? direct : throw new FileNotFoundException("同套程序缺少 BetterGI.exe", direct);
        if (File.Exists(direct)) return direct;
        if ((current.Name.Equals("MultiplayerHoeingAssistant", StringComparison.OrdinalIgnoreCase) ||
             current.Name.Equals("MHA", StringComparison.OrdinalIgnoreCase)) &&
            current.Parent is { } tools &&
            (tools.Name.Equals("Tools", StringComparison.OrdinalIgnoreCase) || tools.Name.Equals("Tool", StringComparison.OrdinalIgnoreCase)) &&
            tools.Parent is { } root)
        {
            var paired = Path.Combine(root.FullName, "BetterGI.exe");
            var marked = InstallationPipeScope.IsIsolatedPackage(root.FullName);
            if (File.Exists(paired)) return paired;
            if (marked) throw new FileNotFoundException("同套程序缺少 BetterGI.exe", paired);
        }
        return null;
    }

    /// <summary>A ready connection refreshes the existing panel's catalog without launching or sending a task.</summary>
    private async Task RefreshTaskCenterCatalogAfterConnectionAsync()
    {
        TaskCenterHost? host;
        lock (_taskCenterHostGate) host = _disposing ? null : _taskCenterHost;
        if (host is null) return;
        try { await host.Catalog.RefreshAsync().ConfigureAwait(false); }
        catch (Exception ex) { AddLog("[任务中心] 资源目录暂时无法读取：" + ex.Message); }
    }
}
