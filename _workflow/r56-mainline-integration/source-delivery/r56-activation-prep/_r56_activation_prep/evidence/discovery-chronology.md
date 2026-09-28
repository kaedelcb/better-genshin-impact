# 交付发现扫描时间线

扫描器来源：权威源worktree未跟踪的tools/mistletoe/deliveries.py，通过--root指向本隔离worktree。本次不改公共index/JSON。

- 开工阶段首次只读扫描曾报告现有r5-slot-process-cross-process-2026-09-28登记HEAD漂移（退出码2）；未尝试修复或改索引。
- 对照源worktree登记文件重新核实后，登记JSON与工作树内容发生变化；再次只读扫描退出码0，ok=true，队列登记8条，discovery/error为空。
- 成功开工扫描原输出在evidence/delivery-discovery-open.json。后续扫描器会将本报告呈现为候选；候选不等于主线已登记。
- 本批交付由_r56_activation_prep/report.md被扫描发现；扫描器不消费本批sidecar delivery-registration.json，公共索引尚待主线唯一写者更新，机器状态为“未登记候选”。这是预期交接状态，不是注册已完成。
- 收口重新扫描并保存evidence/delivery-discovery-closeout.json。若源登记继续变化，以原始结果与相应源文件SHA为准；本包不写公共登记。

未删除或掩盖早期漂移记录，也未声称并行队列不变。



## Natural boundary scan

After the report appeared in the managed worktree, the read-only scan used the current source registry and returned exit code 2, ok=false, errors=[], eight registered queue rows, and one unregistered report candidate: this preparation report at fixed HEAD 5e7e7e22f11daad0c86795368e9a21bb14d79b19. The candidate hash was B923EC49521362F4CF2E7F6401042BEE630F4439554866BF92A0B831E81BBCA8 at scan time. The nonzero result is the expected discovery signal because the mainline owner has not registered this report; it is not a consumer or integration pass. Raw output and exit code are saved in delivery-discovery-boundary.json and delivery-discovery-boundary.exitcode.txt.
