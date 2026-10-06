using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalDragEffectsTests
{
    [Theory]
    [InlineData(DragDropEffects.Move, DragDropEffects.Move)]
    [InlineData(DragDropEffects.Copy, DragDropEffects.None)]
    [InlineData(DragDropEffects.Move | DragDropEffects.Copy, DragDropEffects.Move)]
    public Task NodeDrop_NegotiatesTheMoveEffectAllowedByItsSource(DragDropEffects allowed, DragDropEffects expected)
        => Check((view, node) => Assert.Equal(expected, Over(view, new DataObject(typeof(NodeEditVm), node), allowed)));

    [Fact]
    public Task CatalogDrop_PreservesCopy_AndRejectsMoveOnlySources()
        => Check((view, _) =>
        {
            var item = new CatalogSourceVm(new TaskCenterResourceEntry { Kind = TaskCenterResourceKind.ConfigGroup,
                StableId = "group:test", DisplayName = "test" });
            var data = new DataObject(typeof(CatalogSourceVm), item);
            Assert.Equal(DragDropEffects.Copy, Over(view, data, DragDropEffects.Copy));
            Assert.Equal(DragDropEffects.None, Over(view, data, DragDropEffects.Move));
        });

    [Fact]
    public Task UnknownPayload_IsRejected()
        => Check((view, _) => Assert.Equal(DragDropEffects.None,
            Over(view, new DataObject(DataFormats.Text, "unrecognized"), DragDropEffects.Move | DragDropEffects.Copy)));

    private static DragDropEffects Over(ScheduleListView view, IDataObject data, DragDropEffects allowed)
    {
        var canvas = (Canvas)view.FindName("Timeline");
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.LeftMouseButton, allowed, canvas, new Point(100, 100)]);
        args.RoutedEvent = DragDrop.DragOverEvent;
        canvas.RaiseEvent(args);
        Assert.True(args.Handled);
        return args.Effects;
    }

    private static async Task Check(Action<ScheduleListView, NodeEditVm> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var root = Path.Combine(Environment.GetEnvironmentVariable("FORMAL_UI_EVIDENCE") ?? Path.GetTempPath(),
                    "drag-effects-" + Guid.NewGuid().ToString("N"));
                var host = new TaskCenterHost(Path.Combine(root,"flows"),Path.Combine(root,"runs"),Path.Combine(root,"catalog.json"),()=>null,()=>true,()=>null);
                var doc = new WorkflowDocument { Name = "drag effects", Nodes = [new() {
                    NodeId = "a", Kind = "resource.oneDragonConfig", Ref = new() { Config = "A" } }] };
                host.SaveFlow(doc,null);
                var vm = new TaskCenterPanelViewModel(host,autoRefresh:false);
                vm.EditFlowCommand.Execute(vm.Flows.Single());
                var view = new ScheduleListView { DataContext = vm };
                check(view, vm.Editing!.Nodes[0]);
                done.SetResult();
            }
            catch (Exception e) { done.SetException(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
