using System.Collections.Generic;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask.TaskProgress;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.Service.Interface;

public interface IScriptService
{
    /// <param name="job">[A2] 作业描述符：非空时经 TaskRunner 漏斗登记进 JobRegistry；null = 不登记（旧行为）。</param>
    Task RunMulti(IEnumerable<ScriptGroupProject> projectList, string? groupName = null, TaskProgress? taskProgress = null, JobDescriptor? job = null);
}
