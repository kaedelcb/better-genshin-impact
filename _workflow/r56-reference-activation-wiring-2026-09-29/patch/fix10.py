import pathlib
q=pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
u=q.read_text(encoding="utf-8")
old='''        var tampered = tx.LoadManifest()!;                              // 降级并重算摘要（伪造者知道算法）
        tampered.RealEffectsRequired = false;
        File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(tampered, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));'''
new='''        var tampered = tx.LoadManifest()!;                              // 降级**并重算摘要**（伪造者知道算法）⇒ manifest 仍可验证
        tampered.RealEffectsRequired = false;
        tampered.ManifestIntegrity = MigrationSwitchTransaction.ComputeManifestIntegrity(tampered);
        File.WriteAllText(tx.ManifestPath,
            System.Text.Json.JsonSerializer.Serialize(tampered, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Assert.NotNull(tx.LoadValidated());                            // 结构校验被绕过（这正是要防的情形）'''
assert u.count(old)==1
q.write_text(u.replace(old,new,1),encoding="utf-8")
print("downgrade fixture now recomputes digest")
