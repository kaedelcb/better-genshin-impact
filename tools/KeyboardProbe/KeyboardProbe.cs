// 独立、只观察的输入诊断工具。兼容 Windows 自带 .NET Framework 4.x。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BetterGI.KeyboardProbe
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--self-test")
            {
                try { return ProbeForm.SelfTest(args[1]); }
                catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); return 1; }
            }
            Application.Run(new ProbeForm());
            return 0;
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RawHeader { public uint Type, Size; public IntPtr Device, WParam; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RawKeyboard
        {
            public ushort MakeCode, Flags, Reserved, VKey;
            public uint Message, ExtraInformation;
        }
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(IntPtr token, int type, IntPtr data, int size, out int required);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        internal static string Integrity(int pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid), token = IntPtr.Zero, data = IntPtr.Zero;
            try
            {
                if (process == IntPtr.Zero || !OpenProcessToken(process, 8, out token)) return "unknown:" + Marshal.GetLastWin32Error();
                int size;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out size);
                if (size <= 0 || size > 65536) return "unknown-token-size";
                data = Marshal.AllocHGlobal(size);
                if (!GetTokenInformation(token, 25, data, size, out size)) return "unknown:" + Marshal.GetLastWin32Error();
                IntPtr sid = Marshal.ReadIntPtr(data);
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                if (count == 0) return "unknown-sid";
                return "0x" + Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1))).ToString("X");
            }
            finally
            {
                if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
            }
        }
    }

    internal sealed class Counter
    {
        public long Down { get; set; }
        public long Up { get; set; }
    }

    internal sealed class KeySurface : Panel
    {
        internal Action<int, bool> Received;
        internal bool HandsOff;
        internal KeySurface()
        {
            TabStop = true;
            BackColor = Color.FromArgb(231, 242, 250);
            SetStyle(ControlStyles.Selectable, true);
            MouseDown += delegate { Focus(); Invalidate(); };
            GotFocus += delegate { Invalidate(); };
            LostFocus += delegate { Invalidate(); };
        }
        protected override bool IsInputKey(Keys keyData) { return true; }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x100 || m.Msg == 0x104 || m.Msg == 0x101 || m.Msg == 0x105)
                if (Received != null) Received(m.WParam.ToInt32(), m.Msg == 0x100 || m.Msg == 0x104);
            base.WndProc(ref m);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            string text = HandsOff ? "松手观察中：请不要再按键盘。\n观察计数是否仍增加即可。" : Focused ? "测试区已获得焦点\n请分别按下并松开 M、F、W、空格、Esc、F8，各 3 次。" : "点击此处，让测试区获得焦点（测试键：M / F / W / 空格 / Esc / F8）";
            TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, Color.FromArgb(25, 52, 73), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }

    internal sealed class ProbeForm : Form
    {
        private readonly ComboBox stage = new ComboBox();
        private readonly Button start = new Button(), stop = new Button(), folder = new Button(), hands = new Button(), export = new Button();
        private readonly CheckBox observeInjected = new CheckBox();
        private readonly Label phase = new Label();
        private readonly Label status = new Label();
        private readonly TextBox display = new TextBox();
        private readonly KeySurface surface = new KeySurface();
        private readonly Timer timer = new Timer();
        private readonly Stopwatch clock = new Stopwatch();
        private Dictionary<string, Counter> raw = new Dictionary<string, Counter>();
        private Dictionary<string, Counter> window = new Dictionary<string, Counter>();
        private readonly List<object> samples = new List<object>();
        private readonly List<string> errors = new List<string>();
        private readonly List<string> savedCaptures = new List<string>();
        private ModifierObservation modifiers;
        private InjectionObservation injection;
        private Dictionary<string, Counter> handsRaw, handsWindow;
        private long handsStarted = -1;
        private bool injectionRequested;
        private int activeStageIndex;
        private long captureEndMs = 30000, handsAfterMs = 20000;
        private Action captureFinished;
        private string lastExport;
        private object environment;
        private DateTime started;
        private string activeStage, savedPath;
        private bool capturing, registered, testing;
        private int lastSample = -1, rawPackets, rawZeroDevice, rawUnknown;
        private int handsRawPackets, handsRawOther;
        private long lastTick, maxTickGap;
        private readonly string outputDirectory;

        internal ProbeForm() : this(null) { }
        private ProbeForm(string output)
        {
            outputDirectory = output ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterGI-KeyboardProbe", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id);
            Text = "BGI 按键故障诊断 v2.0 · 独立工具";
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(920, 690);
            MinimumSize = new Size(920, 690);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 8, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "先采故障现场，再自行停止任务、退出 BGI 分别做对照。每轮前 20 秒测试，后 10 秒松手观察。\n默认只观察原始输入和修饰键，不发送按键、不记录文字。先完成一轮默认采集，再按需启用模拟输入观察。", AutoSize = false }, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            stage.DropDownStyle = ComboBoxStyle.DropDownList; stage.Width = 190;
            stage.Items.AddRange(new object[] { "1. 故障现场（BGI 运行）", "2. 自行停止任务后", "3. 自行退出 BGI 后", "正常状态基线" }); stage.SelectedIndex = 0;
            start.Text = "开始 30 秒"; start.Width = 125; start.Click += delegate { StartCapture(); };
            stop.Text = "结束并保存"; stop.Width = 125; stop.Enabled = false; stop.Click += delegate { StopCapture(); };
            folder.Text = "打开目录"; folder.Width = 100; folder.Enabled = false;
            folder.Click += delegate { try { Process.Start("explorer.exe", "\"" + outputDirectory + "\""); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            hands.Text = "松手观察 10 秒"; hands.Width = 145; hands.Enabled = false; hands.Click += delegate { BeginHandsOff(true); };
            export.Text = "导出对照包"; export.Width = 125; export.Enabled = false; export.Click += delegate { ExportComparison(); };
            actions.Controls.AddRange(new Control[] { stage, start, stop, hands, export, folder }); layout.Controls.Add(actions, 0, 1);
            observeInjected.Dock = DockStyle.Fill; observeInjected.Text = "可选：观察模拟输入标记（安装只读钩子，可能改变时序；不能判断发送者就是 BGI）";
            layout.Controls.Add(observeInjected, 0, 2);
            phase.Dock = DockStyle.Fill; phase.BackColor = Color.FromArgb(255, 245, 220); phase.Padding = new Padding(8);
            phase.Text = "准备就绪。默认模式先保存原始现场；所有阶段切换只改变标签，不会停止任务或退出 BGI。";
            layout.Controls.Add(phase, 0, 3);
            surface.Dock = DockStyle.Fill; surface.Received = OnWindowKey; layout.Controls.Add(surface, 0, 4);
            display.Dock = DockStyle.Fill; display.Multiline = true; display.ReadOnly = true; display.ScrollBars = ScrollBars.Vertical;
            display.Font = new Font("Consolas", 10); layout.Controls.Add(display, 0, 5);
            status.Dock = DockStyle.Fill; status.Text = "尚未采集。左右 Ctrl / Alt / Shift / Win 每约 50ms 观察一次，记录变化和可见持续时间。"; layout.Controls.Add(status, 0, 6);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Esc / F8 只在蓝色测试区测试。松手阶段不要碰键盘；可以观察游戏。\n报告不能读取 BGI 内部定时器或发键重试状态；未观察到事件不等于没有异常输入。" }, 0, 7);
            Controls.Add(layout);
            timer.Interval = 50; timer.Tick += Tick;
            FormClosing += delegate { if (capturing) StopCapture(); };
            FormClosed += delegate { timer.Dispose(); };
        }

        protected override bool ShowWithoutActivation { get { return testing; } }

        internal static string Bucket(int key)
        {
            switch (key) { case 0x4D: return "M"; case 0x46: return "F"; case 0x57: return "W"; case 0x20: return "Space"; case 0x1B: return "Esc"; case 0x77: return "F8"; default: return null; }
        }
        private static void Count(Dictionary<string, Counter> target, int key, bool down)
        {
            string bucket = Bucket(key); if (bucket == null) return;
            Counter counter; if (!target.TryGetValue(bucket, out counter)) target[bucket] = counter = new Counter();
            if (down) counter.Down++; else counter.Up++;
        }
        private static Dictionary<string, Counter> Copy(Dictionary<string, Counter> source)
        {
            var result = new Dictionary<string, Counter>();
            foreach (var pair in source) result[pair.Key] = new Counter { Down = pair.Value.Down, Up = pair.Value.Up };
            return result;
        }
        private bool Register(bool remove)
        {
            var devices = new[] { new Native.RawDevice { Page = 1, Usage = 6, Flags = remove ? 1u : 0x100u, Target = remove ? IntPtr.Zero : Handle } };
            bool ok = Native.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf(typeof(Native.RawDevice)));
            if (!ok) errors.Add((remove ? "unregister-raw:" : "register-raw:") + Marshal.GetLastWin32Error());
            return ok;
        }
        private void StartCapture()
        {
            if (capturing) return;
            if (!testing) { captureEndMs = 30000; handsAfterMs = 20000; }
            raw.Clear(); window.Clear(); samples.Clear(); errors.Clear();
            rawPackets = rawZeroDevice = rawUnknown = 0; lastSample = -1; lastTick = maxTickGap = 0;
            savedPath = null; handsStarted = -1; modifiers = new ModifierObservation();
            handsRawPackets = handsRawOther = 0; surface.HandsOff = false; surface.Invalidate();
            handsRaw = new Dictionary<string, Counter>(); handsWindow = new Dictionary<string, Counter>();
            injectionRequested = observeInjected.Checked; injection = null;
            activeStage = stage.Text; activeStageIndex = stage.SelectedIndex; environment = EnvironmentSnapshot();
            started = DateTime.UtcNow; clock.Restart(); capturing = true;
            registered = Register(false);
            if (injectionRequested)
            {
                injection = new InjectionObservation();
                if (!injection.Start()) errors.Add("optional-hook-start:" + injection.Error);
            }
            start.Enabled = stage.Enabled = observeInjected.Enabled = export.Enabled = false; stop.Enabled = hands.Enabled = true;
            if (!testing) surface.Focus();
            PollModifiers(); Sample(); UpdatePhase(); timer.Start();
        }
        private void OnWindowKey(int key, bool down)
        {
            if (!capturing) return;
            Count(window, key, down);
            if (handsStarted >= 0) Count(handsWindow, key, down);
        }
        private void PollModifiers()
        {
            var states = new bool[8];
            for (int i = 0; i < 8; i++) states[i] = (Native.GetAsyncKeyState(ModifierObservation.Keys[i]) & 0x8000) != 0;
            modifiers.Observe(states, clock.ElapsedMilliseconds);
        }
        private void BeginHandsOff(bool manual)
        {
            if (!capturing || handsStarted >= 0) return;
            handsStarted = clock.ElapsedMilliseconds;
            surface.HandsOff = true; surface.Invalidate();
            if (manual) captureEndMs = handsStarted + 10000;
            if (injection != null) injection.Counts.HandsOff = true;
            hands.Enabled = false; Sample(); UpdatePhase();
        }
        private void UpdatePhase()
        {
            long now = clock.ElapsedMilliseconds;
            phase.Text = handsStarted >= 0
                ? "松手观察：请放开所有按键，不要再按键盘。剩余 " + Math.Max(0, (captureEndMs - now + 999) / 1000) + " 秒。\n当前修饰键：" + modifiers.Display()
                : "按键测试：在蓝色区域分别按下并松开 M / F / W / 空格 / Esc / F8。\n" + Math.Max(0, (handsAfterMs - now + 999) / 1000) + " 秒后进入松手观察，也可点击“松手观察 10 秒”。当前修饰键：" + modifiers.Display();
        }
        private void Tick(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            maxTickGap = Math.Max(maxTickGap, now - lastTick); lastTick = now;
            PollModifiers();
            if (handsStarted < 0 && now >= handsAfterMs) BeginHandsOff(false);
            if ((int)(now / 1000) != lastSample) Sample();
            display.Text = Summary();
            UpdatePhase();
            status.Text = "阶段：" + activeStage + "。原始输入注册：" + (registered ? "成功" : "失败，见错误字段") + "。\n模拟输入观察：" + (injection == null ? "未开启" : injection.Installed && !injection.Stopped ? "已安装（无事件不能证明没有模拟输入）" : "不可用，见错误字段") + "。已保存 " + savedCaptures.Count + " 轮。";
            if (now >= captureEndMs) StopCapture();
        }
        private string Summary()
        {
            var text = new StringBuilder("测试键   原始 Down/Up   测试区 Down/Up   模拟标记 Down/Up\r\n");
            var injected = injection == null ? null : injection.Counts.Snapshot(0, 1);
            foreach (string key in new[] { "M", "F", "W", "Space", "Esc", "F8" })
            {
                Counter a, b; if (!raw.TryGetValue(key, out a)) a = new Counter(); if (!window.TryGetValue(key, out b)) b = new Counter();
                string value = injected == null ? "未开启" : injected[key].Down + "/" + injected[key].Up;
                text.AppendFormat("{0,-7} {1,4}/{2,-4}       {3,4}/{4,-4}        {5}\r\n", key, a.Down, a.Up, b.Down, b.Up, value);
            }
            text.Append("\r\n在游戏中按键时，测试区计数不增加是正常的。\r\n原始事件不代表游戏已经处理按键；合成输入可能没有原始事件。");
            if (handsStarted >= 0)
                text.Append("\r\n松手后：原始事件 " + handsRawPackets + " 条；模拟标记按下 " + (injection == null ? "未开启" : TotalDown(injection.Counts.Snapshot(1, 1)).ToString()) + " 次（含非测试键汇总）。");
            return text.ToString();
        }
        private static long TotalDown(Dictionary<string, Counter> values)
        {
            long total = 0; foreach (var value in values.Values) total += value.Down; return total;
        }
        private void Sample()
        {
            lastSample = (int)(clock.ElapsedMilliseconds / 1000);
            uint pid; IntPtr foreground = Native.GetForegroundWindow(); uint tid = Native.GetWindowThreadProcessId(foreground, out pid);
            string name = "unknown";
            try { using (var process = Process.GetProcessById((int)pid)) name = process.ProcessName; } catch { }
            if (samples.Count < 64) samples.Add(new { elapsedMs = clock.ElapsedMilliseconds, handsOff = handsStarted >= 0, foregroundHwnd = foreground.ToInt64(), foregroundPid = pid, foregroundTid = tid, foregroundProcess = name, foregroundEnabled = Native.IsWindowEnabled(foreground), foregroundLayout = Native.GetKeyboardLayout(tid).ToInt64(), testSurfaceFocused = surface.Focused, modifierAsyncState = modifiers.States(), raw = Copy(raw), testWindow = Copy(window), injected = injection == null ? null : injection.Counts.Snapshot(0, 1) });
        }
        private void StopCapture()
        {
            if (!capturing) return;
            timer.Stop(); PollModifiers(); Sample(); capturing = false; clock.Stop();
            modifiers.Finish(clock.ElapsedMilliseconds);
            if (injection != null) injection.Stop();
            if (registered) { Register(true); registered = false; }
            start.Enabled = stage.Enabled = observeInjected.Enabled = true; stop.Enabled = hands.Enabled = false;
            export.Enabled = savedCaptures.Count > 0;
            var report = new { schema = "bgi-keyboard-probe-v2", toolVersion = "2.0", stage = activeStage, syntheticSelfTest = testing, startedUtc = started.ToString("O"), durationMs = clock.ElapsedMilliseconds, environment = environment, rawPacketCount = rawPackets, rawZeroDeviceCount = rawZeroDevice, rawOtherKeyCount = rawUnknown, maxUiTimerGapMs = maxTickGap, raw = raw, testWindow = window, handsOff = new { startedMs = handsStarted, observedDurationMs = handsStarted < 0 ? 0 : clock.ElapsedMilliseconds - handsStarted, rawPacketCount = handsRawPackets, rawOtherKeyCount = handsRawOther, raw = handsRaw, testWindow = handsWindow, instructionsOnlyNotProofHandsWereOff = true }, modifiers = modifiers.Report(), injectionRequested = injectionRequested, injection = injection == null ? null : injection.Report(), samples = samples, errors = errors, limits = "No SendInput or typed text/window titles/command lines. Optional passive hook observes injected flags, not sender identity, and can alter timing. RawInput is not proof of game delivery. GetAsyncKeyState is sampled on probe thread, not BGI. Missing events are not proof of absence." };
            display.Text = Summary();
            try
            {
                Directory.CreateDirectory(outputDirectory);
                savedPath = Path.Combine(outputDirectory, "capture-" + started.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                using (var stream = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(new JavaScriptSerializer().Serialize(report));
                savedCaptures.Add(savedPath); folder.Enabled = export.Enabled = true; status.Text = "已保存第 " + savedCaptures.Count + " 轮：" + savedPath;
                if (activeStageIndex < 2) stage.SelectedIndex = activeStageIndex + 1;
                phase.Text = activeStageIndex == 0 ? "故障现场已保存。下一步请自行停止当前任务，再点击开始采集第二阶段。" : activeStageIndex == 1 ? "停止后记录已保存。下一步请自行退出 BGI，再点击开始采集第三阶段。" : "本轮已保存。可以导出对照包；如需模拟输入观察，请勾选后手动选择相同阶段再采一轮。";
            }
            catch (Exception ex)
            {
                status.Text = "保存失败：" + ex.Message;
                if (!testing) MessageBox.Show(status.Text, "请保留此窗口");
            }
            if (captureFinished != null) captureFinished();
        }
        private void ExportComparison()
        {
            if (capturing || savedCaptures.Count == 0) return;
            try
            {
                lastExport = Path.Combine(outputDirectory, "对照包-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip");
                using (var archive = ZipFile.Open(lastExport, ZipArchiveMode.Create))
                {
                    foreach (var file in savedCaptures) archive.CreateEntryFromFile(file, Path.GetFileName(file));
                    using (var writer = new StreamWriter(archive.CreateEntry("说明.txt").Open(), new UTF8Encoding(false)))
                        writer.Write("BGI KeyboardProbe v2.0 对照采集，共 " + savedCaptures.Count + " 轮。各 JSON 含阶段标签和 UTC 时间。阶段由用户选择，不证明 BGI 已停止/退出。模拟标记不能归因到 BGI；请附同期 BGI 日志。工具不记录输入文字，但进程路径可能包含用户名。");
                }
                status.Text = "对照包已导出：" + lastExport;
            }
            catch (Exception ex) { status.Text = "导出失败：" + ex.Message; if (!testing) MessageBox.Show(status.Text); }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0xFF && capturing)
            {
                try
                {
                    uint size = 0, headerSize = (uint)Marshal.SizeOf(typeof(Native.RawHeader));
                    uint query = Native.GetRawInputData(m.LParam, 0x10000003, IntPtr.Zero, ref size, headerSize);
                    if (query != uint.MaxValue && size <= 4096 && size >= headerSize)
                    {
                        IntPtr buffer = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            uint received = Native.GetRawInputData(m.LParam, 0x10000003, buffer, ref size, headerSize);
                            if (received != uint.MaxValue && received >= headerSize) ReadRaw(buffer, received);
                            else if (errors.Count < 20) errors.Add("raw-read-failed:" + Marshal.GetLastWin32Error());
                        }
                        finally { Marshal.FreeHGlobal(buffer); }
                    }
                    else if (errors.Count < 20) errors.Add("raw-size-query-failed:" + Marshal.GetLastWin32Error());
                }
                catch (Exception ex) { if (errors.Count < 20) errors.Add("raw-read:" + ex.GetType().Name); }
            }
            // WM_INPUT 的清理交给默认窗口过程；不吞掉系统输入。
            base.WndProc(ref m);
        }
        private void ReadRaw(IntPtr buffer, uint size)
        {
            int headerSize = Marshal.SizeOf(typeof(Native.RawHeader));
            if (size < headerSize + Marshal.SizeOf(typeof(Native.RawKeyboard))) return;
            var header = (Native.RawHeader)Marshal.PtrToStructure(buffer, typeof(Native.RawHeader));
            if (header.Type != 1) return;
            var keyboard = (Native.RawKeyboard)Marshal.PtrToStructure(IntPtr.Add(buffer, headerSize), typeof(Native.RawKeyboard));
            if (keyboard.VKey == 255) return;
            rawPackets++; if (header.Device == IntPtr.Zero) rawZeroDevice++;
            if (Bucket(keyboard.VKey) == null) rawUnknown++;
            Count(raw, keyboard.VKey, (keyboard.Flags & 1) == 0);
            if (handsStarted >= 0)
            {
                handsRawPackets++; if (Bucket(keyboard.VKey) == null) handsRawOther++;
                Count(handsRaw, keyboard.VKey, (keyboard.Flags & 1) == 0);
            }
        }
        private static object EnvironmentSnapshot()
        {
            var processes = new List<object>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (name.IndexOf("BetterGI", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("BetterGenshin", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("NexusBGI", StringComparison.OrdinalIgnoreCase) < 0 && name != "YuanShen" && name != "GenshinImpact" && name != "MultiplayerHoeingAssistant") continue;
                        string path = "unavailable", version = "unavailable";
                        try { path = process.MainModule.FileName; version = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { }
                        processes.Add(new { pid = process.Id, name = name, session = process.SessionId, integrityRid = Native.Integrity(process.Id), path = path, version = version });
                    }
                    catch { }
                }
            }
            using (var self = Process.GetCurrentProcess()) return new { toolVersion = "2.0", os = Environment.OSVersion.VersionString, is64Bit = Environment.Is64BitProcess, pid = self.Id, session = self.SessionId, integrityRid = Native.Integrity(self.Id), timezone = TimeZoneInfo.Local.Id, processes = processes };
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        private void SyntheticRaw(int key, bool up)
        {
            int headerSize = Marshal.SizeOf(typeof(Native.RawHeader));
            int size = headerSize + Marshal.SizeOf(typeof(Native.RawKeyboard));
            IntPtr data = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new Native.RawHeader { Type = 1, Size = (uint)size, Device = new IntPtr(1) }, data, false);
                Marshal.StructureToPtr(new Native.RawKeyboard { VKey = (ushort)key, Flags = (ushort)(up ? 1 : 0) }, IntPtr.Add(data, headerSize), false);
                ReadRaw(data, (uint)size);
                ReadRaw(data, 1); // 截短包不能增加计数。
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        private void SavePreview(string file)
        {
            using (var bitmap = new Bitmap(Width, Height))
            {
                DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                bitmap.Save(file);
            }
        }

        // Native messages target only this test window. Injected flags below are synthetic data,
        // never SendInput. Real hook install/uninstall is tested separately from flag classification.
        internal static int SelfTest(string resultPath)
        {
            var serializer = new JavaScriptSerializer();
            var modifierTest = new ModifierObservation();
            var state = new bool[8]; state[4] = true;
            modifierTest.Observe(state, 0);
            state[3] = true; modifierTest.Observe(state, 50);
            state[4] = false; modifierTest.Observe(state, 100);
            state[3] = false; modifierTest.Observe(state, 200);
            state[0] = true; modifierTest.Observe(state, 250);
            modifierTest.Finish(300); modifierTest.Finish(500);
            var modReport = serializer.DeserializeObject(serializer.Serialize(modifierTest.Report())) as Dictionary<string, object>;
            var intervals = (object[])modReport["heldIntervals"];
            Require(intervals.Length == 3, "modifier intervals/idempotent finish");
            var alt = (Dictionary<string, object>)intervals[0]; var ctrl = (Dictionary<string, object>)intervals[1]; var shift = (Dictionary<string, object>)intervals[2];
            Require((string)alt["key"] == "LAlt" && Convert.ToInt64(alt["observedDurationMs"]) == 100 && (bool)alt["alreadyDownAtCaptureStart"], "initial held left Alt");
            Require((string)ctrl["key"] == "RCtrl" && Convert.ToInt64(ctrl["observedDurationMs"]) == 150, "separate right Ctrl duration");
            Require((bool)shift["stillDownAtCaptureEnd"] && Convert.ToInt64(shift["observedDurationMs"]) == 50, "open modifier interval");
            var countTest = new HookCounts();
            countTest.Observe(0x4D, false, 0); countTest.HandsOff = true;
            countTest.Observe(0x46, false, 0x12); countTest.Observe(0x46, true, 0x12); countTest.Observe(0x41, false, 0x10);
            countTest.Enabled = false; countTest.Observe(0x46, false, 0x10);
            Require(countTest.Snapshot(0, 0)["M"].Down == 1 && countTest.Snapshot(1, 1)["F"].Down == 1 && countTest.Snapshot(1, 1)["F"].Up == 1 && countTest.Snapshot(0, 2)["F"].Down == 1 && countTest.Snapshot(0, 1)["Other"].Down == 1 && !countTest.Snapshot(0, 1).ContainsKey("A"), "injected flags/privacy/frozen counts");
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultPath)), "self-test-captures-" + Guid.NewGuid().ToString("N"));
            using (var form = new ProbeForm(directory))
            {
                form.testing = true; form.ShowInTaskbar = false; form.Opacity = 0;
                int round = 0; bool handsInjected = false, earlyHandsChecked = false; string failure = null;
                var checkpoints = new List<object>();
                var exercise = new Timer { Interval = 100 };
                Action launch = delegate
                {
                    form.captureEndMs = round == 2 ? 30000 : 1200;
                    form.handsAfterMs = round == 2 ? 20000 : 600;
                    form.observeInjected.Checked = round == 1 || round == 3;
                    form.StartCapture();
                    Require(form.registered, "Raw Input registration");
                    if (round == 1 || round == 3) Require(form.injection != null && form.injection.Installed, "optional passive hook registration");
                    form.SyntheticRaw(0x46, false); form.SyntheticRaw(0x46, true); form.SyntheticRaw(0x41, false);
                    Native.PostMessage(form.surface.Handle, 0x100, new IntPtr(0x46), new IntPtr(1));
                    Native.PostMessage(form.surface.Handle, 0x101, new IntPtr(0x46), new IntPtr(unchecked((int)0xC0000001)));
                };
                form.captureFinished = delegate
                {
                    try
                    {
                        Require(!form.registered && !form.capturing && form.errors.Count == 0, "capture cleanup");
                        Require(form.savedPath != null && File.Exists(form.savedPath), "report saved");
                        Require(form.raw["F"].Down >= 1 && form.raw["F"].Up >= 1 && form.window["F"].Down >= 1 && form.window["F"].Up >= 1 && form.rawUnknown >= 1 && !form.raw.ContainsKey("A"), "F/native-window/privacy counters");
                        Require(form.modifiers.PollCount >= 5, "high frequency modifier polling");
                        if (round == 1)
                        {
                            Require(handsInjected && form.injection.Stopped && form.injection.UnhookError == 0 && form.injection.Error == 0, "hook cleanup / hands-off phase");
                            Require(form.injection.Counts.Snapshot(1, 1)["F"].Down >= 1, "hands-off injected count");
                        }
                        if (round == 2) Require(form.clock.ElapsedMilliseconds >= 30000 && form.handsStarted >= 20000 && form.clock.ElapsedMilliseconds - form.handsStarted >= 9000 && form.modifiers.PollCount > 100, "real 30 second timer / hands-off transition");
                        checkpoints.Add(new { stage = form.activeStage, durationMs = form.clock.ElapsedMilliseconds, pollCount = form.modifiers.PollCount, optionalHook = form.injectionRequested });
                        form.SavePreview(Path.Combine(Path.GetDirectoryName(resultPath), "preview-stage-" + round + ".png"));
                        round++;
                        if (round == 3)
                        {
                            form.captureFinished = null; launch();
                            return; // Exercise timer closes an active optional capture.
                        }
                        launch();
                    }
                    catch (Exception ex) { failure = ex.ToString(); form.captureFinished = null; form.Close(); }
                };
                exercise.Tick += delegate
                {
                    try
                    {
                        if (round == 1 && form.handsStarted >= 0 && !handsInjected)
                        {
                            handsInjected = true;
                            form.injection.Counts.Observe(0x46, false, 0x12); form.injection.Counts.Observe(0x46, true, 0x12);
                            form.SyntheticRaw(0x46, false); form.SyntheticRaw(0x46, true);
                            form.display.Text = form.Summary(); form.UpdatePhase();
                            form.SavePreview(Path.Combine(Path.GetDirectoryName(resultPath), "preview-hands-off.png"));
                        }
                        if (round == 3 && form.clock.ElapsedMilliseconds >= 250)
                        {
                            form.BeginHandsOff(true);
                            Require(form.captureEndMs - form.handsStarted == 10000, "manual hands-off countdown");
                            earlyHandsChecked = true; form.Close();
                        }
                    }
                    catch (Exception ex) { failure = ex.ToString(); form.captureFinished = null; form.Close(); }
                };
                form.Shown += delegate { try { launch(); exercise.Start(); } catch (Exception ex) { failure = ex.ToString(); form.captureFinished = null; form.Close(); } };
                Application.Run(form);
                exercise.Stop(); exercise.Dispose();
                form.ExportComparison();
                bool zipOk = false;
                if (form.lastExport != null && File.Exists(form.lastExport))
                    using (var zip = ZipFile.OpenRead(form.lastExport)) zipOk = zip.Entries.Count == 5;
                bool ok = failure == null && round == 3 && earlyHandsChecked && form.savedCaptures.Count == 4 && zipOk && !form.registered && form.injection.Stopped && form.injection.UnhookError == 0;
                File.WriteAllText(resultPath, serializer.Serialize(new { passed = ok, failure = failure, captures = form.savedCaptures, comparisonZip = form.lastExport, checkpoints = checkpoints, checks = "Modifier transition/duration/initial-held/open-ended intervals; injected/lower-integrity/unflagged classification; F/raw packet/own native window/privacy counters; optional hook registration/unregistration; real 30-second auto-stop; hands-off and manual countdown; stage advancement; active-close cleanup; multi-capture ZIP. Synthetic event data only, no system SendInput or physical/game reproduction." }), new UTF8Encoding(false));
                return ok ? 0 : 1;
            }
        }
    }
}
