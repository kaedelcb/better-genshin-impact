using System.Reflection;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Preservation Property Test - Property 2: 联机模式行为不变
///
/// **Validates: Requirements 3.1, 3.2, 3.3**
///
/// 通过静态代码审查验证联机模式相关代码结构（房主/成员分支、路线上传、等待逻辑）
/// 在修复前后保持不变。
///
/// 此测试在未修复代码上预期 PASS（确认联机模式基线行为）。
/// 修复后同样应 PASS（确认无回归）。
/// </summary>
public class SingleModeNullRefPreservationTest
{
    private static readonly string? RepoRoot = FindRepoRoot(AppContext.BaseDirectory);
    private static readonly string FilePath = Path.Combine(
        RepoRoot ?? "", "BetterGenshinImpact", "GameTask", "AutoHoeing", "AutoHoeingTask.cs");

    private static string[]? _lines;

    private static string[] GetLines()
    {
        if (_lines == null)
        {
            Assert.True(RepoRoot != null, "无法找到仓库根目录（BetterGenshinImpact.sln 所在目录）");
            Assert.True(File.Exists(FilePath), $"找不到源文件：{FilePath}");
            _lines = File.ReadAllLines(FilePath);
        }
        return _lines;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3.1 联机路线同步逻辑结构完整性
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证 ProcessRoutesByGroup 方法中存在联机模式条件块（步骤1注释）
    ///
    /// 无论 if 语句是否被注释，步骤1的注释文字和代码块本身应始终存在于源文件中。
    /// 这确认联机同步逻辑代码结构未被删除或重构。
    ///
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Fact]
    public void Preservation_MultiplayerSyncBlock_CommentAndCodeExist()
    {
        var lines = GetLines();
        var content = string.Join("\n", lines);

        // 步骤1注释应存在（无论是否与 if 同行）
        Assert.Contains("步骤1：联机模式路线列表同步", content);

        // 联机同步块的核心条件表达式应存在于文件中
        Assert.Contains("_config.MultiplayerEnabled && _coordinatorClientRef != null", content);
    }

    /// <summary>
    /// 验证房主身份判断逻辑（UID 匹配 + MultiplayerRole 兜底）结构完整
    ///
    /// 房主判断逻辑：优先使用 UID 匹配，仅在 PlayerUid 为空时使用 MultiplayerRole 兜底。
    /// 此结构应在修复前后保持不变。
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Fact]
    public void Preservation_HostRoleDetermination_UidMatchWithFallback()
    {
        var lines = GetLines();
        var content = string.Join("\n", lines);

        // UID 匹配逻辑：_coordinatorClientRef.HostPlayerUid == _config.PlayerUid
        Assert.Contains("_coordinatorClientRef.HostPlayerUid == _config.PlayerUid", content);

        // MultiplayerRole 兜底逻辑
        Assert.Contains("_config.MultiplayerRole == \"host\"", content);

        // PlayerUid 非空检查（决定使用哪种判断方式）
        Assert.Contains("string.IsNullOrEmpty(_config.PlayerUid)", content);
    }

    /// <summary>
    /// 验证房主分支：CD 过滤后上传路线列表（SetHostRouteListAsync）逻辑存在
    ///
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Fact]
    public void Preservation_HostBranch_SetHostRouteListAsyncExists()
    {
        var lines = GetLines();
        var content = string.Join("\n", lines);

        // 房主上传路线列表
        Assert.Contains("SetHostRouteListAsync", content);

        // 房主分支注释标识
        Assert.Contains("房主：CD 过滤后上传最终路线文件名列表", content);
    }

    /// <summary>
    /// 验证成员分支：等待房主路线列表就绪事件（HostRouteListReady）逻辑存在
    ///
    /// **Validates: Requirements 3.1**
    /// </summary>
    [Fact]
    public void Preservation_MemberBranch_HostRouteListReadyEventExists()
    {
        var lines = GetLines();
        var content = string.Join("\n", lines);

        // 成员等待事件
        Assert.Contains("HostRouteListReady", content);

        // 成员直接拉取兜底（multiplayer-member-skip-round-stuck-roundend-sync-fix：
        // 原 GetHostRouteListAsync + IsHostRouteListUploadedAsync 两次独立查询已合并为
        // 一次原子 GetHostRouteListStatusAsync，消除 TOCTOU）
        Assert.Contains("GetHostRouteListStatusAsync", content);

        // 成员分支注释标识
        Assert.Contains("成员：等待房主路线列表就绪事件", content);
    }

    [Theory]
    [InlineData(true, true, false, 1)]
    [InlineData(false, true, false, 2)]
    [InlineData(false, false, false, 2)]
    [InlineData(false, true, true, 2)]
    public async Task MemberRouteWaitDistinguishesEmptyUploadFromTimeout(
        bool initiallyUploaded, bool finallyUploaded, bool lateNonEmpty, int expectedReads)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var reads = new List<GatewayEnvelope>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            reads.Add(request);
            var uploaded = reads.Count == 1 ? initiallyUploaded : finallyUploaded;
            var names = reads.Count == 2 && lateNonEmpty ? new[] { "late-route.json" } : Array.Empty<string>();
            return Task.FromResult(new GatewayEnvelope
            {
                Name = request.Name,
                Type = GatewayProtocol.MessageTypes.Response,
                Payload = GatewayEnvelope.ToPayload(new { value = new { uploaded, routeNames = names } })
            });
        };

        var task = new AutoHoeingTask();
        if (!finallyUploaded)
        {
            var error = await Assert.ThrowsAsync<HoeingStartupException>(
                () => task.ReadMemberHostRoutesAsync(client, TimeSpan.Zero));
            Assert.Equal("房主路线列表", error.Stage);
        }
        else
        {
            var routes = await task.ReadMemberHostRoutesAsync(client, TimeSpan.Zero);
            if (lateNonEmpty)
                Assert.Equal("late-route.json", Assert.Single(routes));
            else
                Assert.Empty(routes);
        }

        Assert.Equal(expectedReads, reads.Count);
        Assert.All(reads, request =>
        {
            Assert.Equal(GatewayProtocol.Names.RoomGetState, request.Name);
            Assert.Equal(GatewayProtocol.StateSections.HostRouteListStatus, request.GetString("section"));
        });
    }

    [Fact]
    public async Task ConfirmedEmptyHostUploadMarksTheCooperativeRoundEmpty()
    {
        await using var client = new CoordinatorClient
        {
            _testIsConnectedOverride = true,
            HostPlayerUid = "room-host"
        };
        var reads = new List<GatewayEnvelope>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            reads.Add(request);
            return Task.FromResult(new GatewayEnvelope
            {
                Name = request.Name,
                Type = GatewayProtocol.MessageTypes.Response,
                Payload = GatewayEnvelope.ToPayload(new
                {
                    value = new { uploaded = true, routeNames = Array.Empty<string>() }
                })
            });
        };
        var task = new AutoHoeingTask();
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(AutoHoeingTask);
        type.GetField("_config", privateInstance)!.SetValue(task,
            new AutoHoeingConfig { MultiplayerEnabled = true, PlayerUid = "room-member" });
        type.GetField("_coordinatorClientRef", privateInstance)!.SetValue(task, client);
        var scopeType = type.GetNestedType("CooperativeTaskScope", BindingFlags.NonPublic)!;
        var scope = Activator.CreateInstance(scopeType, nonPublic: true)!;
        var entry = type.GetMethod("ProcessRoutesByGroupCore", privateInstance)!;
        var route = new RouteInfo { FileName = "local-only.json", Selected = true, Group = 1 };
        var routes = new List<RouteInfo> { route };

        await Assert.IsAssignableFrom<Task>(entry.Invoke(task, [routes, "test", null, scope]));

        Assert.True((bool)scopeType.GetProperty("EmptyRound")!.GetValue(scope)!);
        var request = Assert.Single(reads);
        Assert.Equal(GatewayProtocol.Names.RoomGetState, request.Name);
        Assert.Equal(GatewayProtocol.StateSections.HostRouteListStatus, request.GetString("section"));
        var handler = typeof(CoordinatorClient).GetField(nameof(CoordinatorClient.HostRouteListReady), privateInstance)!;
        Assert.Null(handler.GetValue(client));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3.2 房主/成员角色判断结构完整性（行级验证）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证 isHost 变量赋值行存在，且使用三元表达式（UID 优先 + 兜底）
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Fact]
    public void Preservation_IsHostAssignment_TernaryExpressionStructure()
    {
        var lines = GetLines();

        // 找到 isHost 赋值行
        var isHostLine = lines
            .Select((line, idx) => (line, idx))
            .FirstOrDefault(t => t.line.Contains("bool isHost =") && t.line.Contains("IsNullOrEmpty"));

        Assert.True(isHostLine.line != null,
            "未找到 'bool isHost = !string.IsNullOrEmpty(...)' 赋值行，房主判断逻辑可能被修改");

        // 验证三元表达式结构：下一行应包含 HostPlayerUid == _config.PlayerUid
        var nextLine = lines[isHostLine.idx + 1];
        Assert.Contains("HostPlayerUid == _config.PlayerUid", nextLine);

        // 再下一行应包含 MultiplayerRole 兜底
        var fallbackLine = lines[isHostLine.idx + 2];
        Assert.Contains("MultiplayerRole", fallbackLine);
    }

    /// <summary>
    /// 验证 isHost 条件判断行（含 HostPlayerUid 空检查兜底）存在
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Fact]
    public void Preservation_IsHostCondition_WithEmptyHostUidFallback()
    {
        var lines = GetLines();
        var content = string.Join("\n", lines);

        // if (isHost || string.IsNullOrEmpty(_coordinatorClientRef.HostPlayerUid))
        Assert.Contains("if (isHost || string.IsNullOrEmpty(_coordinatorClientRef.HostPlayerUid))", content);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3.3 单机模式路线过滤逻辑结构完整性
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 验证 ProcessRoutesByGroup 中按组过滤路线的逻辑存在
    ///
    /// **Validates: Requirements 3.3**
    /// </summary>
    [Fact]
    public void Preservation_SingleMode_GroupFilterLogicExists()
    {
        var lines = GetLines();
        var content = string.Join("\n", lines);

        // 按组过滤：r.Group == targetGroup && r.Selected
        Assert.Contains("r.Group == targetGroup && r.Selected", content);

        // GroupIndex 配置项引用
        Assert.Contains("_config.GroupIndex", content);
    }

    [Theory]
    [InlineData(false, 0, true, true)]
    [InlineData(false, 0, false, false)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 0, true, false)]
    public void LocalCooldownOnlySkipsUnforcedSoloRoutes(
        bool multiplayer, int startIndex, bool onCooldown, bool expectedSkip)
    {
        var task = new AutoHoeingTask();
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AutoHoeingTask).GetField("_config", privateInstance)!.SetValue(task,
            new AutoHoeingConfig { MultiplayerEnabled = multiplayer, StartRouteIndex = startIndex });
        var route = new RouteInfo { FileName = "route.json", NormalMonsterCount = 1 };
        if (onCooldown)
        {
            var cdManager = (CdManager)typeof(AutoHoeingTask).GetField("_cdManager", privateInstance)!.GetValue(task)!;
            cdManager.RecordCompletion(route, duration: 60);
        }

        Assert.Equal(expectedSkip, task.ShouldSkipRouteOnLocalCooldown(route));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 辅助方法
    // ─────────────────────────────────────────────────────────────────────────

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BetterGenshinImpact.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
