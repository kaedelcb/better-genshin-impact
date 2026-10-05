from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
paths=['BetterGenshinImpact/Service/ExternalInterface/ExternalInterfaceConfigurationPlane.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs']
with s.Session(root,'native-single-ui-gap-'+sys.argv[1]):
    if sys.argv[1]=='begin':
        facts=[]
        for rel in paths:
            p=root/rel;data=p.read_bytes();s.write(base/'single-before'/rel,data);facts.append(dict(path=rel,bytes=len(data),lines=data.count(b'\n'),sha256=hashlib.sha256(data).hexdigest(),bom=data.startswith(b'\xef\xbb\xbf'),crlf=b'\r\n' in data))
        s.write(base/'single-before.json',json.dumps(facts,indent=2).encode())
        p=root/'_workflow/local-wait-admission-gates-20261004/plan.json';data=p.read_bytes();s.write(base/'single-plan-before.json',data);v=json.loads(data.decode('utf-8-sig'))
        v['native_single_ui_closeout_admission']={'policy':'mistletoe-release-first-20261005-v2','function':'C02 native single/whole-dragon mixed authoring','gap':'actual describe reports false while task.single.native=true; append UI excludes all singles','entry':'ext.config.describe and WorkflowEditVm.RefreshCatalog/AppendNodeCommand','consequence':'cannot select native single nodes from normal task-center UI','minimum':'real-time supported task projection + owner config/taskId/revision-preserving append; cache/unknown exclusion; targeted red-green + actual UI/IPC readback','next':'same complete candidate and user validation content','severity':'important','independent_review_remaining':0,'independent_closed':False}
        encoded=json.dumps(v,ensure_ascii=False,indent=2).encode('utf-8');assert len(encoded)>len(data);s.write(p,encoded,mode='wb')
    elif sys.argv[1]=='implement':
        p=root/paths[0];data=p.read_bytes();old=b'singleExecutionSupported = t.LegacyIndex != null';assert data.count(old)==1
        s.write(p,data.replace(old,b'singleExecutionSupported = t.LegacyIndex != null || t.Schema == "native"'),mode='wb')
        p=root/paths[1];data=p.read_bytes();nl='\r\n' if b'\r\n' in data else '\n';text=data.decode('utf-8-sig')
        text=text.replace('SingleTask 禁追加','实时支持的单项可追加，缓存单项仅展示')
        old='foreach (var e in snap.Entries.Where(e => e.Kind != TaskCenterResourceKind.SingleTask)) // 单项禁追加'
        assert text.count(old)==1
        text=text.replace(old,'foreach (var e in snap.Entries.Where(e => e.Kind != TaskCenterResourceKind.SingleTask'+nl+'            || !snap.IsDegraded && !e.IsFromCache && e.SingleExecutionSupported && e.Enabled == true'+nl+'            && new CatalogSourceVm(e).TaskId is { Length: > 0 }))')
        text=text.replace('TaskCenterResourceKind.ConfigGroup => "resource.configGroup",','TaskCenterResourceKind.ConfigGroup => "resource.configGroup",'+nl+'                TaskCenterResourceKind.SingleTask => "resource.singleTask",')
        text=text.replace('Config = src.DisplayName,   // BGI 启动合同寻址名（与 R1 产物一致）','Config = src.Kind == TaskCenterResourceKind.SingleTask ? src.OwnerConfig : src.DisplayName,'+nl+'                TaskId = src.TaskId, // 单项始终绑定所属配置及稳定身份，不能按显示名称寻址')
        text=text.replace('SingleTask 已在填充时排除','单项仅接受实时可执行身份')
        old='public string? ConfigRevision => _entry.ConfigRevision;';assert text.count(old)==1
        text=text.replace(old,old+nl+'''    public string? OwnerConfig => _entry.OwnerConfig;
    public string? TaskId
    {
        get
        {
            if (_entry.Kind != TaskCenterResourceKind.SingleTask || string.IsNullOrWhiteSpace(_entry.OwnerConfig)) return null;
            var prefix = "single:" + _entry.OwnerConfig + ":";
            return _entry.StableId.StartsWith(prefix, StringComparison.Ordinal)
                ? _entry.StableId[prefix.Length..] : null;
        }
    }'''.replace('\n',nl))
        text=text.replace('(_entry.Kind == TaskCenterResourceKind.OneDragonConfig ? "整龙" : "配置组")','(_entry.Kind == TaskCenterResourceKind.OneDragonConfig ? "整龙" : _entry.Kind == TaskCenterResourceKind.SingleTask ? "单项" : "配置组")')
        encoded=text.encode('utf-8');encoded=(b'\xef\xbb\xbf'+encoded) if data.startswith(b'\xef\xbb\xbf') else encoded
        assert len(encoded)>len(data);s.write(p,encoded,mode='wb')
        s.write(base/'single-after.json',json.dumps([dict(path=rel,sha256=hashlib.sha256((root/rel).read_bytes()).hexdigest(),bytes=(root/rel).stat().st_size) for rel in paths],indent=2).encode())
