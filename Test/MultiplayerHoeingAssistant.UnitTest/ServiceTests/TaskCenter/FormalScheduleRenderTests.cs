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
                var draft=vm.Editing!;draft.ScheduleNode(draft.Nodes[0],360);draft.ScheduleNode(draft.Nodes[1],365);
                var view=new ScheduleListView {DataContext=vm,Width=1080,Height=660};
                view.Measure(new Size(1080,660));view.Arrange(new Rect(0,0,1080,660));view.UpdateLayout();
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
