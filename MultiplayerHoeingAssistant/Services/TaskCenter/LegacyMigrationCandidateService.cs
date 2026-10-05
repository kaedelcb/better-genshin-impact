using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneDragonMigration.Core;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record LegacyMigrationCandidateReport(string CandidateDirectory, IReadOnlyList<string> WorkflowIds, bool Reused);

internal static class LegacyMigrationCandidateService
{
    internal static LegacyMigrationCandidateReport Prepare(string sourceUserRoot, string candidateRoot, WorkflowStore store)
    {
        var source = Path.GetFullPath(sourceUserRoot).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (MigrationSwitchTransaction.HasReparsePoint(source) || MigrationSwitchTransaction.HasReparsePoint(destination) ||
            source.Equals(destination, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("迁移源和候选目录须独立且没有目录链接；原件未修改");
        var configs = Path.Combine(source,"OneDragon");
        if (!Directory.Exists(configs)) throw new InvalidOperationException("旧User目录中没有OneDragon配置");
        var files = Directory.GetFiles(configs,"*.json").OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length==0) throw new InvalidOperationException("旧User目录中没有一条龙配置文件");
        if (files.Any(MigrationSwitchTransaction.HasReparsePoint)) throw new InvalidOperationException("配置文件有链接，未读取或转换");
        var schedule=Path.Combine(source,"config.json");
        if (!File.Exists(schedule)) schedule=null;
        var inputs=files.Concat(schedule is null ? [] : new[] {schedule}).ToArray();
        var sourceHashes=inputs.ToDictionary(p=>p,p=>OneDragonMigrationEngine.Sha256Of(File.ReadAllBytes(p)),StringComparer.OrdinalIgnoreCase);
        var sourceKey=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.ToUpperInvariant())))[..16].ToLowerInvariant();
        var scope=Path.Combine(destination,sourceKey);
        Directory.CreateDirectory(scope);
        using var lease=new FileStream(Path.Combine(scope,"prepare.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var indexPath=Path.Combine(scope,"source-index.json");
        JsonObject index;
        if (File.Exists(indexPath))
        {
            index=JsonNode.Parse(File.ReadAllBytes(indexPath)) as JsonObject ?? throw new InvalidOperationException("迁移索引损坏，原件保留；不能重建身份");
            if (index["source"]?.GetValue<string>()!=source || index["sourceHashes"] is not JsonObject hashes ||
                hashes.Count!=sourceHashes.Count || sourceHashes.Any(p=>hashes[p.Key]?.GetValue<string>()!=p.Value))
                throw new InvalidOperationException("旧数据在上次准备后有变化；保留已有映射和流程，不能自动覆盖或重置身份");
            var candidate=index["candidateDirectory"]!.GetValue<string>();
            if (!Path.GetFullPath(candidate).StartsWith(scope+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("迁移候选目录身份不一致，未导入");
            VerifyOutputs(candidate,index["outputs"]!.AsObject());
            ImportCandidates(candidate,index["flowIds"]!.AsObject(),store);
            VerifyInputs(configs,files,sourceHashes);
            return new(candidate,index["flowIds"]!.AsObject().Select(x=>x.Key).ToArray(),true);
        }
        if (Directory.EnumerateDirectories(scope).Any())
            throw new InvalidOperationException("先前准备有产物但完整映射索引缺失，原件保留；请从原manifest恢复，不生成新身份");
        var result=OneDragonMigrationEngine.Migrate(files,schedule,scope);
        VerifyInputs(configs,files,sourceHashes);
        if (result.ActivationBlockers.Count!=0)
            throw new InvalidOperationException("旧数据存在迁移阻断，报告已保留于"+result.CandidateDir+"；未导入或激活："+string.Join("；",result.ActivationBlockers));
        var flowIds=new JsonObject();
        var outputHashes=new JsonObject();
        foreach (var path in result.WrittenFiles)
        {
            var relative=Path.GetRelativePath(result.CandidateDir!,path);
            outputHashes[relative]=OneDragonMigrationEngine.Sha256Of(File.ReadAllBytes(path));
            if (!relative.StartsWith("flows"+Path.DirectorySeparatorChar,StringComparison.Ordinal)) continue;
            var flow=JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
            var name=flow["name"]!.GetValue<string>();
            var id="wf-migr-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceKey+"\n"+name)))[..16].ToLowerInvariant();
            if (store.List().Any(x=>x.WorkflowId==id))
                throw new InvalidOperationException("已有同源迁移流程而映射索引缺失，原件保留；不能覆盖或重建身份");
            flowIds[id]=relative;
        }
        index=new JsonObject { ["version"]=1,["source"]=source,["sourceHashes"]=new JsonObject(sourceHashes.Select(x=>new KeyValuePair<string,JsonNode?>(x.Key,JsonValue.Create(x.Value)))),
            ["candidateDirectory"]=result.CandidateDir,["outputs"]=outputHashes,["flowIds"]=flowIds};
        var temporary=indexPath+"."+Guid.NewGuid().ToString("N")+".tmp";
        using (var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            var data=System.Text.Encoding.UTF8.GetBytes(index.ToJsonString());file.Write(data);file.Flush(true);
        }
        File.Move(temporary,indexPath,false); // 索引先于导入持久化，重试沿用已生成映射与同一流程身份。
        ImportCandidates(result.CandidateDir!,flowIds,store);
        VerifyInputs(configs,files,sourceHashes);
        return new(result.CandidateDir!,flowIds.Select(x=>x.Key).ToArray(),false);
    }

    internal static void VerifyOutputs(string candidate,JsonObject outputs)
    {
        foreach (var item in outputs)
        {
            if (!MigrationSwitchTransaction.IsSafeRelativePath(item.Key.Replace('\\','/')))
                throw new InvalidOperationException("迁移产物路径不安全，未导入");
            var file=Path.Combine(candidate,item.Key);
            if (MigrationSwitchTransaction.HasReparsePoint(file) || !File.Exists(file) ||
                OneDragonMigrationEngine.Sha256Of(File.ReadAllBytes(file))!=item.Value!.GetValue<string>())
                throw new InvalidOperationException("迁移产物缺失或已变化，保留原索引；未导入");
        }
    }

    private static void ImportCandidates(string candidate,JsonObject flowIds,WorkflowStore store)
    {
        foreach (var item in flowIds)
        {
            var relative=item.Value!.GetValue<string>();
            if (!MigrationSwitchTransaction.IsSafeRelativePath(relative.Replace('\\','/')))
                throw new InvalidOperationException("迁移流程路径不安全");
            if (store.List().Any(x=>x.WorkflowId==item.Key)) continue; // 已有候选、已激活或用户编辑均不覆盖。
            var file=Path.Combine(candidate,relative);
            var doc=JsonSerializer.Deserialize<MultiplayerHoeingAssistant.Models.WorkflowDocument>(File.ReadAllBytes(file))!;
            if (doc.Activation?.Status!="candidate-ready") throw new InvalidOperationException("迁移流程不是可准备候选");
            doc.WorkflowId=item.Key;
            store.Save(doc,null);
        }
    }

    private static void VerifyInputs(string configs,string[] files,Dictionary<string,string> hashes)
    {
        if (!Directory.GetFiles(configs,"*.json").OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).SequenceEqual(files) ||
            hashes.Any(p=>!File.Exists(p.Key)||OneDragonMigrationEngine.Sha256Of(File.ReadAllBytes(p.Key))!=p.Value))
            throw new InvalidOperationException("迁移准备期间源配置已变化，候选不能据此激活；源文件没有被修改");
    }
}
