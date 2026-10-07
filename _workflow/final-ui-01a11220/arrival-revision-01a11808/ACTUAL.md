# 同一产物的有限实际修复重演

源码候选检查点8cab51bfa，助手DLL b56d5bb48fbb003803bee92e958e2b15914ea7c4aabc58cfac0a91f32646146a；BGI DLL/EXE保持原身份，实际Session1、显式NEXUSBGI_DATA_ROOT为既定独立助手根，两个EXE以各自目录启动。完整编译输入313项与restored/source-hashes.json逐SHA一致，原独立Sol/high pass仅绑定旧源码，新六行修复未独立复核，余额0。

原失败run-8386f7c03944保留：新路径gate→新增节点→end已保存并登记重载，旧产物实际却仅gate/end。原跨BGI纪元拒绝run-6e5697b907bd和冷恢复取消run-f4b7a4d95adc字节也保持，不重跑或修改原记录。原同BGI冷恢复在旧源码产物上实际读回nextDay次日同刻、原时刻/策略/epoch与零发送保持；本修复仅触及节点时间等待之后的到达边界，未改根触发等待或冷恢复准入，不将它外推为同时重启BGI/助手成功。

修复重演：正式UI启动新的run-ba802a8f2909，原修订332dd1c8、gate等待05:30；三步添加新固定判断n-944c012a，正式检查器连接gate.yes→新增节点、新增节点.yes→end，no未选支线保持。05:23:42保存修订81fd132c，05:24:11登记边界重载；等待期间原修订/时刻/零节点保持。05:30到达后真实结果为['gate', 'n-944c012a', 'end']各一次，结果branchYes、branchYes、succeeded，新修订81fd132c00239df77ca0eace4c7156a64e82b28901bce5dcef7f34310f98a878与落盘flow字节SHA一致，未选支线零到达、资源提交零，Succeeded终态。

正常退出助手及BGI，apps出口[0,0]、Job树0，协议恢复；product/User9501和真实Debug/User9522前后SHA集合无差异，未移真实目录、未改第三方JS、未启动游戏。实际run/flow、原生UI源、argv/进程来源和数据hash保留在actual/。

这证明本机判断/结束流程的插入→保存→节点边界重载→实际到达已成立，不代替八原生/组/JS/宏游戏效果、资源执行Skip、账号/兑换码/准备/队友或关机验收，也不代替新增源码独立复核。完整Goal未complete，原级发现/历史/报告/失败/预算原样保持。
