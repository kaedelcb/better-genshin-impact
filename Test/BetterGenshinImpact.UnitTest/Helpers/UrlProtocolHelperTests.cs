using System.Security;
using BetterGenshinImpact.Helpers;
using Microsoft.Win32;

namespace BetterGenshinImpact.UnitTest.Helpers;

public sealed class UrlProtocolHelperTests
{
    private static async Task WithTemporaryScheme(Func<RegistryKey, Task> test)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var path = @"Software\BetterGI.UrlProtocol.Tests-" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = root.CreateSubKey(path);
            await test(key);
        }
        finally { root.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [Fact]
    public Task RealRegistrationCanBeCheckedThroughAReadOnlyKeyWithoutRewriting()
        => WithTemporaryScheme(async key =>
        {
            UrlProtocolHelper.Register(key);
            Assert.Equal(UrlProtocolHelper.ProtocolName, key.GetValue(string.Empty));
            Assert.Equal(string.Empty, key.GetValue("URL Protocol"));
            using var readOnly = key.OpenSubKey(string.Empty, writable: false)!;
            Assert.True(UrlProtocolHelper.IsRegistered(readOnly));
            var writes = 0;
            await UrlProtocolHelper.RegisterAsync(() => UrlProtocolHelper.IsRegistered(readOnly),
                () => { writes++; throw new SecurityException("correct read-only association must remain unchanged"); });
            Assert.Equal(0, writes);
            using var command = key.OpenSubKey(@"shell\open\command")!;
            Assert.True(UrlProtocolHelper.GetPath(out var expected));
            Assert.Equal(expected, command.GetValue(string.Empty));
        });

    [Fact]
    public Task MissingUrlProtocolMarkerIsRepairedInsteadOfTreatedAsRegistered()
        => WithTemporaryScheme(async key =>
        {
            UrlProtocolHelper.Register(key);
            key.DeleteValue("URL Protocol");
            Assert.False(UrlProtocolHelper.IsRegistered(key));
            await UrlProtocolHelper.RegisterAsync(() => UrlProtocolHelper.IsRegistered(key),
                () => UrlProtocolHelper.Register(key));
            Assert.True(UrlProtocolHelper.IsRegistered(key));
        });

    [Fact]
    public async Task CorrectRegistrationDoesNotRequestWriteAccess()
    {
        var checks = 0;
        var writes = 0;
        await UrlProtocolHelper.RegisterAsync(() => { checks++; return true; },
            () => { writes++; throw new SecurityException("write access must not be requested"); });
        Assert.Equal(1, checks);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task MissingOrOutdatedRegistrationIsWrittenOnce()
    {
        var checks = 0;
        var writes = 0;
        await UrlProtocolHelper.RegisterAsync(() => { checks++; return false; }, () => writes++);
        Assert.Equal(1, checks);
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData("security", false)]
    [InlineData("security", true)]
    [InlineData("access", false)]
    [InlineData("access", true)]
    [InlineData("io", false)]
    [InlineData("io", true)]
    public async Task RegistryAccessFailureDoesNotEscapeTheAwaitedStartupStep(string kind, bool duringWrite)
    {
        Exception failure = kind switch
        {
            "security" => new SecurityException("registry access denied"),
            "access" => new UnauthorizedAccessException("registry access denied"),
            _ => new IOException("registry unavailable"),
        };
        var writes = 0;
        var continuedStartup = false;
        await UrlProtocolHelper.RegisterAsync(
            () => duringWrite ? false : throw failure,
            () => { writes++; throw failure; });
        continuedStartup = true;
        Assert.True(continuedStartup);
        Assert.Equal(duringWrite ? 1 : 0, writes);
    }

    [Fact]
    public async Task UnexpectedProgrammingFailureIsNotHiddenAsRegistryRestriction()
    {
        var failure = new InvalidOperationException("unexpected registration failure");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UrlProtocolHelper.RegisterAsync(() => false, () => throw failure)));
    }
}
