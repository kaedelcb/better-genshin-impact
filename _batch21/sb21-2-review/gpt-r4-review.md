# SB21-2 GPT R4 复核记录

- 会诊：GPT `gpt-6-astra`，effort `medium`；R4 请求在一次全量 R5.3 文件的本地预检拒绝后，改为精确 §24.120.4—§24.122 摘录发送。预检拒绝未计会诊次数；本轮 R4 已发送并返回，SB21-2 累计 4/8。会诊范围仍仅 BO-10/BO-12。
- 总体：复核未提出新的实现层缺陷；认可当前规则和同锁键并发边界有明确限定。但指出以下两项重要级证据缺口，施工方不得降级：

## R4-1：v4 item generation 溢出反例被根 H 遮蔽

原 `overflow-item-generation` 样例同时把 `generationHighWater` 和 item `generation` 设为超出 Int64 的 JSON 整数。`Load` 先在根 H 校验失败，不能证明 `ParseGeneration` 会拒绝 item generation 溢出；测试期待标记 `generation` 也会被 `generationHighWater` 字段名中的子串满足，进一步削弱了判别力。

**处置**：根 H 改为合法 `long.MaxValue`，item generation 仍为 `9223372036854775808`，错误标记要求 item `generation`。定向测试 `Load_V4MissingOrInvalidHighWaterAndItemGeneration_FailsClosed` 通过。反向突变只把 `ParseGeneration` 对 Int64 读取失败分支改成 `value = long.MaxValue`；同测试在溢出 item 上以 `Assert.Throws(): No exception was thrown` 失败，没有编译失败。源文件按字节副本恢复，修改前/突变/恢复 SHA 分别为 `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289`、`9726E89B312EFB9722EE44C386C9008550A25DD95C1E88A0682866D09BE418C4`、`731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289`。完整日志和 TRX 在 `mutations-r4/item-generation-overflow/`。

## R4-2：全量差集执行用例计数不严谨

原摘要把按显示名统计的 1493 个同名项写成“共享执行用例”，与基线、最终总数的用例实例算术不一致；需按 TRX `testId` 提供精确差集。

**处置**：新生成 `assistant-full-test-diff.md` 和 `.json`，直接比较基线/最终 TRX 的唯一 testId。基线 1498、最终 1507、共享 1497、移除 1、新增 10、共享结果变化 17、共享结果不变 1480；校验 `1497+1=1498` 及 `1497+10=1507`。移除的是本批旧 C5 测试名称，更准确的新名称计入新增；其余 9 项为新增验证用例，没有材料外基线用例消失。

修正后的上述两项证据已作为 R5 当前复核材料。R4 自身未送达失败；之前另有本地输入长度预检拒绝，未发送不计次。具体尝试序号与台账为准。

## R4 后续验证补记（非 GPT 会诊结论）

R4-1 按重要级证据缺口保留。此前一次完整 generation 运行紧跟在饱和接受突变之后，报出 item overflow 未抛异常；该运行很可能使用了突变构建产物，不能据此归因为原生产实现缺陷。原因不作无证据推断。

为隔离这个歧义，使用已保存的原始 `LocalWaitQueueStore.cs`（SHA-256 `731EDB6F8796B5D4AEE66DE5E36486FAAF928A4780515CDE7C658D687D141289`）做助手测试项目 `--no-incremental` 构建。v4 根/项 overflow 两个测试通过 2/2，独立 `Load_V4ItemGenerationOverflowWithValidHighWater_FailsClosedWithoutMutation` 通过；这确认无需更改原 `TryGetValue<long>` 生产守卫。随后在该原实现上只将 item 解析失败分支突变为饱和接受 `long.MaxValue`，独立用例因无异常而失败；原文件恢复前后 SHA 均为上述值。权威记录、日志和 TRX 位于 `item-generation-overflow-original-source-probe/` 与 `mutations-r4/item-generation-overflow-original-parser/`。保留了独立夹具，生产解析实现按最小改动恢复原样。

助手最终构建为 0 错误（58 警告），generation 定向 45/45，LocalWait 190/190，助手全量 1506/2/0/1508；全量差集按 testId 守恒为共享 1497、移除 1、新增 11、结果变化 17、不变 1480。GPT R4 原会诊判断保持不变；本补记是施工方后续证据，R4-1 仍待 R5 复核，不自行核销重要等级。
