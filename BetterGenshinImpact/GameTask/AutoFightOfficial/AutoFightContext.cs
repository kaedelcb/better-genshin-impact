// using BetterGenshinImpact.Core.Simulator;
// using BetterGenshinImpact.GameTask.AutoFightOfficial.Assets;
// using BetterGenshinImpact.Model;
//
// namespace BetterGenshinImpact.GameTask.AutoFightOfficial;

// /// <summary>
// /// 自动战斗上下文
// /// 请在启动BetterGI以后再初始化
// /// </summary>
// public class AutoFightContext : Singleton<AutoFightContext>
// {
//     private AutoFightContext()
//     {
//         Simulator = TaskContext.Instance().PostMessageSimulator;
//     }
//
//     /// <summary>
//     /// find资源
//     /// </summary>
//     public AutoFightAssets FightAssets => AutoFightAssets.Get(CaptureRegion);
//
//     /// <summary>
//     /// 战斗专用的PostMessage模拟键鼠操作
//     /// </summary>
//     public readonly PostMessageSimulator Simulator;
// }
