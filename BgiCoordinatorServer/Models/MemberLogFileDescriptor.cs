namespace BgiCoordinatorServer.Models;

/// <summary>
/// 远程成员日志文件列表项（远程成员完整日志下载：目标端 ReportMemberLogFiles 上报 →
/// 服务端纯透传广播 MemberLogFileList 给观众端，不存储）。
/// </summary>
public class MemberLogFileDescriptor
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWrite { get; set; }
}
