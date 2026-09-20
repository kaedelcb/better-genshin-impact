using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5.6/R5.8「迁移演练」入口** 夹具（施工方内置、**owner 0 点击**）：验证一键演练在**独立配置根**上跑完整事务
/// （快照→引用更新→激活→回滚演练→提交→真实回滚→**逐字节比对**）并产出结构化报告；
/// 同时验证**拒绝**把真实 User 目录当作演练配置根。**本入口不执行真实 User 目录切换**（须 owner 另行下令）。
/// </summary>
public sealed class R58MigrationRehearsalTests : IDisposable
{
    private readonly string _root;
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    public R58MigrationRehearsalTests()
        => _root = Path.Combine(Path.GetTempPath(), "r58m-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Rehearsal_RunsFullCycle_OnIndependentRoot_AndReports()
    {
        var report = MigrationRehearsal.Run(_root, userConfigRoot: Path.Combine(_root, "real-user"), () => Now);

        Assert.True(report.Success, report.Summary);
        Assert.All(report.Steps, s => Assert.True(s.Success, s.Name + ":" + s.Detail));
        Assert.Contains(report.Steps, s => s.Name.Contains("逐字节比对") && s.Success);
        Assert.Contains(report.Steps, s => s.Name.Contains("回滚演练") && s.Success);
        Assert.False(string.IsNullOrEmpty(report.SnapshotManifestHash));       // 快照清单哈希可取证
        Assert.False(string.IsNullOrEmpty(report.TransactionId));              // 事务标识可取证
        Assert.Equal("RolledBack", report.FinalStage);                         // 终态＝回滚完成
        Assert.Equal(new[] { "a.json", "sub/b.json" }, report.ComparedFiles);   // **完整文件集**参与比对
        Assert.Contains(report.Steps, s => s.Name.Contains("回滚终态校验") && s.Success);
        Assert.True(File.Exists(report.ManifestPath));                         // manifest 可查
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(report.ConfigRoot), StringComparison.OrdinalIgnoreCase);
        // 独立根不变量：演练配置根与事务根都必须位于演练根内（因而与真实 User 目录无关；
        // 注意「临时目录路径里可能包含 Users 字样」，故不能按字符串判定是否真实 User 目录）
        Assert.True(Path.GetFullPath(report.TransactionRoot).StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>**失败即停**：注入在「激活」阶段抛错 ⇒ 不得制造任何迁移变更（`a.json` 仍为基线内容），报告带事务标识与失败步骤。</summary>
    [Fact]
    public void Rehearsal_FailureStopsBeforeAnyMigrationChange()
    {
        var report = MigrationRehearsal.Run(_root, null, () => Now,
            stageHook: stage => { if (stage == MigrationStage.Activated) throw new InvalidOperationException("注入激活失败"); });

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "异常" && !s.Success);
        var a = Path.Combine(report.ConfigRoot, "a.json");
        Assert.True(File.Exists(a));
        Assert.Equal("{\"v\":1}", File.ReadAllText(a));     // **未被改成 v2**（失败即停）
        Assert.False(string.IsNullOrEmpty(report.TransactionId));
    }

    /// <summary>**写入前拒绝**：演练根与真实 User 目录重叠 ⇒ 直接拒绝且**未写入任何内容**。</summary>
    [Fact]
    public void Rehearsal_OverlappingUserRoot_RejectedBeforeAnyWrite()
    {
        var userRoot = Path.Combine(_root, "user-config");
        Directory.CreateDirectory(userRoot);
        var marker = Path.Combine(userRoot, "keep.json");
        File.WriteAllText(marker, "{\"keep\":1}");

        var report = MigrationRehearsal.Run(userRoot, userConfigRoot: userRoot, () => Now);

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        Assert.Equal("{\"keep\":1}", File.ReadAllText(marker));                         // 既有文件字节不变
        Assert.Empty(Directory.EnumerateFiles(userRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).Where(n => n != "keep.json").ToList());           // 未写入任何新内容
    }

    [Fact]
    public void Rehearsal_RepeatedRuns_AreIndependent()
    {
        var first = MigrationRehearsal.Run(Path.Combine(_root, "run1"), null, () => Now);
        var second = MigrationRehearsal.Run(Path.Combine(_root, "run2"), null, () => Now);
        Assert.True(first.Success, first.Summary);
        Assert.True(second.Success, second.Summary);
        Assert.NotEqual(first.RehearsalRoot, second.RehearsalRoot);           // 每次新建独占目录
        Assert.True(Directory.Exists(first.RehearsalRoot));
        Assert.True(Directory.Exists(second.RehearsalRoot));
    }

    /// <summary>
    /// **路径解析失败的结构化拒绝**：非法真实 User 根（含 NUL）必须在任何写入前转换为
    /// `独立根校验` 失败报告，不能以异常逃逸，也不能创建演练目录。
    /// </summary>
    [Fact]
    public void Rehearsal_InvalidUserRoot_IsStructuredRejectionBeforeWrite()
    {
        var report = MigrationRehearsal.Run(_root, userConfigRoot: "bad\0user", () => Now);

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        Assert.False(Directory.Exists(_root)); // 写入前拒绝，连演练根都未创建
    }

    /// <summary>
    /// **非法演练根的结构化拒绝**：含 NUL 的 rehearsalRoot 不能在 `Path.GetFullPath` 处抛出。
    /// </summary>
    [Fact]
    public void Rehearsal_InvalidRehearsalRoot_IsStructuredRejection()
    {
        var report = MigrationRehearsal.Run("bad\0root", null, () => Now);

        Assert.False(report.Success);
        Assert.Contains(report.Steps, s => s.Name == "独立根校验" && !s.Success);
        Assert.Equal("", report.RehearsalRoot);
        Assert.Equal("", report.ConfigRoot);
    }

    /// <summary>生产解析器正常路径：BGI 可执行文件存在且同目录 User 为真实目录时才返回规范化根。</summary>
    [Fact]
    public void ProductionResolver_ValidBgiPathWithUserDir_ReturnsCanonicalRoot()
    {
        var bgiDir = Path.Combine(_root, "bgi");
        var userDir = Path.Combine(bgiDir, "User");
        Directory.CreateDirectory(userDir);
        var exePath = Path.Combine(bgiDir, "BetterGenshinImpact.exe");
        File.WriteAllText(exePath, "fake");

        var resolved = MultiplayerHoeingAssistant.ViewModels.MainViewModel.TryResolveBgiUserConfigRoot(exePath);

        Assert.Equal(Path.GetFullPath(userDir), resolved);
    }

    /// <summary>User 必须是已存在目录；缺失或不是目录时生产解析必须返回 null。</summary>
    [Fact]
    public void ProductionResolver_UserMissingOrFile_ReturnsNull()
    {
        var bgiDir = Path.Combine(_root, "bgi");
        Directory.CreateDirectory(bgiDir);
        var exePath = Path.Combine(bgiDir, "BetterGenshinImpact.exe");
        File.WriteAllText(exePath, "fake");
        Assert.Null(MultiplayerHoeingAssistant.ViewModels.MainViewModel.TryResolveBgiUserConfigRoot(exePath));

        File.WriteAllText(Path.Combine(bgiDir, "User"), "not-a-directory");
        Assert.Null(MultiplayerHoeingAssistant.ViewModels.MainViewModel.TryResolveBgiUserConfigRoot(exePath));
    }

    /// <summary>原始配置含首尾控制字符时必须拒绝，不得先 Trim 再接受。</summary>
    [Fact]
    public void ProductionResolver_ControlCharsBeforeNormalization_ReturnsNull()
    {
        var bgiDir = Path.Combine(_root, "bgi");
        Directory.CreateDirectory(Path.Combine(bgiDir, "User"));
        var exePath = Path.Combine(bgiDir, "BetterGenshinImpact.exe");
        File.WriteAllText(exePath, "fake");

        var resolved = MultiplayerHoeingAssistant.ViewModels.MainViewModel.TryResolveBgiUserConfigRoot(
            "\t" + exePath + "\r\n");

        Assert.Null(resolved);
    }

    /// <summary>
    /// 生产启动与演练隔离必须消费同一 `BgiPath` 配置来源；这是静态一致性守卫，
    /// 不放宽真实工作目录/链接差异仍归 R5.8 环境门禁的限定。
    /// </summary>
    [Fact]
    public void ProductionResolver_UsesSameConfiguredBgiPathAsProcessMonitor()
    {
        var root = RepoRoot();
        var startup = File.ReadAllText(Path.Combine(root, "MultiplayerHoeingAssistant", "ViewModels", "MainViewModel.cs"));
        var external = File.ReadAllText(Path.Combine(root, "MultiplayerHoeingAssistant", "ViewModels", "MainViewModel.BgiExternal.cs"));

        Assert.Contains("_processMonitor = new BgiProcessMonitor(_config.BgiPath", startup, StringComparison.Ordinal);
        Assert.Contains("host.UserConfigRootProvider ??= ResolveBgiUserConfigRoot;", external, StringComparison.Ordinal);
        Assert.Contains("ResolveBgiUserConfigRootCore(Config?.BgiPath,", external, StringComparison.Ordinal);
        Assert.Contains("MatchesRunningBgiImagePaths(configuredBgiPath, runningImagePaths)", external, StringComparison.Ordinal);
    }

    /// <summary>运行实例同一性核心行为：匹配成功、无实例按配置目标、多实例/映像读取失败/来源抛错拒绝。</summary>
    [Fact]
    public void ProductionResolverRunningImageIdentity_Matrix()
    {
        var bgiDir = Path.Combine(_root, "bgi");
        var otherDir = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(bgiDir, "User"));
        Directory.CreateDirectory(otherDir);
        var exePath = Path.Combine(bgiDir, "BetterGenshinImpact.exe");
        var otherExe = Path.Combine(otherDir, "BetterGenshinImpact.exe");
        File.WriteAllText(exePath, "fake");
        File.WriteAllText(otherExe, "fake");

        Assert.NotNull(MainViewModel.ResolveBgiUserConfigRootCore(exePath, []));
        Assert.NotNull(MainViewModel.ResolveBgiUserConfigRootCore(exePath, [exePath]));
        Assert.Null(MainViewModel.ResolveBgiUserConfigRootCore(exePath, [otherExe]));
        Assert.Null(MainViewModel.ResolveBgiUserConfigRootCore(exePath, [exePath, exePath]));
        Assert.Null(MainViewModel.ResolveBgiUserConfigRootCore(exePath, [null]));
        Assert.Null(MainViewModel.ResolveBgiUserConfigRootCore(exePath, [exePath], enumerationComplete: false));
        Assert.Null(MainViewModel.ResolveBgiUserConfigRootCore(exePath, [], enumerationComplete: false));
        Assert.Null(MainViewModel.ResolveBgiUserConfigRootCore(exePath, [exePath, null], enumerationComplete: false));
        Assert.Null(MainViewModel.ResolveBgiUserConfigRootCore(exePath,
            () => throw new InvalidOperationException("枚举失败")));
    }

    /// <summary>路径身份解析拒绝 UNC/不可比较命名空间，且不把访问失败当作不存在的尾段。</summary>
    [Fact]
    public void PathIdentity_UnprovenNamespaces_AreRejected()
    {
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            @"\\server\share\User", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\User", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            "relative\\path", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            @"C:relative", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            @"C:\dir\file.json:stream", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            @"C:\probe\NUL", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            @"C:\probe\CON.txt", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            @"C:\probe\COM1", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            @"C:\probe\COM¹.txt", out _));
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryNormalizeLocalDriveAbsolute(
            @"C:\probe\LPT²", out _));
        var fileAsDirectory = Path.Combine(_root, "file-as-directory", "child");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "file-as-directory"), "file");
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            fileAsDirectory, out _));

        var extendedLocal = @"\\?\" + Path.GetFullPath(_root);
        Assert.True(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            extendedLocal, out var canonical));
        Assert.Equal(Path.GetFullPath(_root), canonical);
    }

    /// <summary>
    /// 严格进程枚举核心：完整空枚举成功；枚举/当前会话读取失败、匹配后另一进程会话读取失败均拒绝；
    /// 已退出进程可安全忽略。替换为宽容版或吞掉异常会触发本夹具失败。
    /// </summary>
    [Fact]
    public void StrictProcessEnumeration_CoreMatrix()
    {
        Assert.True(MultiplayerHoeingAssistant.Services.BgiProcessMonitor
            .TryGetCurrentSessionBgiProcessesStrictCore(
                () => [], () => 7, _ => 7, _ => false, out var none, out _));
        Assert.Empty(none);

        Assert.False(MultiplayerHoeingAssistant.Services.BgiProcessMonitor
            .TryGetCurrentSessionBgiProcessesStrictCore(
                () => throw new InvalidOperationException("枚举失败"), () => 7, _ => 7, _ => false,
                out _, out var enumFailure));
        Assert.Equal("InvalidOperationException", enumFailure);

        var order = new List<string>();
        Assert.False(MultiplayerHoeingAssistant.Services.BgiProcessMonitor
            .TryGetCurrentSessionBgiProcessesStrictCore(
                () => { order.Add("enumerate"); return []; },
                () => { order.Add("session"); throw new InvalidOperationException("当前会话读取失败"); },
                _ => 7, _ => false,
                out _, out var sessionFailure));
        Assert.Equal("InvalidOperationException", sessionFailure);
        Assert.Equal(["session"], order); // 当前会话读取失败时不得先枚举并遗留数组

        var mixedMatched = new System.Diagnostics.Process();
        var otherSession = new System.Diagnostics.Process();
        var exited = new System.Diagnostics.Process();
        var disposedOnMixedSuccess = new List<System.Diagnostics.Process>();
        Assert.True(MultiplayerHoeingAssistant.Services.BgiProcessMonitor
            .TryGetCurrentSessionBgiProcessesStrictCore(
                () => [mixedMatched, otherSession, exited],
                () => 7,
                process => ReferenceEquals(process, mixedMatched)
                    ? 7
                    : ReferenceEquals(process, otherSession)
                        ? 8
                        : throw new InvalidOperationException("已退出"),
                process => ReferenceEquals(process, exited),
                out var mixed, out _,
                disposeProcess: disposedOnMixedSuccess.Add));
        Assert.Equal([mixedMatched], mixed); // 匹配项移交调用方
        Assert.Equal([otherSession, exited], disposedOnMixedSuccess); // 其他会话与已退出项释放

        var failureMatched = new System.Diagnostics.Process();
        var unknown = new System.Diagnostics.Process();
        var failureTail = new System.Diagnostics.Process();
        var disposedOnFailure = new List<System.Diagnostics.Process>();
        Assert.False(MultiplayerHoeingAssistant.Services.BgiProcessMonitor
            .TryGetCurrentSessionBgiProcessesStrictCore(
                () => [failureMatched, unknown, failureTail],
                () => 7,
                process => ReferenceEquals(process, failureMatched)
                    ? 7
                    : throw new InvalidOperationException("会话身份未知"),
                _ => false,
                out _, out var partialUnknown,
                disposeProcess: disposedOnFailure.Add));
        Assert.Equal("InvalidOperationException", partialUnknown);
        Assert.Equal([failureMatched, unknown, failureTail], disposedOnFailure); // 失败点之后元素也释放且恰好一次

        var exitedOnly = new System.Diagnostics.Process();
        var disposedExited = new List<System.Diagnostics.Process>();
        Assert.True(MultiplayerHoeingAssistant.Services.BgiProcessMonitor
            .TryGetCurrentSessionBgiProcessesStrictCore(
                () => [exitedOnly],
                () => 7,
                _ => throw new InvalidOperationException("已退出"),
                _ => true,
                out var ignoredExited, out _,
                disposeProcess: disposedExited.Add));
        Assert.Empty(ignoredExited);
        Assert.Equal([exitedOnly], disposedExited);
    }

    /// <summary>注入探测委托：访问拒绝/句柄打开失败/最终路径读取失败均立即拒绝，不回退父目录。</summary>
    [Fact]
    public void PathIdentity_InjectedFailures_AreRejected()
    {
        var target = Path.Combine(_root, "probe", "child");
        var accessTrace = new List<string>();
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            target,
            path =>
            {
                accessTrace.Add("attributes:" + path);
                throw new UnauthorizedAccessException("拒绝访问");
            },
            path => { accessTrace.Add("open:" + path); return new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false); },
            _ => { accessTrace.Add("final"); return null; },
            out _));
        Assert.Equal(["attributes:" + target], accessTrace); // 目标访问失败立即拒绝，不回退父目录

        var invalidHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false);
        var handleFailureTrace = new List<string>();
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            target,
            path => { handleFailureTrace.Add("attributes:" + path); return FileAttributes.Directory; },
            path =>
            {
                handleFailureTrace.Add("open:" + path);
                return invalidHandle;
            },
            _ =>
            {
                handleFailureTrace.Add("final");
                return null;
            },
            out _));
        Assert.Equal(["attributes:" + target, "open:" + target], handleFailureTrace);
        Assert.True(invalidHandle.IsClosed);

        var validHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(1), false);
        var finalFailureTrace = new List<string>();
        Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
            target,
            path => { finalFailureTrace.Add("attributes:" + path); return FileAttributes.Directory; },
            path => { finalFailureTrace.Add("open:" + path); return validHandle; },
            handle =>
            {
                finalFailureTrace.Add(ReferenceEquals(handle, validHandle) ? "final:same-handle" : "final:other-handle");
                return null;
            },
            out _));
        Assert.Equal(["attributes:" + target, "open:" + target, "final:same-handle"], finalFailureTrace);
        Assert.True(validHandle.IsClosed);

        var illegalInputs = new List<string>
        {
            "relative\\path",
            "C:relative",
            @"\\server\share\User",
            @"\\?\UNC\server\share\User",
            @"\\?\Volume{00000000-0000-0000-0000-000000000000}\User",
            @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1",
            @"C:\probe\file.json:stream",
            "bad\0path",
            @"C:\probe\bad" + "\0" + "name",
            @"C:\probe\bad" + "\t" + "name",
            @"C:\probe\bad?name",
        };
        var reservedNames = new List<string> { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };
        for (var i = 1; i <= 9; i++)
        {
            reservedNames.Add("COM" + i);
            reservedNames.Add("LPT" + i);
        }
        reservedNames.AddRange(["COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"]);
        foreach (var name in reservedNames)
        {
            illegalInputs.Add(@"C:\probe\" + name);
            illegalInputs.Add(@"C:\probe\" + name + ".txt");
        }

        foreach (var illegal in illegalInputs)
        {
            var illegalProbeCalls = 0;
            Assert.False(MultiplayerHoeingAssistant.Services.PathIdentity.TryCanonicalizeForComparison(
                illegal,
                _ => { illegalProbeCalls++; return FileAttributes.Directory; },
                _ => { illegalProbeCalls++; return new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, false); },
                _ => { illegalProbeCalls++; return null; },
                out _));
            Assert.Equal(0, illegalProbeCalls); // 原始形态非法时不得触碰文件系统
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未能定位仓库根目录（生产来源一致性守卫需要源码路径）。");
    }
}
