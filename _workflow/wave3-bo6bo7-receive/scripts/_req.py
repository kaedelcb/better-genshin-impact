import io
d='_workflow/wave3-bo6bo7-receive'
req = '''# 会诊请求 v1（接收批 `wave3-bo6bo7-receive`，2026-09-28）

固定模型/强度：`gpt-6-astra` / `medium`（与来源 BO-6/BO-7 子批一致，不降配）。
固定快照：主线 `main-OldTeaBag-B168`，开工 HEAD `6fd6207e58ec93dcc38645c12131a9a512f5fc69`（未提交状态，材料见下面清单）。
渠道：既有 GPT 会诊工具（本批默认）。容量预检见 `consultation/preflight-v1.json`。

## 审查对象

这是一个**接收批**：把来源独立交付 `wave3-bo6-bo7-2026-09-28`（隔离 worktree，交付 HEAD `e009068e22b18d89f4eb9f8947fa937dfa8c925c`，开工基线 `e2613a851bd45c28fdd56b84dfc10784e1d9c9b8`）的 BO-6/BO-7 修复接入茶包主线，并在主线 HEAD 上重新取得证据。来源侧四项原级义务已由 owner 批准的两次追加独立会诊按原等级裁定 closed（BO-6 R19 MUST、BO-6 R21 F4 IMPORTANT、BO-7 R21 F1 MUST、BO-7 R21 F2 MUST）。本批**不**施工 BO-8/BO-9、R5.6、R6.1、R6 diff guard，不重开 BO-13／BO-4／BO-10-12。

接收方式：不合并隔离分支。用 `git checkout e009068e2 -- <路径>` 逐字节导入来源相对基线的 8 个产品/测试/状态文档文件；另逐字节收录少量权威证据到 `_workflow/wave3-bo6-bo7/`（部分收录，边界见该目录 `INTAKE-NOTE.md`）。6 份 closeout 快照的 report.json/packet.md 与逐突变 TRX 日志包留在来源 worktree，不搬入。

## 请回答的问题

1. **接收是否忠实**：本批声明的"导入字节与来源已审增量逐字节相同"（依据是本批 manifest 的 `scoped-staged.diff` 与 findings.md 中的等价性说明）是否成立？有无被静默改写、漏掉或越界导入的文件？
2. **集成版本证据是否支持接收结论**：定向 93/93（93 个 testId 与来源最终 TRX 完全相同）、助手全量 1562/2/0/1564、同条件基线 1558/2/0/1560、testId 1556 unchanged/8 added/4 removed/0 changed —— 这些数字与材料是否自洽？有没有把旧证据当作当前证据、或"0 changed"被过度解读的地方？
3. **8 项反向突变**：在集成字节上重新执行的 8 项突变（baseline Passed / mutant Failed / restored Passed，命中具名目标断言，mutant SHA 与来源记录完全相同）是否足以证明目标行为仍具判别力？有无应当补做而未做的突变？
4. **四项原级义务的字节绑定**：被接收的 `WorkflowRunner.cs` 是否就是两名来源审查者当时核验的字节（主线工作区 CRLF `5470cfcb…`，仓库 blob LF `181aa93e…`）？有无会削弱"四项义务仍闭环"这一接收结论的问题？
5. **F-1（建议级）**：来源散文把声明面清单 SHA 记为 `9EAA1A7F…`，而随提交清单实为 `84E1D93D…`（其后 §24.125.5 再次改变声明面）。本批不改写已与登记哈希绑定的来源工件，改在 R5.3 §24.126.4 登记权威口径。该处置是否合规？级别判定（建议级）是否有异议？
6. **F-2（BO-6/7-D1，建议级）**：两处注释陈旧，本批不改已审源码，留待下一次绑定 `WorkflowRunner.cs` 哈希的批次。该处置是否可接受？

## 请特别核查的边界

- 本批**未**做实机、真实 User、BGI 生产进程、R5.8、E3/E4/E5 或热键面验证；外部执行边界为 fake。
- `ClaimSurfaceManifest.txt` 是生成文件（220 KB）。本批仅新增 R5.3 §24.126 的三行承载声明关键词文本 ⇒ 清单 599→602 行（+3/-0），无既有声明行被改或消失；随后无变量复跑通过，两次 SHA 稳定为 `5D91461178DA98729562B486060A4322F6A96C3469778A064DCA52B7D8B4D267`。未附整份生成清单；如需，请指明。
- **未附** `_workflow/wave3-bo6bo7-receive/regression/testid-comparison-receive.json`（约 1.0 MB，内部冗余内嵌两份完整 TRX 解析结果）；其审查要点由 `regression/testid-comparison-summary.json` 承载。
- 构建均使用 `-p:DeployToBgiTools=false`；部署目标 `BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/Tools/MultiplayerHoeingAssistant`（及非 x64 同名目录）在全部构建/测试前后 LastWriteTimeUtc 与文件数未变（1158 个文件，09/26/2026 21:41:14 UTC）。

## 本批文件与材料外变更（送审材料须区分二者）

- 本批文件：`_workflow/wave3-bo6bo7-receive/**`（新增）；`_workflow/wave3-bo6-bo7/**` 的 14 个逐字节导入文件 + 新增 `INTAKE-NOTE.md`；以及导入并追加登记的 8 个来源文件（`WorkflowRunner.cs`、`WorkflowRunnerTests.cs`、`LocalWaitIdentityTranslationTests.cs`、`ClaimSurfaceManifest.txt`、R5.3、`_batch21/b21_plan.md`、`_batch21/sb21-4-handoff-2026-09-28.md`、`槲寄生调度器总计划.md`）。
- 材料外变更（非本批写入者，开工前即存在）：`Docs/design/mistletoe-session-relay-2026-09-24.md`、`Docs/design/unified-job-registry-master-plan.md` 的未提交修改；工作区大量既有未跟踪 `.bak`/`.stale`/历史批次目录/TestResults/日志/截图，以及 `_workflow/sb21-4/**`、`_batch21/**` 未跟踪工件。本批未触碰、未清理、未提交它们，也不据路径推断其写者。
- 全局 `git status --porcelain` 与本批 staged/unstaged diff 见送审快照目录。

## 输出要求

请按等级给出发现（阻断／必改／重要／建议），逐项写明依据（文件与行号）、可观察后果与置信度；并明确回答"本接收批是否仍有未闭合的 MUST/IMPORTANT"。范围仅限本批声明的材料；不要求也不得据此复核其他批次。
'''
io.open(d+'/consultation/review-request-v1.md','w',encoding='utf-8',newline='\n').write(req)
print('request bytes', len(req.encode('utf-8')))
