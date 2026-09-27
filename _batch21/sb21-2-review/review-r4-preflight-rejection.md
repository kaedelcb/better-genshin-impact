# SB21-2 会诊 R4 本地预检拒绝

尝试调用 `gpt_review` 的 allowlist 含完整 `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`。工具在实际 dispatch 前返回 `file exceeds 524288 bytes`；该请求未发给 GPT、没有审查结论，按规则登记为本地预检拒绝，不计 8 次发送额度。已备有只含 §24.120.4、§24.121 和 §24.122 的当前锚点摘录 `_batch21/sb21-2-review/r5_3_sb21_2_current_excerpt.md`；R4 重试将使用此 excerpt 替代整本 R5.3 文档。
