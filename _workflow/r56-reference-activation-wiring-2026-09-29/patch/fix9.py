import pathlib
q=pathlib.Path("Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/R56ReferenceActivationWiringTests.cs")
u=q.read_text(encoding="utf-8")
old='''        Assert.Equal(2, tx.LoadManifest()!.ChangedFiles.Count + tx.LoadManifest()!.ReferenceWriteSet.Count - 1);'''
new='''        Assert.Single(tx.LoadManifest()!.ChangedFiles);                                  // 登记未被重入污染
        Assert.Single(tx.LoadManifest()!.ReferenceWriteSet);                             // 写集未被重入污染'''
assert u.count(old)==1
q.write_text(u.replace(old,new,1),encoding="utf-8")
print("assertion fixed")
