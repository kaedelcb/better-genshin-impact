import sys,os,json,hashlib,subprocess,xml.etree.ElementTree as ET
from pathlib import Path
base=Path(__file__).parent;root=Path.cwd();p=root/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'
original=p.read_bytes();records=[]
def sha(b):return hashlib.sha256(b).hexdigest()
def restore(b):
    temp=p.with_name(p.name+'.node-causal-'+str(os.getpid())+'.tmp');temp.write_bytes(b);os.replace(temp,p)
def run(name,leg,filt):
    folder=base/(name+'-'+leg)
    code=subprocess.call([sys.executable,'-B',str(base/'run-stop-check.py'),folder.name,filt],cwd=root)
    tree=ET.parse(folder/'results.trx');ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    rows=[dict(id=r.get('testId'),name=r.get('testName'),outcome=r.get('outcome'),error=r.findtext('t:Output/t:ErrorInfo/t:Message','',ns),stack=r.findtext('t:Output/t:ErrorInfo/t:StackTrace','',ns)) for r in tree.findall('.//t:UnitTestResult',ns)]
    records.append(dict(mutation=name,leg=leg,source=p.relative_to(root).as_posix(),source_sha256=sha(p.read_bytes()),exit=code,directory=folder.relative_to(root).as_posix(),results=rows))
    (base/'node-pfp-r2-observation.json').write_text(json.dumps(dict(kind='ordinary P/F/P; not authenticated receipt',records=records),indent=2),encoding='utf-8')
    if leg=='negative':
        assert code==1 and any(r['outcome']=='Failed' and 'Expected: Unavailable' in r['error'] and 'Actual:   Effective' in r['error'] for r in rows)
    else:assert code==0 and rows and all(r['outcome']=='Passed' for r in rows)
for name,filt,legacy in [('node-original-mapping-r2','FullyQualifiedName~OriginalTerminalStop_MissingOrChangedNodeMapping',False),('legacy-all-anchors-r2','FullyQualifiedName~LegacyTerminalStop_AllNewAnchors',True)]:
    try:
        assert p.read_bytes()==original
        run(name,'baseline',filt)
        text=original.decode('utf-8-sig');nl='\r\n' if b'\r\n' in original else '\n';text=text.replace('\r\n','\n')
        start=text.index('        // Every possibly sent node retains its original operation, even when flow registration remains.')
        end=text.index('        if (run.AdmissionParentSource is { Kind: AdmissionParentKind.PanelFlowRegistration } parent',start)
        text=text[:start]+text[end:]
        if legacy:
            needle='            || TerminalReleaseEvidence.Submissions(run).Any(s => s.SendAttempted || !string.IsNullOrEmpty(s.JobId)\n                || !string.IsNullOrEmpty(s.AcceptedSendIdentity))\n'
            assert text.count(needle)==1;text=text.replace(needle,'')
        mutant=(b'\xef\xbb\xbf' if original.startswith(b'\xef\xbb\xbf') else b'')+text.replace('\n',nl).encode()
        restore(mutant);run(name,'negative',filt)
    finally:
        restore(original);assert sha(p.read_bytes())==sha(original)
        records.append(dict(mutation=name,leg='source-restoration',original_sha256=sha(original),restored_sha256=sha(p.read_bytes()),atomic=True))
        (base/'node-pfp-r2-observation.json').write_text(json.dumps(dict(kind='ordinary P/F/P; not authenticated receipt',records=records),indent=2),encoding='utf-8')
    run(name,'restored',filt)
