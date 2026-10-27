using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace BetterGI.KeyboardProbe
{
    // Only modifier transitions are retained. Ordinary input is aggregated, never transcribed.
    internal sealed class ModifierObservation
    {
        internal static readonly int[] Keys = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
        internal static readonly string[] Names = { "LShift", "RShift", "LCtrl", "RCtrl", "LAlt", "RAlt", "LWin", "RWin" };
        private readonly bool[] down = new bool[8], initial = new bool[8];
        private readonly long[] since = new long[8];
        private readonly List<object> changes = new List<object>(), holds = new List<object>();
        private bool initialized, ended;
        internal int PollCount;
        internal long MaxPollGapMs;
        private long previous;
        internal int Dropped;

        internal void Observe(bool[] states, long elapsed)
        {
            if (ended) return;
            PollCount++;
            if (initialized) MaxPollGapMs = Math.Max(MaxPollGapMs, elapsed - previous);
            previous = elapsed;
            for (int i = 0; i < 8; i++)
            {
                if (!initialized)
                {
                    down[i] = initial[i] = states[i]; since[i] = elapsed;
                    continue;
                }
                if (states[i] == down[i]) continue;
                if (changes.Count < 8192) changes.Add(new { key = Names[i], elapsedMs = elapsed, isDown = states[i] }); else Dropped++;
                if (down[i]) AddHold(i, elapsed, false);
                down[i] = states[i]; since[i] = elapsed; initial[i] = false;
            }
            initialized = true;
        }
        private void AddHold(int i, long elapsed, bool open)
        {
            if (holds.Count < 8192) holds.Add(new { key = Names[i], observedStartMs = since[i], observedEndMs = elapsed, observedDurationMs = elapsed - since[i], alreadyDownAtCaptureStart = initial[i], stillDownAtCaptureEnd = open }); else Dropped++;
        }
        internal void Finish(long elapsed)
        {
            if (ended) return;
            for (int i = 0; i < 8; i++) if (down[i]) AddHold(i, elapsed, true);
            ended = true;
        }
        internal Dictionary<string, bool> States()
        {
            var value = new Dictionary<string, bool>();
            for (int i = 0; i < 8; i++) value[Names[i]] = down[i];
            return value;
        }
        internal string Display()
        {
            var names = new List<string>();
            for (int i = 0; i < 8; i++) if (down[i]) names.Add(Names[i]);
            return names.Count == 0 ? "未观察到按住" : string.Join(" + ", names);
        }
        internal object Report() { return new { requestedPollMs = 50, polls = PollCount, maxPollGapMs = MaxPollGapMs, transitions = changes, heldIntervals = holds, finalState = States(), dropped = Dropped, note = "Sampled GetAsyncKeyState on probe UI thread. Durations are observed, not exact physical hold times; short transitions and inaccessible input desktops may not be visible." }; }
    }

    internal sealed class HookCounts
    {
        internal static readonly string[] Names = { "M", "F", "W", "Space", "Esc", "F8", "Other" };
        // phase (all / hands-off), source (unflagged / injected / lower-integrity), key, edge.
        private readonly long[] counts = new long[2 * 3 * 7 * 2];
        internal volatile bool HandsOff;
        internal volatile bool Enabled = true;
        internal long CallbackErrors;
        internal static int Index(int key)
        {
            switch (key) { case 0x4D: return 0; case 0x46: return 1; case 0x57: return 2; case 0x20: return 3; case 0x1B: return 4; case 0x77: return 5; default: return 6; }
        }
        internal void Observe(int key, bool up, uint flags)
        {
            if (!Enabled) return;
            int index = Index(key), edge = up ? 1 : 0;
            int source = (flags & 0x10) != 0 ? 1 : 0;
            Add(0, source, index, edge);
            bool hands = HandsOff;
            if (hands) Add(1, source, index, edge);
            if ((flags & 2) != 0) { Add(0, 2, index, edge); if (hands) Add(1, 2, index, edge); }
        }
        private void Add(int phase, int source, int key, int edge) { Interlocked.Increment(ref counts[((phase * 3 + source) * 7 + key) * 2 + edge]); }
        internal Dictionary<string, Counter> Snapshot(int phase, int source)
        {
            var result = new Dictionary<string, Counter>();
            for (int i = 0; i < 7; i++)
            {
                int index = ((phase * 3 + source) * 7 + i) * 2;
                result[Names[i]] = new Counter { Down = Interlocked.Read(ref counts[index]), Up = Interlocked.Read(ref counts[index + 1]) };
            }
            return result;
        }
    }

    // Optional WH_KEYBOARD_LL observer on a dedicated pumping thread. Never consumes input.
    internal sealed class InjectionObservation
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Message { public IntPtr HWnd; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint thread, uint message, UIntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string module);
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private Thread thread;
        private HookProc callback;
        private IntPtr handle;
        private uint threadId;
        private volatile bool stopRequested;
        internal readonly HookCounts Counts = new HookCounts();
        internal bool Installed, Stopped;
        internal int Error, UnhookError;

        internal bool Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "KeyboardProbe passive input observer" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!ready.WaitOne(2000)) { Error = -1; Stop(); return false; }
            return Installed;
        }
        private void Run()
        {
            try
            {
                Message message;
                PeekMessage(out message, IntPtr.Zero, 0, 0, 0); // Ensure queue before allowing Stop().
                threadId = GetCurrentThreadId();
                callback = OnKeyboard;
                if (stopRequested) return;
                handle = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
                Installed = handle != IntPtr.Zero;
                if (!Installed) { Error = Marshal.GetLastWin32Error(); return; }
                ready.Set();
                while (!stopRequested)
                {
                    int result = GetMessage(out message, IntPtr.Zero, 0, 0);
                    if (result <= 0) { if (result < 0) Error = Marshal.GetLastWin32Error(); break; }
                }
            }
            catch { Error = -2; }
            finally
            {
                if (handle != IntPtr.Zero && !UnhookWindowsHookEx(handle)) UnhookError = Marshal.GetLastWin32Error();
                handle = IntPtr.Zero; Stopped = true; ready.Set();
                GC.KeepAlive(callback);
            }
        }
        private IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
        {
            try
            {
                int kind = message.ToInt32();
                if (code == 0 && (kind == 0x100 || kind == 0x101 || kind == 0x104 || kind == 0x105))
                    Counts.Observe(Marshal.ReadInt32(data), kind == 0x101 || kind == 0x105, unchecked((uint)Marshal.ReadInt32(data, 8)));
            }
            catch { Interlocked.Increment(ref Counts.CallbackErrors); }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }
        internal void Stop()
        {
            Counts.Enabled = false;
            stopRequested = true;
            if (threadId != 0) PostThreadMessage(threadId, 0x12, UIntPtr.Zero, IntPtr.Zero);
            if (thread != null)
            {
                if (!thread.Join(2000)) Error = -3;
                else ready.Close();
            }
        }
        internal object Report()
        {
            return new { requested = true, installed = Installed, stopped = Stopped, error = Error, unhookError = UnhookError, callbackErrors = Counts.CallbackErrors, unflagged = Counts.Snapshot(0, 0), injected = Counts.Snapshot(0, 1), lowerIntegrityInjected = Counts.Snapshot(0, 2), handsOffUnflagged = Counts.Snapshot(1, 0), handsOffInjected = Counts.Snapshot(1, 1), note = "Passive hook always calls CallNextHookEx. Unflagged does not prove physical. Injected does not identify BGI or any sender. Windows can silently remove a timed-out hook; zero counts do not prove absence. Observation may change hook timing/order." };
        }
    }
}
