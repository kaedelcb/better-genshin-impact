# SB21-2 GPT R5 复核结果与处置

- 模型／强度：GPT `gpt-6-astra`／medium；请求已发出并返回，SB21-2 累计 5/8。范围只限 BO-10/BO-12。完整状态与差异随审材料见 `review-r5-status.txt`、`review-r5-related-unstaged-in-scope.diff`、`review-r5-material-out-status.txt` 和 `review-r5-material-out-unstaged.diff`。
- 总结：未发现新的 MUST 或 IMPORTANT。GPT 认为 R4-1 与 R4-2 的重要级证据缺口已可闭环；保持原发现等级，不把结论扩展成生产门已开或实机已验。
- 复核依据：R4-1 的根 `generationHighWater=long.MaxValue` 合法、item generation `9223372036854775808` 独立溢出，具名断言检查 item 错误与原文件字节不变；原 Store 源上饱和接受突变使目标夹具失败，权威记录的原/恢复 SHA 相同。R4-2 以 TRX `testId` 统计：基线 1498、最终 1508、共享 1497、移除 1、新增 11、结果变化 17、不变 1480，且 `1497+1=1498`、`1497+11=1508`。
- GPT 还确认 C5 Remove/prune 夹具从同一重开 Store 复核旧/新请求，结果计数是过期 1、有效 1；这不承诺 at-most-once。Consumer 没有 sender 依赖或助手生产调用点，本批没有真实发送，生产门保持关闭。

## 非阻断证据修订

1. **早期 item-overflow probe 的二进制身份不确定。** 该文件记录 `originalSourceSha256=731EDB…`、而 `strictSourceBeforeSha256` 与 `restoredSourceSha256=8C8DA0…`。不能由此断言早期失败源于 mutant DLL，也不能当作原实现有缺陷的证据。现已改为“身份有歧义，不据此归因”；精确源码恢复只引用 `mutations-r4/item-generation-overflow-original-parser/evidence.json`（原/恢复 SHA 均 `731EDB…`）。
2. **long-generation 夹具的单代际批次输入不一致。** `other` 的 generation 为 0，却与 2147483648 项一起使用同一个 `generation` 参数。已将两个身份拆成各自代际的 Trigger 调用，并用 `other` 的真实 gen0 验证其去重键保留。修订后测试项目非增量构建 0 错误/79 警告，generation 定向 45/45、LocalWait 190/190、助手全量 1506/2/0/1508 全绿；恢复态 TRX 在 `targeted-r5-restored-final/`、`localwait-r5-restored-final/`、`assistant-full-r5-restored-final/`。
3. **testId 差集报告残留 `$finalPath`。** 已替换为实际最终 TRX 路径 `_batch21/sb21-2-review/assistant-full-r5-counter-final/sb21-2-assistant-full-r5-counter.trx`。
4. **异常类型措辞应限于可解析快照。** Cleanup 再读取损坏文件时可先抛 `LocalWaitQueueCorruptException`；“任一字节变化均抛 `LocalWaitQueueConcurrentUpdateException`”过宽。安全结论应表述为检测/解析/提交失败均不会让旧快照覆盖外部状态。已在 R5 复核上下文及 §24.122.3 限定措辞。
5. **突变日志的证据粒度。** 若 `mutant.log` 未打印断言异常文本，失败位置应由目标测试/TRX 或测试汇总支持，不单独归因给该日志；R5 材料已注明此区分。

## 证据与限制

R5 对提交的材料作只读复核；GPT 没有执行本机命令，也没有独立重算底层 TRX/哈希。R5 返回要点由本会话保存为本记录，并与本批现存测试、哈希和快照交叉引用。实际构建、定向/全量测试、反向突变及其边界仍以本地日志、TRX、源码哈希和后续重跑为准。
