from pathlib import Path
import hashlib,json
ROOT=Path(__file__).resolve().parents[2]
BACK=Path(__file__).resolve().parent/'flow-context/red'
changes={}
def change(rel,old,new):
 p=ROOT/rel
 if rel not in changes:
  raw=p.read_bytes(); assert raw==(BACK/('original-'+p.name)).read_bytes(),rel
  changes[rel]=raw
 raw=changes[rel]; newline='\r\n' if b'\r\n' in raw else '\n'
 a=old.replace('\n',newline).encode();b=new.replace('\n',newline).encode()
 assert raw.count(a)==1,(rel,old[:70],raw.count(a));changes[rel]=raw.replace(a,b)
vm='MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs'
parts='MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs'
undo='MultiplayerHoeingAssistant/ViewModels/WorkflowUndoVm.cs'
view='MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs'
xaml='MultiplayerHoeingAssistant/Views/ScheduleListView.xaml'
change(vm,'    public ObservableCollection<WorkflowListItemVm> Flows { get; } = [];','''    public ObservableCollection<WorkflowListItemVm> Flows { get; } = [];
    private string? _selectedWorkflowId;
    public string? SelectedWorkflowId { get => _selectedWorkflowId; private set => SetProperty(ref _selectedWorkflowId, value); }''')
change(vm,'        if (Editing is null) return true;\n        SetStatus("已有未保存的编辑草稿，请先保存或放弃后再操作。", isError: true);','''        if (Editing is null) return true;
        try
        {
            // 未改动且磁盘修订仍一致的草稿可以切换；原始无效输入、新建和冲突草稿必须保留。
            if (!Editing.HasUnsavedChanges && _host.LoadFlowSnapshot(Editing.Draft.WorkflowId!).Revision == Editing.BaseRevision)
            { Editing = null; return true; }
        }
        catch (Exception) { } // 无法复核时保留草稿，不把未知视为安全丢弃。
        SetStatus("已有未保存或修订冲突的编辑草稿，请先保存或放弃后再操作。", isError: true);''')
change(vm,'        if (p is not WorkflowListItemVm item) return;\n        BeginPreview(item.WorkflowId);','''        if (p is not WorkflowListItemVm item || !GuardNoOpenDraft()) return;
        BeginPreview(item.WorkflowId);''')
change(vm,'    public RelayCommand EditFlowCommand => new(p =>','''    public RelayCommand SelectFlowCommand => new(p =>
    {
        if (p is not WorkflowListItemVm item || Editing?.Draft.WorkflowId == item.WorkflowId) return;
        if (!GuardNoOpenDraft()) return;
        if (item.CanEdit) BeginEdit(item.WorkflowId); else BeginPreview(item.WorkflowId);
    });

    public RelayCommand EditFlowCommand => new(p =>''')
change(vm,'    public WorkflowEditVm? Editing { get => _editing; private set { SetProperty(ref _editing, value); OnPropertyChanged(nameof(IsEditing)); } }','''    public WorkflowEditVm? Editing { get => _editing; private set { SetProperty(ref _editing, value); if (value is not null) SelectedWorkflowId = value.Draft.WorkflowId; OnPropertyChanged(nameof(IsEditing)); } }''')
change(vm,'    public WorkflowPreviewVm? Previewing { get => _previewing; private set { SetProperty(ref _previewing, value); OnPropertyChanged(nameof(IsPreviewing)); } }','''    public WorkflowPreviewVm? Previewing { get => _previewing; private set { SetProperty(ref _previewing, value); if (value is not null) SelectedWorkflowId = value.WorkflowId; OnPropertyChanged(nameof(IsPreviewing)); } }''')
change(vm,'            Editing?.RefreshCatalog(_host.Catalog);','''            Editing?.RefreshCatalog(_host.Catalog);
            if (Previewing is { } preview)
            {
                var current = _host.LoadFlowSnapshot(preview.WorkflowId);
                if (current.Revision != preview.Revision) BeginPreview(preview.WorkflowId);
            }
            OnPropertyChanged(nameof(SelectedWorkflowId));''')
change(vm,'    public string Name => _entry.Name;','''    public string Name => _entry.Name;
    public string ChoiceLabel => Name + (IsCandidate ? "（只读候选）" : IsQuarantined ? "（已隔离）" : "");''')
change(parts,'        RefreshCatalog(catalog);\n        InitializeSchedule();','''        RefreshCatalog(catalog);
        InitializeSchedule();
        _initialEditState = CaptureEditState();''')
change(undo,'    private bool _scheduleReady,_restoringUndo,_scheduleChanging;','''    private bool _scheduleReady,_restoringUndo,_scheduleChanging;
    private readonly string _initialEditState;
    internal bool HasUnsavedChanges => CaptureEditState() != _initialEditState;
    // Compare raw editor values too: invalid or whitespace-only input must not disappear during a switch.
    private string CaptureEditState() => JsonSerializer.Serialize(new
    {
        Document = Draft,
        Flow = Fields(this).Where(p => p.Key is not ("AppendSourceIndex" or "AppendStatusText")).ToDictionary(p => p.Key, p => p.Value),
        Nodes = Nodes.Select(n => Fields(n).Where(p => p.Key != "Index").ToDictionary(p => p.Key, p => p.Value)).ToArray(),
        Lanes = Lanes.ToArray(),
    });''')
change(parts,'    public string Title { get; private set; } = "";\n    public IReadOnlyList<string> Lines','''    public string Title { get; private set; } = "";
    internal string WorkflowId { get; private set; } = "";
    internal string Revision { get; private set; } = "";
    public IReadOnlyList<string> Lines''')
change(parts,'"candidate-ready 迁移候选：只读预览；正式激活由 R5 事务迁移完成（D13），此处不提供激活/另存。"','"candidate-ready 候选：只读预览；可从流程管理激活，完成资源与账号校验后再启动。"')
change(parts,'return new WorkflowPreviewVm { Title = $"预览 · {doc.Name}", Lines = lines, Notice = notice };','return new WorkflowPreviewVm { WorkflowId = doc.WorkflowId!, Revision = revision, Title = $"预览 · {doc.Name}", Lines = lines, Notice = notice };')
change(view,'''        if (Draft?.Draft.WorkflowId is { } id && Host?.Flows.FirstOrDefault(f=>f.WorkflowId==id) is { } flow)
        { _choosing=true;FlowChoice.SelectedItem=flow;_choosing=false; }''','''        SyncChoice();''')
change(view,'''        if (e.PropertyName != "Editing") return;
        Observe();
        if (Draft?.Draft.WorkflowId is { } id && Host?.Flows.FirstOrDefault(f=>f.WorkflowId==id) is { } flow)
        { _choosing=true; FlowChoice.SelectedItem=flow; _choosing=false; }''','''        if (e.PropertyName is not ("Editing" or "Previewing" or "SelectedWorkflowId")) return;
        Observe();''')
change(view,'''    private void FlowChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_choosing || Host is null || FlowChoice.SelectedItem is not WorkflowListItemVm flow) return;
        if (Draft?.Draft.WorkflowId == flow.WorkflowId) return;
        if(flow.CanEdit)Host.EditFlowCommand.Execute(flow);else Host.PreviewFlowCommand.Execute(flow);
        if(Draft?.Draft.WorkflowId is { } id && id!=flow.WorkflowId)
        { _choosing=true;FlowChoice.SelectedItem=Host.Flows.FirstOrDefault(f=>f.WorkflowId==id);_choosing=false; }
    }''','''    private void SyncChoice()
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
    }''')
change(xaml,'  <local:ScheduleChoiceLabelConverter x:Key="ChoiceLabel"/>','''  <local:ScheduleChoiceLabelConverter x:Key="ChoiceLabel"/>
  <DataTemplate x:Key="ChoiceText"><TextBlock Text="{Binding Converter={StaticResource ChoiceLabel}}"/></DataTemplate>''')
change(xaml,'<TextBlock Text="{Binding SelectedItem, RelativeSource={RelativeSource TemplatedParent}, Converter={StaticResource ChoiceLabel}}"/>','<ContentPresenter Content="{Binding SelectedItem, RelativeSource={RelativeSource TemplatedParent}}" ContentTemplate="{Binding ItemTemplate, RelativeSource={RelativeSource TemplatedParent}, TargetNullValue={StaticResource ChoiceText}}"/>')
change(xaml,'<ComboBox.ItemTemplate><DataTemplate><TextBlock Text="{Binding Converter={StaticResource ChoiceLabel}}"/></DataTemplate></ComboBox.ItemTemplate>','<ComboBox.ItemTemplate><DataTemplate><TextBlock Text="{Binding ChoiceLabel}"/></DataTemplate></ComboBox.ItemTemplate>')
assert json.loads((BACK/'result.json').read_text())['build']==0
for rel,raw in changes.items():
 p=ROOT/rel;assert p.read_bytes()==(BACK/('original-'+p.name)).read_bytes(),rel
for rel,raw in changes.items():
 p=ROOT/rel;before=p.read_bytes();assert len(raw)>len(before)*.9,rel;p.write_bytes(raw)
 print(rel,len(before),len(raw),hashlib.sha256(raw).hexdigest())
