from pathlib import Path
import sys,json
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
with s.Session(root,'full-product-live-drag-gesture') as budget:
    budget.track(base)
    p=root/'MultiplayerHoeingAssistant/Views/MistletoePage.xaml.cs';raw=p.read_bytes();s.write(base/'before-gesture/MistletoePage.xaml.cs',raw)
    nl='\r\n' if b'\r\n' in raw else '\n';t=raw.decode('utf-8-sig').replace('\r\n','\n')
    start=t.index('    private NodeEditVm? _taskDragCandidate;');end=t.index('    private void TaskNode_DragOver',start)
    t=t[:start]+'''    private void TaskNode_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NodeEditVm node } handle
            || Vm?.TaskCenter.Editing?.Nodes.Contains(node) != true) return;
        // 手柄只负责拖拽，按下即进入原生拖拽；点击原位或Esc均不改变草稿。
        e.Handled = true;
        StartDragAutoScroll(TaskCenterScroller);
        try { DragDrop.DoDragDrop(handle, new DataObject(typeof(NodeEditVm), node), DragDropEffects.Move); }
        finally { StopDragAutoScroll(); }
    }

'''+t[end:]
    t=t.replace('private System.Windows.Threading.DispatcherTimer? _dragScrollTimer;', 'private System.Windows.Threading.DispatcherTimer? _dragScrollTimer;\n    private ScrollViewer? _dragScrollTarget;')
    t=t.replace('private void StartDragAutoScroll()\n    {','private void StartDragAutoScroll(ScrollViewer? target = null)\n    {\n        _dragScrollTarget = target ?? FlowScroller;')
    t=t.replace('_dragScrollTimer?.Stop();','_dragScrollTimer?.Stop();\n        _dragScrollTarget = null;')
    t=t.replace('            var p = FlowScroller.PointFromScreen(new Point(pt.X, pt.Y));','            var scroller = _dragScrollTarget;\n            if (scroller is null || !scroller.IsVisible) return;\n            var p = scroller.PointFromScreen(new Point(pt.X, pt.Y));')
    t=t.replace('FlowScroller.ActualWidth','scroller.ActualWidth').replace('FlowScroller.ViewportHeight','scroller.ViewportHeight').replace('FlowScroller.ScrollToVerticalOffset(FlowScroller.VerticalOffset','scroller.ScrollToVerticalOffset(scroller.VerticalOffset')
    data=t.replace('\n',nl).encode();data=(b'\xef\xbb\xbf'+data) if raw.startswith(b'\xef\xbb\xbf') else data;s.write(p,data,mode='wb')
    p=root/'MultiplayerHoeingAssistant/Views/MistletoePage.xaml';raw=p.read_bytes();s.write(base/'before-gesture/MistletoePage.xaml',raw)
    t=raw.decode('utf-8-sig');old='<ScrollViewer VerticalScrollBarVisibility="Auto">';anchor=t.index('<!-- BGI 任务状态判断卡片')
    at=t.rfind(old,0,anchor);assert at!=-1
    t=t[:at]+t[at:].replace(old,'<ScrollViewer x:Name="TaskCenterScroller" VerticalScrollBarVisibility="Auto">',1)
    old='PreviewMouseLeftButtonDown="TaskNode_MouseDown" PreviewMouseLeftButtonUp="TaskNode_MouseUp" MouseMove="TaskNode_MouseMove"';assert t.count(old)==1
    t=t.replace(old,'Width="18" FontSize="14" TextAlignment="Center" PreviewMouseLeftButtonDown="TaskNode_MouseDown"')
    data=t.encode();data=(b'\xef\xbb\xbf'+data) if raw.startswith(b'\xef\xbb\xbf') else data;s.write(p,data,mode='wb')
    s.write(base/'gesture-admission.json',json.dumps(dict(function='C02 drag authoring',evidence='actual drag followed by save left original node sequence/revision unchanged',minimum='explicit handle native gesture on press, larger handle, task-center edge scrolling; same identity-preserving drop method',no_execution_changes=True),indent=2).encode())
