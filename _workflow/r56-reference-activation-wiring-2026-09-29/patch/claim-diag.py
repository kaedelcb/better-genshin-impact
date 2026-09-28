import xml.etree.ElementTree as ET, pathlib
ns='{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
t=ET.parse(r'_workflow/r56-reference-activation-wiring-2026-09-29/claims/claim-diag.trx')
out=[]
for r in t.getroot().iter(ns+'UnitTestResult'):
    if r.get('outcome')!='Passed':
        m=r.find('.//'+ns+'Message')
        out.append((m.text or '')[:2000] if m is not None else 'no message')
pathlib.Path('_workflow/r56-reference-activation-wiring-2026-09-29/claims/claim-diag.txt').write_text("\n".join(out), encoding='utf-8')
print("written")
