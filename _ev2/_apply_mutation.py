import sys

# [ev2 2026-09-25] RecordTerminal 两侧分裂表征批的反向突变脚本（第 4 轮建议 6 重构）。
# 用法：python _ev2/_apply_mutation.py <M1..M7> apply|revert
#
# 行尾纪律（第 4 轮建议 6 修复）：按目标文件原始字节探测换行风格（CRLF/LF），
# 读入用 universal newlines（模式可跨行尾匹配），写回统一转回该文件原风格——
# 两目标文件均为单一行尾形态（BgiTaskCoordinator.cs＝全 CRLF、JobRegistry.cs＝全 LF），
# 故 apply→revert 往返可做到字节级还原，不再产生行尾改写副作用（第 1 轮曾以
# universal-newlines 直写把工作副本改成 LF，靠 git restore 收拾，本脚本已根治）。

TARGETS = {
    "M1": r"BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs",
    "M2": r"BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs",
    "M3": r"BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs",
    "M4": r"BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs",
    "M5": r"BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs",
    "M6": r"BetterGenshinImpact/Service/Execution/JobRegistry.cs",
    "M7": r"BetterGenshinImpact/Service/ExternalInterface/BgiTaskCoordinator.cs",
}

PAIRS = {
    # M1：执行完成路径（普通 Task<bool> 路径）队列状态改写——守护 FIX-1/2/3/4/5 的队列侧断言
    "M1": (
        'RecordTerminal(item.TaskHandle, "completed", cancelled: cancelled);',
        'RecordTerminal(item.TaskHandle, "failed", cancelled: cancelled); // MUT-EV2-M1',
    ),
    # M2：RecordTerminal 注册表兜底写入拆除——守护 FIX-3/5/6/7/8 的注册表侧断言（Running 僵尸）；
    # FIX-1×2 与 FIX-2 预期保持绿（执行体先终态已落注册表＝先终态者赢方向对照）
    "M2": (
        "TryRegistryTerminal(taskHandle, state, code, message, cancelled);",
        "_ = (state, code); // MUT-EV2-M2: 注册表兜底写入拆除",
    ),
    # M3：completed+cancelled 的注册表映射丢弃取消区分——守护 FIX-5
    "M3": (
        '"completed" => cancelled ? (JobState.Cancelled, JobErrorCodes.CancelledUser) : (JobState.Succeeded, null),',
        '"completed" => (JobState.Succeeded, null), // MUT-EV2-M3: 丢弃取消区分',
    ),
    # M4：queueCancelled 的注册表兜底映射改写为成功——守护 FIX-7
    "M4": (
        '_ => (JobState.Cancelled, JobErrorCodes.CancelledUser), // queueCancelled',
        '_ => (JobState.Succeeded, null), // MUT-EV2-M4: queueCancelled 兜底改写为成功',
    ),
    # M5：failed 的注册表映射丢弃真实错误码——守护 FIX-8（task_busy 归因丢失）
    "M5": (
        '"failed" => (JobState.Failed, errorCode ?? JobErrorCodes.TaskStartFailed),',
        '"failed" => (JobState.Failed, JobErrorCodes.TaskStartFailed), // MUT-EV2-M5: 丢弃真实错误码',
    ),
    # M6：TryMarkTerminal 摘除已终态拒绝守卫（先终态者赢方向反转＝可覆盖）——
    # 直接守护 FIX-1×2 的注册表侧断言（执行体终态被协调器 Succeeded 覆盖 ⇒ 断言红）。
    # （MUT-EV2-M6 同时意味着 JobRegistryTests 的先终态者赢合同夹具会红——定向过滤帧只跑本批类，
    # 该既有影响如实在此登记，不进本批红帧名单。）
    "M6": (
        "            if (!_jobs.TryGetValue(jobId, out var job) || job.IsTerminal)\n            {\n                return false;\n            }\n\n            job.ErrorCode = errorCode;",
        "            if (!_jobs.TryGetValue(jobId, out var job))\n            {\n                return false; // MUT-EV2-M6: 已终态拒绝守卫摘除（方向反转）\n            }\n\n            job.ErrorCode = errorCode;",
    ),
    # M7：Submit 队列满路径的注册表 Rejected 写入拆除——守护 FIX-9（队列满 ⇒ Rejected(queue_full)
    # 只存在于注册表）。锚点为 _pending.Count 满路径（夹具确定可达）；Channel 物理满兜底路径
    # （同款调用，极端竞态、夹具不可确定构造）不在本突变范围，如实登记为未钉边界。
    "M7": (
        '                // [A2.4] 连"被拒"也是注册表里的一条事实（Rejected 终态），与漏斗 Rejected(task_busy) 同哲学\n                TryRegistrySubmitQueued(item);\n                TryRegistryTerminal(item.TaskHandle, JobState.Rejected, JobErrorCodes.QueueFull, "队列已满", false);',
        '                // [A2.4] 连"被拒"也是注册表里的一条事实（Rejected 终态），与漏斗 Rejected(task_busy) 同哲学\n                _ = item; // MUT-EV2-M7: 队列满路径注册表写入拆除',
    ),
}

mut, mode = sys.argv[1], sys.argv[2]
p = TARGETS[mut]
old, new = PAIRS[mut]

raw = open(p, "rb").read()
newline = "\r\n" if raw.count(b"\r\n") * 2 >= raw.count(b"\n") else "\n"
src = open(p, encoding="utf-8").read()

old_n, new_n = old.replace("\n", newline), new.replace("\n", newline)
if mode == "apply":
    assert src.count(old_n) == 1, f"pattern not unique for {mut}: {src.count(old_n)}"
    out = src.replace(old_n, new_n)
else:
    assert src.count(new_n) == 1, f"mutant not found for {mut}: {src.count(new_n)}"
    out = src.replace(new_n, old_n)
open(p, "w", encoding="utf-8", newline="").write(out)
print(mode, mut, "ok (newline=%r)" % newline)
