import pathlib
# 建议级：演练报告文案如实化（MigrationRehearsal 使用**无真实副作用端口**的事务框架）
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs")
s = p.read_text(encoding="utf-8")
def rep(old, new, cnt=1):
    global s
    assert s.count(old) == cnt, (s.count(old), old[:90])
    s = s.replace(old, new, cnt)
rep('''            var activated = Step("激活（candidate→active）", tx.MarkActivated());''',
    '''            // **如实命名（R5.6 A 项第 1 轮会诊建议级）**：本入口**未注入真实副作用端口**，
            // 该步是**阶段标记演练**，不代表真实的 `candidate-ready → active` 写入（真实激活接线见迁移事务的真实副作用入口）。
            var activated = Step("激活阶段标记（无真实副作用端口；真实激活见 R5.6 A 项）", tx.MarkActivated());''')
rep('''            var refUpd = Step("引用更新完成", tx.MarkReferenceUpdateCompleted());''',
    '''            var refUpd = Step("引用更新阶段标记（无真实副作用端口）", tx.MarkReferenceUpdateCompleted());''')
rep('''/// **R5.6/R5.8「迁移演练」入口（§21.4）**：在**独立配置根**上跑完整切换事务（不接触真实 User 目录）并产出报告。''',
    '''/// **R5.6/R5.8「迁移演练」入口（§21.4）**：在**独立配置根**上跑完整切换事务（不接触真实 User 目录）并产出报告。
/// **能力边界（如实）**：本入口使用**未注入真实副作用端口**的事务框架，其「引用更新／激活」两步为**阶段标记演练**
/// （`MarkReferenceUpdateCompleted`／`MarkActivated`）；真实引用写入与 `candidate-ready → active` 写入由
/// `MigrationSwitchTransaction.ApplyReferenceUpdate`／`ActivateCandidate` 承担（见 R5.6 A 项）。''')
p.write_text(s, encoding="utf-8")
print("rehearsal wording fixed")
