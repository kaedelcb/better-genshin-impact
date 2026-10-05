from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
with s.Session(root,'full-product-observed-ui-refinement') as budget:
    budget.track(base)
    changes={
      'MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs':[
        ('public sealed record StartPointVm(string WorkflowId, string Revision, string NodeId, string Label);','public sealed record StartPointVm(string WorkflowId, string Revision, string NodeId, string Label)\n    {\n        public override string ToString() => Label;\n    }')],
      'MultiplayerHoeingAssistant/Views/MistletoePage.xaml':[
        ('SelectedItem="{Binding TaskCenter.SelectedStartPoint, Mode=TwoWay}" Style="{StaticResource GoldComboBox}" MinWidth="240"','SelectedItem="{Binding TaskCenter.SelectedStartPoint, Mode=TwoWay}" Style="{StaticResource GoldComboBox}" Width="360" ToolTip="{Binding TaskCenter.SelectedStartPoint.Label}"'),
        ('PreviewMouseLeftButtonDown="TaskNode_MouseDown" MouseMove="TaskNode_MouseMove"','PreviewMouseLeftButtonDown="TaskNode_MouseDown" PreviewMouseLeftButtonUp="TaskNode_MouseUp" MouseMove="TaskNode_MouseMove"')],
      'MultiplayerHoeingAssistant/Views/MistletoePage.xaml.cs':[
        ('        _taskDragStart = e.GetPosition(null);\n        e.Handled = true;','        _taskDragStart = e.GetPosition(null);\n        (sender as FrameworkElement)?.CaptureMouse();\n        e.Handled = true;'),
        ('    private void TaskNode_MouseMove(object sender, MouseEventArgs e)','    private void TaskNode_MouseUp(object sender, MouseButtonEventArgs e)\n    {\n        _taskDragCandidate = null;\n        (sender as FrameworkElement)?.ReleaseMouseCapture();\n    }\n\n    private void TaskNode_MouseMove(object sender, MouseEventArgs e)'),
        ('        if (sender is not FrameworkElement handle) return;\n        StartDragAutoScroll();','        if (sender is not FrameworkElement handle) return;\n        handle.ReleaseMouseCapture();\n        StartDragAutoScroll();')]
    }
    for rel,replacements in changes.items():
        p=root/rel;raw=p.read_bytes();s.write(base/'before-refine'/rel,raw);nl='\r\n' if b'\r\n' in raw else '\n';t=raw.decode('utf-8-sig').replace('\r\n','\n')
        for old,new in replacements:assert t.count(old)==1,(rel,old);t=t.replace(old,new)
        data=t.replace('\n',nl).encode();data=(b'\xef\xbb\xbf'+data) if raw.startswith(b'\xef\xbb\xbf') else data;s.write(p,data,mode='wb')
    s.write(base/'refine-admission.json',json.dumps(dict(function='C02/C06 usable interaction UI',evidence='actual screenshot: selection record string expanded combo and hid start button; drag handle missed movement',minimum='bounded width/readable selected label; capture and release mouse around drag',core_changes=False,review_remaining=0),indent=2).encode())
