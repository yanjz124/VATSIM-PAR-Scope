using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RadarStreamerPlugin
{
    /// <summary>
    /// Sends NDJSON lines over UDP from a background thread so vPilot's network and UI
    /// threads never block on socket I/O. Drops lines if the queue backs up.
    /// </summary>
    internal sealed class LineSender : IDisposable
    {
        private const int MaxQueued = 2000;
        private const int MaxDatagramBytes = 1400;

        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(MaxQueued);
        private readonly UdpClient _udp = new UdpClient();
        private readonly IPEndPoint _endpoint;
        private readonly Thread _thread;
        private readonly Action<string> _log;
        private long _dropped;
        private long _lastErrorLogMs;

        public LineSender(string host, int port, Action<string> log)
        {
            _log = log;
            _endpoint = new IPEndPoint(ResolveHost(host), port);
            _thread = new Thread(Run) { IsBackground = true, Name = "RadarStreamer UDP" };
            _thread.Start();
        }

        public long Dropped => Interlocked.Read(ref _dropped);

        public void Enqueue(string line)
        {
            if (!_queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
        }

        private void Run()
        {
            var batch = new StringBuilder(MaxDatagramBytes);
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                // Coalesce whatever is already queued into one datagram (lines stay intact).
                batch.Clear().Append(line);
                while (batch.Length < MaxDatagramBytes / 2 && _queue.TryTake(out var next))
                    batch.Append(next);
                Send(batch.ToString());
            }
        }

        private void Send(string text)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                _udp.Send(bytes, bytes.Length, _endpoint);
            }
            catch (Exception ex)
            {
                // Rate-limit error logging: a closed scope makes every send fail.
                long now = Protocol.UnixMs();
                if (now - _lastErrorLogMs > 30000)
                {
                    _lastErrorLogMs = now;
                    _log("UDP send error: " + ex.Message);
                }
            }
        }

        private static IPAddress ResolveHost(string host)
        {
            if (IPAddress.TryParse(host, out var ip)) return ip;
            foreach (var a in Dns.GetHostAddresses(host))
                if (a.AddressFamily == AddressFamily.InterNetwork) return a;
            return IPAddress.Loopback;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(500);
            _udp.Dispose();
        }
    }
}
