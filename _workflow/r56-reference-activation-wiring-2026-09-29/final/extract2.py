import xml.etree.ElementTree as ET
ns={"t":"http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
t=ET.parse(r"_workflow/r56-reference-activation-wiring-2026-09-29/final/targeted-final.trx")
for r in t.getroot().iter("{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult"):
    if r.get("outcome")=="Failed" and ("RevertsActivation" in r.get("testName") or "ManifestTamper" in r.get("testName")):
        print("===",r.get("testName"))
        st=r.find(".//t:StackTrace",ns)
        print((st.text or "")[:900] if st is not None else "no stack")
