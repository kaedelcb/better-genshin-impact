from pathlib import Path
import sys,json
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent
with s.Session(root,'full-product-authoring-catalog-connect') as budget:
    budget.track(base)
    changes={
     'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs':[
      ('    public LocalWaitQueueStore LocalWaitQueue { get; }','''    /// <summary>显式连接作者目录；复用执行环境连接，只读资源，不创建或启动运行。</summary>
    public async Task<HostActionResult> ConnectResourceCatalogAsync()
    {
        lock (_gate)
        {
            if (_shutdown) return HostActionResult.Unavailable("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } blocked) return HostActionResult.Unavailable(blocked);
        }
        try
        {
            if (await EnsureExecutionEnvironmentAsync(_shutdownCts.Token).ConfigureAwait(false) is { } error)
                return HostActionResult.Unavailable(error);
            var snapshot = await _catalog.RefreshAsync(_shutdownCts.Token).ConfigureAwait(false);
            return snapshot.IsDegraded
                ? HostActionResult.Unavailable("资源目录未就绪：" + snapshot.DegradedReason)
                : HostActionResult.Effective($"已连接资源目录（{snapshot.Entries.Count}项），未启动任何任务。");
        }
        catch (Exception ex) { return HostActionResult.Unavailable("连接资源失败：" + ex.Message); }
    }

    public LocalWaitQueueStore LocalWaitQueue { get; }''')],
     'MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs':[
      ('    public RelayCommand PrepareLegacyMigrationCommand','''    private bool _connectingResources;
    public RelayCommand ConnectResourcesCommand => new(async _ =>
    {
        if (_connectingResources) return;
        _connectingResources = true;
        SetStatus("正在连接BGI资源目录…", false);
        try { ApplyActionResult(await _host.ConnectResourceCatalogAsync()); }
        finally { _connectingResources = false; Refresh(); }
    });

    public RelayCommand PrepareLegacyMigrationCommand''')],
     'MultiplayerHoeingAssistant/Views/MistletoePage.xaml':[
      ('                                    <TextBlock Text="🗂️  流程列表"','''                                    <Grid.RowDefinitions>
                                        <RowDefinition Height="Auto"/>
                                        <RowDefinition Height="Auto"/>
                                    </Grid.RowDefinitions>
                                    <TextBlock Text="🗂️  流程列表"'''),
      ('                                    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">\n                                        <Button Content="刷新"','''                                    <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Left" Margin="0,8,0,0">
                                        <Button Content="连接资源" ToolTip="连接BGI资源；未运行时启动已配置的BGI，不执行任务。" Style="{DynamicResource BtnGhost}" Command="{Binding TaskCenter.ConnectResourcesCommand}" Margin="0,0,8,0"/>
                                        <Button Content="刷新"''')]
    }
    for rel,replacements in changes.items():
        p=root/rel;data=p.read_bytes();s.write(base/'before-connect'/rel,data);nl='\r\n' if b'\r\n' in data else '\n';t=data.decode('utf-8-sig').replace('\r\n','\n')
        for old,new in replacements:assert t.count(old)==1,(rel,old);t=t.replace(old,new)
        encoded=t.replace('\n',nl).encode();encoded=(b'\xef\xbb\xbf'+encoded) if data.startswith(b'\xef\xbb\xbf') else encoded;s.write(p,encoded,mode='wb')
    s.write(base/'authoring-admission.json',json.dumps(dict(function='C01/C02 fresh authoring from live resources',gap='actual initial UI: running BGI, ext catalog not connected, empty cached chooser; no explicit connect action',minimum='explicit authoring connect via existing environment gate and read-only catalog; zero run creation, capability/shutdown guards',next='same complete runtime and final UI verification',review_remaining=0),indent=2).encode())
