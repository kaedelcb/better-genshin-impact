using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalScheduleRenderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    public async Task ParentScroll_DoesNotExpandTimelineIntoStrategyForm(double offset)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "formal-viewport-" + Guid.NewGuid().ToString("N"));
                var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
                    Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument { Name = "完整策略入口", Nodes = [new() { NodeId = "end", Kind = "control.end" }] }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                vm.EditFlowCommand.Execute(vm.Flows.Single());
                var view = new ScheduleListView { DataContext = vm };
                var form = new TextBox { Text = "流程名称", Height = 36 };
                var content = new StackPanel();
                content.Children.Add(new Border { Height = 120 });
                content.Children.Add(view);
                content.Children.Add(form);
                content.Children.Add(new Border { Height = 360 });
                var outer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                window = new Window { Content = outer, Width = 960, Height = 680,
                    ShowActivated = false, Left = -20000, Top = -20000 };
                window.Show();
                PumpLayout(window);
                var timeline = Assert.IsType<ScrollViewer>(view.FindName("TimelineScroll"));
                var inspector = Assert.IsType<ScrollViewer>(view.FindName("InspectorScroll"));
                var baselineHeight = timeline.Height;
                outer.ScrollToVerticalOffset(offset);
                PumpLayout(window);
                Assert.Equal(offset, outer.VerticalOffset, 3);
                // Real owner resize runs the same viewport update used by loaded/rebound views.
                window.Width += 20;
                PumpLayout(window);
                Assert.Equal(baselineHeight, timeline.Height, 3);
                Assert.Equal(baselineHeight, inspector.Height, 3);
                outer.ScrollToBottom();
                PumpLayout(window);
                var formTop = form.TransformToAncestor(outer).Transform(new Point()).Y;
                Assert.InRange(formTop, 0, outer.ViewportHeight - form.ActualHeight);
                Assert.Same(vm.Editing, view.DataContext is TaskCenterPanelViewModel current ? current.Editing : null);
                Assert.Empty(host.ListActiveRuns());
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void PumpLayout(Window window)
    {
        window.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    [Fact]
    public async Task RealWpfView_RendersExistingDraftAndSharedNodeData()
    {
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var root=Path.Combine(Path.GetTempPath(),"formal-render-"+Guid.NewGuid().ToString("N"));
                var host=new TaskCenterHost(Path.Combine(root,"flows"),Path.Combine(root,"runs"),Path.Combine(root,"catalog.json"),()=>null,()=>true,()=>null);
                var doc=new WorkflowDocument {Name="正式调度列表渲染",Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="每日任务"}},new(){NodeId="b",Kind="resource.oneDragonConfig",Ref=new(){Config="采集素材"}}]};
                host.SaveFlow(doc,null);var vm=new TaskCenterPanelViewModel(host,autoRefresh:false);vm.EditFlowCommand.Execute(vm.Flows.Single());
                var draft=vm.Editing!;draft.AddLane();draft.ScheduleNode(draft.Nodes[0],360);draft.Nodes[0].LaneSpan=2;draft.ScheduleNode(draft.Nodes[1],365,1);
                var view=new ScheduleListView {DataContext=vm,Width=1080,Height=660};
                view.Measure(new Size(1080,660));view.Arrange(new Rect(0,0,1080,660));view.UpdateLayout();
                Assert.Equal(Visibility.Collapsed,((StackPanel)view.FindName("CatalogPane")).Visibility);
                Assert.Equal(Visibility.Visible,((StackPanel)view.FindName("InspectorPane")).Visibility);
                var timeline=(Canvas)view.FindName("Timeline");
                Assert.Contains(timeline.Children.OfType<Button>(),b=>b.Content is string label && label.Contains("2 项") && b.Width>=448);
                Assert.Equal("DisplayName",((ListBox)view.FindName("Tray")).DisplayMemberPath);
                var bitmap=new RenderTargetBitmap(1080,660,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
                Assert.Same(draft,vm.Editing);Assert.Equal("06:00",draft.Nodes[0].ScheduleTimeText);
                if(Environment.GetEnvironmentVariable("FORMAL_UI_EVIDENCE") is { } evidence)
                {var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(evidence,"schedule-render.png"));encoder.Save(stream);}
                var canvas=(Canvas)view.FindName("Timeline");
                var cluster=Assert.Single(canvas.Children.OfType<Button>().Where(b=>b.Content is string text && text.StartsWith("▸")));
                cluster.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));view.UpdateLayout();
                var cards=canvas.Children.OfType<Button>().Where(b=>b.Tag is NodeEditVm).OrderBy(Canvas.GetTop).ToArray();
                Assert.Equal(2,cards.Length);Assert.True(Canvas.GetTop(cards[1])-Canvas.GetTop(cards[0])>=cards[0].ActualHeight);
                var second=new ScheduleListView {DataContext=vm,Width=1080,Height=660};second.Measure(new Size(1080,660));second.Arrange(new Rect(0,0,1080,660));second.UpdateLayout();
                draft.ScheduleNode(draft.Nodes[0],390);
                Assert.Same(view.DataContext,second.DataContext);Assert.Same(draft,vm.Editing);
                Assert.Contains(((Canvas)second.FindName("Timeline")).Children.OfType<Button>(),b=>b.Content is string text && text.Contains("06:30"));
                completed.SetResult();
            }
            catch(Exception ex){completed.SetException(ex);}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
