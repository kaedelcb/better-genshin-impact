using System.ComponentModel;
using System.Diagnostics;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class BgiProcessBindingTests
{
    private static readonly BgiEpoch Epoch = new() { ProcessId = 42, StartTicksUtc = 420 };
    private const string ActualPath = @"C:\BetterGI\BetterGI.exe";

    [Fact]
    public void CurrentProcessWithItsActualPathIsAccepted()
    {
        using var current = Process.GetCurrentProcess();
        var epoch = new BgiEpoch { ProcessId = current.Id, StartTicksUtc = current.StartTime.ToUniversalTime().Ticks };

        var result = BgiProcessBinding.Verify(epoch, Environment.ProcessPath, current.SessionId);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Message);
    }

    [Fact]
    public void DifferentExistingProgramPathIsRejectedWithBothPaths()
    {
        using var current = Process.GetCurrentProcess();
        var epoch = new BgiEpoch { ProcessId = current.Id, StartTicksUtc = current.StartTime.ToUniversalTime().Ticks };
        var otherProgram = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var result = BgiProcessBinding.Verify(epoch, otherProgram, current.SessionId);

        Assert.False(result.Success);
        Assert.Contains("程序不一致", result.Message);
        Assert.Contains(Environment.ProcessPath!, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(otherProgram, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("未发送任务", result.Message);
    }

    [Fact]
    public void ReusedPidWithDifferentStartTimeIsRejected()
    {
        using var current = Process.GetCurrentProcess();
        var epoch = new BgiEpoch { ProcessId = current.Id, StartTicksUtc = current.StartTime.ToUniversalTime().Ticks - 1 };

        var result = BgiProcessBinding.Verify(epoch, Environment.ProcessPath, current.SessionId);

        Assert.False(result.Success);
        Assert.Contains("进程已重启或连接身份已经变化", result.Message);
        Assert.DoesNotContain("程序不一致", result.Message);
    }

    [Fact]
    public void DifferentWindowsSessionIsRejectedIndependently()
    {
        using var current = Process.GetCurrentProcess();
        var epoch = new BgiEpoch { ProcessId = current.Id, StartTicksUtc = current.StartTime.ToUniversalTime().Ticks };

        var result = BgiProcessBinding.Verify(epoch, Environment.ProcessPath, current.SessionId + 1);

        Assert.False(result.Success);
        Assert.Contains("其他 Windows 会话", result.Message);
        Assert.DoesNotContain("程序不一致", result.Message);
    }

    [Fact]
    public void IdentityReaderAccessDeniedIsNotReportedAsWrongProgram()
    {
        var result = BgiProcessBinding.VerifyCore(Epoch, ActualPath, 4,
            _ => throw new Win32Exception(5), PathIdentity.TryCanonicalizeForComparison);

        Assert.False(result.Success);
        Assert.Contains("读取 BGI 进程身份失败", result.Message);
        Assert.Contains("Win32 5", result.Message);
        Assert.Contains("未发送任务", result.Message);
        Assert.DoesNotContain("程序不一致", result.Message);
        Assert.DoesNotContain("配置 BGI", result.Message);
    }

    [Theory]
    [InlineData(true, "无法规范化正在运行的 BGI 程序路径")]
    [InlineData(false, "无法规范化 BGI 配置路径")]
    public void CanonicalizationFailureIsNotReportedAsWrongProgram(bool failActual, string expectedMessage)
    {
        var calls = 0;
        bool Canonicalize(string? path, out string canonical)
        {
            calls++;
            canonical = path ?? "";
            return failActual ? calls != 1 : calls != 2;
        }

        var result = BgiProcessBinding.VerifyCore(Epoch, ActualPath, 4,
            _ => new BgiProcessIdentity(42, 420, 4, ActualPath), Canonicalize);

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.Message);
        Assert.Contains("未发送任务", result.Message);
        Assert.DoesNotContain("程序不一致", result.Message);
    }

    [Fact]
    public void CanonicalizationAccessDeniedRetainsItsStageAndWin32Error()
    {
        bool Canonicalize(string? path, out string canonical)
        {
            canonical = "";
            throw new Win32Exception(5);
        }

        var result = BgiProcessBinding.VerifyCore(Epoch, ActualPath, 4,
            _ => new BgiProcessIdentity(42, 420, 4, ActualPath), Canonicalize);

        Assert.False(result.Success);
        Assert.Contains("规范化正在运行的 BGI 程序路径失败", result.Message);
        Assert.Contains("Win32 5", result.Message);
        Assert.DoesNotContain("程序不一致", result.Message);
    }

    [Fact]
    public void ReaderCannotSubstituteAnotherProcess()
    {
        var result = BgiProcessBinding.VerifyCore(Epoch, ActualPath, 4,
            _ => new BgiProcessIdentity(43, 420, 4, ActualPath), PathIdentity.TryCanonicalizeForComparison);

        Assert.False(result.Success);
        Assert.Contains("连接身份已经变化", result.Message);
        Assert.DoesNotContain("程序不一致", result.Message);
    }

    [Fact]
    public void InvalidEpochDoesNotReadAnyProcess()
    {
        var called = false;
        var result = BgiProcessBinding.VerifyCore(new BgiEpoch { ProcessId = 0, StartTicksUtc = 420 }, ActualPath, 4,
            _ => { called = true; return new BgiProcessIdentity(42, 420, 4, ActualPath); },
            PathIdentity.TryCanonicalizeForComparison);

        Assert.False(called);
        Assert.False(result.Success);
        Assert.Contains("进程身份尚未确认", result.Message);
    }
}
