---
name: requesting-code-review
description: Use for a requested code review or a focused independent assessment that can resolve a concrete correctness or delivery question. Routine edits and each subagent task do not automatically require a review.
---

# Requesting Code Review

This is the local delivery-focused adaptation, revised on 2026-10-08 with user authorization. The original MIT license and upstream source metadata remain as historical attribution; they do not claim that this adapted file is byte-identical to the upstream version.

Use review when the user requests it, a complex change has an unresolved concrete correctness question, or the current delivery requirements include a final independent assessment. Do not default to reviewing every task, file, minor edit, or subagent result.

## Prepare useful context

Give the independent reviewer the requested behavior, relevant version and worktree changes, affected entry points, existing evidence, and specific unresolved questions. References are navigation, not a reading whitelist. Do not replace raw artifacts with the implementer's whole reasoning history.

State what decision the review will support and when it ends. Reuse a valid earlier review when the relevant source, dependencies, and conditions match. When further review is authorized and needed, review the affected scope; do not start a new full workflow by default.

## Act on the result

Fix confirmed defects that prevent the promised behavior or create reachable wrong execution, data loss, or stop failure. Preserve the original findings and severity. Follow the user's current criteria for deferring ordinary bugs; Important is not an automatic order to stop unrelated work.

Record the actual independent conclusion and its limits. Do not invent a pass, alter a source report, or call self-review independent review. Any explicitly authorized review model, effort, request limit, and release requirements remain binding.

An optional reviewer template is available in [code-reviewer.md](code-reviewer.md). Use only the sections needed for this review. All template instructions remain subordinate to the user's current scope, acceptance criteria, and finite authorization; they do not create new requirements or require enumerating unrelated history.
