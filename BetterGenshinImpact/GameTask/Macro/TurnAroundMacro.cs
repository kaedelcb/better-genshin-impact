using BetterGenshinImpact.Core.Simulator;
using System.Threading;
using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.GameTask.Macro
{
    public class TurnAroundMacro
    {
        public static void Done()
        {
            if (TaskContext.Instance().Config.MacroConfig.RunaroundMouseXInterval == 0)
            {
                TaskContext.Instance().Config.MacroConfig.RunaroundMouseXInterval = 1;
            }

            // LCB修改为自动拾取
            //==============================完成后打开钓鱼标记======LCB==========================================
            TaskContext.Instance().Config.AutoFishingConfig.Enabled = false; //钓鱼触发新功能
            //==============================================================================
            new ScanPickTask().Start(CancellationToken.None).Wait();
            //==============================完成后打开钓鱼标记======LCB==========================================
            TaskContext.Instance().Config.AutoFishingConfig.Enabled = true; //钓鱼触发新功能
            //==============================================================================
        }
    }
}
