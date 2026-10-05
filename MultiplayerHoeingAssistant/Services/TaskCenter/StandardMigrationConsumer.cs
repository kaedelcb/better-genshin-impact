using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal static class StandardMigrationConsumer
{
    internal const string Operation = "ext.config.migrateStandard";
    private sealed record FileInput(string configName, string contentFile, string contentRevision, string sourceRevision);
    internal sealed record Plan(string InstallId, string SourceRoot, JsonObject SourceHashes, object[] Files, string RecordFile, bool HasWorkflowHistory);
    internal sealed record InstallationBinding(string InstallId, string UserRoot, string State);
    private sealed record PeerStatus(string State, string UserRoot);

    internal static Plan? Find(string candidateRoot, WorkflowSnapshot snapshot, bool hasWorkflowHistory)
    {
        if (!Directory.Exists(candidateRoot)) return null; // Old manually imported candidates retain their original revision contract.
        if (MigrationSwitchTransaction.HasReparsePoint(candidateRoot)) throw new InvalidOperationException("迁移目录有链接");
        foreach (var scope in Directory.GetDirectories(candidateRoot))
        {
            if (MigrationSwitchTransaction.HasReparsePoint(scope)) throw new InvalidOperationException("迁移索引目录有链接");
            var path = Path.Combine(scope, "source-index.json");
            if (!File.Exists(path)) continue;
            if (MigrationSwitchTransaction.HasReparsePoint(path)) throw new InvalidOperationException("迁移索引有链接");
            var index = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            if (index["flowIds"] is not JsonObject ids || !ids.ContainsKey(snapshot.Document.WorkflowId!)) continue;
            var candidate = Path.GetFullPath(index["candidateDirectory"]!.GetValue<string>());
            if (!candidate.StartsWith(Path.GetFullPath(scope) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("迁移目录身份不一致");
            var outputs = index["outputs"]!.AsObject();
            LegacyMigrationCandidateService.VerifyOutputs(candidate, outputs);
            var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(candidate, "manifest.json")))!.AsObject();
            var files = new List<object>();
            foreach (var node in snapshot.Document.Nodes)
            {
                if (node.Kind != "resource.oneDragonConfig" || node.Ref?.Config is not { Length: > 0 } name)
                    throw new InvalidOperationException("迁移候选含未映射资源，不能自动安装");
                var key = node.Ref.ConfigKey;
                var source = manifest["sources"]!.AsArray().OfType<JsonObject>().Single(s => s["configKey"]!.GetValue<string>() == key);
                var relative = Path.Combine("standard", "OneDragon", name + ".json");
                if (!MigrationSwitchTransaction.IsSafeRelativePath(relative.Replace('\\', '/')) || !outputs.ContainsKey(relative))
                    throw new InvalidOperationException("迁移标准文件映射缺失，不猜测名称");
                var bytes = File.ReadAllBytes(Path.Combine(candidate, relative));
                var revision = Convert.ToHexString(SHA256.HashData(bytes));
                if (!revision.Equals(node.Ref.Revision, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("迁移候选引用已变化，未安装");
                var sourceName = source["file"]!.GetValue<string>();
                if (!MigrationSwitchTransaction.IsSafeRelativePath(sourceName) || Path.GetFileName(sourceName) != sourceName)
                    throw new InvalidOperationException("旧配置来源身份非法");
                if (!files.OfType<FileInput>().Any(f => f.configName == name))
                    files.Add(new FileInput(name, Path.Combine(candidate, relative), revision, source["sha256"]!.GetValue<string>()));
            }
            var orderedFiles = files.OfType<FileInput>().OrderBy(f => f.configName, StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray();
            var id = "std-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                snapshot.Document.WorkflowId + "\n" + JsonSerializer.Serialize(orderedFiles))))[..32].ToLowerInvariant();
            return new(id, index["source"]!.GetValue<string>(), index["sourceHashes"]!.AsObject(), orderedFiles,
                Path.Combine(scope, "installation-bindings", id, "binding.json"), hasWorkflowHistory || snapshot.Document.Activation?.Status == "active");
        }
        return null;
    }

    internal static async Task InstallAsync(Plan plan, IResourceCatalogTransport transport)
    {
        RequireCapability(transport);
        var binding = LoadBinding(plan);
        var status = await SendAsync(plan, "status", transport, binding?.UserRoot);
        if (binding == null && status.State is not ("absent" or "rolledBack"))
            throw new InvalidOperationException("BGI已有安装记录但原本机目标绑定缺失，原记录保留；不能猜测安装归属");
        if (binding != null && status.State == "absent")
            throw new InvalidOperationException("原目标安装记录缺失，不能猜测先前写入；原配置与本机绑定保留");
        if (status.State is "absent" or "rolledBack") VerifySource(plan);
        else if (status.State is not ("prepared" or "installed"))
            throw new InvalidOperationException("安装有未完成回退，请先恢复该迁移");
        binding ??= new(plan.InstallId, status.UserRoot, "prepared");
        SaveBinding(plan, binding); // Bind the peer before any request that can write native configurations.
        if ((await SendAsync(plan, "install", transport, binding.UserRoot)).State != "installed")
            throw new InvalidOperationException("标准配置安装未得到真实读回确认，流程未激活");
        SaveBinding(plan, binding with { State = "installed" });
    }

    internal static async Task RollbackAsync(Plan plan, IResourceCatalogTransport transport)
    {
        var binding = await ValidateRollbackPeerAsync(plan, transport);
        if (binding == null) return;
        if ((await SendAsync(plan, "rollback", transport, binding.UserRoot)).State != "rolledBack")
            throw new InvalidOperationException("流程已回退，但BGI标准配置回退未确认；安装记录保留，请重新执行迁移回退");
        SaveBinding(plan, binding with { State = "rolledBack" });
    }

    internal static async Task<InstallationBinding?> ValidateRollbackPeerAsync(Plan plan, IResourceCatalogTransport transport)
    {
        RequireCapability(transport);
        var binding = LoadBinding(plan);
        var status = await SendAsync(plan, "status", transport, binding?.UserRoot);
        if (binding == null && status.State == "absent" && !plan.HasWorkflowHistory) return null;
        if (binding == null || status.State == "absent")
            throw new InvalidOperationException("原安装绑定或BGI安装记录缺失，不能认定整体回退；原件与映射保留");
        return binding;
    }

    private static void RequireCapability(IResourceCatalogTransport transport)
    {
        if (!transport.IsReady || !transport.HasCapability("config.standardMigration"))
            throw new InvalidOperationException("请连接支持标准迁移安装的本版BGI，未修改旧配置");
    }

    private static async Task<PeerStatus> SendAsync(Plan plan, string action, IResourceCatalogTransport transport, string? expectedUserRoot = null)
    {
        var response = await transport.SendAsync(Operation, new { installId = plan.InstallId, action, expectedUserRoot,
            files = action == "install" ? plan.Files : null }, CancellationToken.None);
        if (response == null) throw new InvalidOperationException("BGI安装/回退未确认；保留映射与安装身份，在静止后重试或迁移回退");
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement.GetProperty("userRoot").GetString();
        if (root == null || !Path.IsPathFullyQualified(root) || expectedUserRoot != null &&
            !Path.GetFullPath(root).Equals(Path.GetFullPath(expectedUserRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前BGI不是本次标准安装的原目标，未认定回退；请连接原安装位置后重试");
        return new(doc.RootElement.GetProperty("status").GetString() ?? throw new InvalidOperationException("安装状态未知"), Path.GetFullPath(root));
    }

    private static InstallationBinding? LoadBinding(Plan plan)
    {
        if (MigrationSwitchTransaction.HasReparsePoint(plan.RecordFile)) throw new InvalidOperationException("本机安装绑定有链接");
        if (!File.Exists(plan.RecordFile))
        {
            if (Directory.Exists(Path.GetDirectoryName(plan.RecordFile)))
                throw new InvalidOperationException("原安装意图目录存在但绑定缺失，原件保留；不能猜测先前写入或重建目标");
            return null;
        }
        var binding = JsonSerializer.Deserialize<InstallationBinding>(File.ReadAllBytes(plan.RecordFile))
            ?? throw new InvalidOperationException("本机安装绑定损坏，原件保留");
        if (binding.InstallId != plan.InstallId || !Path.IsPathFullyQualified(binding.UserRoot) ||
            binding.State is not ("prepared" or "installed" or "rolledBack"))
            throw new InvalidOperationException("本机安装绑定身份不符，原件保留");
        return binding;
    }

    private static void SaveBinding(Plan plan, InstallationBinding binding)
    {
        if (MigrationSwitchTransaction.HasReparsePoint(plan.RecordFile)) throw new InvalidOperationException("本机安装绑定有链接");
        Directory.CreateDirectory(Path.GetDirectoryName(plan.RecordFile)!);
        var temporary = plan.RecordFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(JsonSerializer.SerializeToUtf8Bytes(binding)); file.Flush(true); }
        File.Move(temporary, plan.RecordFile, File.Exists(plan.RecordFile));
    }

    private static void VerifySource(Plan plan)
    {
        var root = Path.GetFullPath(plan.SourceRoot);
        if (MigrationSwitchTransaction.HasReparsePoint(root)) throw new InvalidOperationException("旧配置来源有链接");
        var configFiles = Directory.GetFiles(Path.Combine(root, "OneDragon"), "*.json");
        if (configFiles.Length != plan.SourceHashes.Count(x => Path.GetDirectoryName(x.Key)!.Equals(Path.Combine(root, "OneDragon"), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("旧配置文件集合已变化，未安装");
        foreach (var pair in plan.SourceHashes)
        {
            var path = Path.GetFullPath(pair.Key);
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                MigrationSwitchTransaction.HasReparsePoint(path) || !File.Exists(path) ||
                !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(pair.Value!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("迁移准备后旧数据已变化，未安装；原候选与原件保留");
        }
    }
}
