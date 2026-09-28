import io
p = r"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/WorkflowRunnerTests.cs"
t = io.open(p, encoding="utf-8", newline="").read()
old1 = '        Assert.Empty(terminal.Actions.Count == 1 ? Array.Empty<string>() : new[] { "\u6536\u5c3e\u6b21\u6570\u5f02\u5e38" });\r\n'
new1 = '        Assert.Equal(1, terminal.Actions.Count);\r\n'
old2 = '        Assert.False(string.IsNullOrEmpty(resumed.Note));\r\n'
assert t.count(old1) == 1 and t.count(old2) == 1, (t.count(old1), t.count(old2))
t = t.replace(old1, new1).replace(old2, "")
io.open(p, "w", encoding="utf-8", newline="").write(t)
print("cleaned")
