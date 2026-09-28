import json, pathlib
W = pathlib.Path(r"E:\Program Files\better-genshin-impact-LCB\_workflow\r56-reference-activation-wiring-2026-09-29")
mp = W/"manifest.json"; m = json.loads(mp.read_text(encoding="utf-8-sig"))
extras = [
 {"path":"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs","role":"source"},
 {"path":"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationSwitchTransaction.cs","role":"source"},
 {"path":"MultiplayerHoeingAssistant/Services/TaskCenter/MigrationRehearsal.cs","role":"source"},
 {"path":"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs","role":"source"},
 {"path":"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56MigrationSwitchTransactionTests.cs","role":"source"},
 {"path":"Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt","role":"source"},
]
m["packet"] = m["packet"] + extras
mp.write_text(json.dumps(m,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print("packet entries:", len(m["packet"]))
