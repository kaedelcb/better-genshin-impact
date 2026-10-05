from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
def edit(rel,changes):
    p=root/rel;data=p.read_bytes();nl='\r\n' if b'\r\n' in data else '\n';text=data.decode('utf-8-sig').replace('\r\n','\n')
    for old,new in changes:
        assert text.count(old)==1,(rel,old[:70],text.count(old));text=text.replace(old,new)
    encoded=text.replace('\n',nl).encode();encoded=(b'\xef\xbb\xbf'+encoded) if data.startswith(b'\xef\xbb\xbf') else encoded
    assert len(encoded)>len(data)*.98;s.write(p,encoded,mode='wb')
with s.Session(root,'full-product-task-center-ui-implementation') as budget:
    budget.track(base)
    edit('MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs',[
      ('WorkflowRunState.Waiting => $"等待触发（{run.Wait?.NextTriggerAt:MM-dd HH:mm}）",','WorkflowRunState.Waiting => $"等待触发（{run.Wait?.NextTriggerAt:MM-dd HH:mm}）",\n            WorkflowRunState.LocalWaitParking => "本地等待停驻（可停止或显式恢复）",'),
      ('CanStop = run.State is WorkflowRunState.Running or WorkflowRunState.Waiting or WorkflowRunState.Paused or WorkflowRunState.Completing or WorkflowRunState.Unknown;','CanStop = run.State is WorkflowRunState.Running or WorkflowRunState.Waiting or WorkflowRunState.Paused or WorkflowRunState.Completing or WorkflowRunState.Unknown or WorkflowRunState.LocalWaitParking;'),
      ('CanResume = run.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused;','CanResume = run.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused or WorkflowRunState.LocalWaitParking;')])
    edit('MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs',[
      ('    private void Renumber()','''    /// <summary>同一草稿内拖拽或插入；保留节点对象、稳定身份及全部未知字段。</summary>
    public void MoveNodeTo(NodeEditVm source, NodeEditVm target, bool after)
    {
        var from = Nodes.IndexOf(source);
        var to = Nodes.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        var destination = to + (after ? 1 : 0);
        if (from < destination) destination--;
        if (destination == from) return;
        Nodes.Move(from, destination);
        var model = Draft.Nodes[from];
        Draft.Nodes.RemoveAt(from);
        Draft.Nodes.Insert(destination, model);
        Renumber();
    }

    public RelayCommand InsertNodeBeforeCommand => new(p =>
    {
        if (p is not NodeEditVm target || !Nodes.Contains(target)) return;
        var count = Nodes.Count;
        AppendNodeCommand.Execute(null);
        if (Nodes.Count == count + 1) MoveNodeTo(Nodes[^1], target, false);
    });

    private void Renumber()''')])
    edit('MultiplayerHoeingAssistant/Views/MistletoePage.xaml',[
      ('节点（↑↓ 调序，无拖拽编辑器；顺序即执行序）','节点（拖动 ⠿ 或 ↑↓ 调序；保存后可在运行卡片重载修订）'),
      ('<Border BorderBrush="{DynamicResource GoldLine}" BorderThickness="1" CornerRadius="6" Padding="10,8" Margin="0,0,0,6">','<Border BorderBrush="{DynamicResource GoldLine}" BorderThickness="1" CornerRadius="6" Padding="10,8" Margin="0,0,0,6" AllowDrop="True" DragOver="TaskNode_DragOver" Drop="TaskNode_Drop">'),
      ('<TextBlock Text="{Binding Index, StringFormat=\'{}{0}.\'}"','<TextBlock Text="⠿" Cursor="SizeAll" ToolTip="拖动调整节点顺序" Margin="0,0,8,0" PreviewMouseLeftButtonDown="TaskNode_MouseDown" MouseMove="TaskNode_MouseMove"/>\n                                                            <TextBlock Text="{Binding Index, StringFormat=\'{}{0}.\'}"'),
      ('<Button Content="编辑资源" Style="{DynamicResource BtnGhost}"','<Button Content="在此插入" ToolTip="把下方所选资源插入到此节点之前，保存后重载修订生效" Style="{DynamicResource BtnGhost}" Command="{Binding DataContext.TaskCenter.Editing.InsertNodeBeforeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}" Margin="0,0,4,0"/>\n                                                            <Button Content="编辑资源" Style="{DynamicResource BtnGhost}"')])
    edit('MultiplayerHoeingAssistant/Views/MistletoePage.xaml.cs',[
      ('    // ================= 拖拽重排 =================','''    private NodeEditVm? _taskDragCandidate;
    private Point _taskDragStart;

    private void TaskNode_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _taskDragCandidate = (sender as FrameworkElement)?.DataContext as NodeEditVm;
        _taskDragStart = e.GetPosition(null);
        e.Handled = true;
    }

    private void TaskNode_MouseMove(object sender, MouseEventArgs e)
    {
        if (_taskDragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _taskDragStart.X) < 6 && Math.Abs(pos.Y - _taskDragStart.Y) < 6) return;
        var node = _taskDragCandidate;
        _taskDragCandidate = null;
        if (sender is not FrameworkElement handle) return;
        StartDragAutoScroll();
        try { DragDrop.DoDragDrop(handle, new DataObject(typeof(NodeEditVm), node), DragDropEffects.Move); }
        finally { StopDragAutoScroll(); }
    }

    private void TaskNode_DragOver(object sender, DragEventArgs e)
    {
        var node = e.Data.GetData(typeof(NodeEditVm)) as NodeEditVm;
        e.Effects = node is not null && Vm?.TaskCenter.Editing?.Nodes.Contains(node) == true
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TaskNode_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NodeEditVm target } card
            && e.Data.GetData(typeof(NodeEditVm)) is NodeEditVm source)
            Vm?.TaskCenter.Editing?.MoveNodeTo(source, target, e.GetPosition(card).Y > card.ActualHeight / 2);
        e.Handled = true;
    }

    // ================= 拖拽重排 =================''')])
    facts=[]
    for row in json.loads((base/'before.json').read_text()):
        p=root/row['path'];facts.append(dict(path=row['path'],bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
    s.write(base/'after-ui.json',json.dumps(facts,indent=2).encode())
