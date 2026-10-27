// 独立、只观察的输入诊断工具。兼容 Windows 自带 .NET Framework 4.x。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
            string text = Focused ? "测试区已获得焦点\n请分别按下并松开 M、W、空格、Esc、F8，各 3 次。" : "点击此处，让测试区获得焦点";
            TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, Color.FromArgb(25, 52, 73), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }

    internal sealed class ProbeForm : Form
    {
        private readonly ComboBox stage = new ComboBox();
        private readonly Button start = new Button(), stop = new Button(), folder = new Button();
        private readonly Label status = new Label();
        private readonly TextBox display = new TextBox();
        private readonly KeySurface surface = new KeySurface();
        private readonly Timer timer = new Timer();
        private readonly Stopwatch clock = new Stopwatch();
        private Dictionary<string, Counter> raw = new Dictionary<string, Counter>();
        private Dictionary<string, Counter> window = new Dictionary<string, Counter>();
        private readonly List<object> samples = new List<object>();
        private readonly List<string> errors = new List<string>();
        private object environment;
        private DateTime started;
        private string activeStage, savedPath;
        private bool capturing, registered, testing;
        private int lastSample = -1, rawPackets, rawZeroDevice, rawUnknown;
        private long lastTick, maxTickGap;
        private readonly string outputDirectory;
        private static readonly int[] ModifierKeysToRead = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
        private static readonly string[] ModifierNames = { "LShift", "RShift", "LCtrl", "RCtrl", "LAlt", "RAlt", "LWin", "RWin" };

        internal ProbeForm() : this(null) { }
        private ProbeForm(string output)
        {
            outputDirectory = output ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BetterGI-KeyboardProbe", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id);
            Text = "BGI 按键故障诊断 · 只观察";
            Font = new Font("Microsoft YaHei UI", 10);
            ClientSize = new Size(780, 560);
            MinimumSize = new Size(730, 550);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 6, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "故障出现时先保留现场，选择阶段后点击开始。先在下方测试区按键，再回到游戏按 M / W / 空格。\n30 秒后自动保存。仅记录测试键计数和系统状态，不记录输入文字、不安装键盘钩子、不发送按键。", AutoSize = false }, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            stage.DropDownStyle = ComboBoxStyle.DropDownList; stage.Width = 190;
            stage.Items.AddRange(new object[] { "故障现场（BGI 仍运行）", "停止任务后", "退出 BGI 后对照", "正常状态基线" }); stage.SelectedIndex = 0;
            start.Text = "开始 30 秒"; start.Width = 125; start.Click += delegate { StartCapture(); };
            stop.Text = "结束并保存"; stop.Width = 125; stop.Enabled = false; stop.Click += delegate { StopCapture(); };
            folder.Text = "打开结果目录"; folder.Width = 135; folder.Enabled = false;
            folder.Click += delegate { try { Process.Start("explorer.exe", "\"" + outputDirectory + "\""); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            actions.Controls.AddRange(new Control[] { stage, start, stop, folder }); layout.Controls.Add(actions, 0, 1);
            surface.Dock = DockStyle.Fill; surface.Received = OnWindowKey; layout.Controls.Add(surface, 0, 2);
            display.Dock = DockStyle.Fill; display.Multiline = true; display.ReadOnly = true; display.ScrollBars = ScrollBars.Vertical;
            display.Font = new Font("Consolas", 11); layout.Controls.Add(display, 0, 3);
            status.Dock = DockStyle.Fill; status.Text = "尚未采集。原始事件与窗口事件只作为定位线索，不能单独判定哪个程序拦截。"; layout.Controls.Add(status, 0, 4);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "对照时使用同一实体键盘。Esc / F8 只在测试区测试，避免触发游戏或 BGI 的快捷键。" }, 0, 5);
            Controls.Add(layout);
            timer.Interval = 100; timer.Tick += Tick;
            FormClosing += delegate { if (capturing) StopCapture(); };
            FormClosed += delegate { timer.Dispose(); };
        }

        protected override bool ShowWithoutActivation { get { return testing; } }

        internal static string Bucket(int key)
        {
            switch (key) { case 0x4D: return "M"; case 0x57: return "W"; case 0x20: return "Space"; case 0x1B: return "Esc"; case 0x77: return "F8"; default: return null; }
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
            raw.Clear(); window.Clear(); samples.Clear(); errors.Clear();
            rawPackets = rawZeroDevice = rawUnknown = 0; lastSample = -1; lastTick = maxTickGap = 0;
            activeStage = stage.Text; environment = EnvironmentSnapshot();
            started = DateTime.UtcNow; clock.Restart(); capturing = true;
            registered = Register(false);
            start.Enabled = stage.Enabled = false; stop.Enabled = true;
            if (!testing) surface.Focus();
            Sample(); timer.Start();
        }
        private void OnWindowKey(int key, bool down) { if (capturing) Count(window, key, down); }
        private void Tick(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            maxTickGap = Math.Max(maxTickGap, now - lastTick); lastTick = now;
            if ((int)(now / 1000) != lastSample) Sample();
            display.Text = Summary();
            status.Text = "采集中，剩余 " + Math.Max(0, 30 - now / 1000) + " 秒。Raw Input 注册：" + (registered ? "成功" : "失败，见结果错误字段") + "。";
            if (now >= 30000) StopCapture();
        }
        private string Summary()
        {
            var text = new StringBuilder("测试键      原始 Down/Up      测试区 Down/Up\r\n");
            foreach (string key in new[] { "M", "W", "Space", "Esc", "F8" })
            {
                Counter a, b; if (!raw.TryGetValue(key, out a)) a = new Counter(); if (!window.TryGetValue(key, out b)) b = new Counter();
                text.AppendFormat("{0,-10}  {1,4}/{2,-4}           {3,4}/{4,-4}\r\n", key, a.Down, a.Up, b.Down, b.Up);
            }
            text.Append("\r\n在游戏中按键时，测试区计数不增加是正常的。\r\n原始事件不代表游戏已经处理按键；合成输入可能没有原始事件。");
            return text.ToString();
        }
        private void Sample()
        {
            lastSample = (int)(clock.ElapsedMilliseconds / 1000);
            uint pid; IntPtr foreground = Native.GetForegroundWindow(); uint tid = Native.GetWindowThreadProcessId(foreground, out pid);
            string name = "unknown";
            try { using (var process = Process.GetProcessById((int)pid)) name = process.ProcessName; } catch { }
            var modifiers = new Dictionary<string, bool>();
            for (int i = 0; i < ModifierKeysToRead.Length; i++) modifiers[ModifierNames[i]] = (Native.GetAsyncKeyState(ModifierKeysToRead[i]) & 0x8000) != 0;
            if (samples.Count < 64) samples.Add(new { elapsedMs = clock.ElapsedMilliseconds, foregroundHwnd = foreground.ToInt64(), foregroundPid = pid, foregroundTid = tid, foregroundProcess = name, foregroundEnabled = Native.IsWindowEnabled(foreground), foregroundLayout = Native.GetKeyboardLayout(tid).ToInt64(), testSurfaceFocused = surface.Focused, modifierAsyncState = modifiers, raw = Copy(raw), testWindow = Copy(window) });
        }
        private void StopCapture()
        {
            if (!capturing) return;
            timer.Stop(); Sample(); capturing = false; clock.Stop();
            if (registered) { Register(true); registered = false; }
            start.Enabled = stage.Enabled = true; stop.Enabled = false;
            var report = new { schema = "bgi-keyboard-probe-v1", stage = activeStage, syntheticSelfTest = testing, startedUtc = started.ToString("O"), durationMs = clock.ElapsedMilliseconds, environment = environment, rawPacketCount = rawPackets, rawZeroDeviceCount = rawZeroDevice, rawOtherKeyCount = rawUnknown, maxUiTimerGapMs = maxTickGap, raw = raw, testWindow = window, samples = samples, errors = errors, limits = "No keyboard hook, no SendInput, no typed text/window titles/command lines. RawInput is not proof of game delivery; GetAsyncKeyState is sampled on the probe thread, not inside BGI. Missing raw input is not proof of hardware failure. No per-process hook attribution." };
            display.Text = Summary();
            try
            {
                Directory.CreateDirectory(outputDirectory);
                savedPath = Path.Combine(outputDirectory, "capture-" + started.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                using (var stream = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(new JavaScriptSerializer().Serialize(report));
                folder.Enabled = true; status.Text = "已保存：" + savedPath;
            }
            catch (Exception ex)
            {
                status.Text = "保存失败：" + ex.Message;
                if (!testing) MessageBox.Show(status.Text, "请保留此窗口");
            }
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
            using (var self = Process.GetCurrentProcess()) return new { toolVersion = "1.0", os = Environment.OSVersion.VersionString, is64Bit = Environment.Is64BitProcess, pid = self.Id, session = self.SessionId, integrityRid = Native.Integrity(self.Id), timezone = TimeZoneInfo.Local.Id, processes = processes };
        }

        // 只向自己的隐藏测试窗口投递消息，不注入系统按键、不操控 BGI/游戏。
        internal static int SelfTest(string resultPath)
        {
            string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultPath)), "self-test-captures-" + Guid.NewGuid().ToString("N"));
            using (var form = new ProbeForm(directory))
            {
                form.testing = true; form.ShowInTaskbar = false; form.Opacity = 0;
                form.Shown += delegate
                {
                    form.StartCapture();
                    if (!form.registered) { form.StopCapture(); form.Close(); return; }
                    int headerSize = Marshal.SizeOf(typeof(Native.RawHeader));
                    int size = headerSize + Marshal.SizeOf(typeof(Native.RawKeyboard));
                    IntPtr data = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(new Native.RawHeader { Type = 1, Size = (uint)size, Device = new IntPtr(1) }, data, false);
                        Marshal.StructureToPtr(new Native.RawKeyboard { VKey = 0x4D, MakeCode = 50 }, IntPtr.Add(data, headerSize), false);
                        form.ReadRaw(data, (uint)size);
                        Marshal.StructureToPtr(new Native.RawKeyboard { VKey = 0x4D, Flags = 1 }, IntPtr.Add(data, headerSize), false);
                        form.ReadRaw(data, (uint)size);
                        form.ReadRaw(data, 1); // 截短包不可计数。
                        Marshal.StructureToPtr(new Native.RawKeyboard { VKey = 0x41 }, IntPtr.Add(data, headerSize), false);
                        form.ReadRaw(data, (uint)size); // 非测试键只能汇总。
                    }
                    finally { Marshal.FreeHGlobal(data); }
                    Native.PostMessage(form.surface.Handle, 0x100, new IntPtr(0x4D), new IntPtr(1));
                    Native.PostMessage(form.surface.Handle, 0x101, new IntPtr(0x4D), new IntPtr(unchecked((int)0xC0000001)));
                    var finish = new Timer { Interval = 500 };
                    finish.Tick += delegate
                    {
                        finish.Stop(); finish.Dispose(); form.StopCapture();
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                            bitmap.Save(Path.ChangeExtension(resultPath, ".png"));
                        }
                        form.Close();
                    };
                    finish.Start();
                };
                Application.Run(form);
                Counter r, w;
                bool ok = form.raw.TryGetValue("M", out r) && r.Down == 1 && r.Up == 1 && form.window.TryGetValue("M", out w) && w.Down == 1 && w.Up == 1 && form.rawUnknown == 1 && !form.raw.ContainsKey("A") && form.errors.Count == 0 && form.savedPath != null && File.Exists(form.savedPath) && !form.registered;
                File.WriteAllText(resultPath, new JavaScriptSerializer().Serialize(new { passed = ok, capture = form.savedPath, checks = "Raw packet parsing/down-up/truncated packet/privacy whitelist; native messages to own test window; raw registration/unregistration; JSON export. Synthetic only, no physical keyboard or game reproduction." }), new UTF8Encoding(false));
                return ok ? 0 : 1;
            }
        }
    }
}
