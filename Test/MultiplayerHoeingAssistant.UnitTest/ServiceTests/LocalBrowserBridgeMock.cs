using MultiplayerHoeingAssistant.Services;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;

LocalBrowserHandoff.Identity HandoffIdentity(bool executor) => new("mock-owner", "模拟本机", "mock-room",
    Environment.GetEnvironmentVariable("DODOCO_TEST_INSTANCE") ?? "mock-instance", "mock-only-secret", executor, Process.GetCurrentProcess().SessionId);
if (args.Contains("--handoff-client"))
{
    var identity = HandoffIdentity(false);
    var targets = await LocalBrowserHandoff.FindExecutorsAsync(identity);
    if (args.Contains("--handoff-list")) { Console.WriteLine(targets.Count); return; }
    if (args.FirstOrDefault(arg => arg.StartsWith("--instance=", StringComparison.Ordinal)) is { } selected)
        targets = targets.Where(target => target.InstanceId == selected[11..]).ToArray();
    if (targets.Count != 1)
    {
        Console.Error.WriteLine("HANDOFF_TARGET_COUNT=" + targets.Count);
        Environment.ExitCode = 2;
        return;
    }
    Console.WriteLine(await LocalBrowserHandoff.GetPairingUrlAsync(identity, targets[0]));
    return;
}

// Test-only loopback host. All returned content is synthetic; no user configuration is loaded.
using var image = new Bitmap(16, 12);
using var output = new MemoryStream();
image.Save(output, ImageFormat.Jpeg);
var jpeg = Convert.ToBase64String(output.ToArray());
using var bridge = new LocalBrowserBridge((request, _) => Task.FromResult(request.Operation switch
{
    "local.identity" => new LocalBrowserResult("ok", Data: new { uid = "mock-owner", name = "模拟本机",
        clientInstanceId = "mock-instance", machineId = "mock-machine", serverUrl = Environment.GetEnvironmentVariable("DODOCO_TEST_SERVER"),
        roomCode = Environment.GetEnvironmentVariable("DODOCO_TEST_ROOM") ?? "mock-room", status = new { taskRunning = false, taskStatusAvailable = true } }),
    "log.poll" => new LocalBrowserResult("ok", Data: new { cursor = 1, lines = request.Args["cursor"] is null ? new[] { "[10:00:00.000] [INF] [Main] 模拟本机日志" } : Array.Empty<string>(), dropped = 0, sourceAvailable = true }),
    "log.files" => new LocalBrowserResult("ok", Data: new { files = Array.Empty<object>() }),
    "screenshot.capture" => new LocalBrowserResult("ok", Data: new { uid = "mock-owner", jpegBase64 = jpeg, width = 16, height = 12, capturedAt = DateTime.UtcNow }),
    "watch.read" => new LocalBrowserResult("ok", Data: new { rules = Array.Empty<object>(), dates = Array.Empty<string>(),
        settings = new { muteAll = false, incidentSnapshotEnabled = false, incidentBufferSeconds = 10, incidentCaptureIntervalSeconds = 1,
            incidentPreSeconds = 3, incidentPostSeconds = 3, incidentRetentionCount = 50, stallThresholdMinutes = 3 } }),
    "report.dates" => new LocalBrowserResult("ok", Data: new { dates = Array.Empty<string>() }),
    _ => new LocalBrowserResult("error", "模拟端不支持此操作")
}));
using var handoff = new LocalBrowserHandoff(() => HandoffIdentity(true), _ => bridge.CreatePairingUrl(), Console.Error.WriteLine);
Console.WriteLine(args.Contains("--handoff-executor") ? "READY" : bridge.CreatePairingUrl());
Console.Out.Flush();
Console.ReadLine();
