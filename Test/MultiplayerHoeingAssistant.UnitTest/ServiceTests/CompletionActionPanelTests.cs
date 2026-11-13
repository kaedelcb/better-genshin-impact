using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
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
    // 默认 ComboBox 不会重现生产主题遗漏 SelectionBoxItemTemplate 时的 record 显示问题。
    // 只有当前 App.xaml 的收起文字、弹出项和保存身份同时正确，才可接受本次显示修复。
    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    [InlineData("workflow")]
    public Task ProductionGoldThemeShowsReadableCollapsedChoicesAndPopupLabels(string type) => OnSta(async () =>
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "主题截图必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var resources = ProductionComboResources();
        var calls = 0;
        var editor = new CompletionActionPanel(Settings(type, "node-c", "rev-1"), ["收尾"], ["收尾"], "完成后动作主题测试",
            (kind, name, id) =>
            {
                calls++;
                return Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id));
            }, createCombo: () => ThemedCombo(resources));
        await editor.CatalogLoadTask;
        var window = Show(editor, resources);
        try
        {
            await SettleLayout(window);
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, calls);
            Assert.Contains("3. 收取", VisibleText(editor.NodeCombo));
            Assert.DoesNotContain(VisibleText(editor.NodeCombo), text => text.Contains("NodeChoice"));
            if (type == "workflow")
            {
                Assert.Contains("计划收尾", VisibleText(editor.NameCombo));
                Assert.DoesNotContain(VisibleText(editor.NameCombo), text => text.Contains("WorkflowChoice"));
                await AssertThemedPopup(window, editor.NameCombo, resources, "计划收尾");
                Assert.Contains("计划收尾", VisibleText(editor.NameCombo));
            }
            else
            {
                var textBox = Assert.IsType<TextBox>(editor.NameCombo.Template.FindName("PART_EditableTextBox", editor.NameCombo));
                Assert.True(textBox.IsVisible);
                Assert.Equal("收尾", textBox.Text);
            }
            await AssertThemedPopup(window, editor.NodeCombo, resources, "1. 准备", "3. 收取");
            Assert.Contains("3. 收取", VisibleText(editor.NodeCombo));
            Assert.Equal(1, calls); // 生产模板回填及开关下拉均不能被当成换目标。
            Assert.True(editor.TryGetSettings(out var selected, out var error), error);
            Assert.Equal(type, selected.SpecifiedTaskType);
            Assert.Equal(type == "workflow" ? "计划收尾" : "收尾", selected.SpecifiedTaskName);
            Assert.Equal(type == "workflow" ? "wf-finish" : "", selected.SpecifiedWorkflowId);
            Assert.Equal("node", selected.SpecifiedStartMode);
            Assert.Equal("node-c", selected.SpecifiedStartTaskId);
            Assert.Equal("收取", selected.SpecifiedStartTaskName);
            Assert.Equal("rev-1", selected.SpecifiedConfigRevision);
            Assert.True(editor.TryGetChanges(out var unchanged, out error), error);
            Assert.Null(unchanged);
            SaveThemedScreenshot(window, $"completion-action-themed-{type}.png");
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    [InlineData("workflow")]
    public Task ThemedRefreshButtonIsKeyboardAccessibleAndPreservesExplicitStartSelection(string type) => OnSta(async () =>
    {
        var resources = ProductionComboResources();
        var calls = 0;
        var editor = new CompletionActionPanel(Settings(type, "node-c", "rev-1"), ["收尾"], ["收尾"], "",
            (kind, name, id) => Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id, $"rev-{++calls}")),
            createCombo: () => ThemedCombo(resources));
        await editor.CatalogLoadTask;
        var window = Show(editor, resources);
        try
        {
            await SettleLayout(window);
            var button = editor.RefreshButton;
            var border = Assert.Single(VisualDescendants<Border>(button).Where(candidate =>
                ReferenceEquals(candidate.TemplatedParent, button) && candidate.Background is SolidColorBrush
                && candidate.CornerRadius.TopLeft > 0 && candidate.CornerRadius.TopRight > 0
                && candidate.CornerRadius.BottomLeft > 0 && candidate.CornerRadius.BottomRight > 0));
            var background = Assert.IsType<SolidColorBrush>(border.Background).Color;
            Assert.True(background.A >= 0xC0 && Math.Max(background.R, Math.Max(background.G, background.B)) < 0x80,
                "刷新按钮的实际模板应使用深色底，不能退回系统灰白按钮。");
            var text = Assert.Single(VisualDescendants<TextBlock>(button).Where(block => block.IsVisible && block.Text == "刷新任务目录"));
            var foreground = Assert.IsType<SolidColorBrush>(text.Foreground).Color;
            Assert.True(foreground.R > foreground.G && foreground.G > foreground.B && foreground.R >= 0xB0,
                "刷新按钮的实际文字应为金色。");
            Assert.True(button.Focusable);
            Assert.True(KeyboardNavigation.GetIsTabStop(button));
            window.Activate();
            Assert.True(editor.NodeCombo.Focus());
            Assert.True(editor.NodeCombo.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
            await SettleLayout(window);
            Assert.True(button.IsKeyboardFocused);

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            await SettleLayout(window);
            Assert.Equal(2, calls);
            Assert.Equal(1, editor.StartModeCombo.SelectedIndex);
            Assert.Equal(1, editor.NodeCombo.SelectedIndex);
            Assert.Contains("3. 收取", VisibleText(editor.NodeCombo));
            Assert.False(editor.TryGetSettings(out var selected, out var error));
            Assert.NotNull(error);
            Assert.Equal("node", selected.SpecifiedStartMode);
            Assert.Equal("node-c", selected.SpecifiedStartTaskId);
            Assert.Equal(type == "workflow" ? "wf-finish" : "", selected.SpecifiedWorkflowId);
            Assert.Equal("rev-1", selected.SpecifiedConfigRevision); // 保留原确认；刷新不代替用户确认新版本。
            await AssertThemedPopup(window, editor.NodeCombo, resources, "1. 准备", "3. 收取");
            Assert.True(editor.TryGetSettings(out selected, out error), error);
            Assert.Equal("node-c", selected.SpecifiedStartTaskId);
            Assert.Equal("rev-2", selected.SpecifiedConfigRevision);

            editor.StartModeCombo.SelectedIndex = 0;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            await SettleLayout(window);
            Assert.Equal(3, calls);
            Assert.Equal(0, editor.StartModeCombo.SelectedIndex);
            Assert.False(editor.TryGetSettings(out selected, out error));
            Assert.NotNull(error);
            Assert.Equal("beginning", selected.SpecifiedStartMode);
            Assert.Empty(selected.SpecifiedStartTaskId);
            Assert.Equal("rev-2", selected.SpecifiedConfigRevision);
            await AssertThemedPopup(window, editor.StartModeCombo, resources, "从头开始执行", "从某个任务开始执行");
            Assert.True(editor.TryGetSettings(out selected, out error), error);
            Assert.Equal("beginning", selected.SpecifiedStartMode);
            Assert.Empty(selected.SpecifiedStartTaskId);
            Assert.Equal("rev-3", selected.SpecifiedConfigRevision);

            editor.StartModeCombo.SelectedIndex = -1;
            Assert.False(editor.TryGetSettings(out _, out _));
            Assert.False(editor.TryGetChanges(out _, out _));
            editor.StartModeCombo.SelectedIndex = 1;
            Assert.Equal(-1, editor.NodeCombo.SelectedIndex);
            Assert.False(editor.TryGetSettings(out _, out _));
            Assert.False(editor.TryGetChanges(out _, out _));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    [InlineData("workflow")]
    public Task OnlyExplicitRefreshUsesStartupCapableLoaderAndKeepsConfirmedSelection(string type) => OnSta(async () =>
    {
        var reads = 0;
        var refreshes = 0;
        var editor = new CompletionActionPanel(Settings(type, "node-c", "rev-1"), ["收尾"], ["收尾"], "",
            (kind, name, id) =>
            {
                reads++;
                return Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id));
            }, refreshCatalog: (kind, name, id, token) =>
            {
                Assert.True(token.CanBeCanceled);
                Assert.Equal(type, kind);
                refreshes++;
                return Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id));
            });
        await editor.CatalogLoadTask;
        Assert.Equal(0, refreshes);
        var window = Show(editor);
        try
        {
            editor.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, reads);
            Assert.Equal(1, refreshes);
            Assert.True(editor.TryGetSettings(out var saved, out var error), error);
            Assert.Equal("node", saved.SpecifiedStartMode);
            Assert.Equal("node-c", saved.SpecifiedStartTaskId);
            Assert.Equal("rev-1", saved.SpecifiedConfigRevision);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosingOrChangingTargetCancelsPendingStartupCapableRefresh(bool close) => OnSta(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var editor = new CompletionActionPanel(Settings("group", "node-c", "rev-1"), ["收尾", "新目标"], [], "",
            (kind, name, id) => Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id)),
            refreshCatalog: async (_, _, _, token) =>
            {
                calls++;
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { if (token.IsCancellationRequested) cancelled.TrySetResult(); }
            });
        await editor.CatalogLoadTask;
        var window = Show(editor);
        try
        {
            editor.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var refresh = editor.CatalogLoadTask;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(editor.RefreshButton.IsEnabled);
            editor.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, calls);
            if (close) window.Close();
            else editor.NameCombo.SelectedItem = "新目标";
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await refresh.WaitAsync(TimeSpan.FromSeconds(2));
            if (!close)
            {
                await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(editor.TryGetSettings(out var saved, out var error), error);
                Assert.Equal("新目标", saved.SpecifiedTaskName);
                Assert.Empty(saved.SpecifiedStartTaskId);
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("group")]
    [InlineData("onedragon")]
    [InlineData("workflow")]
    public Task RefreshCanConfirmPreviouslyUnavailableBeginningWithoutReselectingIt(string type) => OnSta(async () =>
    {
        var initial = Settings(type, "", "");
        initial.SpecifiedStartMode = "beginning";
        initial.SpecifiedStartTaskName = "";
        var editor = new CompletionActionPanel(initial, ["收尾"], ["收尾"], "",
            (_, _, _) => Task.FromResult<CompletionTargetCatalog?>(new([], null, "执行端尚未连接")),
            refreshCatalog: (kind, name, id, _) => Task.FromResult<CompletionTargetCatalog?>(Catalog(kind, name, id)));
        await editor.CatalogLoadTask;
        var window = Show(editor);
        try
        {
            Assert.False(editor.TryGetSettings(out _, out _));
            editor.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(editor.TryGetSettings(out var saved, out var error), error);
            Assert.Equal("beginning", saved.SpecifiedStartMode);
            Assert.Empty(saved.SpecifiedStartTaskId);
            Assert.Equal("rev-1", saved.SpecifiedConfigRevision);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("group", "配置二")]
    [InlineData("onedragon", "新一条龙")]
    public Task RefreshWithoutCachedNamesPopulatesTargetsWithoutChoosingOne(string type, string pickedName) => OnSta(async () =>
    {
        var initial = Settings(type, "", "");
        initial.SpecifiedTaskName = "";
        initial.SpecifiedStartTaskName = "";
        initial.SpecifiedStartMode = "beginning";
        var editor = new CompletionActionPanel(initial, [], [], "",
            (kind, name, id) => Task.FromResult<CompletionTargetCatalog?>(name.Length == 0
                ? new([], null, "尚未连接") : Catalog(kind, name, id)),
            refreshCatalog: (_, name, _, token) =>
            {
                Assert.Empty(name);
                token.ThrowIfCancellationRequested();
                return Task.FromResult<CompletionTargetCatalog?>(new([], null, "请选择执行目标",
                    ConfigGroups: ["配置一", "配置二"], OneClickConfigs: ["新一条龙"]));
            });
        await editor.CatalogLoadTask;
        var window = Show(editor);
        try
        {
            Assert.Empty(editor.NameCombo.Items);
            editor.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(pickedName, editor.NameCombo.Items.Cast<string>());
            Assert.Equal(-1, editor.NameCombo.SelectedIndex);
            Assert.Empty(editor.NameCombo.Text);
            Assert.False(editor.TryGetSettings(out _, out _));
            editor.NameCombo.SelectedItem = pickedName;
            await editor.CatalogLoadTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(editor.TryGetSettings(out var saved, out var error), error);
            Assert.Equal(pickedName, saved.SpecifiedTaskName);
            Assert.Equal("beginning", saved.SpecifiedStartMode);
        }
        finally { window.Close(); }
    });

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

    private static ResourceDictionary ProductionComboResources([CallerFilePath] string sourcePath = "")
    {
        var root = Path.GetFullPath("../../..", Path.GetDirectoryName(sourcePath)!);
        var source = XDocument.Load(Path.Combine(root, "MultiplayerHoeingAssistant", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = source.Root!.Element(presentation + "Application.Resources");
        Assert.NotNull(resources);
        var dictionary = new XElement(presentation + "ResourceDictionary",
            source.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration), resources.Nodes());
        // 不创建 Application，避免全局单例、Dispatcher 或 App 启动逻辑污染其他控件用例。
        return Assert.IsType<ResourceDictionary>(XamlReader.Parse(dictionary.ToString()));
    }

    private static ComboBox ThemedCombo(ResourceDictionary resources) => new()
    {
        Style = (Style)resources["GoldComboBox"],
        ItemContainerStyle = (Style)resources["GoldComboItem"]
    };

    private static async Task AssertThemedPopup(Window window, ComboBox combo, ResourceDictionary resources, params string[] expected)
    {
        Assert.Same(resources["GoldComboBox"], combo.Style);
        Assert.Same(resources["GoldComboItem"], combo.ItemContainerStyle);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onClosed = (_, _) => closed.TrySetResult();
        combo.DropDownClosed += onClosed;
        combo.IsDropDownOpen = true;
        try
        {
            await SettleLayout(window);
            var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
            Assert.True(popup.IsOpen);
            var content = Assert.IsAssignableFrom<FrameworkElement>(popup.Child);
            content.UpdateLayout();
            Assert.Equal(expected, VisibleText(content));
            for (var index = 0; index < combo.Items.Count; index++)
            {
                var item = Assert.IsType<ComboBoxItem>(combo.ItemContainerGenerator.ContainerFromIndex(index));
                Assert.Same(resources["GoldComboItem"], item.Style);
            }
        }
        finally
        {
            combo.IsDropDownOpen = false;
            try { await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { combo.DropDownClosed -= onClosed; }
            await SettleLayout(window);
        }
    }

    private static string[] VisibleText(DependencyObject root) => VisualDescendants<TextBlock>(root)
        .Where(block => block.IsVisible && block.ActualWidth > 0 && block.ActualHeight > 0 && block.Text.Length > 0)
        .Select(block => block.Text).ToArray();

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static async Task SettleLayout(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static void SaveThemedScreenshot(Window window, string name)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "禁止向真实助手数据目录写入截图。");
        Directory.CreateDirectory(AssistantDataDirectory.Root);
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(AssistantDataDirectory.Root, name));
        encoder.Save(output);
    }

    private static Window Show(CompletionActionPanel editor, ResourceDictionary? resources = null)
    {
        var window = new Window
        {
            Content = editor.Root, Width = 460, Height = 700,
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000
        };
        if (resources is not null)
        {
            window.Resources = resources;
            window.Background = new SolidColorBrush(Color.FromRgb(0x17, 0x15, 0x2E));
        }
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
