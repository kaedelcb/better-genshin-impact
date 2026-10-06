using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public static class WorkflowScheduleTransfer
{
    private const string Schema="mistletoe.scheduleList";
    public static void Export(WorkflowStore store,string destination)
    {
        var entries=store.List();
        if(entries.Any(e=>e.Status==WorkflowFileStatus.Quarantined))throw new InvalidOperationException("列表中存在隔离文件，请先处置；未导出不完整列表。");
        var target=Path.GetFullPath(destination);
        if(entries.Any(e=>target.StartsWith(Path.GetDirectoryName(Path.GetFullPath(e.FilePath))!+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("导出目标不能位于流程存储目录内");
        var flows=new JsonArray();
        foreach(var e in entries)
        {
            var snapshot=store.LoadSnapshot(e.WorkflowId);
            var json=JsonNode.Parse(JsonSerializer.Serialize(snapshot.Document))!;
            json.AsObject().Remove(WorkflowMigrationConsumer.TransactionField);
            StripAccounts(json);flows.Add(json);
        }
        var package=new JsonObject{["schema"]=Schema,["schemaVersion"]=1,["flows"]=flows};
        var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,package.ToJsonString(new(){WriteIndented=true}),new UTF8Encoding(false));File.Move(temp,target,overwrite:true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public static IReadOnlyList<string> Import(WorkflowStore store,string source)
    {
        var bytes=File.ReadAllBytes(source);var json=JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("调度列表须为JSON对象");
        if(json["schema"]?.GetValue<string>()!=Schema || json["schemaVersion"]?.GetValue<int>()!=1 || json["flows"] is not JsonArray flows)
            throw new InvalidDataException("不支持的调度列表格式");
        var documents=new List<WorkflowDocument>();
        foreach(var item in flows)
        {
            if(item is not JsonObject o || o["schema"]?.GetValue<string>()!=WorkflowDocumentSchema.Name || o["schemaVersion"]?.GetValue<int>()!=WorkflowDocumentSchema.Version)
                throw new InvalidDataException("列表包含不支持的流程；尚未导入任何项目。");
            var doc=o.Deserialize<WorkflowDocument>() ?? throw new InvalidDataException("流程为空");
            if(string.IsNullOrWhiteSpace(doc.Name) || doc.Nodes is null || doc.Triggers is null || doc.Terminal is null || doc.Nodes.Any(n=>n is null || n.Strategies is null))
                throw new InvalidDataException("流程必要字段无效；尚未导入任何项目。");
            doc.WorkflowId=WorkflowStore.NewWorkflowId();
            doc.ExtensionData ??=new();doc.ExtensionData.Remove(WorkflowMigrationConsumer.TransactionField);
            doc.ExtensionData["importedSchedule"]=JsonSerializer.SerializeToElement(true);
            doc.Activation=new(){Status="candidate-ready"};documents.Add(doc);
        }
        // 先完整判型，再一次持有原存储锁写入新身份；无覆盖既有流程，不自动执行。
        return store.WithMigrationWindow(()=>
        {
            var imported=new List<string>();
            try{foreach(var doc in documents){store.Save(doc,null);imported.Add(doc.WorkflowId!);}}
            catch(Exception ex){throw new IOException($"列表导入写入失败；已保留 {imported.Count} 个只读候选，原流程未覆盖。请刷新查看后处置。",ex);}
            return (IReadOnlyList<string>)imported;
        });
    }
    private static void StripAccounts(JsonNode node)
    {
        if(node is JsonObject obj)
        {
            foreach(var key in obj.Select(p=>p.Key).ToArray())
                if(new[]{"uid","bindingCode","GenshinUid","AccountBindingCode","AccountBinding"}.Contains(key,StringComparer.OrdinalIgnoreCase))obj.Remove(key);
                else if(obj[key] is {} child)StripAccounts(child);
        }
        else if(node is JsonArray list)foreach(var child in list)if(child is not null)StripAccounts(child);
    }
}
