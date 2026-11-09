using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace CorelMcp.Fixtures {
    public static class Dialogs {
        delegate IntPtr DialogProc(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
        [StructLayout(LayoutKind.Sequential)] struct Message { public IntPtr hwnd; public uint message; public UIntPtr wparam; public IntPtr lparam; public uint time; public int x; public int y; public uint extra; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr CreateDialogIndirectParamW(IntPtr instance, IntPtr template, IntPtr owner, DialogProc callback, IntPtr param);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref Message message);
        static ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();
        static Thread thread; static IntPtr owner; static bool stopping; static int actions;
        static List<IntPtr> windows = new List<IntPtr>();
        static DialogProc callback = delegate(IntPtr hwnd, uint message, IntPtr w, IntPtr l) {
            if (message == 0x0111) { Interlocked.Increment(ref actions); return new IntPtr(1); }
            return IntPtr.Zero;
        };
        static T OnUi<T>(Func<T> action) {
            T result = default(T); Exception error = null;
            using (var done = new ManualResetEvent(false)) {
                queue.Enqueue(delegate { try { result = action(); } catch (Exception e) { error = e; } finally { done.Set(); } });
                if (!done.WaitOne(2000)) throw new TimeoutException("Fixture не отвечает.");
            }
            if (error != null) throw error; return result;
        }
        public static string Start() {
            stopping = false;
            thread = new Thread(delegate() {
                owner = CreateWindowExW(0, "STATIC", "CorelMcp fixture owner", 0x00CF0000, 10, 10, 400, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                while (!stopping) {
                    Action action; while (queue.TryDequeue(out action)) action();
                    Message message; while (PeekMessageW(out message, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessageW(ref message); }
                    Thread.Sleep(5);
                }
                foreach (var hwnd in windows) DestroyWindow(hwnd);
                DestroyWindow(owner);
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            return OnUi<string>(delegate { return owner.ToInt64().ToString(); });
        }
        public static string Create(string profile, string text) {
            return OnUi<string>(delegate {
                byte[] bytes;
                using (var memory = new MemoryStream()) using (var writer = new BinaryWriter(memory)) {
                    writer.Write((uint)0x80C00080); writer.Write((uint)0); writer.Write((ushort)0);
                    writer.Write((short)10); writer.Write((short)10); writer.Write((short)220); writer.Write((short)80);
                    writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); bytes = memory.ToArray();
                }
                IntPtr template = Marshal.AllocHGlobal(bytes.Length); IntPtr dialog;
                try { Marshal.Copy(bytes, 0, template, bytes.Length); dialog = CreateDialogIndirectParamW(IntPtr.Zero, template, owner, callback, IntPtr.Zero); }
                finally { Marshal.FreeHGlobal(template); }
                if (dialog == IntPtr.Zero) throw new InvalidOperationException("Не создан native dialog.");
                windows.Add(dialog);
                CreateWindowExW(0, "STATIC", text, 0x50000000, 8, 8, 350, 40, dialog, new IntPtr(100), IntPtr.Zero, IntPtr.Zero);
                CreateWindowExW(0, "BUTTON", profile == "save" ? "Сохранить" : "OK", 0x50000000, 10, 55, 80, 25, dialog, new IntPtr(profile == "save" ? 6 : 1), IntPtr.Zero, IntPtr.Zero);
                ShowWindow(dialog, 4); // SW_SHOWNOACTIVATE: never steal the user's focus.
                return dialog.ToInt64().ToString();
            });
        }
        public static void Close(string hwnd) { OnUi<bool>(delegate { return DestroyWindow(new IntPtr(Int64.Parse(hwnd))); }); }
        public static int Actions { get { return actions; } }
        public static void Hang() { queue.Enqueue(delegate { Thread.Sleep(5000); }); }
        public static void Stop() { stopping = true; if (thread != null) thread.Join(2000); }
    }
}
