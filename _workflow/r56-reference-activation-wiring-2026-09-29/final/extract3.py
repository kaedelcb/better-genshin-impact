import xml.etree.ElementTree as ET
ns={"t":"http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
t=ET.parse(r"_workflow/r56-reference-activation-wiring-2026-09-29/final/wiring-final.trx")
for r in t.getroot().iter("{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult"):
    if r.get("outcome")=="Failed":
        print("===",r.get("testName"))
        m=r.find(".//t:Message",ns); st=r.find(".//t:StackTrace",ns)
        print((m.text or "")[:500] if m is not None else "")
        print((st.text or "")[:400] if st is not None else "")
