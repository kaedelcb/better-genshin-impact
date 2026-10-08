using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>正常用户路径的模型入口检查；真实WPF使用仍由同产物实际入口验证。</summary>
public sealed class TaskCenterUsabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tc-usability-" + Guid.NewGuid().ToString("N"));
    private int _runnerCreations;
    private int _executionCalls;

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private TaskCenterHost Host(bool failReadiness = false) => new(
        Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "catalog.json"),
        () => null, null,
        (_, workflows, runs) =>
        {
            Interlocked.Increment(ref _runnerCreations);
            var boundary = new NoExecution(this);
            return new WorkflowRunner(workflows, runs, boundary, boundary, boundary);
        },
        () => failReadiness ? (false, "BGI未就绪（隔离测试）") : (true, null),
        ensureExecutionReady: failReadiness ? _ => Task.FromResult<string?>("BGI未就绪（隔离测试）") : null);

    private sealed class NoExecution(TaskCenterUsabilityTests owner) : IWorkflowExecutionBoundary,
        IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public bool SingleNativeSupported => true;
        private Exception UnexpectedExecution()
        {
            Interlocked.Increment(ref owner._executionCalls);
            return new InvalidOperationException("本测试不得执行资源、前置或收尾。");
        }
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct) => throw UnexpectedExecution();
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct) => throw UnexpectedExecution();
        public Task RequestCancelAsync(string jobId, CancellationToken ct) => throw UnexpectedExecution();
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => throw UnexpectedExecution();
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct) => throw UnexpectedExecution();
    }

    private static WorkflowDocument Plan(string id = "wf-existing") => new()
    {
        WorkflowId = id,
        Name = "已有计划",
        Activation = new() { Status = "active" },
        Nodes = [new() { NodeId = "end", Kind = "control.end" }],
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossDayChoice_SaveAndReopen_PreservesOtherLoopParameters(bool skip)
    {
        var host = Host();
        var document = Plan();
        document.Loop = new WorkflowLoop
        {
            Mode = "scheduled",
            Params = new()
            {
                ["time"] = JsonSerializer.SerializeToElement("04:00"),
                ["deadline"] = JsonSerializer.SerializeToElement("23:00"),
                ["skipAcrossDays"] = JsonSerializer.SerializeToElement(!skip),
                ["futurePolicy"] = JsonSerializer.SerializeToElement(new { preserve = true, value = 7 }),
            },
        };
        host.SaveFlow(document, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        vm.Editing!.SkipAcrossDays = skip;

        vm.SaveFlowCommand.Execute(null);

        Assert.False(vm.StatusIsError);
        var reopened = new TaskCenterPanelViewModel(Host(), autoRefresh: false);
        Assert.Equal(skip, reopened.Editing!.SkipAcrossDays);
        var loop = host.LoadFlowSnapshot(document.WorkflowId!).Document.Loop!;
        Assert.Equal(skip, loop.GetBool("skipAcrossDays"));
        Assert.Equal("04:00", loop.GetString("time"));
        Assert.Equal("23:00", loop.GetString("deadline"));
        var future = loop.Params["futurePolicy"];
        Assert.Equal(JsonValueKind.Object, future.ValueKind);
        Assert.Equal(new[] { "preserve", "value" }, future.EnumerateObject().Select(p => p.Name).OrderBy(name => name).ToArray());
        Assert.Equal(JsonValueKind.True, future.GetProperty("preserve").ValueKind);
        Assert.True(future.GetProperty("preserve").GetBoolean());
        Assert.Equal(JsonValueKind.Number, future.GetProperty("value").ValueKind);
        Assert.Equal(7, future.GetProperty("value").GetInt32());
        Assert.Empty(host.Runs.List());
    }

    [Fact]
    public void UntouchedCrossDayDefault_RenameDoesNotInventOrDropLoopFields()
    {
        var host = Host();
        var document = Plan();
        document.Loop = new() { Mode = "scheduled", Params = new()
        {
            ["time"] = JsonSerializer.SerializeToElement("04:00"),
            ["futurePolicy"] = JsonSerializer.SerializeToElement(new[] { "保留", "原值" }),
        } };
        host.SaveFlow(document, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        Assert.True(vm.Editing!.SkipAcrossDays);
        vm.Editing.NameText = "只改名称";

        vm.SaveFlowCommand.Execute(null);

        var restored = Host().LoadFlowSnapshot(document.WorkflowId!).Document;
        Assert.Equal("只改名称", restored.Name);
        Assert.False(restored.Loop!.Params.ContainsKey("skipAcrossDays"));
        var future = restored.Loop.Params["futurePolicy"];
        Assert.Equal(JsonValueKind.Array, future.ValueKind);
        Assert.Equal(2, future.GetArrayLength());
        Assert.All(future.EnumerateArray(), item => Assert.Equal(JsonValueKind.String, item.ValueKind));
        Assert.Equal(new[] { "保留", "原值" }, future.EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Theory]
    [InlineData("yes", "branchYes")]
    [InlineData("no", "branchNo")]
    public void ConditionFollowCurrentLane_SelectsActualSuccessorInsteadOfEnding(string side, string result)
    {
        var host = Host();
        var document = Plan();
        document.ExtensionData = new() { ["scheduleLanes"] = JsonSerializer.SerializeToElement(new[] { "主车道", "支线" }) };
        document.Nodes =
        [
            new() { NodeId = "choice", Kind = "control.condition", ExtensionData = new() { ["scheduleLane"] = JsonSerializer.SerializeToElement(1) },
                Path = new() { Yes = "$end", No = "$end", Condition = new() { Kind = "constant", Value = true } } },
            new() { NodeId = "unrelated-main", Kind = "resource.oneDragonConfig", Ref = new() { Config = "主车道资源" } },
            new() { NodeId = "next-in-lane", Kind = "resource.oneDragonConfig", Ref = new() { Config = "支线资源" },
                ExtensionData = new() { ["scheduleLane"] = JsonSerializer.SerializeToElement(1) } },
        ];
        host.SaveFlow(document, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        vm.Editing!.SetTarget(vm.Editing.Nodes[0], null, side);
        var plan = new WorkflowPlan(vm.Editing.BuildSubmissionCopy());
        Assert.True(plan.TryLocate("choice", 0, 0, out var current));

        var next = plan.Next(current, result);

        Assert.NotNull(next);
        Assert.Equal("next-in-lane", next.NodeId);
        Assert.Equal(1, next.PathLane);
    }

    [Fact]
    public void OpenExistingPlans_DefaultsToEditablePlan_WithoutStartingEither()
    {
        var host = Host();
        var candidate = Plan("wf-a-candidate");
        candidate.Activation!.Status = "candidate-ready";
        host.SaveFlow(candidate, null);
        var active = Plan("wf-z-active");
        host.SaveFlow(active, null);

        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);

        Assert.Equal(active.WorkflowId, vm.SelectedWorkflowId);
        Assert.Equal(active.WorkflowId, vm.Editing!.Draft.WorkflowId);
        Assert.True(vm.HasFlows);
        Assert.True(vm.CanAddTasks);
        Assert.Null(vm.Previewing);
        Assert.Empty(host.Runs.List());
        Assert.Equal(0, _runnerCreations);
    }

    [Fact]
    public void EmptyWorkspace_NewPlanGivesTheNextAddTaskStep_WithoutStarting()
    {
        var host = Host();
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        Assert.False(vm.HasFlows);
        Assert.False(vm.CanAddTasks);
        Assert.Contains("先新建", vm.WorkspaceHint);

        vm.NewFlowCommand.Execute(null);

        Assert.True(vm.HasFlows);
        Assert.True(vm.IsEmptyPlan);
        Assert.True(vm.CanAddTasks);
        Assert.Contains("添加任务", vm.WorkspaceHint);
        Assert.Single(vm.Flows);
        Assert.Empty(host.Runs.List());
        Assert.Equal(0, _runnerCreations);
    }

    [Fact]
    public async Task SaveEditing_RealSaveButtonKeepsSelectedWorkspaceAvailableForFurtherChanges()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var host = Host();
                var document = Plan();
                host.SaveFlow(document, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                vm.Editing!.NameText = "保存后继续安排";
                var view = new ScheduleListView { DataContext = vm };
                view.Measure(new Size(1080, 700));
                view.Arrange(new Rect(0, 0, 1080, 700));
                view.UpdateLayout();
                static IEnumerable<DependencyObject> Children(DependencyObject parent)
                {
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    {
                        var child = VisualTreeHelper.GetChild(parent, i);
                        yield return child;
                        foreach (var nested in Children(child)) yield return nested;
                    }
                }
                var save = Children(view).OfType<Button>().Single(button => Equals(button.Content, "保存"));

                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.False(vm.StatusIsError);
                Assert.NotNull(vm.Editing);
                Assert.Equal(document.WorkflowId, vm.Editing.Draft.WorkflowId);
                Assert.Equal("保存后继续安排", vm.Editing.NameText);
                Assert.True(vm.CanAddTasks);
                Assert.Empty(host.Runs.List());
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StartSelectedPlan_SavesDraftFirst_ThenReadinessFailureKeepsChanges()
    {
        var host = Host(failReadiness: true);
        var document = Plan();
        host.SaveFlow(document, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        vm.Editing!.NameText = "用户保存的安排";
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(vm.StatusIsError) && vm.StatusIsError) failed.TrySetResult();
        };
        vm.PropertyChanged += handler;
        try
        {
            vm.StartSelectedPlanCommand.Execute(Assert.Single(vm.Flows));
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("用户保存的安排", host.LoadFlowSnapshot(document.WorkflowId!).Document.Name);
            Assert.Equal("用户保存的安排", vm.Editing!.NameText);
            Assert.False(vm.Editing.HasUnsavedChanges);
            Assert.Empty(host.Runs.List());
            Assert.Equal(0, _executionCalls);
            Assert.Contains("BGI未就绪", vm.StatusMessage);
        }
        finally { vm.PropertyChanged -= handler; await host.ShutdownAsync(); }
    }

    [Fact]
    public void StartSelectedPlan_SaveConflictPreservesDraftAndDoesNotStart()
    {
        var host = Host(failReadiness: true);
        var document = Plan();
        host.SaveFlow(document, null);
        var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var draft = vm.Editing!;
        draft.NameText = "本地未保存修改";
        var newer = host.LoadFlowSnapshot(document.WorkflowId!);
        newer.Document.Name = "另一个写者的新修订";
        host.SaveFlow(newer.Document, newer.Revision);

        vm.StartSelectedPlanCommand.Execute(Assert.Single(vm.Flows));

        Assert.True(vm.StatusIsError);
        Assert.Same(draft, vm.Editing);
        Assert.Equal("本地未保存修改", draft.NameText);
        Assert.Equal("另一个写者的新修订", host.LoadFlowSnapshot(document.WorkflowId!).Document.Name);
        Assert.Empty(host.Runs.List());
        Assert.Equal(0, _runnerCreations);
    }
}
