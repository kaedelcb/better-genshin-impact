using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
class Program
{
    static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
    static async Task<int> Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_,n) => File.Exists(Path.Combine(args[0],n.Name+".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(args[0],n.Name+".dll")) : null;
        if(args.Length>3 && args[3]=="property")
        {
            var assembly=Assembly.LoadFrom(Path.Combine(args[0],"BetterGenshinImpact.UnitTest.dll"));
            var type=assembly.GetType("BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests.WaitPointReportTests",true)!;
            var result=(bool)type.GetMethod("WaitPointReport_SyncPointIdValidation_WorksCorrectly")!.Invoke(Activator.CreateInstance(type),new object[]{"__"})!;
            var product=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="BetterGI");
            File.WriteAllText(Path.Combine(args[1],"property-counterexample.json"),JsonSerializer.Serialize(new{input="__",property_result=result,product=product.Location,product_sha=Hash(product.Location),scope="direct existing property method; no game or SDK transport"}));
            return result?3:0;
        }
        return await Run(args);
    }
    static async Task<int> Run(string[] args)
    {
        var evidence=args[1];var root=Path.Combine(evidence,"host-data");
        using var client = new BgiExternalClient();
        var state=await client.StartAsync();
        if(state!=BgiExternalLinkState.Ready)throw new Exception("real SDK not ready: "+state);
        await client.RefreshStatusSnapshotAsync("non-game-validation");
        File.WriteAllText(Path.Combine(evidence,"hello-status.json"),JsonSerializer.Serialize(new{state,client.BgiVersion,client.SessionId,client.Capabilities,client.LatestStatusSnapshotJson}));
        if(args.Length>3 && args[3]=="inspect")
        {
            var fence=await client.SendCommandAsync("ext.execution.manualStopFence");
            File.WriteAllText(Path.Combine(evidence,"manual-stop-fence.json"),JsonSerializer.Serialize(new{client.ServerEpoch,response=fence,frequency=System.Diagnostics.Stopwatch.Frequency,actual_sdk=typeof(BgiExternalClient).Assembly.Location,module_sha=Hash(typeof(BgiExternalClient).Assembly.Location)}));
            return fence.Success?0:2;
        }
        if(args.Length>3 && args[3]=="editor")
        {
            var name="验收-统一版本-01a10c87";
            var desc=await client.SendCommandAsync("ext.config.describe",new{configName=name});
            File.WriteAllText(Path.Combine(evidence,"describe.json"),JsonSerializer.Serialize(desc));
            if(!desc.Success || desc.Data is null)throw new Exception("describe unavailable");
            using var doc=JsonDocument.Parse(desc.Data);
            var revision=doc.RootElement.GetProperty("configRevision").GetString();
            var opened=await client.SendCommandAsync("ext.config.openResourceEditor",new{configName=name,expectedConfigRevision=revision});
            File.WriteAllText(Path.Combine(evidence,"editor.json"),JsonSerializer.Serialize(opened));
            Console.WriteLine(JsonSerializer.Serialize(opened));
            return opened.Success?0:2;
        }
        var host=new TaskCenterHost(Path.Combine(root,"flows"),Path.Combine(root,"runs"),Path.Combine(root,"catalog.json"),()=>client,()=>true,()=>null);
        var prepared=await host.PrepareLegacyMigrationAsync(args[2]);
        if(!prepared.Ok)throw new Exception(prepared.Message);
        var entry=host.Workflows.List().Single();var original=Hash(entry.FilePath);
        var activated=await host.ActivateMigrationCandidateAsync(entry.WorkflowId);
        File.WriteAllText(Path.Combine(evidence,"activation.json"),JsonSerializer.Serialize(activated));
        if(!activated.Ok)throw new Exception(activated.Message);
        var active=host.Workflows.LoadSnapshot(entry.WorkflowId);
        if(active.Document.Activation?.Status!="active")throw new Exception("active readback mismatch");
        File.WriteAllText(Path.Combine(evidence,"active-readback.json"),JsonSerializer.Serialize(active.Document));
        var rollback=await host.RollbackMigrationAsync(entry.WorkflowId);
        if(!rollback.Ok)throw new Exception(rollback.Message);
        if(Hash(entry.FilePath)!=original)throw new Exception("workflow original bytes not restored");
        var repeat=await host.RollbackMigrationAsync(entry.WorkflowId);
        if(!repeat.Ok)throw new Exception(repeat.Message);
        File.WriteAllText(Path.Combine(evidence,"result.json"),JsonSerializer.Serialize(new{prepared,activated,rollback,repeat,workflow_sha_restored=original,actual_sdk=typeof(BgiExternalClient).Assembly.Location,actual_module_sha=Hash(typeof(BgiExternalClient).Assembly.Location),fake_transport=false,game_launched=false}));
        await host.ShutdownAsync();
        return 0;
    }
}
