import xml.etree.ElementTree as ET, pathlib
ns='{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
out=[]
for r in ET.parse(r'_workflow/r56-reference-activation-wiring-2026-09-29/final/diag.trx').getroot().iter(ns+'UnitTestResult'):
    if r.get('outcome')!='Passed':
        m=r.find('.//'+ns+'Message'); st=r.find('.//'+ns+'StackTrace')
        out.append(r.get('testName','')+"\n"+(m.text or '')[:400]+"\n"+((st.text or '').splitlines() or [''])[0][:200])
pathlib.Path('_workflow/r56-reference-activation-wiring-2026-09-29/final/diag.txt').write_text("\n".join(out),encoding='utf-8')
print("ok")
