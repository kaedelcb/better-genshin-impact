from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');s=p.read_bytes().decode().replace('\r\n','\n')
start=s.index('                            // Fault only the original binding');end=s.index('\n                        }\n                        else',start)
block=s[start:end];s=s[:start]+'                            // Handoff fault is injected after the new Host recovery scan, at the Resume source boundary.'+s[end:]
anchor='''                        if (sourceFault)
                        {
                            var denied'''
s=s.replace(anchor,'                        if (sourceFault && handoff)\n                        {\n'+block+'\n                        }\n'+anchor)
p.write_bytes(s.replace('\n','\r\n').encode())
