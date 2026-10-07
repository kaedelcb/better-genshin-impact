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
    private readonly HashSet<ActiveRunVm> _observedRuns=[];
    private sealed record ControlPreset(string Title,bool End,int ConditionKind=0){public override string ToString()=>Title;}
    private static readonly ControlPreset[] Presets=[new("判断 · 星期",false),new("判断 · 时间窗口",false,1),new("判断 · 固定选择",false,2),new("判断 · 伴随观察结果",false,4),new("动作 · 结束流程",true)];
    private readonly HashSet<string> _expanded = [];
    private double _hourHeight = 64;
    private readonly double[] _hourY = new double[25];
    private Window? _popup;
    private Window? _observedWindow;
    private Rect? _popupBounds;
    private bool _popupView, _drawing, _choosing, _resizing;
    private const double Left = 65, Header = 38;
    private double LaneWidth = 230;
    private Point? _dragOrigin;
    private object? _dragSource;
    private bool _dragging, _redrawAfterGesture;
    public ScheduleListView()
    {
        InitializeComponent(); DataContextChanged += (_, _) => Observe();
        Catalog.PreviewMouseLeftButtonDown+=RememberDragStart;Tray.PreviewMouseLeftButtonDown+=RememberDragStart;
        PreviewMouseLeftButtonUp+=(_,_)=>FinishGesture();
    }
    private void ViewLoaded(object sender, RoutedEventArgs e) => Observe();
    private void ViewUnloaded(object sender, RoutedEventArgs e) => Detach();
    private void Detach()
    {
        if(_observedWindow is not null)_observedWindow.SizeChanged-=WindowResized;
        _observedWindow=null;
        if (_observedHost is not null) _observedHost.PropertyChanged -= HostChanged;
        if (_observedHost is not null) _observedHost.ActiveRuns.CollectionChanged-=RunsChanged;
        foreach(var run in _observedRuns)run.PropertyChanged-=RunChanged;_observedRuns.Clear();
        if (_observedDraft is not null) { _observedDraft.Nodes.CollectionChanged -= NodesChanged; _observedDraft.Lanes.CollectionChanged -= NodesChanged; _observedDraft.PropertyChanged-=DraftChanged; }
        foreach (var node in _observedNodes) node.PropertyChanged -= NodeChanged;
        _observedNodes.Clear(); _observedHost = null; _observedDraft = null;
    }
    private void Observe()
    {
        Detach(); _observedHost = Host; _observedDraft = Draft;
        if (_observedHost is not null) _observedHost.PropertyChanged += HostChanged;
        if (_observedHost is not null) _observedHost.ActiveRuns.CollectionChanged+=RunsChanged;
        ObserveRuns();
        if (_observedDraft is not null) { _observedDraft.Nodes.CollectionChanged += NodesChanged; _observedDraft.Lanes.CollectionChanged += NodesChanged; _observedDraft.PropertyChanged+=DraftChanged; }
        ObserveNodes();
        SyncChoice();
        Draw();
        DraftChanged(this,new PropertyChangedEventArgs("SelectedNode"));
        _observedWindow=Window.GetWindow(this);
        if(_observedWindow is not null)
        {_observedWindow.SizeChanged+=WindowResized;Dispatcher.BeginInvoke(new Action(UpdateViewport));}
    }
    private void WindowResized(object sender,SizeChangedEventArgs e)=>Dispatcher.BeginInvoke(new Action(UpdateViewport));
    private void UpdateViewport()
    {
        if(_observedWindow is not {} window || !IsLoaded || !IsVisible || TimelineContent.Visibility!=Visibility.Visible || !window.IsAncestorOf(TimelineContent))return;
        var top=TimelineContent.TransformToAncestor(window).Transform(new Point()).Y;
        // 外层滚动只改变位置，不扩大时间轴；下方完整策略表单仍须可滚动到达。
        for (DependencyObject? ancestor=VisualTreeHelper.GetParent(TimelineContent);
             ancestor is not null && ancestor!=window; ancestor=VisualTreeHelper.GetParent(ancestor))
            if (ancestor is ScrollViewer parentScroll) top+=parentScroll.VerticalOffset;
        var height=Math.Max(180,window.ActualHeight-top-100);
        if(Math.Abs(TimelineScroll.Height-height)>1)
        {TimelineScroll.Height=height;InspectorScroll.Height=height;}
    }
    private void HostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Editing" or "Previewing" or "SelectedWorkflowId")) return;
        Observe();
    }
    private void NodesChanged(object? sender, NotifyCollectionChangedEventArgs e) { ObserveNodes(); Draw(); }
    private void RunsChanged(object? sender,NotifyCollectionChangedEventArgs e){ObserveRuns();Draw();}
    private void RunChanged(object? sender,PropertyChangedEventArgs e)=>Draw();
    private void ObserveRuns()
    {foreach(var run in _observedRuns)run.PropertyChanged-=RunChanged;_observedRuns.Clear();if(Host is null)return;foreach(var run in Host.ActiveRuns){_observedRuns.Add(run);run.PropertyChanged+=RunChanged;}}
    private void DraftChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(WorkflowEditVm.IsRestoringSchedule)){Draw();return;}
        if(e.PropertyName=="SelectedNode")
        {CatalogPane.Visibility=Visibility.Collapsed;InspectorPane.Visibility=Draft?.SelectedNode is null?Visibility.Collapsed:Visibility.Visible;Draw();}
    }
    private void ObserveNodes()
    {
        foreach (var old in _observedNodes) old.PropertyChanged -= NodeChanged;
        _observedNodes.Clear();
        if (Draft is null) return;
        foreach (var node in Draft.Nodes) { _observedNodes.Add(node); node.PropertyChanged += NodeChanged; }
    }
    private void NodeChanged(object? sender, PropertyChangedEventArgs e) {if(!_resizing)Draw();}
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
        if (Timeline is null || _drawing || Draft?.IsRestoringSchedule==true) return;
        if (_dragOrigin is not null || _dragging) {_redrawAfterGesture=true;return;}
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
            // Reserve space after the last card/collection for the lane action.
            Timeline.Height=_hourY[24]+180;
            for(var hour=0;hour<=24;hour++)
            { Text($"{(hour+Offset/60)%24:00}:00",4,_hourY[hour]-8); Line(Left,_hourY[hour],Timeline.Width,_hourY[hour],"#38343C"); }
            if(_hourHeight>=200 || _expanded.Count>0)
                for(var hour=0;hour<24;hour++)
                    if(_hourHeight>=200 || _expanded.Any(k=>k.StartsWith(hour+":")))
                    {
                        var pixelsPerMinute=(_hourY[hour+1]-_hourY[hour])/60;
                        var step=new[]{1,2,5,10,15,30,60}.First(n=>n*pixelsPerMinute>=16);
                        for(var tickMinute=step;tickMinute<60;tickMinute+=step){var display=hour*60+tickMinute;Text($"{((display+Offset)%1440)/60:00}:{tickMinute:00}",4,Y(display)-6,"#817A89");Line(Left,Y(display),Timeline.Width,Y(display),"#292735");}
                    }
            for(var lane=0;lane<lanes;lane++)
            {
                var index=lane; var x=Left+lane*LaneWidth;
                Text(draft?.Lanes[lane] ?? "主车道",x+6,8); Line(x,Header,x,Timeline.Height,"#41404A");
                if (lane>0) { var remove=new Button { Content="×",Padding=new Thickness(4,0,4,0) }; remove.Click+=(_,_)=>{try{draft?.RemoveLane(index);}catch(InvalidOperationException ex){MessageBox.Show(ex.Message,"删除车道");}}; Canvas.SetLeft(remove,x+LaneWidth-28); Canvas.SetTop(remove,3); Timeline.Children.Add(remove); }
            }
            var add=new Button { Content="＋车道",Padding=new Thickness(4,0,4,0) }; add.Click+=(_,_)=>draft?.AddLane(); Canvas.SetLeft(add,Left+8); Canvas.SetTop(add,Timeline.Height-30); Timeline.Children.Add(add);
            if(draft is null) { Text("选择一份可编辑流程，或新建调度列表。候选保持只读预览。",Left+8,Header+30); Tray.ItemsSource=null; return; }
            Tray.ItemsSource=draft.Nodes.Where(n=>n.ScheduleMinute is null).ToArray();
            foreach(var laneGroup in CollisionLaneGroups(draft.Nodes,lanes))
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
                    var groupLane=group.Min(n=>Math.Clamp(n.LaneIndex,0,lanes-1));
                    var groupEnd=group.Max(n=>Math.Clamp(n.LaneIndex+n.LaneSpan,groupLane+1,lanes));
                    var key=DisplayMinute(group[0])/60+":"+groupLane+":"+group[0].Model.NodeId;
                    if(group.Count>1 && !_expanded.Contains(key))
                    {
                        var content=string.Join("\n",group.Take(5).Select(n=>$"{n.ScheduleTimeText}  {n.DisplayName}"));
                        var card=new Button { Content=$"▸ {group.Count} 项 · 就地展开\n"+content, Width=LaneWidth*(groupEnd-groupLane)-12,HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brush("#242634"),Foreground=Brush("#E0C479"),Padding=new Thickness(8),ToolTip="跨列相交任务合为同一集合；每项仍保留真实时刻，点击展开分钟刻度" };
                        card.Click+=(_,_)=>{foreach(var hour in group.Select(n=>DisplayMinute(n)/60).Distinct())_expanded.Add(hour+":"+groupLane+":"+group[0].Model.NodeId);Draw();}; Place(card,Left+groupLane*LaneWidth+6,Y(DisplayMinute(group[0]))); 
                    }
                    else
                    {
                        foreach(var instant in group.GroupBy(DisplayMinute))
                        {
                            var same=instant.ToArray();
                            if(same.Length>1)
                            {
                                var start=same.Min(n=>Math.Clamp(n.LaneIndex,0,lanes-1));var end=same.Max(n=>Math.Clamp(n.LaneIndex+n.LaneSpan,start+1,lanes));
                                var card=new Button {Content=$"{same[0].ScheduleTimeText} · 同刻 {same.Length} 项 ▾",Width=LaneWidth*(end-start)-12,Background=Brush("#242634"),Foreground=Brush("#E0C479")};
                                var menu=new ContextMenu();foreach(var node in same){var item=new MenuItem {Header=node.DisplayName};item.Click+=(_,_)=>draft.SelectedNode=node;menu.Items.Add(item);} card.Click+=(_,_)=>{menu.PlacementTarget=card;menu.IsOpen=true;};Place(card,Left+start*LaneWidth+6,Y(instant.Key));
                            }
                            else NodeCard(same[0],Math.Clamp(same[0].LaneIndex,0,lanes-1));
                        }
                    }
                }
            }
            DrawRoutes(draft);
            var now=DateTime.Now;var minute=(now.Hour*60+now.Minute-Offset+1440)%1440; Line(Left,Y(minute),Timeline.Width,Y(minute),"#DD7865");Text("现在 "+now.ToString("HH:mm"),4,Y(minute)+3,"#DD7865");
        }
        finally { _drawing=false; }
    }
    private void Place(FrameworkElement element,double x,double y) {Canvas.SetLeft(element,x);Canvas.SetTop(element,y);Timeline.Children.Add(element);}
    private IEnumerable<IGrouping<int,NodeEditVm>> CollisionLaneGroups(IEnumerable<NodeEditVm> nodes,int lanes)
    {
        var groups=nodes.Where(n=>n.ScheduleMinute is not null).Select(n=>new List<NodeEditVm>{n}).ToList();
        Rect Bounds(List<NodeEditVm> group)
        {
            var x=group.Min(n=>Math.Clamp(n.LaneIndex,0,lanes-1));var end=group.Max(n=>Math.Clamp(n.LaneIndex+n.LaneSpan,x+1,lanes));
            var y=group.Min(n=>Y(DisplayMinute(n)));var bottom=Math.Max(group.Max(n=>Y(DisplayMinute(n))+38),y+(group.Count==1?38:44+Math.Min(5,group.Count)*16));
            return new Rect(x*LaneWidth,y,(end-x)*LaneWidth,bottom-y);
        }
        bool changed;
        do
        {
            changed=false;
            for(var a=0;a<groups.Count && !changed;a++)for(var b=a+1;b<groups.Count;b++)
                if(Bounds(groups[a]) is var first && Bounds(groups[b]) is var second
                    && first.Left<second.Right && second.Left<first.Right && first.Top<second.Bottom && second.Top<first.Bottom)
                {groups[a].AddRange(groups[b]);groups.RemoveAt(b);changed=true;break;}
        }while(changed);
        var lanesByNode=groups.SelectMany(g=>g.Select(n=>(node:n,lane:g.Min(m=>Math.Clamp(m.LaneIndex,0,lanes-1))))).ToDictionary(x=>x.node,x=>x.lane);
        return lanesByNode.Keys.GroupBy(n=>lanesByNode[n]);
    }
    private void NodeCard(NodeEditVm node,int lane)
    {
        var runs=Host?.ActiveRuns.Where(r=>r.WorkflowId==Draft?.Draft.WorkflowId).ToArray() ?? [];
        var marker=runs.Any(r=>r.CurrentNodeId==node.NodeIdentity)?" ▶ 当前":runs.Any(r=>r.NextNodeIds.Contains(node.NodeIdentity))?" → 下一候选":"";
        var observer=string.IsNullOrWhiteSpace(node.ObservationKeyword)?"":" 📡";
        var card=new Button {Content=$"{node.ScheduleTimeText}  {(node.ScheduleModeIndex==1?"🔒":node.ScheduleModeIndex==2?"🕊":"●")}  {node.DisplayName}{observer}{marker}",Width=LaneWidth*Math.Clamp(node.LaneSpan,1,(Draft?.Lanes.Count ?? 1)-lane)-12,Height=32,Padding=new Thickness(6),HorizontalContentAlignment=HorizontalAlignment.Left,Background=Brush(marker.Length>0?"#343D3B":"#242634"),Foreground=Brush("#E0C479"),Tag=node,ToolTip=node.KindName+" · "+node.StrategySummary};
        card.Click+=(_,_)=>{if(Draft is {} draft) draft.SelectedNode=node;};card.PreviewMouseLeftButtonDown+=RememberDragStart;card.PreviewMouseMove+=NodeDrag;Place(card,Left+lane*LaneWidth+6,Y(DisplayMinute(node)));
        var grip=new System.Windows.Controls.Primitives.Thumb{Width=9,Height=25,Background=Brush("#9B8653"),Cursor=Cursors.SizeWE,ToolTip="横拖调整相邻车道覆盖"};
        double movement=0;var start=node.LaneSpan;
        grip.DragStarted+=(_,_)=>{Draft?.RememberSchedule();_resizing=true;start=node.LaneSpan;movement=0;};
        grip.DragDelta+=(_,args)=>{movement+=args.HorizontalChange;node.LaneSpan=Math.Clamp(start+(int)Math.Round(movement/LaneWidth),1,(Draft?.Lanes.Count ?? 1)-lane);card.Width=LaneWidth*node.LaneSpan-12;};
        grip.DragCompleted+=(_,_)=>{_resizing=false;Draw();};Place(grip,Left+(lane+node.LaneSpan)*LaneWidth-16,Y(DisplayMinute(node))+3);
    }
    private void DrawRoutes(WorkflowEditVm draft)
    {
        if(draft.Nodes.Count!=draft.Draft.Nodes.Count || !draft.Nodes.Select(n=>n.Model).SequenceEqual(draft.Draft.Nodes))return;
        MultiplayerHoeingAssistant.Models.WorkflowDocument doc;
        try{doc=draft.BuildSubmissionCopy();}catch(InvalidOperationException){return;} // 未完成输入只影响预览；保存仍响亮校验。
        var plan=new MultiplayerHoeingAssistant.Services.WorkflowPlan(doc);
        if(!plan.HasPaths || !plan.Preflight(true).Executable)return;
        for(var i=0;i<doc.Nodes.Count;i++)
        {
            var source=draft.Nodes[i];if(source.ScheduleMinute is null)continue;
            var sides=source.IsCondition?new[]{"branchYes","branchNo"}:new[]{"succeeded"};
            foreach(var lane in Enumerable.Range(source.LaneIndex,Math.Clamp(source.LaneSpan,1,draft.Lanes.Count-source.LaneIndex))) foreach(var side in sides)
            {
                var current=new MultiplayerHoeingAssistant.Services.WorkflowNodeOccurrence(source.NodeIdentity,i,0,0){PathLane=lane};
                var next=plan.Next(current,side);if(next is null)continue;var target=draft.Nodes[next.SequenceIndex];if(target.ScheduleMinute is null)continue;
                var x1=Left+(lane+.5)*LaneWidth;var y1=Y(DisplayMinute(source))+32;var x2=Left+((next.PathLane ?? target.LaneIndex)+.5)*LaneWidth;var y2=Y(DisplayMinute(target));
                var line=new System.Windows.Shapes.Line{X1=x1,Y1=y1,X2=x2,Y2=y2,Stroke=Brush(side=="branchNo"?"#CA7979":"#729C88"),StrokeThickness=1.5,IsHitTestVisible=false};Timeline.Children.Insert(0,line);
                Text(source.IsCondition?(side=="branchYes"?"是":"否"):"↓",x1+3,y1+2);
            }
        }
    }
    private void SyncChoice()
    {
        var wasChoosing = _choosing;
        _choosing = true;
        try { FlowChoice.SelectedItem = Host?.Flows.FirstOrDefault(f => f.WorkflowId == Host.SelectedWorkflowId); }
        finally { _choosing = wasChoosing; }
    }
    private void FlowChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_choosing || Host is null || FlowChoice.SelectedItem is not WorkflowListItemVm flow) return;
        _choosing = true;
        try { Host.SelectFlowCommand.Execute(flow); }
        finally { _choosing = false; SyncChoice(); }
    }
    private void TargetSelected(object sender,SelectionChangedEventArgs e)
    {
        if(sender is ComboBox {IsKeyboardFocusWithin:true,SelectedValue:string target,DataContext:NodeEditVm source} choice && Draft is {} draft)
        {
            if(choice.Tag as string=="observer")source.ObservationSourceNode=target;
            else draft.SetTarget(source,target,choice.Tag as string ?? "next");
        }
    }
    private void ChooseTarget(object sender,RoutedEventArgs e)
    {
        if(Draft is not {} draft || draft.SelectedNode is not {} source)return;
        var side=(sender as Button)?.Tag as string ?? "next";
        var menu=new ContextMenu();
        void Choice(string label,string? target){var item=new MenuItem{Header=label};item.Click+=(_,_)=>{draft.SetTarget(source,target,side);Draw();};menu.Items.Add(item);}
        Choice("沿本车道向下",null);Choice("结束此分支","$end");
        foreach(var node in draft.Nodes.Where(n=>n!=source))Choice("节点："+node.DisplayName,node.NodeIdentity);
        for(var lane=0;lane<draft.Lanes.Count;lane++)Choice("车道："+draft.Lanes[lane],"lane:"+lane);
        var at=new MenuItem{Header="指定车道的时刻…"};at.Click+=(_,_)=>{
            var panel=new StackPanel{Margin=new Thickness(16)};var lanes=new ComboBox{ItemsSource=draft.Lanes,SelectedIndex=0};var time=new TextBox{Text="12:00",Margin=new Thickness(0,8,0,8)};var ok=new Button{Content="连接"};
            panel.Children.Add(lanes);panel.Children.Add(time);panel.Children.Add(ok);var dialog=new Window{Title="选择去向时刻",Content=panel,Width=280,Height=190,Owner=Window.GetWindow(this),WindowStartupLocation=WindowStartupLocation.CenterOwner};
            ok.Click+=(_,_)=>{if(!TimeOnly.TryParseExact(time.Text,"HH:mm",out _)){time.ToolTip="请输入HH:mm";return;}var target="time:"+lanes.SelectedIndex+":"+time.Text;draft.SetTarget(source,target,side);dialog.Close();Draw();};dialog.ShowDialog();
        };menu.Items.Add(at);menu.PlacementTarget=(Button)sender;menu.IsOpen=true;
    }
    private void AddCondition(object sender,RoutedEventArgs e){Draft?.AddControl(false);Draw();}
    private void AddEnd(object sender,RoutedEventArgs e){Draft?.AddControl(true);Draw();}
    private void ApplyInspector(object sender,RoutedEventArgs e){Draft?.ApplyInspectorSchedule();Draw();}
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
        var menu=new ContextMenu {Background=Brush("#222535"),Foreground=Brush("#E0C479")};
        menu.Items.Add(new MenuItem {Header="连接BGI资源目录",Command=host.ConnectResourcesCommand});
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem {Header="导入单个流程…",Command=host.ImportFlowCommand});
        var export=new MenuItem {Header="导出当前流程…",IsEnabled=FlowChoice.SelectedItem is WorkflowListItemVm {IsQuarantined:false}};
        export.Click+=ExportFlow;menu.Items.Add(export);
        menu.Items.Add(new MenuItem {Header="导入整份调度列表…",Command=host.ImportScheduleCommand});
        menu.Items.Add(new MenuItem {Header="导出整份调度列表…",Command=host.ExportScheduleCommand});
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem {Header="读取旧数据并生成迁移候选…",Command=host.PrepareLegacyMigrationCommand});
        var activate=new MenuItem {Header="激活当前候选",IsEnabled=FlowChoice.SelectedItem is WorkflowListItemVm {IsCandidate:true,IsQuarantined:false}};
        activate.Click+=ActivateFlow;menu.Items.Add(activate);
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
    private void ShowCatalog(object sender,RoutedEventArgs e)
    {CatalogPane.Visibility=CatalogPane.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;InspectorPane.Visibility=CatalogPane.Visibility==Visibility.Visible?Visibility.Collapsed:Draft?.SelectedNode is null?Visibility.Collapsed:Visibility.Visible;Catalog.Focus();}
    private void TraySelected(object sender,SelectionChangedEventArgs e){if(Tray.SelectedItem is NodeEditVm node && Draft is {} draft)draft.SelectedNode=node;}
    private void AddJudgment(object sender,RoutedEventArgs e)
    {if(Draft is {} draft && sender is Button {Tag:string kind}){var node=draft.AddControl(false);node.ConditionKindIndex=int.Parse(kind);Draw();}}
    private void AddPreset(ControlPreset preset,int? minute,int lane)
    {if(Draft is {} draft){var node=draft.AddControl(preset.End);if(!preset.End)node.ConditionKindIndex=preset.ConditionKind;draft.ScheduleNode(node,minute,lane);}}
    private void RememberDragStart(object sender,MouseButtonEventArgs e)
    {_dragSource=(sender as FrameworkElement)?.Tag as NodeEditVm ?? sender;_dragOrigin=e.GetPosition(this);}
    private void FinishGesture()
    {
        _dragOrigin=null;_dragSource=null;
        if(_redrawAfterGesture && !_dragging)
        {_redrawAfterGesture=false;Dispatcher.BeginInvoke(new Action(Draw));}
    }
    private bool CanBeginDrag(object sender,MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed){FinishGesture();return false;}
        var source=(sender as FrameworkElement)?.Tag as NodeEditVm ?? sender;
        if(_resizing || !ReferenceEquals(source,_dragSource) || _dragOrigin is not {} origin)return false;
        var position=e.GetPosition(this);
        if(Math.Abs(position.X-origin.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y-origin.Y)<SystemParameters.MinimumVerticalDragDistance)return false;
        _dragOrigin=null;_dragSource=null;return true;
    }
    private void Drag(object sender,object value,DragDropEffects effects)
    {
        _dragging=true;
        try {DragDrop.DoDragDrop((DependencyObject)sender,new DataObject(value.GetType(),value),effects);}
        finally {_dragging=false;FinishGesture();Draw();}
    }
    private void PresetDrag(object sender,MouseEventArgs e)
    {if(sender is Button {Tag:string kind} button && CanBeginDrag(sender,e)){var preset=kind=="end"?Presets[^1]:Presets.First(p=>!p.End && p.ConditionKind==int.Parse(kind));Drag(button,preset,DragDropEffects.Copy);}}
    private void SearchChanged(object sender,TextChangedEventArgs e)
    {if(Catalog is null)return;var view=System.Windows.Data.CollectionViewSource.GetDefaultView(Catalog.ItemsSource);if(view is null)return;view.Filter=item=>item is CatalogSourceVm src && src.Line.Contains(ResourceSearch.Text,StringComparison.OrdinalIgnoreCase);}
    private void AddTask(object sender,RoutedEventArgs e)
    {
        if(Draft is not {} draft)return;
        var pick=new ComboBox {ItemsSource=draft.AppendSources.Cast<object>().Concat(Presets),MinWidth=360,MaxWidth=560,Style=(Style)FindResource(typeof(ComboBox)),ItemContainerStyle=(Style)FindResource(typeof(ComboBoxItem))};var time=new TextBox {Text="",Margin=new Thickness(0,8,0,8),ToolTip="HH:mm，留空暂不排程",Style=(Style)FindResource(typeof(TextBox))};var lane=new ComboBox {ItemsSource=draft.Lanes,SelectedIndex=0,Style=(Style)FindResource(typeof(ComboBox)),ItemContainerStyle=(Style)FindResource(typeof(ComboBoxItem))};var next=new Button {Content="下一步",Margin=new Thickness(0,12,0,0),Style=(Style)FindResource(typeof(Button))};var summary=new TextBlock {Text="第一步：选择任务、判断或动作",Margin=new Thickness(0,0,0,10),Foreground=Brush("#E5D9BB"),TextWrapping=TextWrapping.Wrap,MaxWidth=560};var panel=new StackPanel {Margin=new Thickness(18)};panel.Children.Add(summary);panel.Children.Add(pick);panel.Children.Add(time);panel.Children.Add(lane);panel.Children.Add(next);time.Visibility=lane.Visibility=Visibility.Collapsed;
        var window=new Window {Title="添加任务",Content=panel,SizeToContent=SizeToContent.WidthAndHeight,Owner=Window.GetWindow(this),WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush("#1C1E2A")};var step=1;
        next.Click+=(_,_)=>
        {
            if(!ReferenceEquals(Draft,draft)){window.Close();return;}
            if(step==1){if(pick.SelectedItem is not (CatalogSourceVm or ControlPreset))return;step=2;summary.Text="第二步：选择时间和车道（空=待安排）";time.Visibility=lane.Visibility=Visibility.Visible;return;}
            int? minute=null;if(time.Text.Length>0){if(!TimeOnly.TryParseExact(time.Text,"HH:mm",out var parsed)){summary.Text="请输入HH:mm，例如12:30";return;}minute=parsed.Hour*60+parsed.Minute;}
            if(step==2){step=3;summary.Text=$"第三步：{pick.SelectedItem}\n{(minute is null?"暂不排程":time.Text)} · {lane.SelectedItem}";pick.IsEnabled=time.IsEnabled=lane.IsEnabled=false;next.Content="确认添加";return;}
            if(pick.SelectedItem is CatalogSourceVm resource)draft.AddScheduledResource(resource,minute,lane.SelectedIndex);
            else if(pick.SelectedItem is ControlPreset preset)AddPreset(preset,minute,lane.SelectedIndex);
            window.Close();Draw();
        };window.ShowDialog();
    }
    private void ResourceDrag(object sender,MouseEventArgs e)
    {if(Catalog.SelectedItem is CatalogSourceVm source && Draft is not null && CanBeginDrag(sender,e))Drag(Catalog,source,DragDropEffects.Copy);}
    private void NodeDrag(object sender,MouseEventArgs e)
    {var node=(sender as FrameworkElement)?.Tag as NodeEditVm ?? Tray.SelectedItem as NodeEditVm;if(node is not null && Draft?.Nodes.Contains(node)==true && CanBeginDrag(sender,e))Drag(sender,node,DragDropEffects.Move);}
    private void TimelineDragOver(object sender,DragEventArgs e)
    {var requested=e.Data.GetDataPresent(typeof(NodeEditVm))?DragDropEffects.Move:(e.Data.GetDataPresent(typeof(CatalogSourceVm))||e.Data.GetDataPresent(typeof(ControlPreset)))?DragDropEffects.Copy:DragDropEffects.None;e.Effects=Draft is not null?requested & e.AllowedEffects:DragDropEffects.None;e.Handled=true;}
    private void TimelineDrop(object sender,DragEventArgs e)
    {if(Draft is not {} draft)return;var point=e.GetPosition(Timeline);var lane=Math.Clamp((int)((point.X-Left)/LaneWidth),0,draft.Lanes.Count-1);var minute=(MinuteAt(point.Y)+Offset)%1440;if(_hourHeight<200)minute=minute/5*5;if(e.Data.GetData(typeof(NodeEditVm)) is NodeEditVm node && draft.Nodes.Contains(node))draft.ScheduleNode(node,minute,lane);else if(e.Data.GetData(typeof(CatalogSourceVm)) is CatalogSourceVm resource)draft.AddScheduledResource(resource,minute,lane);else if(e.Data.GetData(typeof(ControlPreset)) is ControlPreset preset)AddPreset(preset,minute,lane);Draw();e.Handled=true;}
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
        var topmost=new MenuItem {Header="窗口置顶",IsCheckable=true};topmost.Checked+=(_,_)=>{if(_popup is not null)_popup.Topmost=true;};topmost.Unchecked+=(_,_)=>{if(_popup is not null)_popup.Topmost=false;};_popup.ContextMenu=new ContextMenu();_popup.ContextMenu.Items.Add(topmost);
        MinHeight=0;Height=110;TimelineContent.Visibility=Footnote.Visibility=Visibility.Collapsed;
        _popup.Closed+=(_,_)=>{if(_popup is {} closed)_popupBounds=new Rect(closed.Left,closed.Top,closed.ActualWidth,closed.ActualHeight);_popup=null;MinHeight=360;Height=double.NaN;TimelineContent.Visibility=Footnote.Visibility=Visibility.Visible;Dispatcher.BeginInvoke(new Action(UpdateViewport));};_popup.Show();
        view._choosing=true;view.FlowChoice.SelectedItem=FlowChoice.SelectedItem;view._choosing=false;
    }
}

public sealed class ScheduleChoiceLabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)
        => value switch { WorkflowListItemVm flow=>flow.Name+(flow.IsCandidate?"（只读候选）":flow.IsQuarantined?"（已隔离）":""), NodeEditVm node=>node.DisplayName, ComboBoxItem item=>item.Content, CatalogSourceVm source=>source.Line, null=>"", _=>value.ToString() ?? "" };
    public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>System.Windows.Data.Binding.DoNothing;
}
