import pathlib
p = pathlib.Path("MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs")
s = p.read_text(encoding="utf-8")
old = "                    catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)"
new = "                    catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or ArgumentException)"
assert s.count(old) == 1
s = s.replace(old, new, 1)
old2 = "        catch (System.Text.Json.JsonException)\n        {\n            reason = \"json_invalid\";"
assert s.count(old2) == 1
p.write_text(s, encoding="utf-8")
print("json exception qualified")
