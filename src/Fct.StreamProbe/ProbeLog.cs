using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Fct.StreamProbe
{
    // A self-contained, non-blocking log sink for the probe. The SDK event handlers run on the
    // RingBufferDataSubscription dispatch thread (shared with OverlayPlugin's ~20 handlers), so the
    // probe must NEVER do file I/O inline — that would stall every other subscriber. Instead each
    // Write enqueues a line onto a bounded buffer drained by ONE writer thread; when the buffer is
    // full the oldest line is dropped (the probe loses log lines, never blocks the stream). This is
    // the same drop-oldest discipline the real BridgeForwarder will need — proven here cheaply.
    internal sealed class ProbeLog : IDisposable
    {
        private readonly Queue<string> _queue = new Queue<string>();
        private readonly object _gate = new object();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly Thread _writer;
        private readonly string _path;
        private readonly int _capacity;
        private long _dropped;
        private volatile bool _running = true;

        public ProbeLog(string fileName = "streamprobe.log", int capacity = 8192)
        {
            _capacity = capacity;
            _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            try { File.WriteAllText(_path, $"# Fct.StreamProbe log — opened {DateTime.Now:O}{Environment.NewLine}"); }
            catch { /* best effort */ }

            _writer = new Thread(WriteLoop) { Name = "Fct.StreamProbe.Writer", IsBackground = true };
            _writer.Start();
        }

        public long DroppedCount => Interlocked.Read(ref _dropped);

        // tag is a short fixed-width category (INIT/SDK/NET/ACT/SNAP/…); message is free text.
        public void Write(string tag, string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff}  {tag,-9} {message}";
            lock (_gate)
            {
                if (_queue.Count >= _capacity)
                {
                    _queue.Dequeue();                 // drop oldest — never block the caller
                    Interlocked.Increment(ref _dropped);
                }
                _queue.Enqueue(line);
            }
            _signal.Set();
        }

        private void WriteLoop()
        {
            var batch = new StringBuilder();
            while (_running)
            {
                _signal.WaitOne(250);
                Drain(batch);
            }
            Drain(batch); // final flush on shutdown
        }

        private void Drain(StringBuilder batch)
        {
            batch.Clear();
            lock (_gate)
                while (_queue.Count > 0)
                    batch.AppendLine(_queue.Dequeue());

            if (batch.Length == 0) return;
            try { File.AppendAllText(_path, batch.ToString()); }
            catch { /* file locked / gone — drop this batch rather than throw on the writer thread */ }
        }

        public void Dispose()
        {
            try { Write("CLOSE", $"shutting down. dropped={DroppedCount}"); } catch { }
            _running = false;
            _signal.Set();
            try { _writer.Join(2000); } catch { }
            _signal.Dispose();
        }
    }
}
