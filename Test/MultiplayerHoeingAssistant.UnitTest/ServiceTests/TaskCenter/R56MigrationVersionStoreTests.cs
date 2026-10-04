using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class R56MigrationVersionStoreTests
{
    private static (string Root, string Config, string Artifacts, WindowsTxfMigrationVersionStore Store) Fixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "r56-owned-version-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "config");
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(artifacts);
        return (root, config, artifacts, new(config, artifacts));
    }

    [Fact]
    public void CurrentV15_DataWriterSameOwnedIdentityThroughSymlink_PreservesOutsideBytesAndReceipt()
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "a.json");
        var baseline = Encoding.UTF8.GetBytes("BASELINE_OWNED_BYTES");
        File.WriteAllBytes(path, baseline);
        var input = f.Store.ReadVersion("a.json");
        var outside = Path.Combine(f.Root, "outside-config");
        Directory.CreateDirectory(outside);
        var retained = Path.Combine(outside, "a.json");
        File.Move(path, retained);
        File.CreateSymbolicLink(path, retained);
        var outsideStore = new WindowsTxfMigrationVersionStore(outside, f.Artifacts);
        Assert.True(input.Matches(outsideStore.ReadVersion("a.json")));
        Assert.Equal(baseline, File.ReadAllBytes(retained));
        Assert.NotNull(new FileInfo(path).LinkTarget);
        var called = false;
        var result = f.Store.ExecuteFile("a.json", input, (_, _, _) =>
        {
            called = true;
            return new(Encoding.UTF8.GetBytes("F"), false,
                (_, _) => [new("escaped-data-receipt.json", Encoding.UTF8.GetBytes("APPLIED"))]);
        });
        Assert.Equal(baseline, File.ReadAllBytes(retained)); // Byte safety, not a reason-string assertion.
        Assert.True(input.Matches(outsideStore.ReadVersion("a.json")));
        Assert.False(result.Success);
        Assert.False(result.CommitAttempted);
        Assert.False(result.CommitConfirmed);
        Assert.False(called);
        Assert.False(File.Exists(Path.Combine(f.Artifacts, "escaped-data-receipt.json")));
        Assert.NotNull(new FileInfo(path).LinkTarget);
        Assert.Equal(Path.GetFullPath(retained), Path.GetFullPath(new FileInfo(path).ResolveLinkTarget(true)!.FullName));
    }

    [Fact]
    public void CurrentV15_DirectoryAliasWithSameOwnedTarget_IsNotDeletedOrPublished()
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "owned");
        Directory.CreateDirectory(path);
        var input = f.Store.ReadVersion("owned", MigrationEntryKind.Directory);
        var outside = Path.Combine(f.Root, "outside-directory");
        Directory.CreateDirectory(outside);
        var retained = Path.Combine(outside, "owned");
        Directory.Move(path, retained);
        Directory.CreateSymbolicLink(path, retained);
        var outsideStore = new WindowsTxfMigrationVersionStore(outside, f.Artifacts);
        Assert.True(input.Matches(outsideStore.ReadVersion("owned", MigrationEntryKind.Directory)));
        Assert.NotNull(new DirectoryInfo(path).LinkTarget);
        var called = false;
        var result = f.Store.RemoveDirectory("owned", input, _ =>
        {
            called = true;
            return [new("escaped-directory-receipt.json", Encoding.UTF8.GetBytes("DELETED"))];
        });
        Assert.NotNull(new DirectoryInfo(path).LinkTarget); // Foreign namespace entry must be preserved.
        Assert.True(Directory.Exists(retained));
        Assert.True(input.Matches(outsideStore.ReadVersion("owned", MigrationEntryKind.Directory)));
        Assert.False(result.Success);
        Assert.False(result.CommitConfirmed);
        Assert.False(called);
        Assert.False(File.Exists(Path.Combine(f.Artifacts, "escaped-directory-receipt.json")));
        Assert.Equal(Path.GetFullPath(retained), Path.GetFullPath(new DirectoryInfo(path).ResolveLinkTarget(true)!.FullName));
    }

    [Fact]
    public void CurrentV15_ArtifactParentAlias_IsRejectedBeforeCreatingOutsideChildren()
    {
        var f = Fixture();
        var outside = Path.Combine(f.Root, "outside-artifact-parent");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.bin");
        var original = new byte[] { 5, 17, 29, 41 };
        File.WriteAllBytes(sentinel, original);
        var outsideStore = new WindowsTxfMigrationVersionStore(outside, f.Config);
        var sentinelVersion = outsideStore.ReadVersion("sentinel.bin");
        var alias = Path.Combine(f.Artifacts, "alias");
        Directory.CreateSymbolicLink(alias, outside);
        Assert.NotNull(new DirectoryInfo(alias).LinkTarget);
        var leaked = Xunit.Record.Exception(() => f.Store.WriteArtifacts(
            [new("alias/new-child/receipt.json", Encoding.UTF8.GetBytes("MUST_NOT_ESCAPE"))]));
        // The old helper can refuse late yet already have created this outside directory.
        Assert.False(Directory.Exists(Path.Combine(outside, "new-child")));
        Assert.False(File.Exists(Path.Combine(outside, "new-child", "receipt.json")));
        Assert.Equal(original, File.ReadAllBytes(sentinel));
        Assert.True(sentinelVersion.Matches(outsideStore.ReadVersion("sentinel.bin")));
        Assert.NotNull(new DirectoryInfo(alias).LinkTarget);
        Assert.NotNull(leaked); // void primitive communicates refusal by its existing exception contract.
        Assert.Single(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void CurrentV15_OwnedEmptyDirectoryDeletion_CommitsWithReceipt()
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "owned-empty");
        Directory.CreateDirectory(path);
        var input = f.Store.ReadVersion("owned-empty", MigrationEntryKind.Directory);
        var result = f.Store.RemoveDirectory("owned-empty", input,
            _ => [new("legal-empty-directory-receipt.json", Encoding.UTF8.GetBytes("DELETED"))]);
        Assert.True(result.Success, result.Reason);
        Assert.True(result.CommitConfirmed);
        Assert.NotNull(result.Output);
        Assert.False(result.Output!.Exists);
        Assert.Equal(MigrationEntryKind.Directory, result.Output.Kind);
        Assert.False(Directory.Exists(path));
        Assert.Equal("DELETED", File.ReadAllText(Path.Combine(f.Artifacts, "legal-empty-directory-receipt.json")));
    }

    [Fact]
    public void ForeignInputBeforeWrite_IsRejectedAndPreserved()
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "a.json");
        File.WriteAllText(path, "B");
        var input = f.Store.ReadVersion("a.json");
        File.WriteAllText(path, "FOREIGN_X");
        var prepared = false;
        var result = f.Store.ExecuteFile("a.json", input, (_, _, _) =>
        {
            prepared = true;
            return new(Encoding.UTF8.GetBytes("F"), false, (_, _) => []);
        });
        Assert.False(result.Success);
        Assert.False(prepared);
        Assert.False(result.CommitAttempted);
        Assert.Equal("FOREIGN_X", File.ReadAllText(path));
    }

    [Fact]
    public void DataAndAppliedReceipt_CommitTogether_AndShortOutputIsTruncated()
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "a.json");
        File.WriteAllText(path, "BASELINE_LONG");
        var input = f.Store.ReadVersion("a.json");
        var result = f.Store.ExecuteFile("a.json", input, (_, _, _) => new(Encoding.UTF8.GetBytes("F"), false,
            (output, _) => [new("receipt.json", Encoding.UTF8.GetBytes(output.Sha256!))]));
        Assert.True(result.Success, result.Reason);
        Assert.True(result.CommitConfirmed);
        Assert.Equal("F", File.ReadAllText(path));
        Assert.Equal(MigrationFileVersion.Hash(Encoding.UTF8.GetBytes("F")), File.ReadAllText(Path.Combine(f.Artifacts, "receipt.json")));
        Assert.Equal(input.FileId, result.Output!.FileId);
    }

    [Theory]
    [InlineData("after_data")]
    [InlineData("after_receipt")]
    [InlineData("after_handles")]
    public void InterruptedBeforeCommit_LeavesNeitherDataNorAppliedReceipt(string station)
    {
        var f = Fixture();
        var path = Path.Combine(f.Config, "a.json");
        File.WriteAllText(path, "B");
        var input = f.Store.ReadVersion("a.json");
        var reached = false;
        var result = f.Store.ExecuteFile("a.json", input, (_, _, _) => new(Encoding.UTF8.GetBytes("F"), false,
            (_, _) => [new("receipt.json", Encoding.UTF8.GetBytes("APPLIED"))]), fault: step =>
        {
            if (step == station) { reached = true; throw new IOException("owned_interruption"); }
        });
        Assert.True(reached);
        Assert.False(result.Success);
        Assert.False(result.CommitConfirmed);
        Assert.Equal("B", File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(f.Artifacts, "receipt.json")));
    }

    [Fact]
    public void NewParentAndAddedFile_AreOneTransaction()
    {
        var f = Fixture();
        var parents = new Dictionary<string, MigrationFileVersion>
        {
            ["nested"] = MigrationFileVersion.Absent(MigrationEntryKind.Directory)
        };
        var result = f.Store.ExecuteFile("nested/a.json", MigrationFileVersion.Absent(), (_, _, _) =>
            new(Encoding.UTF8.GetBytes("F"), false, (_, _) => [new("receipt.json", Encoding.UTF8.GetBytes("APPLIED"))]), parents);
        Assert.True(result.Success, result.Reason);
        Assert.Single(result.Directories);
        Assert.NotNull(result.Directories[0].Output.FileId);
        Assert.Equal("F", File.ReadAllText(Path.Combine(f.Config, "nested/a.json")));
        Assert.True(File.Exists(Path.Combine(f.Artifacts, "receipt.json")));
    }

    [Fact]
    public void OwnedDirectoryWithForeignChild_IsNotRecursivelyDeleted()
    {
        var f = Fixture();
        Directory.CreateDirectory(Path.Combine(f.Config, "nested"));
        var input = f.Store.ReadVersion("nested", MigrationEntryKind.Directory);
        var foreign = Path.Combine(f.Config, "nested/foreign.json");
        File.WriteAllText(foreign, "X");
        var result = f.Store.RemoveDirectory("nested", input, _ => [new("deleted.json", Encoding.UTF8.GetBytes("DELETED"))]);
        Assert.False(result.Success);
        Assert.False(result.CommitConfirmed);
        Assert.Equal("X", File.ReadAllText(foreign));
        Assert.False(File.Exists(Path.Combine(f.Artifacts, "deleted.json")));
    }
}
