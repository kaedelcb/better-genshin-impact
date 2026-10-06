# 正式UI修复实机阶段与本机快速结束收口接续

唯一标记 OWN-ROOT-TERMINAL-20261007-FROM-01a112a0；来源01a112a0-504d-7f71-b5ba-96f62711bab0，前继OWN-ROOT-DRAG-ACCEPT-20261007-FROM-01a11220。总产品未完成。

## 现场与完成边界

同项目local原工作区 E:\Program Files\better-genshin-impact-LCB，main-OldTeaBag-B168。源码候选983fea524（视图修复/新WPF回归）、e2c7bb902（本机样本准备）；最终HEAD以本交接提交读回为准。保护大量材料外变化、暂存和旧失败，不造干净工作区。

原生完整Goal本线程active已真实读回，交接先原生paused读回再建唯一接班；新Goal先active读回再施工。来源最新turn_context2026-10-06T20:40:54.579Z为gpt-6.1-sol/medium，与settings一致，接班显式继承并核实际rollout。已决定交接是因正式UI聚焦阶段结束，下一项是独立的仲裁结束收口，避免把显示/运行Succeeded与安全终局混合；不按时长/工具数强制切换。

本轮真实拖动：普通点击23:50保持，节点Move拖到22:30，真实独立窗口撤销回23:50；新增车道、跨道移动和精确拖柄覆盖2生效，节点无复制。三步添加判断21:00与结束23:55；原资源21:35；是分支实际连到n-090b94d1，保存022d99cb。只有明确拖动改时刻。

实机又发现启动4次Visual上级异常、展开分钟重叠、午夜车道按钮遮挡、候选同名及分支类型名、运行状态黑字。ui-polish/red：真实WPF3Failed/0Passed/0Skip，命中Visual异常、内部类型名和59个标签重叠；末尾车道断言当时未执行，不算独立红证据。修复只在两份视图：祖先/可见检查、自适应刻度、末尾留空间、名称与候选身份、禁用候选启动/编辑、运行文本对比度。green：Rebuild0、56/56/0Skip、source_drift空、build/test Job active0、父exit0。本轮无live源码突变，不合成认证PFP。

同一完整产物仍_workflow/runtime-unified-01a10e1b/product。refresh-polish/result.json五模块精确刷新，助手DLL508fb0f93a7e8b5738fafb7cccc4bc5678e12cc62b3da27d03b3b49bc971d1d3；旧db2fe模块留before。BGI DLL0720fe79e71f1069c51378cc8a0e54e621dd03ea900fca052915c77f9a9147c7、EXE42aa9e13aa823a2796fd96b666360dff29890ed1f4d73798f837fa740ac70795分别核；1351输入、9500User逐SHA同。

第三轮实际启动BGI24428/助手19868、Session1、自有product。用户两次physical Escape停止时停当轮输入，未换通道；用户后来明确“操作可以恢复”后才重新观察现窗续验。原进程没有重复启动。进入槲寄生/任务中心、最大化未再现旧Visual异常；候选名称及启动/编辑禁用真实通过；分钟标签21:15/30/45可读，末尾车道有独立空间。资源连接61项含八类原生单项实条目；打开BGI垫底占位真实参数控件，更新引用并保存，原节点ID/资源修订保持。未修改这些真实参数或User；不把打开参数当保存参数/游戏执行验收。

单流程和整表实际导出在own-runtime/second；单流程同ID重新导入被拒且原SHA同；整表导入wf-242498c6只读候选，启动不产生run。候选激活本轮未实际测试。第三轮正向单流程导入wf-own-import-01a112a0成功，另导入wf-own-control-01a112a0两本机节点；原流程未覆盖。导入反馈把修订SHA作为名称，登记普通显示问题。

未来等待run-c897edad1896真实Waiting→Paused→恢复Waiting→Cancelled，另run-9a416f351b6d因同名选择到原流程重复启动，立即停止，均0提交/节点/收尾。重启后两run及两原flow字节同。第三轮run-cdd4a73f7a8d等待21:00、状态文字清晰、当前/下一候选显示实际节点；停止Cancelled、stopRequested=true、三历史0。

本机判断和结束run-7ac18b5047e9实际Succeeded(state4)，nodeOutcomes为branchYes与succeeded、0BGI提交/0收尾；仅证明本机路径执行。该运行出现“仲裁操作终局回写未确认”，原操作cc8fd9167daf4fe8b7a3cc03cc721ff1至正常退出仍Accepted(requestState5)，不是TerminalCompleted(6)，持久映射已在run内，lease为null。这是新发现的本版正常路径结束收口缺口，保留重要正确性责任；具体后果/根因需定向证实，不能凭日志降级或改数据为成功。

第三轮两应用正常exit0/0，apps-tree-terminal active0、父控制器exit0、协议恢复、product_user_changed空，预算终态后交接，无自有应用/构建/测试/突变/写者锁。第三轮用户游戏窗口曾出现但未操作；不代游戏/账号/关机等用户待测。

## 唯一下一项与全部总目标

本版新发现固定ID：OWN-ROOT-FAST-END-IMPORTANT-1（重要正确性问题，根因与具体用户后果待定向核实；保留原始运行和Accepted操作，不自行降级/关闭）。

先核当前Goal/模型/cwd/源产物/进程/预算/提交，再查TaskCenterHost.Admission.cs:2803 MarkAdmissionTerminalIfAnyAsync、ReconcileAdmissionTerminalCoreBodyAsync、BindOriginalAdmissionMapping调用方、TaskCenterHost驱动finally及RunStore.TrySealTerminalRun：证明快速本机终态是否在流程登记映射/Accepted发布前抢先回写，或具体封印失败原因；不要假定根因。复用既有受理/封印机制，准备确定性生产同入口红反例，最小修复/必要有效因果/相称回归，并在同一新完整产物实际复验终局与重复正常入口。不得放松原映射、未知、seal/退出证明、Stop或身份门；不得编辑运行账或仲裁文件来“修复”。然后补仍缺的全部约定开发侧矩阵、必要独立综合复核及整版版本/启动步骤交付。UI候选激活、实参修改保存等未验内容保持未验；不要一按钮一编译/一函数一审。

沿原交接所有范围/政策/资料：AGENTS与bgi-project-development；总计划页首、complete-usable-v1、UI intake、DELIVERY-COVERAGE/DELIVERY-FIRST-POLICY；外部UI唯一E:\Program Files\mistletoe-ui-design只读/v4；Path-runtime、前继HANDOFF/RUNTIME-MATRIX/causal-repeat/旧恢复证据。车道是路径，流程到达即执行，重复由再次到达/循环决定；无每天一次/OR/AND配额。全部C01/C02/C04-C11/C17/C20、公版、八原生、组/JS/宏混排保持。第三方JS一字不改。

禁止再从Codex移动真实用户目录；r2/external-restored.json原件116/27恢复证明及旧档保持。助手独立NEXUSBGI_DATA_ROOT=own-runtime/assistant-data，任何新运行显式指定，绝不默认根启动；13处统一根候选沿前继，default/MSIX路径标签不能证明物理隔离。旧green2恢复/green3错误与旧预约failed原样保留。

用户本聊天已明确批准累计17.5GiB，实际policy operation1610612736/retained18790481920/min_free8589934592。新授权记录own-runtime/third/storage-authorization-17.5gib.json，旧full-product/storage-authorization-17gib.json不改。第三轮首尝试在17GiB入门拒绝，未创建third/未启动；新授权后同目标第三轮才执行，不伪改旧失败。所有新材料经Session/process_runner，无可清scratch，不绕目录/渠道，不声称OS硬配额，不删除历史材料/User。

原bf733 blocked/98责任、额外2/2余额0、旧286=283P/1F/2NotExecuted和助手/BGI全量失败、ProductionCtor断言等保持。本聊天新增会诊0，无综合pass/receipt；必要复核须具体稳定材料后申请固定次数/范围，不超额/重置。bundle drift、r61缺report原样登记，不扩无关工具。继续读设施/storage/v3/README及并行成果md/json，discover只读发现，无收益不派并行（当前UI串行/领域锁/预算0）。自动本地git commit --only明确归属，不push/部署/合并。

新发现/证据与本批原始来源见own-root-checkpoint/runtime-summary.json/private、own-runtime/third、ui-polish/red/green；private包含自有样本数据，不随意送外部。总Goal仍未达，不complete，不重建原opening/历史/额度。必要下一次交接按原生pause确认与同项目local唯一继承实际模型/档位执行。
