import io
p = r"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"
t = io.open(p, encoding="utf-8", newline="").read()
old = '{ NodeId: "A", LoopIteration: 2, Result = WorkflowRunner.LocalWaitResultWord }'
new = '{ NodeId: "A", LoopIteration: 2, Result: WorkflowRunner.LocalWaitResultWord }'
assert t.count(old) == 1
t = t.replace(old, new)
io.open(p, "w", encoding="utf-8", newline="").write(t)
print("fixed")
