from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');b=p.read_bytes();s=b.decode('utf-8');nl='\r\n' if b'\r\n' in b else '\n';s=s.replace('\r\n','\n')
a=s.index('    public async Task OriginalHost_TerminalSweepYieldsWhileFacadeGateIsHeld()');z=s.index('    private static async Task WaitForOriginalTerminalObserverAsync',a)
chunk=s[a:z];needle='                    var read = store.Read();';assert chunk.count(needle)==1;chunk=chunk.replace(needle,'                    await WaitForOriginalTerminalObserverAsync(host);\n'+needle)
p.write_bytes((s[:a]+chunk+s[z:]).replace('\n',nl).encode())
base=Path(__file__).parent;pfp=(base/'node-pfp.py').read_text().replace("'node-original-mapping-r1'","'node-original-mapping-r3'").replace("'legacy-all-anchors-r1'","'legacy-all-anchors-r3'").replace("'node-pfp-observation.json'","'node-pfp-r3-observation.json'");(base/'node-pfp-r3.py').write_text(pfp,encoding='utf-8')
collect=(base/'collect-node-final.py').read_text().replace("'node-source-final-r2'","'node-source-final-r3'").replace("'node-pfp-r2-observation.json'","'node-pfp-r3-observation.json'");(base/'collect-node-final-r3.py').write_text(collect,encoding='utf-8')
