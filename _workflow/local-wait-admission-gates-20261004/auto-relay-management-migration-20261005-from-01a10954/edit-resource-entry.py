from pathlib import Path
import hashlib,json
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954'
paths=['_workflow/local-wait-admission-gates-20261004/plan.json','BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceProtocol.cs','BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceCommandPlane.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/Views/MistletoePage.xaml']
before=[]
for p in paths:
    b=(root/p).read_bytes(); before.append(dict(path=p,bytes=len(b),lines=b.count(b'\n'),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b))
(base/'resource-before.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
def edit(p,a,b):
    path=root/p; raw=path.read_bytes(); newline='\r\n' if b'\r\n' in raw else '\n'; text=raw.decode('utf-8').replace('\r\n','\n')
    assert text.count(a)==1,(p,a,text.count(a)); text=text.replace(a,b)
    path.write_bytes(text.replace('\n',newline).encode('utf-8'))
p=root/paths[0]; raw=p.read_bytes(); data=json.loads(raw.decode('utf-8-sig'))
assert 'delivery_20261005_management_migration' not in data
data['delivery_20261005_management_migration']={'policy':'mistletoe-release-first-20261005-v2','review_rules_commit':'c0f40993b','storage_policy':'mistletoe-storage-limits-20261005-v1','goal':'01a1097f-f9e2-7db2-9a8a-bf5675da9d9e active native readback after resume','admission':'本版管理资源引用编辑/更新 + 正常迁移激活回退；页面节点无编辑引用按钮、迁移只有演练；用户不能消费正常资源/旧数据。最小接线复用BGI权威编辑器、revision/epoch守卫、旧映射和事务；定向反例证明冲突零覆盖、正常样例保留顺序/重复/过滤/once、回退。下一项固定时刻nextDay/LocalWait截止组合，再统一运行产物。','matrix':['有效引用→BGI原生编辑页，仅打开不启动','过期/缺修订、缺taskId/资源、离线/旧能力→明确拒绝，草稿不变','资源编辑后→显式读取权威新修订→草稿保存，冻结运行不改','正常迁移→备份/合法源映射→事务真实激活→失败与可恢复回退；实际User待具体产物及授权门'],'tests_reason':'无现有实际管理编辑消费点证据；新夹具决定入口是否可接、过期引用是否拒绝、更新是否保留单项身份。旧不变测试不重复。','review':'新增请求0，保留最后最多1Sol/high综合后审给统一候选；原报告/opening/预算不变','readonly_agents':'当前缺口位于同一编辑调用链，直接追查成本低，不派额外探索Agent；正式综合独立复核保持。','bundle':'verify-bundle报告drift原样保留，不改工具/旧receipt；受控构建采用现有storage_limits.Session与process_runner，普通来源不冒称认证'}
p.write_bytes((json.dumps(data,ensure_ascii=False,indent=2)+'\n').replace('\n','\r\n' if b'\r\n' in raw else '\n').encode('utf-8-sig' if raw.startswith(b'\xef\xbb\xbf') else 'utf-8'))
edit(paths[1],'    public const string ConfigDescribe = "ext.config.describe";','    public const string ConfigDescribe = "ext.config.describe";\n    public const string ConfigOpenResourceEditor = "ext.config.openResourceEditor";')
edit(paths[1],'        or ConfigRemoteEditorResult or ConfigApplyGroup','        or ConfigRemoteEditorResult or ConfigApplyGroup or ConfigOpenResourceEditor')
edit(paths[1],'                ["config.revision"] = true,','                ["config.revision"] = true,\n                ["config.resourceEditor"] = true,')
edit(paths[2],'            ExternalInterfaceOperations.ConfigDescribe or ExternalInterfaceOperations.ConfigApplyTaskState =>','            ExternalInterfaceOperations.ConfigOpenResourceEditor =>\n                await ExternalResourceEditor.DispatchAsync(request),\n            ExternalInterfaceOperations.ConfigDescribe or ExternalInterfaceOperations.ConfigApplyTaskState =>')
host='''    internal IResourceCatalogTransport? ResourceEditorTransportForTest { get; set; }

    private IResourceCatalogTransport GetResourceEditorTransport()
    {
        if (!_localExecutionCapability()) throw new InvalidOperationException("资源编辑仅在执行端可用");
        if (ResourceEditorTransportForTest is { } test) return test;
        var client = _clientAccessor() ?? throw new InvalidOperationException("执行端BGI未连接");
        return new BgiExternalCatalogTransport(client);
    }

    public Task OpenResourceEditorAsync(WorkflowNode node)
        => WorkflowResourceEditor.OpenAsync(node, GetResourceEditorTransport());

    public Task<string> ReadResourceRevisionAsync(WorkflowNode node)
        => WorkflowResourceEditor.ReadRevisionAsync(node, GetResourceEditorTransport());

'''
edit(paths[3],'    public string SaveFlow(WorkflowDocument doc, string? expectedRevision)',host+'    public string SaveFlow(WorkflowDocument doc, string? expectedRevision)')
panel='''    public RelayCommand OpenResourceEditorCommand => new(async p =>
    {
        if (p is not NodeEditVm node || Editing?.Nodes.Contains(node) != true) return;
        try
        {
            await _host.OpenResourceEditorAsync(node.Model);
            SetStatus("已打开BGI中的引用资源。保存资源后，请更新引用修订再保存流程。", false);
        }
        catch (Exception ex) { SetStatus("打开资源失败：" + ex.Message, true); }
    });

    public RelayCommand RefreshResourceReferenceCommand => new(async p =>
    {
        if (p is not NodeEditVm node || Editing?.Nodes.Contains(node) != true) return;
        var draft = Editing;
        try
        {
            var revision = await _host.ReadResourceRevisionAsync(node.Model);
            if (!ReferenceEquals(Editing, draft) || !draft.Nodes.Contains(node)) return;
            node.Model.Ref!.Revision = revision;
            node.NotifyReferenceChanged();
            SetStatus("已更新草稿中的引用修订，请保存流程；正在执行的资源保持原身份。", false);
        }
        catch (Exception ex) { SetStatus("更新引用失败：" + ex.Message, true); }
    });

'''
edit(paths[4],'    public RelayCommand NewFlowCommand => new(_ =>',panel+'    public RelayCommand NewFlowCommand => new(_ =>')
edit(paths[5],'    public string ConfigName => Model.Ref?.Config ?? "（未绑定资源）";','    public string ConfigName => Model.Ref?.Config ?? "（未绑定资源）";\n    internal void NotifyReferenceChanged() => OnPropertyChanged(nameof(RevisionShort));')
edit(paths[6],'                                                            <Button Content="↑" Style="{DynamicResource BtnGhost}" Command="{Binding DataContext.TaskCenter.Editing.MoveNodeUpCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"', '''                                                            <Button Content="编辑资源" Style="{DynamicResource BtnGhost}" Command="{Binding DataContext.TaskCenter.OpenResourceEditorCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}" Margin="0,0,4,0"/>
                                                            <Button Content="更新引用" Style="{DynamicResource BtnGhost}" Command="{Binding DataContext.TaskCenter.RefreshResourceReferenceCommand, RelativeSource={RelativeSource AncestorType=UserControl}}" CommandParameter="{Binding}" Margin="0,0,4,0"/>
                                                            <Button Content="↑" Style="{DynamicResource BtnGhost}" Command="{Binding DataContext.TaskCenter.Editing.MoveNodeUpCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"''')
for row in before:
    b=(root/row['path']).read_bytes(); assert len(b)>=row['bytes']; row['after_bytes']=len(b);row['after_sha256']=hashlib.sha256(b).hexdigest()
(base/'resource-edit-observation.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
print('edited',len(paths))
