using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 真实控件验证三类目标的同一选择合同：有效选择保存稳定身份，失效选择不能退回从头。
/// 纯目录单测无法证明 SelectionChanged/TextChanged 与异步目录的组合行为。
/// </summary>
public sealed class CompletionActionPanelTests
{
    [Fact]
    public Task ActualControlsSelectBeginningOrEnabledNodeForAllThreeTargetTypes() => OnSta(async () =>
    {
        foreach (var type in new[] { "group", "onedragon", "workflow" })
        {
            var initial = Settings(type, "node-c", "rev-1");
            var calls = 0;
            var editor = new CompletionActionPanel(initial, ["收尾"], ["收尾"], "完成后动作测试",
                (kind, name, id) =>
                {
                    calls++;
                    return Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id));
                });
            await editor.CatalogLoadTask;
            var window = new Window { Content = editor.Root, Width = 460, Height = 700,
                ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(editor.StartModeCombo.IsVisible);
                Assert.Equal(1, calls); // 模板回填不应被识别为目标变更或另发目录请求。
                Assert.Equal(2, editor.NodeCombo.Items.Count); // 禁用节点不出现在可选起点中。
                Assert.Equal(1, editor.NodeCombo.SelectedIndex);
                Assert.Equal(1, editor.StartModeCombo.SelectedIndex);
                Assert.True(editor.TryGetChanges(out var unchanged, out var unchangedError), unchangedError);
                Assert.Null(unchanged);
                Assert.True(editor.TryGetSettings(out var selected, out var error), error);
                Assert.Equal("node", selected.SpecifiedStartMode);
                Assert.Equal("node-c", selected.SpecifiedStartTaskId);
                Assert.Equal("rev-1", selected.SpecifiedConfigRevision);
                Assert.Equal(type == "workflow" ? "wf-finish" : "", selected.SpecifiedWorkflowId);
                editor.StartModeCombo.SelectedIndex = 0;
                Assert.True(editor.TryGetSettings(out selected, out error), error);
                Assert.Equal("beginning", selected.SpecifiedStartMode);
                Assert.Empty(selected.SpecifiedStartTaskId);
                Assert.Empty(selected.SpecifiedStartTaskName);
                editor.StartModeCombo.SelectedIndex = 1;
                Assert.False(editor.TryGetSettings(out _, out _));
                editor.NodeCombo.SelectedIndex = 0;
                Assert.True(editor.TryGetSettings(out selected, out error), error);
                Assert.Equal("node-a", selected.SpecifiedStartTaskId);
                Assert.Equal("准备", selected.SpecifiedStartTaskName);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public Task DeletedDisabledOrChangedSavedNodeIsRejectedWithoutFallingBack() => OnSta(async () =>
    {
        foreach (var type in new[] { "group", "onedragon", "workflow" })
        foreach (var (nodeId, revision) in new[] { ("deleted", "rev-1"), ("node-disabled", "rev-1"), ("node-c", "old-revision") })
        {
            var editor = new CompletionActionPanel(Settings(type, nodeId, revision), ["收尾"], ["收尾"], "",
                (kind, name, id) => Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id)));
            await editor.CatalogLoadTask;
            var window = Show(editor);
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, editor.StartModeCombo.SelectedIndex);
                Assert.False(editor.TryGetSettings(out var selected, out var error));
                Assert.NotNull(error);
                Assert.Equal("node", selected.SpecifiedStartMode);
                Assert.Equal(nodeId, selected.SpecifiedStartTaskId);
                Assert.Equal(revision, selected.SpecifiedConfigRevision);
                Assert.True(editor.TryGetChanges(out var unchanged, out _));
                Assert.Null(unchanged); // 普通绑定不应触发失效但未编辑的完成动作写入。
                editor.StartModeCombo.SelectedIndex = 0;
                Assert.True(editor.TryGetSettings(out selected, out error), error);
                Assert.Empty(selected.SpecifiedStartTaskId);
                Assert.Equal("rev-1", selected.SpecifiedConfigRevision);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public Task TypeAndTargetChangesClearOldNodeAndIgnoreLateCatalogs() => OnSta(async () =>
    {
        var oldReply = new TaskCompletionSource<CompletionTargetCatalog?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new CompletionActionPanel(Settings("group", "node-c", "rev-1"), ["收尾", "新目标"], ["一条龙"], "",
            (type, name, id) => type == "group" && name == "收尾" ? oldReply.Task
                : Task.FromResult<CompletionTargetCatalog?>(Catalog(type, name, id, "new-revision")));
        var oldLoad = editor.CatalogLoadTask;
        var window = Show(editor);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Same(oldLoad, editor.CatalogLoadTask);
            editor.NameCombo.SelectedItem = "新目标";
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(oldLoad.IsCompleted); // 新目标必须不等待旧目标的未完成请求。
            Assert.True(editor.TryGetSettings(out var selected, out var error), error);
            Assert.Equal("新目标", selected.SpecifiedTaskName);
            Assert.Equal("beginning", selected.SpecifiedStartMode);
            Assert.Empty(selected.SpecifiedStartTaskId);
            oldReply.SetResult(Catalog("group", "收尾", "", "stale-revision"));
            await oldLoad.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(editor.TryGetSettings(out selected, out error), error);
            Assert.Equal("new-revision", selected.SpecifiedConfigRevision);
            editor.StartModeCombo.SelectedIndex = 1;
            editor.NodeCombo.SelectedIndex = 0;
            editor.TypeCombo.SelectedIndex = 1;
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(editor.TryGetSettings(out selected, out error), error);
            Assert.Equal("onedragon", selected.SpecifiedTaskType);
            Assert.Equal("一条龙", selected.SpecifiedTaskName);
            Assert.Empty(selected.SpecifiedStartTaskId);
            Assert.Empty(selected.SpecifiedWorkflowId);
            editor.TypeCombo.SelectedIndex = 2;
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(editor.TryGetSettings(out selected, out error), error);
            Assert.Equal("workflow", selected.SpecifiedTaskType);
            Assert.Equal("wf-finish", selected.SpecifiedWorkflowId);
            Assert.Empty(selected.SpecifiedStartTaskId);
        }
        finally
        {
            oldReply.TrySetResult(Catalog("group", "收尾", ""));
            window.Close();
        }
    });

    [Fact]
    public Task SameEditableTargetTextAndSelectionKeepSavedNodeAfterShow() => OnSta(async () =>
    {
        foreach (var type in new[] { "group", "onedragon" })
        {
            var calls = 0;
            var editor = new CompletionActionPanel(Settings(type, "node-c", "rev-1"), ["收尾"], ["收尾"], "",
                (kind, name, id) =>
                {
                    calls++;
                    return Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id));
                });
            await editor.CatalogLoadTask;
            var load = editor.CatalogLoadTask;
            var window = Show(editor);
            try
            {
                editor.NameCombo.SelectedItem = "收尾";
                editor.NameCombo.Text = " 收尾 ";
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert.Same(load, editor.CatalogLoadTask);
                Assert.Equal(1, calls);
                Assert.Equal(2, editor.NodeCombo.Items.Count);
                Assert.True(editor.TryGetSettings(out var selected, out var error), error);
                Assert.Equal("node", selected.SpecifiedStartMode);
                Assert.Equal("node-c", selected.SpecifiedStartTaskId);
                Assert.Equal("rev-1", selected.SpecifiedConfigRevision);
                Assert.True(editor.TryGetChanges(out var unchanged, out error), error);
                Assert.Null(unchanged);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public Task EditableTextTargetChangeClearsSavedNodeAfterShow() => OnSta(async () =>
    {
        foreach (var type in new[] { "group", "onedragon" })
        {
            var editor = new CompletionActionPanel(Settings(type, "node-c", "rev-1"), ["收尾"], ["收尾"], "",
                (kind, name, id) => Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id,
                    name == "手输目标" ? "new-revision" : "rev-1")));
            await editor.CatalogLoadTask;
            var window = Show(editor);
            try
            {
                var textBox = Assert.IsType<TextBox>(editor.NameCombo.Template.FindName("PART_EditableTextBox", editor.NameCombo));
                textBox.Text = " 手输目标 ";
                await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(editor.TryGetSettings(out var selected, out var error), error);
                Assert.Equal("手输目标", selected.SpecifiedTaskName);
                Assert.Equal("beginning", selected.SpecifiedStartMode);
                Assert.Empty(selected.SpecifiedStartTaskId);
                Assert.Equal("new-revision", selected.SpecifiedConfigRevision);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public Task ResumeOrStopEditsPreserveHiddenLegacyStartFields() => OnSta(async () =>
    {
        foreach (var type in new[] { "group", "workflow" })
        {
            var settings = Settings(type, "saved-node", "saved-revision");
            settings.Policy = TaskConflictPolicy.Resume;
            settings.SpecifiedStartMode = "default";
            settings.SpecifiedTaskPriority = 17;
            var editor = new CompletionActionPanel(settings, ["收尾"], [], "",
                (kind, name, id) => Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id)));
            var window = Show(editor);
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                Assert.True(editor.TryGetChanges(out var unchanged, out var error), error);
                Assert.Null(unchanged);
                editor.PolicyCombo.SelectedIndex = 1; // Action 默认是停止；指定目标控件隐藏。
                Assert.False(editor.StartModeCombo.IsVisible);
                Assert.True(editor.TryGetChanges(out var changed, out error), error);
                Assert.NotNull(changed);
                Assert.Equal(TaskConflictPolicy.Stop, changed.Policy);
                foreach (var pair in CompletionPolicyFields.Parameters(settings).Where(p => p.Key != "policy"))
                    Assert.Equal(pair.Value, CompletionPolicyFields.Parameters(changed)[pair.Key]);
            }
            finally { window.Close(); }
        }
    });

    [Fact]
    public Task MismatchedCatalogCannotPopulateAnotherTargetsNodes() => OnSta(async () =>
    {
        var editor = new CompletionActionPanel(Settings("group", "node-c", "rev-1"), ["收尾"], [], "",
            (_, _, _) => Task.FromResult<CompletionTargetCatalog?>(Catalog("group", "另一目标", "")));
        await editor.CatalogLoadTask;
        var window = Show(editor);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.Empty(editor.NodeCombo.Items);
            Assert.False(editor.TryGetSettings(out var selected, out _));
            Assert.Equal("node", selected.SpecifiedStartMode);
            Assert.Equal("node-c", selected.SpecifiedStartTaskId);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task LegacyInitialValueRequiresExplicitStartAndOldPeerCannotSave() => OnSta(async () =>
    {
        var settings = Settings("group", "", "");
        settings.SpecifiedStartMode = "default";
        var editor = new CompletionActionPanel(settings, ["收尾"], [], "",
            (type, name, id) => Task.FromResult<CompletionTargetCatalog?>(Catalog(type, name, id)));
        await editor.CatalogLoadTask;
        Assert.False(editor.TryGetSettings(out _, out _));
        editor.StartModeCombo.SelectedIndex = 0;
        Assert.True(editor.TryGetSettings(out var selected, out var error), error);
        Assert.Equal("beginning", selected.SpecifiedStartMode);
        var called = false;
        var unsupported = new CompletionActionPanel(settings, [], [], "", (_, _, _) =>
        {
            called = true;
            return Task.FromResult<CompletionTargetCatalog?>(null);
        }, supported: false);
        var window = Show(unsupported);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(called);
            Assert.False(unsupported.TryGetSettings(out _, out _));
            Assert.True(unsupported.TryGetChanges(out var unchanged, out var unchangedError), unchangedError);
            Assert.Null(unchanged); // 共用保存按钮可以只保存普通绑定，不能向旧端推送新起点。
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task OrdinarySettingsDialogPreservesEveryCompletionStartField() => OnSta(() =>
    {
        var config = new AssistConfig
        {
            ServerUrl = "http://localhost:5000", ControlRoomPassword = "1234", PlayerUid = "self", TeamUids = ["self"]
        };
        var settings = Settings("workflow", "node-c", "rev-1");
        CompletionPolicyFields.Apply(config, settings);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var window = (SettingsWindow)Activator.CreateInstance(typeof(SettingsWindow), flags, null, [config], null)!;
        try
        {
            var saved = (AssistConfig)typeof(SettingsWindow).GetMethod("BuildConfig", flags)!.Invoke(window, null)!;
            var expected = CompletionPolicyFields.Parameters(settings);
            var actual = CompletionPolicyFields.Parameters(CompletionPolicyFields.Snapshot(saved));
            foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    private static Window Show(CompletionActionPanel editor)
    {
        var window = new Window
        {
            Content = editor.Root, Width = 460, Height = 700,
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static TaskConflictPolicySettings Settings(string type, string nodeId, string revision) => new()
    {
        Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = type, SpecifiedTaskName = "收尾",
        SpecifiedStartMode = "node", SpecifiedStartTaskId = nodeId, SpecifiedStartTaskName = "收取",
        SpecifiedConfigRevision = revision, SpecifiedWorkflowId = type == "workflow" ? "wf-finish" : ""
    };

    private static CompletionTargetCatalog Catalog(string type, string name, string id, string revision = "rev-1")
    {
        CompletionTargetNode[] nodes = [new("node-a", "准备", true), new("node-disabled", "已禁用", false), new("node-c", "收取", true)];
        return new([new("wf-finish", "计划收尾", revision, nodes)],
            new(type, type == "workflow" ? "计划收尾" : name, type == "workflow" ? id : "", revision, nodes));
    }

    private static async Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
