using System.Text;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class ExternalResourceEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resource-editor-" + Guid.NewGuid().ToString("N"));
    private const string Id = "3ccda867-472f-4c86-bb13-7d209e0a6609";
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpensExactReferencedResourceWithoutChangingConfiguration(bool dragon)
    {
        var bytes = Encoding.UTF8.GetBytes(dragon
            ? "{\"TaskEnabledList\":{\"" + Id + "\":true},\"TaskDefinitions\":{\"" + Id + "\":\"领取邮件\"}}"
            : "{\"Projects\":[]}");
        var path = Seed(dragon, bytes);
        var request = Request(dragon, TaskConfigurationContract.Revision(bytes));
        if (dragon) request.Data!["taskId"] = Id;
        var opened = 0;
        var result = await ExternalResourceEditor.DispatchAsync(request, new(_root), (d,n,id) =>
        {
            Assert.Equal(dragon,d); Assert.Equal("原资源",n); Assert.Equal(dragon ? Id : null,id); opened++;
        });
        Assert.True(result.Success); Assert.Equal(1,opened);
        Assert.Equal("editor_opened", result.Data!["status"]!.Value<string>());
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("missing-task")]
    [InlineData("missing-revision")]
    public async Task InvalidReferenceDoesNotNavigateOrWrite(string defect)
    {
        var bytes=Encoding.UTF8.GetBytes("{\"TaskEnabledList\":{\""+Id+"\":true}}");
        var path=Seed(true,bytes); var request=Request(true,TaskConfigurationContract.Revision(bytes));
        if (defect=="stale") request.Data!["expectedConfigRevision"]="old";
        if (defect=="missing-task") request.Data!["taskId"]="other-task";
        if (defect=="missing-revision") request.Data!.Remove("expectedConfigRevision");
        var opened=0;
        var result=await ExternalResourceEditor.DispatchAsync(request,new(_root),(_,_,_)=>opened++);
        Assert.False(result.Success); Assert.Equal(0,opened); Assert.Equal(bytes,File.ReadAllBytes(path));
        Assert.Equal(defect=="stale" ? "configuration_changed" : defect=="missing-task" ? "task_not_found" : "invalid_request",result.ErrorCode);
    }

    private string Seed(bool dragon,byte[] bytes)
    {
        var dir=Path.Combine(_root,dragon?"OneDragon":"ScriptGroup");Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"原资源.json");File.WriteAllBytes(path,bytes);return path;
    }
    private static InstanceIpcEnvelope Request(bool dragon,string revision)=>new()
    {
        Operation=ExternalInterfaceOperations.ConfigOpenResourceEditor, RequestId=Guid.NewGuid(),
        Data=new JObject {[dragon?"configName":"groupName"]="原资源",["expectedConfigRevision"]=revision},
    };
}
