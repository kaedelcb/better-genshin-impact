using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 配置组级本体模板拾取名单配置。
/// 判定优先级（由高到低）：配置组黑名单 > 配置组白名单 > 全局黑名单 > 有图即拾取。
/// </summary>
[Serializable]
public sealed class AutoPickGroupConfig
{
    /// <summary>
    /// 配置组白名单（交互名）：命中即拾取，覆盖全局黑名单。
    /// </summary>
    public HashSet<string> WhiteList { get; set; } = [];

    /// <summary>
    /// 配置组黑名单（交互名）：优先级最高，命中即不拾取。
    /// </summary>
    public HashSet<string> BlackList { get; set; } = [];
}
