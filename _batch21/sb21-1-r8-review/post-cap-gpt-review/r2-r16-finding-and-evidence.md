# GPT R2 original Batch 51 R16 evidence finding and follow-up

## Original finding (必改)

The submitted report inferred “same object as the R16 measured source” from current working-tree status, which cannot prove no committed source change occurred between R16 and HEAD. The material omitted the §24.63 U text, the R16 object hash/commit anchor, and a locatable current 104/104 run.

## Mechanical follow-up supplied after the eight-request cap

- Design/source anchor: `Docs/design/onedragon-r5-3-external-start-lifecycle-2026-09-21.md`, §24.63 (Batch 51); relevant excerpt is supplied separately.
- Anchor commit: `029cbff66068bb53418727706356c620bc810bc1`.
- Test: `Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/SubmissionPointInventoryTests.cs`.
- Anchor Git blob and current Git blob: `fe254d499b9f43590d1112b657000679a3e4ecdf` (equal).
- Current source SHA-256: `B8093E25BC9DF7486EEB7ECD9F6AB2B00AC1FBBE2085F216499D0BC3FEB697F4`.
- Fresh targeted rerun after the original cap, with `DeployToBgiTools=false`: 104 passed, 0 failed, total 104. TRX: `r16-followup/sb21-1-r16-followup-104.trx`; SHA-256 `DED9D8AD0350DC9A3D2965F8A1C48B5419029F55E650CBC55E160EF8B4BD2E3C`.
- The source/hash/run provenance was mechanically checked, but before this owner-authorized call no consultant acceptance of the follow-up was claimed. Do not turn the historical full-run 1068/2/0/1070 into a new run claim.

## §24.63 U text included for the closeout record

The first post-cap GPT packet accidentally included only the start of §24.63 A–C and omitted the expressly requested U subsection. The complete U subsection is now included at `batch51-r16-u-excerpt.md`, copied from the source document at the current HEAD. It records that R16 itself obtained no consultation result, lists its contemporaneous 104-targeted/1068-full mechanical checks, and carries a later-batch re-verification obligation. The new 104/104 run is evidence for the anchored current source; it does not erase or relabel that historical obligation.

Independent local provenance check after the GPT review: `git rev-parse HEAD:<test path>` and `git rev-parse <anchor commit>:<test path>` both returned `fe254d499b9f43590d1112b657000679a3e4ecdf`; `git ls-tree` at the anchor returned the same blob. `Get-FileHash` on the current source returned `B8093E25BC9DF7486EEB7ECD9F6AB2B00AC1FBBE2085F216499D0BC3FEB697F4`. Parsing the supplied TRX returned 104 `Passed`, zero `Failed`; TRX SHA-256 was `DED9D8AD0350DC9A3D2965F8A1C48B5419029F55E650CBC55E160EF8B4BD2E3C`.
