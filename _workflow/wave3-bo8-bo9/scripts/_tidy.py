import io
p = r"MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs"
t = io.open(p, encoding="utf-8").read()
old = """            // **位置安全性**：锚前的有效停驻义务优先作为恢复点；驱动推进层会过滤其后已完成出现
            // （BO-6／BO-8，覆盖整段推进），
            // 由此同时保留停驻重驱与不重提已完成节点。
"""
new = """            // **位置安全性**：锚前的有效停驻义务优先作为恢复点；驱动推进层会过滤其后已完成出现
            // （BO-6／BO-8，覆盖整段推进），由此同时保留停驻重驱与不重提已完成节点。
"""
assert t.count(old) == 1
io.open(p, "w", encoding="utf-8", newline="\r\n").write(t.replace(old, new))
print("tidied")
