import pathlib, re
p=pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s=p.read_text(encoding="utf-8")
bad = re.findall(r'public void (\w+)\(tx\)', s)
print("damaged method names:", bad)
s = re.sub(r'(public void \w+)\(tx\)', r'\1()', s)
p.write_text(s,encoding="utf-8")
print("fixed")
