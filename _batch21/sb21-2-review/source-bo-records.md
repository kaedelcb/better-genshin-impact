# SB21-2 原始 BO 来源摘录

来源原件：`C:\Users\Administrator\.tools\zcode-relay\test\ledger-batch20.json`，以及 R5.3 §24.120.4 的 BO-10/BO-12 指针。原件只读，未修改。

## BO-10（Wave2 R37 重要-2）

原发现：代际单调性只在“取消墓碑仍在册→重激活”路径成立；`Remove` 或 24h 墓碑裁剪后，同身份重登记走新项分支、Generation 归零，旧 gen0 请求可能通过 ItemId、StableIdentity、Waiting、Generation 全部校验。

R37 原处置：接受有界良性论证——Remove 后旧生命周期请求只会让新项多走一次完整准入，重评请求不含发送许可；合同边界写为“代际生命周期以 Remove/清理终局，非仅重激活”。该处置当时没有持久化移除后的高水位。

R37 同轮建议：同一 `int` 代际到 `int.MaxValue` 后递增会溢出为负数，写侧拒绝、方向 fail-closed，但该项会无法再次重激活；原处置以极低可达性采纳登记，未做高水位修复。

## BO-12（Wave2 R46 重要-2）

原发现：构造并确认以下序列可达：`gen0 → Trigger 产出请求 R(gen0) → Remove → 同身份重登记(gen0) → Consume(R)`。旧请求的 ItemId、StableIdentity、Waiting、代际均匹配，C5 判有效。对称地，`PruneHandledExceptGeneration(identity,"0")` 会保留旧 gen0 键，使新生命周期同代际在同实例中被抑制。不同 loopIteration 由 StableIdentity 隔离；墓碑仍在时重激活递增可正确过期旧请求。

原后果判断：旧请求仅造成确实仍等待的项多走一次完整准入，保守且无发送许可；但 C5 对 Remove/裁剪重登记的 ABA 判别按合同字面静默失效。

R46 原处置：owner 冻结时把问题登记为 BO-12，不修；彻底修复（移除时代际外置记录或墓碑代际延续）归后续 Wave3/接线批 C5 合同面。同 loopIteration 异身份形态由 StableIdentity 隔离。

## 当前子批处理

本次 owner 明确把 BO-10 与 BO-12 划给 SB21-2。SB21-2 将上述原级重要问题落到持久代际高水位和 C5 Store 回读夹具；历史的“gen0 多走一次准入可接受”保留为旧处置记录，但被本批更强的 ABA 修复取代，不伪称历史顾问复核已接受新方案。
