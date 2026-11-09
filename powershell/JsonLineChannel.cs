using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace CorelMcp.Observer {
    public sealed class JsonLineChannel {
        readonly ConcurrentQueue<string> lines = new ConcurrentQueue<string>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly int maxBytes, maxQueued;
        volatile bool ended;
        volatile string fault;
        public JsonLineChannel(int limit, int queueLimit) {
            maxBytes = limit; maxQueued = queueLimit;
            var thread = new Thread(Read); thread.IsBackground = true; thread.Start();
        }
        public bool Ended { get { return ended && lines.IsEmpty; } }
        public string Fault { get { return fault; } }
        void Read() {
            try {
                var stream = Console.OpenStandardInput();
                var buffer = new byte[4096]; var line = new MemoryStream();
                var utf8 = new UTF8Encoding(false, true);
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                    for (int i = 0; i < count; i++) {
                        if (buffer[i] == 10) {
                            if (lines.Count >= maxQueued) { fault = "QUEUE_LIMIT"; return; }
                            var text = utf8.GetString(line.ToArray()).TrimEnd('\r');
                            lines.Enqueue(text); signal.Set(); line.SetLength(0);
                        } else {
                            if (line.Length >= maxBytes) { fault = "FRAME_LIMIT"; return; }
                            line.WriteByte(buffer[i]);
                        }
                    }
                }
                if (line.Length != 0) fault = "INCOMPLETE_FRAME";
            } catch { fault = "CHANNEL_ERROR"; }
            finally { ended = true; signal.Set(); }
        }
        public string Take(int timeoutMs) {
            string value;
            if (lines.TryDequeue(out value)) return value;
            if (ended) return null;
            signal.WaitOne(timeoutMs);
            return lines.TryDequeue(out value) ? value : null;
        }
    }
}
