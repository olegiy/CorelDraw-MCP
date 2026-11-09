using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CorelMcp.Observer {
    // This watchdog can terminate only the observer hosting it. It remains
    // responsive if a window read or journal write blocks the script thread.
    public sealed class ObserverWatchdog : IDisposable {
        readonly int ownerPid, workerPid;
        readonly string ownerStart, workerStart;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer timer;
        long expires = Int64.MaxValue;
        public ObserverWatchdog(int owner, string ownerTime, int worker, string workerTime) {
            ownerPid = owner; ownerStart = ownerTime; workerPid = worker; workerStart = workerTime;
            timer = new Timer(Check, null, 250, 250);
        }
        public void Renew() { Interlocked.Exchange(ref expires, clock.ElapsedMilliseconds + 2000); }
        public void Disarm() { Interlocked.Exchange(ref expires, Int64.MaxValue); }
        void Check(object ignored) {
            if (clock.ElapsedMilliseconds >= Interlocked.Read(ref expires) ||
                !VbaWindows.Alive(ownerPid, ownerStart) || !VbaWindows.Alive(workerPid, workerStart)) Environment.Exit(4);
        }
        public void Dispose() { timer.Dispose(); }
    }
    public sealed class BoundedJournal : IDisposable {
        readonly StreamWriter writer;
        long bytes;
        int count;
        public BoundedJournal(string directory) {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            if (!current.Exists) throw new IOException("Каталог задания недоступен.");
            for (var item = current; item != null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки запрещены.");
            var stream = new FileStream(Path.Combine(current.FullName, "vba-observer.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            writer = new StreamWriter(stream, new UTF8Encoding(false)); writer.AutoFlush = true;
        }
        public bool Append(string value) {
            int length = Encoding.UTF8.GetByteCount(value) + 1;
            if (length > 65536 || bytes + length > 8 * 1024 * 1024 - 1024 || count >= 100) return false;
            writer.WriteLine(value); bytes += length; count++; return true;
        }
        public void Truncated() { writer.WriteLine("{\"code\":\"VBA_OBSERVER_PROTOCOL_ERROR\",\"events_truncated\":true}"); }
        public void Dispose() { writer.Dispose(); }
    }
    // Read-only adapter. No click, close, reset or application COM entry points.
    public static class VbaWindows {
        delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint message, UIntPtr wparam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint tid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, uint bytes, out uint needed);

        static string Handle(IntPtr value) { return unchecked((ulong)value.ToInt64()).ToString(CultureInfo.InvariantCulture); }
        public static string NormalizeHandle(string value) {
            long signed;
            if (Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out signed) && signed < 0) {
                if (signed < Int32.MinValue) throw new ArgumentException("Недопустимый HWND.");
                return unchecked((uint)(int)signed).ToString(CultureInfo.InvariantCulture);
            }
            ulong number;
            if (!UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number == 0 || number > Int64.MaxValue)
                throw new ArgumentException("Недопустимый HWND.");
            return number.ToString(CultureInfo.InvariantCulture);
        }
        static IntPtr Parse(string value) { return new IntPtr(Int64.Parse(NormalizeHandle(value), CultureInfo.InvariantCulture)); }
        static string Class(IntPtr hwnd) {
            var text = new StringBuilder(256); GetClassNameW(hwnd, text, text.Capacity); return text.ToString();
        }
        static string Caption(IntPtr hwnd) {
            var text = new StringBuilder(1025); GetWindowTextW(hwnd, text, text.Capacity); return text.ToString();
        }
        public static Dictionary<string, object> ProcessIdentity(int pid) {
            try {
                using (var process = Process.GetProcessById(pid)) {
                    if (process.HasExited) return null;
                    return new Dictionary<string, object> {
                        {"pid", pid}, {"start_time", process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)},
                        {"session", process.SessionId}
                    };
                }
            } catch { return null; }
        }
        public static bool Alive(int pid, string start) {
            var identity = ProcessIdentity(pid);
            return identity != null && String.Equals((string)identity["start_time"], start, StringComparison.Ordinal);
        }
        public static Dictionary<string, object> Identity(string window) {
            IntPtr hwnd = Parse(window);
            if (!IsWindow(hwnd)) return null;
            uint pid; uint tid = GetWindowThreadProcessId(hwnd, out pid);
            var identity = ProcessIdentity((int)pid);
            if (identity == null || tid == 0) return null;
            uint needed; var desktop = new StringBuilder(257);
            if (!GetUserObjectInformationW(GetThreadDesktop(tid), 2, desktop, (uint)desktop.Capacity * 2, out needed)) return null;
            identity["hwnd"] = Handle(hwnd); identity["tid"] = tid; identity["desktop"] = desktop.ToString();
            return identity;
        }
        public static Dictionary<string, object> Snapshot(string window) {
            var identity = Identity(window);
            var windows = new List<object>();
            var result = new Dictionary<string, object> { {"identity", identity}, {"windows", windows}, {"truncated", false} };
            if (identity == null) return result;
            uint wantedPid = (uint)(int)identity["pid"];
            var watch = Stopwatch.StartNew();
            bool truncated = false;
            EnumWindows(delegate(IntPtr hwnd, IntPtr ignored) {
                if (watch.ElapsedMilliseconds >= 1000) { truncated = true; return false; }
                uint pid; GetWindowThreadProcessId(hwnd, out pid);
                if (pid != wantedPid || !IsWindowVisible(hwnd) || Class(hwnd) != "#32770") return true;
                if (windows.Count >= 64) { truncated = true; return false; }
                var owners = new List<string>(); var seen = new HashSet<long>();
                IntPtr owner = GetWindow(hwnd, 4);
                while (owner != IntPtr.Zero && owners.Count < 16 && seen.Add(owner.ToInt64())) {
                    owners.Add(Handle(owner)); owner = GetWindow(owner, 4);
                }
                string title = Caption(hwnd);
                var controls = new List<object>(); int textBytes = Encoding.UTF8.GetByteCount(title);
                if (title.Length >= 1024) truncated = true;
                EnumChildWindows(hwnd, delegate(IntPtr control, IntPtr unused) {
                    if (watch.ElapsedMilliseconds >= 1000 || controls.Count >= 128) { truncated = true; return false; }
                    uint controlPid; GetWindowThreadProcessId(control, out controlPid);
                    if (controlPid != wantedPid) { truncated = true; return false; }
                    string kind = Class(control); string text = ""; bool readable = true;
                    // Never query editor buffers, custom controls or password inputs.
                    if (kind == "Static" || kind == "Button") {
                        int remaining = 16384 - textBytes;
                        if (remaining <= 0) { truncated = true; return false; }
                        var buffer = new StringBuilder(Math.Min(4097, remaining / 4 + 1)); UIntPtr count;
                        uint timeout = (uint)Math.Max(1, Math.Min(250, 1000 - watch.ElapsedMilliseconds));
                        readable = SendMessageTimeoutW(control, 0x000D, (UIntPtr)(uint)buffer.Capacity, buffer, 0x0002 | 0x0020, timeout, out count) != IntPtr.Zero;
                        if (readable) { text = buffer.ToString(); textBytes += Encoding.UTF8.GetByteCount(text); if (text.Length >= buffer.Capacity - 1) truncated = true; }
                        else truncated = true;
                    }
                    controls.Add(new Dictionary<string, object> {
                        {"hwnd", Handle(control)}, {"id", GetDlgCtrlID(control)}, {"class_name", kind}, {"text", text}, {"readable", readable}
                    });
                    return true;
                }, IntPtr.Zero);
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != wantedPid) { truncated = true; return true; }
                windows.Add(new Dictionary<string, object> {
                    {"hwnd", Handle(hwnd)}, {"class_name", "#32770"}, {"title", title},
                    {"owner_chain", owners}, {"controls", controls}
                });
                return true;
            }, IntPtr.Zero);
            result["truncated"] = truncated;
            var after = Identity(window);
            if (after == null || !String.Equals((string)after["start_time"], (string)identity["start_time"], StringComparison.Ordinal) ||
                (int)after["pid"] != (int)identity["pid"] || (uint)after["tid"] != (uint)identity["tid"] ||
                !String.Equals((string)after["desktop"], (string)identity["desktop"], StringComparison.Ordinal)) {
                result["identity"] = null; windows.Clear();
            }
            return result;
        }
    }
}
