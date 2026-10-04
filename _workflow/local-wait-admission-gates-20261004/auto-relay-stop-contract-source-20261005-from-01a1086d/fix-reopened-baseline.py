from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs')
b=p.read_bytes(); s=b.decode('utf-8').replace('\r\n','\n')
start=s.index('    public async Task HistoricalNodeStop_'); end=s.index('    [Theory]',start)
t=s[start:end]
a=t.index('                    var fields ='); z=t.index('                    try\n',a)
block=t[a:z]+'                    reopened.EnsureRecovered();\n'
t=t[:a]+t[z:]
point=t.index('                    var runPath =')
t=t[:point]+block+t[point:]
s=s[:start]+t+s[end:]; p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
