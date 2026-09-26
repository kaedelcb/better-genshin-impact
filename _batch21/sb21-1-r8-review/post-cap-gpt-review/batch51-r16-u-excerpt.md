Source: `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`, §24.63 U (the complete paragraph item, lines 2339–2352 at the review snapshot).

> **U. 第 16 轮（验证会诊）处置状态与**事后补验入口**（如实登记）**
>
> 1. **通道状态**：本轮拟对「第 15 轮处置后的最终状态」做第 16 轮验证会诊，但**会诊通道不可用**——
>    `gpt-5.6-sol` 连续 3 次返回**账号级限流**；按交接稿纪律降级 **deepseek-flash 只读**时，子代理
>    **三次均未收到任务文本**（返回「没有收到具体任务内容」）⇒ 本轮**未取得会诊结论**（**不**声称已会诊）。
> 2. **机械自检（本轮实际执行；替代不了会诊，如实列明）**：①左边界字符类实际含
>    `\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Pc}\p{Cf}` **全七类**（源码文本核对）；②`\p{Nl}` 后缀反例
>    `A\u2160OpCode = "y"` 在源码中**恰 1 处**；③定向夹具 **104 通过／0 失败**；④全量回归
>    **1068 通过／2 跳过／1070（0 失败，33s）**。
> 3. **本轮改动性质（收窄 §17.4-A ① 的适用范围并留痕）**：仅**新增一支负例断言**＋同步文档计数
>    （**未改生产代码、未改守卫判定逻辑**）⇒ 判定面事实不变，但**文档声明面已变**（故**不**援引措辞豁免）。
> 4. **事后补验入口（可剔除）**：下一批次的**首轮会诊**须**补验**本批最终状态（重点：22 支精确计数的
>    逐类别覆盖、三类静默漏扫登记、43／94／96 计数自洽）；若补验提出阻断/重要项，按 §17.4-A ③ 照常处置，
>    并在此处登记处置结果（本项为**流程性挂账**，非产品缺陷）。

This excerpt was absent from the earlier post-cap review packet; that omission is corrected here. Its historical statement that round 16 received no consultation result remains explicit and is not replaced by the fresh 104/104 mechanical rerun.
