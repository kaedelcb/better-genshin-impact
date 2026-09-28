import pathlib
p = pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
s = p.read_text(encoding="utf-8")
s = s.replace("""    private static void AssertActivation(ref MigrationManifest? unused) { }

""", "")
p.write_text(s, encoding="utf-8")
print("cleaned")
