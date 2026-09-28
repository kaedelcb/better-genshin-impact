# BO-6/BO-7 反向突变证据（当前权威集）

本批共有 8 项当前证据：7 项 v3 Runner 控制流突变，以及 GPT R4 后补的 1 项 Failed 落盘断言突变。旧 v1/v2 尝试日志仍在磁盘留档，但不作为本表关闭判据。全部 raw 文件路径、每阶段 SHA、TRX 结果和总体散列索引见 `raw-mutation-evidence-index.md`。

| ID | 单项反向突变 | 目标 testId / 目标失败 | original → mutant → restored Runner SHA | 证据目录 |
|---|---|---|---|---|
| `bo6-resume-live-park-v3` | 禁用显式 Resume 停驻重算 | `398aed81-8691-87dd-a9cd-c62780f5f924`；实际游标只提交 R/stop，遗漏 P1/Q，line 1008 | `5470cf…1f190f96` → `96212d…83fb13e` → `5470cf…1f190f96` | `bo6-resume-live-park-v3/` |
| `bo6-tail-fail-closed-v3` | 仅移除 unresolved parked aggregation 条件 | `113f020c-1c29-e336-4c1a-3a50b69cbcc8`；期望 Failed、mutant Succeeded，line 1071 | `5470cf…1f190f96` → `ff0ff5…bb5062` → `5470cf…1f190f96` | `bo6-tail-fail-closed-v3/` |
| `bo6-completed-filter-v3` | 禁用恢复推进完成身份过滤 | `398aed81-8691-87dd-a9cd-c62780f5f924`；lead/A/B 完成身份被重提，line 1008 | `5470cf…1f190f96` → `3115f2…dfd22e` → `5470cf…1f190f96` | `bo6-completed-filter-v3/` |
| `bo7-candidate-first-v3` | 反转 candidate/rescue loop 全序比较 | `1d029d1b-a82f-69cc-f8ee-1ba7c7957cdc`；期望 candidate@0、mutant anchor@1，line 1121 | `5470cf…1f190f96` → `ef6ed3…f5562` → `5470cf…1f190f96` | `bo7-candidate-first-v3/` |
| `bo7-rescue-first-v3` | 令 candidate 无条件覆盖 rescue | `7a665a8f-f8e9-11b4-598f-a1f8d24b805a`；期望 P@0、mutant P@1，line 1163 | `5470cf…1f190f96` → `e5fcb0…a705c` → `5470cf…1f190f96` | `bo7-rescue-first-v3/` |
| `bo7-earliest-park-v3` | 把同轮最早有效停驻选择反转为最晚 | `398aed81-8691-87dd-a9cd-c62780f5f924`；漏 P1，line 1008 | `5470cf…1f190f96` → `8668d0…c84e4` → `5470cf…1f190f96` | `bo7-earliest-park-v3/` |
| `bo7-stable-identity-v3` | 错把可变 SequenceIndex 纳入已完成稳定身份 | `398aed81-8691-87dd-a9cd-c62780f5f924`；重提已完成 lead/A/B，line 1008 | `5470cf…1f190f96` → `f04db2…b3df8` → `5470cf…1f190f96` | `bo7-stable-identity-v3/` |
| `bo6-tail-persisted-failure-v4` | 仅移除 TailReached＋有效停驻的 Failed `_runs.Update(run)` | `113f020c-1c29-e336-4c1a-3a50b69cbcc8`；返回对象仍 Failed、RunStore 重载实际 Running，line 1080 | `5470cf…1f190f96` → `a04531…7dcce0` → `5470cf…1f190f96` | `bo6-tail-persisted-failure-v4/` |

v3 七项运行绑定测试源码 SHA `c1197ed5b6f79029104b7949247ac19d9e224fe72dde4c4a23eef0c24e680cd9`；v4 为新增盘上断言后的 `b5f379488b464c0b5808bb3f1bc5c4916e8ffa7ed1cc185b7127b408ee274711`。8 项的 baseline/restored 目标 fact 均 Passed，mutant build 均 exit 0、且实际在预期具名 Runner assertion Failed。每项都保留 separate baseline/mutant/restored build.log、test.log、TRX、experiment JSON、source original 和精确 patch；没有从其他 mutation 复制或合成日志。

v4 修正并强化了 GPT R4 指出的 IMPORTANT 证据：从 `ResumeAsync` 返回后 `_runs.Load(run.RunId)` 重新读取持久状态，断言 `Failed`、`TailReached`、P1/Q wait obligations 保留、`PendingCompletion` null；终态调用集合仍为空。v4 mutation 证明仅在内存设置 Failed 而不写盘不能通过新断言。
