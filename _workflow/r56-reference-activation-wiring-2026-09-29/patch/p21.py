import pathlib
p = pathlib.Path("_workflow/r56-reference-activation-wiring-2026-09-29/patch/run-mutations-v2.py")
s = p.read_text(encoding="utf-8")

old = '''    src_path.write_text(src_text.replace(old, new, 1), encoding="utf-8")
    mutant_sha = sh_b(src_path)'''
new = '''    try:
      src_path.write_text(src_text.replace(old, new, 1), encoding="utf-8")
      mutant_sha = sh_b(src_path)'''
assert s.count(old) == 1
s = s.replace(old, new, 1)

# indent the mutant-phase block and add the finally-restore
old2 = '''    with (md/"mutant-build.log").open("w", encoding="utf-8") as fh:
        rc, out = run(["dotnet","build","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                       "-p:DeployToBgiTools=false","-v","q","--nologo"])
        fh.write("$ dotnet build ...\\n%s\\n[exit=%d]\\n" % (out, rc))
    mutant_build_exit = rc
    mutant_exit, mutant_trx = test_run(md, "mutant.log", "mutant.trx", target_name)
    _, mutant_outcome, mutant_msg, mutant_stack, _ = trx_row(mutant_trx, target_name, case)
    assert mutant_outcome == "Failed" and mutant_exit > 0, (mid, mutant_outcome, mutant_exit)

    src_path.write_text(src_text, encoding="utf-8")
    restored_sha = sh_b(src_path)'''
new2 = '''      with (md/"mutant-build.log").open("w", encoding="utf-8") as fh:
        rc, out = run(["dotnet","build","Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj",
                       "-p:DeployToBgiTools=false","-v","q","--nologo"])
        fh.write("$ dotnet build ...\\n%s\\n[exit=%d]\\n" % (out, rc))
      mutant_build_exit = rc
      mutant_exit, mutant_trx = test_run(md, "mutant.log", "mutant.trx", target_name)
      _, mutant_outcome, mutant_msg, mutant_stack, _ = trx_row(mutant_trx, target_name, case)
    finally:
      src_path.write_text(src_text, encoding="utf-8")     # **无论如何先还原源码**
    assert mutant_outcome == "Failed" and mutant_exit > 0, (mid, mutant_outcome, mutant_exit)

    restored_sha = sh_b(src_path)'''
assert s.count(old2) == 1
s = s.replace(old2, new2, 1)
p.write_text(s, encoding="utf-8")
print("runner hardened with try/finally restore")
