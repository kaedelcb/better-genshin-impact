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
    internal sealed record Plan(string InstallId, string SourceRoot, JsonObject SourceHashes, object[] Files);

    internal static Plan? Find(string candidateRoot, WorkflowSnapshot snapshot)
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
            var id = "std-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                snapshot.Document.WorkflowId + "\n" + JsonSerializer.Serialize(files))))[..32].ToLowerInvariant();
            return new(id, index["source"]!.GetValue<string>(), index["sourceHashes"]!.AsObject(), files.ToArray());
        }
        return null;
    }

    internal static async Task InstallAsync(Plan plan, IResourceCatalogTransport transport)
    {
        RequireCapability(transport);
        var status = await SendAsync(plan, "status", transport);
        if (status is "absent" or "rolledBack") VerifySource(plan);
        else if (status is not ("prepared" or "installed"))
            throw new InvalidOperationException("安装有未完成回退，请先恢复该迁移");
        if (await SendAsync(plan, "install", transport) != "installed")
            throw new InvalidOperationException("标准配置安装未得到真实读回确认，流程未激活");
    }

    internal static async Task RollbackAsync(Plan plan, IResourceCatalogTransport transport)
    {
        RequireCapability(transport);
        var status = await SendAsync(plan, "status", transport);
        if (status == "absent") return;
        if (await SendAsync(plan, "rollback", transport) != "rolledBack")
            throw new InvalidOperationException("流程已回退，但BGI标准配置回退未确认；安装记录保留，请重新执行迁移回退");
    }

    private static void RequireCapability(IResourceCatalogTransport transport)
    {
        if (!transport.IsReady || !transport.HasCapability("config.standardMigration"))
            throw new InvalidOperationException("请连接支持标准迁移安装的本版BGI，未修改旧配置");
    }

    private static async Task<string> SendAsync(Plan plan, string action, IResourceCatalogTransport transport)
    {
        var response = await transport.SendAsync(Operation, new { installId = plan.InstallId, action, files = action == "install" ? plan.Files : null }, CancellationToken.None);
        if (response == null) throw new InvalidOperationException("BGI安装/回退未确认；保留映射与安装身份，在静止后重试或迁移回退");
        using var doc = JsonDocument.Parse(response);
        return doc.RootElement.GetProperty("status").GetString() ?? throw new InvalidOperationException("安装状态未知");
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
