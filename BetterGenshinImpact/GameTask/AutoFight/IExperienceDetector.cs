using System;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 经验检测器统一抽象（multiplayer-hoeing-exp-cap-stop）。
/// 供联机锄地"基于经验判断停止"按开关在两种实现间二选一：
/// - <see cref="ExperienceDetector"/>：只检测精英经验（57/58/60 数字模板 + 像素色校验）。
/// - <c>AllExpDetector</c>：检测所有经验（复用好感任务 exp.png 通用模板）。
/// 生命周期与语义与原 ExperienceDetector 完全一致，调用方（PathExecutor）只面向本接口。
/// </summary>
public interface IExperienceDetector : IDisposable
{
    /// <summary>是否已检测到经验（检测循环命中后置 true）。</summary>
    bool HasDetectedExperience { get; }

    /// <summary>启动后台检测循环。</summary>
    void Start();

    /// <summary>停止检测并等待后台任务结束（未命中时结果落定为 false）。</summary>
    Task StopAsync();
}
