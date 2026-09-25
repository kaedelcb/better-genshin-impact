import sys

p = r"MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs"
src = open(p, encoding="utf-8").read()

# 当前（修复①②后）形态锚点。
PAIRS = {
    "1": (
        "        if (status?.CurrentExecution is not { } execution) return occupant;\n\n        try",
        "        if (status?.CurrentExecution is not { } execution) return occupant;\n        return occupant; // MUT-EV1-1\n\n        try",
    ),
    "2": (
        'TryLog($"[任务中心] 占用者级别事实未知：租约读取状态={read.Status}（{read.Detail ?? "无明细"}）");\n                return occupant;',
        'TryLog($"[任务中心] 占用者级别事实未知：租约读取状态={read.Status}（{read.Detail ?? "无明细"}）");\n                return occupant.WithLevelFacts(ArbitrationTier.System, int.MaxValue, true); // MUT-EV1-2',
    ),
    "3": (
        '"[任务中心] 占用者级别事实解析失败（按未知处理）：" + ex.Message);\n            return occupant;',
        '"[任务中心] 占用者级别事实解析失败（按未知处理）：" + ex.Message);\n            return occupant.WithLevelFacts(ArbitrationTier.System, 1, true); // MUT-EV1-3',
    ),
    "5": (
        "                return occupant; // 仲裁面未组装的接缝态：级别未知",
        "                return occupant.WithLevelFacts(ArbitrationTier.System, 2, true); // MUT-EV1-5",
    ),
    "4": (
        "        if (occupant.State != OccupantFactsState.Occupied || !occupant.HasTrustedIdentity) return occupant;",
        "        if (false) return occupant; // MUT-EV1-4",
    ),
    "6": (
        "            var runs = _runs.List();\n            var operations = read.File?.Handoff?.Operations;",
        "            var runs = _runs.List();\n            return occupant; // MUT-EV1-6（静默多读台账后原样返回）\n            var operations = read.File?.Handoff?.Operations;",
    ),
    "7": (
        "            return occupant.WithLevelFacts(level.Tier, level.Priority, level.HighestClass);",
        "            return new RunningOccupantFacts { State = occupant.State, Reference = occupant.Reference, HasTrustedIdentity = occupant.HasTrustedIdentity, Tier = level.Tier, Priority = level.Priority, HighestClass = level.HighestClass }; // MUT-EV1-7（丢字段构造）",
    ),
    "8": (
        "            return occupant.WithLevelFacts(level.Tier, level.Priority, level.HighestClass);",
        "            return occupant.WithLevelFacts(level.Tier, level.Priority, true); // MUT-EV1-8（错误最高级赋值）",
    ),
    "10": (
        "        if (occupant.State != OccupantFactsState.Occupied || !occupant.HasTrustedIdentity) return occupant;",
        "        _admissionStore?.Read(); return occupant; // MUT-EV1-10（早出静默读租约后原样返回）",
    ),
    "11": (
        "            TryLog(\"[任务中心] 占用者级别事实解析失败（按未知处理）：\" + ex.Message);\n            return occupant;",
        "            TryLog(\"[任务中心] 占用者级别事实解析失败（按未知处理）：\" + ex.Message);\n            return new RunningOccupantFacts { State = occupant.State, Reference = occupant.Reference, HasTrustedIdentity = occupant.HasTrustedIdentity, RunId = occupant.RunId }; // MUT-EV1-11（异常分支局部丢字段/清空级别）",
    ),
    "12": (
        "            // [ev-1 会诊重要项修复] 未组装判定先于台账读取：避免「门面未组装＋台账读取故障」被误记为\n            // 「解析失败」（本分支合同＝原样返回、无解析留痕），也使未组装态不承担台账读取/争用等待成本。\n            var read = _admissionStore?.Read();\n            if (read is null)\n            {\n                return occupant; // 仲裁面未组装的接缝态：级别未知\n            }\n            if (read.Status != ArbitrationLeaseStatus.Valid)\n            {\n                // 非 Valid（Absent/Expired/Corrupt/Unsupported）不得据其内容推级别；如实留痕并保持未知。\n                TryLog($\"[任务中心] 占用者级别事实未知：租约读取状态={read.Status}（{read.Detail ?? \"无明细\"}）\");\n                return occupant;\n            }\n            // [ev-1 会诊重要项修复②] 台账读取后移到非 Valid 判定之后：租约已判非 Valid 时，\n            // 运行台账故障不得把留痕改写成「解析失败」（与未组装判定前置同族，第 7 轮会诊）。\n            var runs = _runs.List();",
        "            var runs = _runs.List();\n            var read = _admissionStore?.Read();\n            if (read is null)\n            {\n                return occupant; // 仲裁面未组装的接缝态：级别未知\n            }\n            if (read.Status != ArbitrationLeaseStatus.Valid)\n            {\n                // 非 Valid（Absent/Expired/Corrupt/Unsupported）不得据其内容推级别；如实留痕并保持未知。\n                TryLog($\"[任务中心] 占用者级别事实未知：租约读取状态={read.Status}（{read.Detail ?? \"无明细\"}）\");\n                return occupant;\n            }",
    ),
    "14": (
        "        if (occupant.State != OccupantFactsState.Occupied || !occupant.HasTrustedIdentity) return occupant;",
        "        if (!occupant.HasTrustedIdentity) return occupant; // MUT-EV1-14（仅删 State 检查）",
    ),
    "13": (
        "            if (read.Status != ArbitrationLeaseStatus.Valid)\n            {\n                // 非 Valid（Absent/Expired/Corrupt/Unsupported）不得据其内容推级别；如实留痕并保持未知。\n                TryLog($\"[任务中心] 占用者级别事实未知：租约读取状态={read.Status}（{read.Detail ?? \"无明细\"}）\");\n                return occupant;\n            }\n            // [ev-1 会诊重要项修复②] 台账读取后移到非 Valid 判定之后：租约已判非 Valid 时，\n            // 运行台账故障不得把留痕改写成「解析失败」（与未组装判定前置同族，第 7 轮会诊）。\n            var runs = _runs.List();",
        "            var runs = _runs.List();\n            if (read.Status != ArbitrationLeaseStatus.Valid)\n            {\n                // 非 Valid（Absent/Expired/Corrupt/Unsupported）不得据其内容推级别；如实留痕并保持未知。\n                TryLog($\"[任务中心] 占用者级别事实未知：租约读取状态={read.Status}（{read.Detail ?? \"无明细\"}）\");\n                return occupant;\n            }",
    ),
}

mut, mode = sys.argv[1], sys.argv[2]
old, new = PAIRS[mut]
if mode == "apply":
    assert src.count(old) == 1, f"pattern not unique for {mut}: {src.count(old)}"
    out = src.replace(old, new)
else:
    assert src.count(new) == 1, f"mutant not found for {mut}: {src.count(new)}"
    out = src.replace(new, old)
open(p, "w", encoding="utf-8", newline="").write(out)
print(mode, mut, "ok")
