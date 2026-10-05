using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;
public sealed class NativeSingleCatalogTests:IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"single-catalog-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
    [Fact]
    public async Task DescribeNativeTaskExposesActualSingleExecutionCapability()
    {
        Directory.CreateDirectory(Path.Combine(_root,"OneDragon"));
        File.WriteAllText(Path.Combine(_root,"OneDragon","native.json"),"{\"Name\":\"native\",\"TaskEnabledList\":{\"task-1\":true},\"TaskDefinitions\":{\"task-1\":\"领取邮件\"},\"TaskOrder\":[\"task-1\"]}");
        var response=await ExternalInterfaceConfigurationPlane.DispatchAsync(new InstanceIpcEnvelope{
            Operation=ExternalInterfaceOperations.ConfigDescribe,RequestId=Guid.NewGuid(),Data=new JObject{["configName"]="native"}},new TaskConfigurationContract(_root));
        Assert.True(response.Success,response.ErrorMessage);
        var task=Assert.Single(response.Data!["tasks"]!.Children());
        Assert.Equal("task-1",task["taskId"]!.Value<string>());
        Assert.True(task["singleExecutionSupported"]!.Value<bool>());
        Assert.Equal("native",task["schema"]!.Value<string>());
    }
}
