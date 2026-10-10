namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 联机锄地绑定的跨成员契约。
///
/// 一个绑定项对应一次顶层启动：配置组和一条龙都只占一个位置。服务端批次在注册阶段
/// 冻结每个成员提交的 layout（项目数量 + 每个位置的类型顺序），所以名称可以因成员而异，
/// 但位置和类型必须一致。
/// </summary>
public sealed record OnlineHoeingBindingValidationResult(
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public string Layout => string.Join(",", Types);
}

public static class OnlineHoeingBindingValidator
{
    public const string GroupType = "group";
    public const string OneDragonType = "onedragon";

    /// <summary>
    /// 校验一份本地或远程下发的绑定。
    /// types 为空表示旧版本数据：按兼容约定把每项解释为配置组；部分缺失则视为格式错误，
    /// 不再静默补齐，避免一条龙被悄悄改成配置组。
    /// </summary>
    public static OnlineHoeingBindingValidationResult Validate(
        IReadOnlyList<string>? names,
        IReadOnlyList<string>? types,
        IEnumerable<string>? configGroups = null,
        IEnumerable<string>? oneClickConfigs = null,
        bool checkAvailability = false)
    {
        var normalizedNames = names?.ToList() ?? [];
        var errors = new List<string>();
        var normalizedTypes = types is null or { Count: 0 }
            ? Enumerable.Repeat(GroupType, normalizedNames.Count).ToList()
            : types.ToList();

        if (normalizedNames.Count == 0)
        {
            errors.Add("至少绑定一项配置组或一条龙。空绑定无法参加联机批次。");
        }

        if (types is { Count: > 0 } && types.Count != normalizedNames.Count)
        {
            errors.Add($"绑定名称有 {normalizedNames.Count} 项，但绑定类型有 {types.Count} 项；两者必须一一对应。");
        }

        var groups = checkAvailability && configGroups != null
            ? new HashSet<string>(configGroups, StringComparer.Ordinal)
            : null;
        var oneClicks = checkAvailability && oneClickConfigs != null
            ? new HashSet<string>(oneClickConfigs, StringComparer.Ordinal)
            : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < normalizedNames.Count; i++)
        {
            var name = normalizedNames[i];
            var type = i < normalizedTypes.Count ? normalizedTypes[i] : "";
            var position = i + 1;

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"第 {position} 项名称为空。");
            }
            else if (!string.Equals(name, name.Trim(), StringComparison.Ordinal))
            {
                errors.Add($"第 {position} 项名称首尾不能有空格：{name}");
            }

            if (type is not (GroupType or OneDragonType))
            {
                errors.Add($"第 {position} 项类型无效：{type}。只能是配置组或一条龙。");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(name)
                && !seen.Add($"{type}\u0000{name}"))
            {
                errors.Add($"第 {position} 项与前面的「{(type == OneDragonType ? "一条龙" : "配置组")}·{name}」重复。");
            }

            if (checkAvailability && !string.IsNullOrWhiteSpace(name))
            {
                var available = type == OneDragonType ? oneClicks : groups;
                if (available != null && !available.Contains(name))
                {
                    errors.Add($"第 {position} 项「{name}」在目标成员当前上报的{(type == OneDragonType ? "一条龙" : "配置组")}列表中不存在。");
                }
            }
        }

        var layout = string.Join(",", normalizedTypes);
        // CoordinatedBatchState.Apply 的协议上限；通常不会触发，但提前给出可理解的提示，
        // 避免用户只能看到服务端的 batch_layout。
        if (layout.Length > 1024)
        {
            errors.Add("绑定项过多，联机批次布局超过协议上限（1024 个字符）。");
        }

        return new OnlineHoeingBindingValidationResult(normalizedNames, normalizedTypes, errors);
    }

    public static string DescribeLayout(IReadOnlyList<string> types)
    {
        if (types.Count == 0) return "空绑定";
        return string.Join(" → ", types.Select(t => t == OneDragonType ? "一条龙" : "配置组"));
    }

    public static string? CompareLayout(
        OnlineHoeingBindingValidationResult expected,
        OnlineHoeingBindingValidationResult actual,
        string actualLabel)
    {
        if (!actual.IsValid)
            return $"{actualLabel}的绑定本身不规范：{string.Join("；", actual.Errors)}";
        if (!expected.IsValid)
            return string.Join("；", expected.Errors);
        if (!string.Equals(expected.Layout, actual.Layout, StringComparison.Ordinal))
        {
            return $"{actualLabel}的绑定结构为「{DescribeLayout(actual.Types)}」，当前结构为「{DescribeLayout(expected.Types)}」。" +
                   "同一轮联机必须保持绑定数量及每个位置的类型/顺序一致；名称可以不同。";
        }
        return null;
    }
}
