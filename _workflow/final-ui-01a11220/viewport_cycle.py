"""Bounded WPF parent-scroll counterexample/fix/mutation under the original delivery goal."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, time, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'viewport-scroll/r2'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
original_size = s.size
scan_retries = []
generated_roots = [ROOT / 'MultiplayerHoeingAssistant/obj', ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/obj']
def stable_generated_size(roots):
    # Retry the complete, unchanged collection when this owned rebuild removes a generated file.
    # Do not omit roots/files or tolerate missing source, User, or published evidence.
    for attempt in range(5):
        try:
            return original_size(roots)
        except FileNotFoundError as error:
            missing = Path(error.filename).absolute()
            if not any(missing.is_relative_to(root) for root in generated_roots) or attempt == 4:
                raise
            scan_retries.append(dict(path=str(missing), attempt=attempt + 1, complete_collection_retried=True))
            time.sleep(0.1)
s.size = stable_generated_size
SOURCE = 'MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs'
TEST = 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalScheduleRenderTests.cs'
FILES = [SOURCE, TEST]
sha = lambda b: hashlib.sha256(b).hexdigest()
ADDITION = '''    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    public async Task ParentScroll_DoesNotExpandTimelineIntoStrategyForm(double offset)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "formal-viewport-" + Guid.NewGuid().ToString("N"));
                var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
                    Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
                host.SaveFlow(new WorkflowDocument { Name = "完整策略入口", Nodes = [new() { NodeId = "end", Kind = "control.end" }] }, null);
                var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
                vm.EditFlowCommand.Execute(vm.Flows.Single());
                var view = new ScheduleListView { DataContext = vm };
                var form = new TextBox { Text = "流程名称", Height = 36 };
                var content = new StackPanel();
                content.Children.Add(new Border { Height = 120 });
                content.Children.Add(view);
                content.Children.Add(form);
                content.Children.Add(new Border { Height = 360 });
                var outer = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                window = new Window { Content = outer, Width = 960, Height = 680,
                    ShowActivated = false, Left = -20000, Top = -20000 };
                window.Show();
                PumpLayout(window);
                var timeline = Assert.IsType<ScrollViewer>(view.FindName("TimelineScroll"));
                var inspector = Assert.IsType<ScrollViewer>(view.FindName("InspectorScroll"));
                var baselineHeight = timeline.Height;
                outer.ScrollToVerticalOffset(offset);
                PumpLayout(window);
                Assert.Equal(offset, outer.VerticalOffset, 3);
                // Real owner resize runs the same viewport update used by loaded/rebound views.
                window.Width += 20;
                PumpLayout(window);
                Assert.Equal(baselineHeight, timeline.Height, 3);
                Assert.Equal(baselineHeight, inspector.Height, 3);
                outer.ScrollToBottom();
                PumpLayout(window);
                var formTop = form.TransformToAncestor(outer).Transform(new Point()).Y;
                Assert.InRange(formTop, 0, outer.ViewportHeight - form.ActualHeight);
                Assert.Same(vm.Editing, view.DataContext is TaskCenterPanelViewModel current ? current.Editing : null);
                Assert.Empty(host.ListActiveRuns());
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void PumpLayout(Window window)
    {
        window.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

'''
OLD = '''        var top=TimelineContent.TransformToAncestor(window).Transform(new Point()).Y;
        var height=Math.Max(180,window.ActualHeight-top-100);'''
NEW = '''        var top=TimelineContent.TransformToAncestor(window).Transform(new Point()).Y;
        // 外层滚动只改变位置，不扩大时间轴；下方完整策略表单仍须可滚动到达。
        for (DependencyObject? ancestor=VisualTreeHelper.GetParent(TimelineContent);
             ancestor is not null && ancestor!=window; ancestor=VisualTreeHelper.GetParent(ancestor))
            if (ancestor is ScrollViewer parentScroll) top+=parentScroll.VerticalOffset;
        var height=Math.Max(180,window.ActualHeight-top-100);'''

def atomic(path, data):
    temporary = path.with_name(path.name + '.own-01a114a3.tmp')
    assert not temporary.exists()
    with temporary.open('xb') as file:
        file.write(data); file.flush(); os.fsync(file.fileno())
    os.replace(temporary, path)
    assert path.read_bytes() == data and not temporary.exists()

def replace(relative, old, new):
    path = ROOT / relative; data = path.read_bytes()
    ending = '\r\n' if b'\r\n' in data else '\n'
    original = old.replace('\n', ending).encode()
    changed = new.replace('\n', ending).encode()
    assert data.count(original) == 1, relative
    atomic(path, data.replace(original, changed))

with s.Session(ROOT, 'own-root-01a114a3-viewport-parent-scroll-causal') as budget:
    old_roots = budget.old_roots[:]
    budget.old_roots = list(dict.fromkeys(old_roots))
    assert set(budget.old_roots) == set(old_roots)
    budget.track(BASE)
    carrier = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
    budget.track(carrier)
    for folder in ['MultiplayerHoeingAssistant/obj', 'Test/MultiplayerHoeingAssistant.UnitTest/obj']:
        budget.track(ROOT / folder)
    def write(path, value):
        s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode())
    before = {relative: (ROOT / relative).read_bytes() for relative in FILES}
    for relative, data in before.items(): s.write(BASE / 'opening/before' / relative, data)
    write(BASE / 'opening/before.json', {relative: dict(sha256=sha(data), bytes=len(data), lines=len(data.splitlines()), crlf=data.count(b'\r\n'), bom=data.startswith(b'\xef\xbb\xbf')) for relative, data in before.items()})
    s.write(BASE / 'opening/git-status.txt', subprocess.check_output(['git', '-c', 'core.longpaths=true', 'status', '--porcelain=v1'], cwd=ROOT))
    write(BASE / 'opening/admission.json', dict(marker='OWN-ROOT-PREVIEW-PARAMS-20261007-FROM-01a11405', function='complete strategy UI and remaining reload/skip runtime matrix', gap='outer scroll changes viewport anchor; rebind/resize grows timeline and keeps the complete strategy form below the viewport', consequence='formal flow name/trigger/loop/terminal fields cannot be reached normally', repair='normalize anchor by ancestor pixel scroll offsets; preserve standalone/popout geometry, draft and node semantics', matrix=['unscrolled owner resize unchanged', 'scrolled owner resize retains viewport and reaches following form', 'same-product main UI complete strategy form and reload/skip'], original_opening_and_history_unchanged=True, review_requests_new=0, review_budget_remaining=0, independent_review=False, actual_ui=False, product_complete=False))
    def stage(name, targeted=False):
        out = BASE / name; out.mkdir(parents=True, exist_ok=False)
        hashes = {file.relative_to(ROOT).as_posix(): sha(file.read_bytes()) for folder in ['MultiplayerHoeingAssistant', 'Test/MultiplayerHoeingAssistant.UnitTest'] for file in (ROOT / folder).rglob('*') if file.suffix in ['.cs', '.xaml', '.csproj'] and '_wpftmp' not in file.name and not any(part in file.parts for part in ['bin', 'obj'])}
        write(out / 'source-hashes.json', hashes)
        env = os.environ.copy(); env.update(FORMAL_UI_EVIDENCE=str(out / 'samples'), NEXUSBGI_DATA_ROOT=str(out / 'own-data'), TEMP=str(out / 'temp'), TMP=str(out / 'temp'))
        Path(env['TEMP']).mkdir(); Path(env['FORMAL_UI_EVIDENCE']).mkdir()
        arguments = ['C:/Program Files/dotnet/dotnet.exe', 'build', str(ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'), '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:DeployToBgiTools=false', '-t:Rebuild', '-maxcpucount:1', '-o', str(carrier)]
        result = dict(stage=name)
        result['build'], _, _ = p.run(arguments, cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='build', timeout=1200)
        if result['build'] == 0:
            filt = 'FullyQualifiedName~ParentScroll_DoesNotExpandTimelineIntoStrategyForm' if targeted else 'FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
            arguments = ['C:/Program Files/dotnet/dotnet.exe', 'vstest', str(carrier / 'MultiplayerHoeingAssistant.UnitTest.dll'), '--TestCaseFilter:' + filt, '--logger:trx;LogFileName=viewport.trx', '--ResultsDirectory:' + str(out)]
            result['test'], _, _ = p.run(arguments, cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='test', timeout=600)
            rows = ET.parse(out / 'viewport.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
            for outcome in ['Passed', 'Failed', 'NotExecuted']: result[outcome] = [row.get('testName') for row in rows if row.get('outcome') == outcome]
        result['source_drift'] = [relative for relative, digest in hashes.items() if not (ROOT / relative).is_file() or sha((ROOT / relative).read_bytes()) != digest]
        write(out / 'result.json', result)
        print(json.dumps(dict(stage=name, build=result['build'], passed=len(result.get('Passed', [])), failed=result.get('Failed'), source_drift=result['source_drift']), ensure_ascii=False), flush=True)
        assert result['build'] == 0 and not result['source_drift'], result
        return result
    if b'ParentScroll_DoesNotExpandTimelineIntoStrategyForm' not in (ROOT / TEST).read_bytes():
        replace(TEST, '    [Fact]\n    public async Task RealWpfView_', ADDITION + '    [Fact]\n    public async Task RealWpfView_')
    else:
        ending = '\r\n' if b'\r\n' in (ROOT / TEST).read_bytes() else '\n'
        assert ADDITION.replace('\n', ending).encode() in (ROOT / TEST).read_bytes()
    red = stage('red', True)
    assert len(red['Failed']) == 1 and len(red['Passed']) == 1, red
    replace(SOURCE, OLD, NEW)
    positive = {relative: (ROOT / relative).read_bytes() for relative in FILES}
    for relative, data in positive.items(): s.write(BASE / 'positive-source' / relative, data)
    green = stage('green'); assert not green['Failed'] and not green['NotExecuted'], green
    try:
        atomic(ROOT / SOURCE, before[SOURCE])
        negative = stage('negative', True)
        assert len(negative['Failed']) == 1 and len(negative['Passed']) == 1, negative
    finally:
        atomic(ROOT / SOURCE, positive[SOURCE])
        write(BASE / 'recovery.json', {relative: dict(sha256=sha((ROOT / relative).read_bytes()), same=(ROOT / relative).read_bytes() == data) for relative, data in positive.items()})
    restored = stage('restored'); assert not restored['Failed'] and not restored['NotExecuted'], restored
    write(BASE / 'after.json', {relative: dict(sha256=sha((ROOT / relative).read_bytes()), bytes=(ROOT / relative).stat().st_size, lines=len((ROOT / relative).read_bytes().splitlines()), crlf=(ROOT / relative).read_bytes().count(b'\r\n'), bom=(ROOT / relative).read_bytes().startswith(b'\xef\xbb\xbf')) for relative in FILES})
    s.write(BASE / 'diff-stat.txt', subprocess.check_output(['git', 'diff', '--stat', '--', *FILES], cwd=ROOT))
    write(BASE / 'checkpoint.json', dict(source_restored=True, red_failed=len(red['Failed']), green_passed=len(green['Passed']), mutant_failed=len(negative['Failed']), restored_passed=len(restored['Passed']), review_requests_new=0, independent_review=False, actual_ui=False, product_complete=False))
    write(BASE / 'generated-scan-retries.json', scan_retries)
    print('VIEWPORT_CYCLE_COMPLETE', flush=True)
