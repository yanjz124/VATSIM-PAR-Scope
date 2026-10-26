using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PARScope.Core.Traffic;

/// <summary>Receives RadarStreamerPlugin datagrams and feeds the parsed messages to a callback.</summary>
public sealed class UdpTrafficSource : IDisposable
{
    private readonly int _port;
    private readonly IClock _clock;
    private readonly Action<TrafficMessage> _sink;
    private readonly ProtocolParser _parser = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public UdpTrafficSource(int port, IClock clock, Action<TrafficMessage> sink)
    {
        _port = port;
        _clock = clock;
        _sink = sink;
    }

    public event Action<string>? Error;

    /// <summary>Local time of the last datagram, or -inf.</summary>
    public double LastDatagramTime { get; private set; } = double.NegativeInfinity;

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (SocketException ex)
        {
            Error?.Invoke($"Cannot listen on UDP {_port}: {ex.Message}");
            return;
        }

        using (udp)
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult res;
                try { res = await udp.ReceiveAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; } // ICMP port-unreachable echoes on Windows

                double now = _clock.Now;
                LastDatagramTime = now;
                foreach (var msg in _parser.ParseDatagram(Encoding.UTF8.GetString(res.Buffer), now))
                    _sink(msg);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(500); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
