using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>The view owns geometry and pointer gestures only. Both windows edit the same host draft.</summary>
public partial class ScheduleListView : UserControl
{
    private TaskCenterPanelViewModel? Host => DataContext as TaskCenterPanelViewModel;
    private WorkflowEditVm? Draft => Host?.Editing;
    private TaskCenterPanelViewModel? _observedHost;
    private WorkflowEditVm? _observedDraft;
    private readonly HashSet<NodeEditVm> _observedNodes = [];
    private readonly HashSet<string> _expanded = [];
    private double _hourHeight = 64;
    private readonly double[] _hourY = new double[25];
    private Window? _popup;
    private Rect? _popupBounds;
    private bool _popupView, _drawing, _choosing;
    private const double Left = 65, Header = 38;
    private double LaneWidth = 230;
    public ScheduleListView() { InitializeComponent(); DataContextChanged += (_, _) => Observe(); }
    private void ViewLoaded(object sender, RoutedEventArgs e) => Observe();
    private void ViewUnloaded(object sender, RoutedEventArgs e) => Detach();
    private void Detach()
    {
        if (_observedHost is not null) _observedHost.PropertyChanged -= HostChanged;
        if (_observedDraft is not null) { _observedDraft.Nodes.CollectionChanged -= NodesChanged; _observedDraft.Lanes.CollectionChanged -= NodesChanged; }
        foreach (var node in _observedNodes) node.PropertyChanged -= NodeChanged;
        _observedNodes.Clear(); _observedHost = null; _observedDraft = null;
    }
    private void Observe()
    {
        Detach(); _observedHost = Host; _observedDraft = Draft;
        if (_observedHost is not null) _observedHost.PropertyChanged += HostChanged;
        if (_observedDraft is not null) { _observedDraft.Nodes.CollectionChanged += NodesChanged; _observedDraft.Lanes.CollectionChanged += NodesChanged; }
        ObserveNodes();
        if (Draft?.Draft.WorkflowId is { } id && Host?.Flows.FirstOrDefault(f=>f.WorkflowId==id) is { } flow)
        { _choosing=true;FlowChoice.SelectedItem=flow;_choosing=false; }
        Draw();
    }
    private void HostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Editing") return;
        Observe();
        if (Draft?.Draft.WorkflowId is { } id && Host?.Flows.FirstOrDefault(f=>f.WorkflowId==id) is { } flow)
        { _choosing=true; FlowChoice.SelectedItem=flow; _choosing=false; }
    }
    private void NodesChanged(object? sender, NotifyCollectionChangedEventArgs e) { ObserveNodes(); Draw(); }
    private void ObserveNodes()
    {
        foreach (var old in _observedNodes) old.PropertyChanged -= NodeChanged;
        _observedNodes.Clear();
        if (Draft is null) return;
        foreach (var node in Draft.Nodes) { _observedNodes.Add(node); node.PropertyChanged += NodeChanged; }
    }
    private void NodeChanged(object? sender, PropertyChangedEventArgs e) => Draw();
    private int Offset => AxisBase?.SelectedIndex == 1 ? 240 : 0;
    private int DisplayMinute(NodeEditVm n) => ((n.ScheduleMinute ?? 0) - Offset + 1440) % 1440;
    private double Y(int minute) { var hour = Math.Clamp(minute / 60,0,23); return _hourY[hour] + (minute % 60) / 60d * (_hourY[hour+1] - _hourY[hour]); }
    private int MinuteAt(double y)
    {
        for (var hour = 0; hour < 24; hour++)
            if (y < _hourY[hour+1]) return Math.Clamp(hour*60+(int)Math.Round((y-_hourY[hour])*60/(_hourY[hour+1]-_hourY[hour])),0,1439);
        return 1439;
    }
    private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private TextBlock Text(string text, double x, double y, string color = "#D5BD78")
    {
        var label = new TextBlock { Text=text, Foreground=Brush(color), FontSize=11 };
        Canvas.SetLeft(label,x); Canvas.SetTop(label,y); Timeline.Children.Add(label); return label;
    }
    private void Line(double x1,double y1,double x2,double y2,string color)
        => Timeline.Children.Add(new System.Windows.Shapes.Line { X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=Brush(color),StrokeThickness=1 });
    private void Draw()
    {
        if (Timeline is null || _drawing) return;
        _drawing = true;
        try
        {
            Timeline.Children.Clear(); var draft = Draft;
            var lanes = Math.Max(1,draft?.Lanes.Count ?? 1);
            LaneWidth=Math.Max(230,(TimelineScroll.ActualWidth-Left-48)/lanes);
            Timeline.Width = Left + lanes*LaneWidth + 30;
            _hourY[0] = Header+14;
            for (var hour=0;hour<24;hour++)
            {
                double height = _hourHeight;
                if (draft is not null && _expanded.Any(k=>k.StartsWith(hour+":")))
                {
                    var minutes=draft.Nodes.Where(n=>n.ScheduleMinute is not null && DisplayMinute(n)/60==hour).Select(DisplayMinute).Distinct().Order().ToArray();
                    var gap=minutes.Zip(minutes.Skip(1),(a,b)=>b-a).DefaultIfEmpty(60).Min();
                    height=Math.Max(height,60d*42/Math.Max(1,gap));
                }
                _hourY[hour+1]=_hourY[hour]+height;
            }
            Timeline.Height=_hourY[24]+40;
            for(var hour=0;hour<=24;hour++)
            { Text($"{(hour+Offset/60)%24:00}:00",4,_hourY[hour]-8); Line(Left,_hourY[hour],Timeline.Width,_hourY[hour],"#38343C"); }
            for(var lane=0;lane<lanes;lane++)
            {
                var index=lane; var x=Left+lane*LaneWidth;
                Text(draft?.Lanes[lane] ?? "主车道",x+6,8); Line(x,Header,x,Timeline.Height,"#41404A");
                if (lane>0) { var remove=new Button { Content="×",Padding=new Thickness(4,0,4,0) }; remove.Click+=(_,_)=>draft?.RemoveLane(index); Canvas.SetLeft(remove,x+LaneWidth-28); Canvas.SetTop(remove,3); Timeline.Children.Add(remove); }
            }
            var add=new Button { Content="＋车道",Padding=new Thickness(4,0,4,0) }; add.Click+=(_,_)=>draft?.AddLane(); Canvas.SetLeft(add,Left+8); Canvas.SetTop(add,Timeline.Height-30); Timeline.Children.Add(add);
            if(draft is null) { Text("选择一份可编辑流程，或新建调度列表。候选保持只读预览。",Left+8,Header+30); Tray.ItemsSource=null; return; }
            Tray.ItemsSource=draft.Nodes.Where(n=>n.ScheduleMinute is null).ToArray();
            foreach(var laneGroup in draft.Nodes.Where(n=>n.ScheduleMinute is not null).GroupBy(n=>Math.Clamp(n.LaneIndex,0,lanes-1)))
            {
                var ordered=laneGroup.OrderBy(DisplayMinute).ToArray();
                var groups=new List<List<NodeEditVm>>();
                foreach(var node in ordered)
                {
                    if(groups.LastOrDefault() is { } last && Y(DisplayMinute(node))-Y(DisplayMinute(last[^1]))<40) last.Add(node);
                    else groups.Add([node]);
                }
                // A collection card is taller than one task. Re-merge its actual display extent.
                for(var i=0;i<groups.Count-1;)
                {
                    var g=groups[i];var height=g.Count==1?38:44+Math.Min(5,g.Count)*16;
                    if(Y(DisplayMinute(groups[i+1][0]))<Y(DisplayMinute(g[0]))+height)
                    {g.AddRange(groups[i+1]);groups.RemoveAt(i+1);if(i>0)i--;}
                    else i++;
                }
                foreach(var group in groups)
                {
                    var key=DisplayMinute(group[0])/60+":"+laneGroup.Key+":"+group[0].Model.NodeId;
                    if(group.Count>1 && !_expanded.Contains(key))
                    {
                        var content=string.Join("\n",group.Take(5).Select(n=>$"{n.ScheduleTimeText}  {n.DisplayName}"));
                        var card=new Button { Content=$"▸ {group.Count} 项 · 就地展开\n"+content, Width=LaneWidth-12,HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brush("#242634"),Foreground=Brush("#E0C479"),Padding=new Thickness(8),ToolTip="每项仍保留真实时刻；点击展开分钟刻度" };
                        card.Click+=(_,_)=>{_expanded.Add(key);Draw();}; Place(card,Left+laneGroup.Key*LaneWidth+6,Y(DisplayMinute(group[0]))); 
                    }
                    else
                    {
                        foreach(var instant in group.GroupBy(DisplayMinute))
                        {
                            var same=instant.ToArray();
                            if(same.Length>1)
                            {
                                var card=new Button {Content=$"{same[0].ScheduleTimeText} · 同刻 {same.Length} 项 ▾",Width=LaneWidth-12,Background=Brush("#242634"),Foreground=Brush("#E0C479")};
                                var menu=new ContextMenu();foreach(var node in same){var item=new MenuItem {Header=node.DisplayName};item.Click+=(_,_)=>draft.SelectedNode=node;menu.Items.Add(item);} card.Click+=(_,_)=>{menu.PlacementTarget=card;menu.IsOpen=true;};Place(card,Left+laneGroup.Key*LaneWidth+6,Y(instant.Key));
                            }
                            else NodeCard(same[0],laneGroup.Key);
                        }
                    }
                }
            }
            var now=DateTime.Now;var minute=(now.Hour*60+now.Minute-Offset+1440)%1440; Line(Left,Y(minute),Timeline.Width,Y(minute),"#DD7865");Text("现在 "+now.ToString("HH:mm"),4,Y(minute)+3,"#DD7865");
        }
        finally { _drawing=false; }
    }
    private void Place(FrameworkElement element,double x,double y) {Canvas.SetLeft(element,x);Canvas.SetTop(element,y);Timeline.Children.Add(element);}
    private void NodeCard(NodeEditVm node,int lane)
    {
        var card=new Button {Content=$"{node.ScheduleTimeText}  {(node.ScheduleModeIndex==1?"🔒":node.ScheduleModeIndex==2?"🕊":"●")}  {node.DisplayName}",Width=LaneWidth-12,Height=32,Padding=new Thickness(6),HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brush("#242634"),Foreground=Brush("#E0C479"),Tag=node,ToolTip=node.KindName+" · "+node.StrategySummary};
        card.Click+=(_,_)=>{if(Draft is {} draft) draft.SelectedNode=node;};card.PreviewMouseMove+=NodeDrag;Place(card,Left+lane*LaneWidth+6,Y(DisplayMinute(node)));
    }
    private void FlowChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_choosing || Host is null || FlowChoice.SelectedItem is not WorkflowListItemVm flow) return;
        if (Draft?.Draft.WorkflowId == flow.WorkflowId) return;
        if(flow.CanEdit)Host.EditFlowCommand.Execute(flow);else Host.PreviewFlowCommand.Execute(flow);
        if(Draft?.Draft.WorkflowId is { } id && id!=flow.WorkflowId)
        { _choosing=true;FlowChoice.SelectedItem=Host.Flows.FirstOrDefault(f=>f.WorkflowId==id);_choosing=false; }
    }
    private void EditFlow(object sender,RoutedEventArgs e)=>Host?.EditFlowCommand.Execute(FlowChoice.SelectedItem);
    private void SaveEditing(object sender,RoutedEventArgs e)
    {
        var id=Draft?.Draft.WorkflowId;if(id is null)return;
        Host?.SaveFlowCommand.Execute(null);
        if(Draft is null && Host?.Flows.FirstOrDefault(f=>f.WorkflowId==id) is {} flow)Host.EditFlowCommand.Execute(flow);
    }
    private void StartFlow(object sender,RoutedEventArgs e) {if(FlowChoice.SelectedItem is WorkflowListItemVm {CanStart:true} flow)Host?.StartFlowCommand.Execute(flow);}
    private void ExportFlow(object sender,RoutedEventArgs e)=>Host?.ExportFlowCommand.Execute(FlowChoice.SelectedItem);
    private void ActivateFlow(object sender,RoutedEventArgs e)=>Host?.ActivateMigrationCandidateCommand.Execute(FlowChoice.SelectedItem);
    private void MoreFlow(object sender,RoutedEventArgs e)
    {
        if(Host is not {} host)return;
        var menu=new ContextMenu();
        menu.Items.Add(new MenuItem {Header="放弃当前草稿",Command=host.DiscardEditCommand});
        menu.Items.Add(new MenuItem {Header="回退迁移",Command=host.RollbackMigrationCommand,CommandParameter=FlowChoice.SelectedItem});
        menu.Items.Add(new MenuItem {Header="迁移演练",Command=host.MigrationRehearsalCommand});
        menu.PlacementTarget=(Button)sender;menu.IsOpen=true;
    }
    private void MoreRun(object sender,RoutedEventArgs e)
    {
        if(Host is not {} host || (sender as Button)?.Tag is not ActiveRunVm run)return;
        var menu=new ContextMenu();menu.Items.Add(new MenuItem {Header="跳过当前节点",Command=host.SkipNodeCommand,CommandParameter=run,IsEnabled=run.CanSkip});
        menu.Items.Add(new MenuItem {Header="重载修订（节点边界）",Command=host.ReloadRunCommand,CommandParameter=run,IsEnabled=run.CanReload});menu.PlacementTarget=(Button)sender;menu.IsOpen=true;
    }
    private void LayoutChanged(object sender,SelectionChangedEventArgs e)=>Draw();
    private void LayoutSizeChanged(object sender,SizeChangedEventArgs e)=>Draw();
    private void ToggleMinutes(object sender,RoutedEventArgs e){_hourHeight=_hourHeight<200?360:64;Draw();}
    private void ZoomOut(object sender,RoutedEventArgs e){_hourHeight=Math.Max(40,_hourHeight-20);Draw();}
    private void ZoomIn(object sender,RoutedEventArgs e){_hourHeight=Math.Min(720,_hourHeight+20);Draw();}
    private void Undo(object sender,RoutedEventArgs e){Draft?.UndoSchedule();Draw();}
    private void ViewKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Z && Keyboard.Modifiers==ModifierKeys.Control){Draft?.UndoSchedule();Draw();e.Handled=true;}}
    private void ShowCatalog(object sender,RoutedEventArgs e)=>Catalog.Focus();
    private void SearchChanged(object sender,TextChangedEventArgs e)
    {if(Catalog is null)return;var view=System.Windows.Data.CollectionViewSource.GetDefaultView(Catalog.ItemsSource);if(view is null)return;view.Filter=item=>item is CatalogSourceVm src && src.Line.Contains(ResourceSearch.Text,StringComparison.OrdinalIgnoreCase);}
    private void AddTask(object sender,RoutedEventArgs e)
    {
        if(Draft is not {} draft)return;
        var pick=new ComboBox {ItemsSource=draft.AppendSources,DisplayMemberPath="Line",MinWidth=320};var time=new TextBox {Text="",Margin=new Thickness(0,8,0,8),ToolTip="HH:mm，留空暂不排程"};var lane=new ComboBox {ItemsSource=draft.Lanes,SelectedIndex=0};var next=new Button {Content="下一步"};var summary=new TextBlock {Text="第一步：选择资源",Margin=new Thickness(0,0,0,10)};var panel=new StackPanel {Margin=new Thickness(18)};panel.Children.Add(summary);panel.Children.Add(pick);panel.Children.Add(time);panel.Children.Add(lane);panel.Children.Add(next);time.Visibility=lane.Visibility=Visibility.Collapsed;
        var window=new Window {Title="添加任务",Content=panel,SizeToContent=SizeToContent.WidthAndHeight,Owner=Window.GetWindow(this),WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#1C1E2A")};var step=1;
        next.Click+=(_,_)=>
        {
            if(!ReferenceEquals(Draft,draft)){window.Close();return;}
            if(step==1){if(pick.SelectedItem is not CatalogSourceVm)return;step=2;summary.Text="第二步：选择时间和车道（空=待安排）";time.Visibility=lane.Visibility=Visibility.Visible;return;}
            int? minute=null;if(time.Text.Length>0){if(!TimeOnly.TryParseExact(time.Text,"HH:mm",out var parsed)){summary.Text="请输入HH:mm，例如12:30";return;}minute=parsed.Hour*60+parsed.Minute;}
            if(step==2){step=3;summary.Text=$"第三步：{((CatalogSourceVm)pick.SelectedItem).DisplayName}\n{(minute is null?"暂不排程":time.Text)} · {lane.SelectedItem}";pick.IsEnabled=time.IsEnabled=lane.IsEnabled=false;next.Content="确认添加";return;}
            draft.AddScheduledResource((CatalogSourceVm)pick.SelectedItem,minute,lane.SelectedIndex);window.Close();Draw();
        };window.ShowDialog();
    }
    private void ResourceDrag(object sender,MouseEventArgs e)
    {if(e.LeftButton==MouseButtonState.Pressed && Catalog.SelectedItem is CatalogSourceVm source && Draft is not null)DragDrop.DoDragDrop(Catalog,new DataObject(typeof(CatalogSourceVm),source),DragDropEffects.Copy);}
    private void NodeDrag(object sender,MouseEventArgs e)
    {var node=(sender as FrameworkElement)?.Tag as NodeEditVm ?? Tray.SelectedItem as NodeEditVm;if(e.LeftButton==MouseButtonState.Pressed && node is not null && Draft?.Nodes.Contains(node)==true)DragDrop.DoDragDrop((DependencyObject)sender,new DataObject(typeof(NodeEditVm),node),DragDropEffects.Move);}
    private void TimelineDragOver(object sender,DragEventArgs e){e.Effects=Draft is not null && (e.Data.GetDataPresent(typeof(NodeEditVm))||e.Data.GetDataPresent(typeof(CatalogSourceVm)))?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private void TimelineDrop(object sender,DragEventArgs e)
    {if(Draft is not {} draft)return;var point=e.GetPosition(Timeline);var lane=Math.Clamp((int)((point.X-Left)/LaneWidth),0,draft.Lanes.Count-1);var minute=(MinuteAt(point.Y)+Offset)%1440;if(_hourHeight<200)minute=minute/5*5;if(e.Data.GetData(typeof(NodeEditVm)) is NodeEditVm node && draft.Nodes.Contains(node))draft.ScheduleNode(node,minute,lane);else if(e.Data.GetData(typeof(CatalogSourceVm)) is CatalogSourceVm resource)draft.AddScheduledResource(resource,minute,lane);Draw();e.Handled=true;}
    private void TimelineClick(object sender,MouseButtonEventArgs e){ }
    private void TrayDrop(object sender,DragEventArgs e){if(e.Data.GetData(typeof(NodeEditVm)) is NodeEditVm node)CancelTime(node);e.Handled=true;}
    private void Unschedule(object sender,RoutedEventArgs e){if(Draft?.SelectedNode is {} node)CancelTime(node);}
    private void CancelTime(NodeEditVm node){if(node.ScheduleModeIndex==1 && MessageBox.Show("取消固定时间后，这个任务将改为按顺序执行。确定取消？","取消定时",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;Draft?.ScheduleNode(node,null,node.LaneIndex);node.ScheduleModeIndex=0;Draw();}
    private void DeleteNode(object sender,RoutedEventArgs e){if(Draft is {} draft && draft.SelectedNode is {} node){draft.RememberSchedule();draft.RemoveNodeCommand.Execute(node);draft.SelectedNode=null;Draw();}}
    private void PopOut(object sender,RoutedEventArgs e)
    {
        if(_popupView){Window.GetWindow(this)?.Close();return;}
        if(_popup is not null){_popup.Activate();return;}
        var view=new ScheduleListView {_popupView=true,DataContext=DataContext};
        view.MinHeight=0;
        _popup=new Window {Title="槲寄生 · 调度列表",Content=view,Width=1100,Height=720,MinWidth=480,MinHeight=360,Background=Brush("#151722")};
        if(_popupBounds is {} bounds){_popup.Left=bounds.Left;_popup.Top=bounds.Top;_popup.Width=bounds.Width;_popup.Height=bounds.Height;}
        _popup.SizeChanged+=(_,_)=>{if(_popup is {} window){var height=Math.Max(120,window.ActualHeight-185);view.TimelineScroll.Height=height;view.InspectorScroll.Height=height;}};
        var topmost=new MenuItem {Header="窗口置顶",IsCheckable=true};topmost.Checked+=(_,_)=>{if(_popup is not null)_popup.Topmost=true;};topmost.Unchecked+=(_,_)=>{if(_popup is not null)_popup.Topmost=false;};_popup.ContextMenu=new ContextMenu();_popup.ContextMenu.Items.Add(topmost);
        MinHeight=0;Height=110;TimelineContent.Visibility=Footnote.Visibility=Visibility.Collapsed;
        _popup.Closed+=(_,_)=>{if(_popup is {} closed)_popupBounds=new Rect(closed.Left,closed.Top,closed.ActualWidth,closed.ActualHeight);_popup=null;MinHeight=540;Height=double.NaN;TimelineContent.Visibility=Footnote.Visibility=Visibility.Visible;};_popup.Show();
        view._choosing=true;view.FlowChoice.SelectedItem=FlowChoice.SelectedItem;view._choosing=false;
    }
}

public sealed class ScheduleChoiceLabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)
        => value switch { WorkflowListItemVm flow=>flow.Name, ComboBoxItem item=>item.Content, CatalogSourceVm source=>source.Line, null=>"", _=>value.ToString() ?? "" };
    public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>System.Windows.Data.Binding.DoNothing;
}
