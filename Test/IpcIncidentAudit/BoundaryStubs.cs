// Only external boundaries are replaced. TaskRunner/gate/capture/cancellation/registry/decider are linked unchanged.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact
{
    public static class App
    {
        public static IServiceProvider ServiceProvider { get; set; } = new ServiceCollection().BuildServiceProvider();
        public static ILogger<T> GetLogger<T>() => NullLogger<T>.Instance;
    }
}
namespace BetterGenshinImpact.Model
{
    public class Singleton<T> where T : new() { public static T Instance { get; } = new(); }
}
namespace BetterGenshinImpact.Core.Config
{
    public sealed class AllConfig { public SuspendedTaskContext? SuspendedTaskContext { get; set; } }
}
namespace BetterGenshinImpact.GameTask
{
    public sealed class ProjectInfo
    {
        public string? Name { get; set; }
        public string? FolderName { get; set; }
        public int Index { get; set; }
        public string? Type { get; set; }
        public ProjectInfo? GroupInfo { get; set; }
        public object? SoloTaskSettingsObject { get; set; }
    }
    public sealed class Progress
    {
        public string? CurrentScriptGroupName { get; set; }
        public ProjectInfo? CurrentScriptGroupProjectInfo { get; set; }
    }
    public sealed class TaskContext
    {
        private static readonly TaskContext Shared = new();
        public static TaskContext Instance() => Shared;
        public bool IsInitialized { get; set; } = true;
        public Core.Config.AllConfig Config { get; } = new();
        public ProjectInfo? CurrentScriptProject { get; set; }
    }
    public sealed class RunnerContext
    {
        public static RunnerContext Instance { get; } = new();
        public Progress? taskProgress { get; set; }
        public string? SoloTaskName { get; set; }
        public bool IsContinuousRunGroup { get; set; }
        // Production Clear does NOT clear taskProgress. Keep precisely that relevant boundary behavior.
        public void Clear() { }
    }
    public interface ISoloTask { string Name { get; } Task Start(CancellationToken token); }
    public sealed class TaskTriggerDispatcher
    {
        public static TaskTriggerDispatcher Instance() => new();
        public void ClearTriggers() { }
        public void SetTriggers(object triggers) { }
    }
    public static class GameTaskManager { public static object LoadInitialTriggers() => new(); }
}
namespace BetterGenshinImpact.GameTask.Common
{
    public static class TaskControl { public static SemaphoreSlim TaskSemaphore { get; } = new(1, 1); }
}
namespace BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception
{
    public sealed class NormalEndException(string message) : System.Exception(message);
}
namespace BetterGenshinImpact.GameTask.AutoOnline
{
    public static class NotifyOnlineTask { public const string TaskName = "联机锄地上线"; }
}
namespace BetterGenshinImpact.GameTask.AutoTrackPath
{
    public static class TpTaskFastDrag { public static void ResetLastSuccessfulTeleportMap() { } }
}
namespace BetterGenshinImpact.Core.Simulator
{
    public static class Simulation { public static void ReleaseAllKey() { } }
}
namespace BetterGenshinImpact.Helpers
{
    public static class UIDispatcherHelper { public static void Invoke(Action action) => action(); }
    public static class SystemControl { public static void ActivateWindow() { } }
}
namespace Wpf.Ui.Violeta.Controls
{
    public static class Toast { public static void Warning(string message) { } public static void Information(string message) { } }
}
namespace BetterGenshinImpact.View
{
    public sealed class MaskWindow
    {
        public static MaskWindow Instance() => new();
        public static MaskWindow? InstanceNullable() => null;
        public object? DataContext => null;
        public void Invoke(Action action) => action();
        public void Show() { }
    }
    public static class HtmlMaskWindow { public static void CloseAll() { } }
}
namespace BetterGenshinImpact.View.Drawable
{
    public sealed class VisionContext
    {
        public static VisionContext Instance() => new();
        public VisionContext DrawContent => this;
        public void ClearAll() { }
    }
}
namespace BetterGenshinImpact.ViewModel
{
    public sealed class MaskWindowViewModel { public bool IsInBigMapUi { get; set; } }
}
namespace BetterGenshinImpact.ViewModel.Pages
{
    public sealed class OneDragonFlowViewModel
    {
        public GameTask.ProjectInfo? SelectedConfig { get; set; }
        public int CurrentExecutingTaskIndex { get; set; }
    }
}
namespace BetterGenshinImpact.Service
{
    public static class ScriptService { public static Task StartGameTask(bool wait = true) => Task.CompletedTask; }
}
namespace BetterGenshinImpact.Service.Notification.Model.Enum
{
    public enum NotificationEvent { TaskCancel, TaskError }
}
namespace BetterGenshinImpact.Service.Notification
{
    public sealed class Notify
    {
        public static Notify Event(Model.Enum.NotificationEvent kind) => new();
        public void Success(string message) { }
        public void Error(string message, Exception error) { }
    }
}
namespace BetterGenshinImpact.Service.Instance
{
    public sealed class InstanceIpcEnvelope
    {
        public int ProtocolVersion { get; set; }
        public string? Kind { get; set; }
        public Guid RequestId { get; set; }
        public string? Operation { get; set; }
        public bool? Success { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public Newtonsoft.Json.Linq.JToken? Data { get; set; }
    }
}
namespace BetterGenshinImpact.Service.ExternalInterface
{
    public sealed class ExternalInterfaceEventHub
    {
        public static ExternalInterfaceEventHub Instance { get; } = new();
        // Deterministic interleaving at an existing production scheduling boundary.
        public Action? OnSlotReleased { get; set; }
        public void PublishTaskSlotReleased() => OnSlotReleased?.Invoke();
        public void PublishTaskStarted(string? name) { }
        public void PublishTaskStopped(bool cancelled) { }
        public void PublishJobTransition(Execution.BgiJob job) { }
        public void PublishJobHeartbeat(Execution.BgiJob job) { }
    }
}
