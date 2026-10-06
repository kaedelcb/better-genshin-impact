using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class FormalPathWindowTests
{
    [Fact]
    public async Task NativePopup_OpenResizeEditClose_KeepsSameDraftAndExplicitEndTarget()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            Window? main=null,popup=null;
            try
            {
                var root=Path.Combine(Path.GetTempPath(),"path-window-"+Guid.NewGuid().ToString("N"));
                var host=new TaskCenterHost(Path.Combine(root,"flows"),Path.Combine(root,"runs"),Path.Combine(root,"catalog.json"),()=>null,()=>true,()=>null);
                var doc=new WorkflowDocument{Name="共享窗口",Nodes=[new(){NodeId="a",Kind="resource.oneDragonConfig",Ref=new(){Config="A"},Path=new(){Next="$end"},Strategies=[new(){Kind="flow.route"}]}]};
                host.SaveFlow(doc,null);var vm=new TaskCenterPanelViewModel(host,autoRefresh:false);vm.EditFlowCommand.Execute(vm.Flows.Single());var draft=vm.Editing!;
                draft.ScheduleNode(draft.Nodes[0],360);
                var view=new ScheduleListView{DataContext=vm};main=new Window{Content=view,Width=1000,Height=700};main.Show();main.UpdateLayout();
                Assert.Equal("$end",draft.BuildSubmissionCopy().Nodes[0].Path!.Next);
                var button=Descendants(view).OfType<Button>().Single(b=>b.Content as string=="⤢弹出窗口");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                popup=(Window?)typeof(ScheduleListView).GetField("_popup",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(view);
                Assert.NotNull(popup);Assert.True(popup.IsVisible);Assert.Same(vm,((ScheduleListView)popup.Content).DataContext);
                popup.Width=850;popup.Height=570;popup.Topmost=true;popup.UpdateLayout();
                draft.ScheduleNode(draft.Nodes[0],390);popup.UpdateLayout();
                Assert.Same(draft,vm.Editing);Assert.Equal("06:30",draft.Nodes[0].ScheduleTimeText);Assert.Equal("$end",draft.BuildSubmissionCopy().Nodes[0].Path!.Next);
                popup.Close();popup=null;main.UpdateLayout();
                Assert.Equal(Visibility.Visible,((Grid)view.FindName("TimelineContent")).Visibility);Assert.Same(draft,vm.Editing);
                done.SetResult();
            }
            catch(Exception ex){done.SetException(ex);}
            finally{popup?.Close();main?.Close();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=VisualTreeHelper.GetChild(parent,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
}
