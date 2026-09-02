namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// fresh 首启先验来源（teleport-fastdrag-prior-fresh-acquisition spec）。
/// </summary>
public enum FreshPriorSource
{
    /// <summary>主动小地图识别成功（fresh 路径，玩家经目标图模板验证在目标图）。</summary>
    Active,

    /// <summary>缓存先验（非 fresh 路径，同任务内连续传送）。</summary>
    Cache,

    /// <summary>无有效先验（fresh 主动识别失败 / 缓存无效）→ 走 SwitchArea 兜底。</summary>
    None
}

/// <summary>
/// fresh 首启先验来源决策。纯函数、无副作用，PBT 友好。
/// </summary>
public static class FastDragFreshPriorDecisions
{
    /// <summary>
    /// 决定 fresh/非 fresh 下的先验来源：
    /// - fresh（isFresh=true）：只用主动识别；成功→Active；失败→None（不回缓存，走 SwitchArea 兜底，BC-1/OQ-4）。
    /// - 非 fresh（isFresh=false）：用缓存；有效→Cache；无效→None。
    /// </summary>
    public static FreshPriorSource Resolve(bool isFresh, bool activeSucceeded, bool cacheHasValid)
    {
        if (isFresh)
        {
            return activeSucceeded ? FreshPriorSource.Active : FreshPriorSource.None;
        }

        return cacheHasValid ? FreshPriorSource.Cache : FreshPriorSource.None;
    }

    /// <summary>有先验（Active 或 Cache）即可参与跳过切区判定；None 不跳过。</summary>
    public static bool HasPrior(FreshPriorSource source) => source != FreshPriorSource.None;
}