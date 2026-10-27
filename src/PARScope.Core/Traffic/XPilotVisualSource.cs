using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PARScope.Core.Geo;

namespace PARScope.Core.Traffic;

/// <summary>
/// Receives the traffic xPilot sends to its "visual machines" (extra X-Plane PCs): every message for its
/// X-Plane plugin is copied to each address set with xPilot's .visualip command, over TCP port 53100. vibe PAR
/// listens there as one more visual machine, so after <c>.visualip 127.0.0.1</c> an xPilot tower view
/// (<c>.towerview</c>) feeds the scope. Read-only: nothing is sent beyond the connection handshake.
///
/// The link is nng's pair1 protocol over TCP: an 8-byte handshake each way (<c>00 'S' 'P' 00</c>, protocol
/// 0x0011, <c>00 00</c>), then messages as a 64-bit big-endian length, a 32-bit hop count and the body.
/// Bodies are MessagePack <c>[type, [fields…]]</c> (xPilot's dto.h).
/// </summary>
public sealed class XPilotVisualSource : IDisposable
{
    /// <summary>xPilot's default plugin and visual-machine port (AppConfig DEFAULT_PLUGIN_PORT).</summary>
    public const int DefaultPort = 53100;
    private const ushort Pair1Protocol = 0x11;
    private const int MaxMessageBytes = 1 << 16; // xPilot drops anything over UINT16_MAX

    private readonly int _port;
    private readonly IClock _clock;
    private readonly Action<TrafficMessage> _sink;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _connections;

    public XPilotVisualSource(int port, IClock clock, Action<TrafficMessage> sink)
    {
        _port = port;
        _clock = clock;
        _sink = sink;
    }

    public event Action<string>? Error;

    /// <summary>Local time of the last message from xPilot, or -inf.</summary>
    public double LastMessageTime { get; private set; } = double.NegativeInfinity;
    /// <summary>xPilot clients currently connected.</summary>
    public int Connections => Volatile.Read(ref _connections);
    /// <summary>Network callsign xPilot reported (e.g. 1234567_TV), when it has connected to VATSIM.</summary>
    public string? Callsign { get; private set; }

    public void Start()
    {
        if (_listener != null) return;
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _listener = null;
            Error?.Invoke($"Cannot listen on TCP {_port}: {ex.Message}");
            return;
        }
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        Interlocked.Increment(ref _connections);
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var ct = _cts.Token;
                await stream.WriteAsync(Handshake(), ct);
                var head = new byte[8];
                await stream.ReadExactlyAsync(head, ct);
                if (!IsPair1Handshake(head)) return;

                var len = new byte[8];
                var buffer = new byte[MaxMessageBytes + 4];
                while (!ct.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(len, ct);
                    ulong n = BinaryPrimitives.ReadUInt64BigEndian(len);
                    if (n < 4 || n > (ulong)buffer.Length) return;
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)n), ct);

                    double now = _clock.Now;
                    LastMessageTime = now;
                    switch (Decode(buffer.AsSpan(4, (int)n - 4), now))
                    {
                        case LinkStatus { Callsign: { } cs }: Callsign = cs; break;
                        case { } msg: _sink(msg); break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or EndOfStreamException) { }
        finally
        {
            Interlocked.Decrement(ref _connections);
        }
    }

    public static byte[] Handshake()
    {
        var h = new byte[] { 0, (byte)'S', (byte)'P', 0, 0, 0, 0, 0 };
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(4), Pair1Protocol);
        return h;
    }

    public static bool IsPair1Handshake(ReadOnlySpan<byte> h) =>
        h.Length >= 8 && h[0] == 0 && h[1] == 'S' && h[2] == 'P' && h[3] == 0 && h[6] == 0 && h[7] == 0
        && BinaryPrimitives.ReadUInt16BigEndian(h[4..]) == Pair1Protocol;

    /// <summary>
    /// One xPilot message body. Aircraft added (ADD) and fast positions (FSTPOS, up to 5 Hz with velocities)
    /// become position reports; DEL removes an aircraft; DISCON (network disconnected) clears them all; CONN
    /// gives the network callsign. Everything else (radio, heartbeats, configuration) is ignored.
    /// </summary>
    public static TrafficMessage? Decode(ReadOnlySpan<byte> body, double now)
    {
        object? msg;
        try { msg = new MsgPackReader(body).Read(); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException or FormatException) { return null; }
        if (msg is not object?[] { Length: >= 2 } m || m[0] is not string type) return null;
        var f = m[1] as object?[] ?? [];

        switch (type)
        {
            // [callsign, airline, typeCode, latitude, longitude, altitudeTrue, heading, bank, pitch]
            case "ADD" when f.Length >= 7 && f[0] is string { Length: > 0 } cs && Num(f[3]) is double lat && Num(f[4]) is double lon && Num(f[5]) is double alt:
                return new PositionReport(cs, now, ReportSource.Added, GeoPoint.FromFeet(lat, lon, alt),
                    TypeCode: f[2] is string { Length: > 0 } t ? t : null, HeadingDeg: Num(f[6]));
            // [callsign, latitude, longitude, altitudeTrue, altitudeAgl, heading, bank, pitch,
            //  vx (east m/s), vy (up m/s), vz (north m/s), vp, vh, vb, noseWheelAngle, speed (kt)]
            case "FSTPOS" when f.Length >= 11 && f[0] is string { Length: > 0 } cs && Num(f[1]) is double lat && Num(f[2]) is double lon && Num(f[3]) is double alt:
            {
                Vec3? vel = Num(f[8]) is double ve && Num(f[9]) is double vu && Num(f[10]) is double vn ? new Vec3(ve, vn, vu) : null;
                double? gs = f.Length > 15 && Num(f[15]) is double s && s > 0 ? s : null;
                return new PositionReport(cs, now, ReportSource.Fast, GeoPoint.FromFeet(lat, lon, alt), vel,
                    HeadingDeg: Num(f[5]), GroundSpeedKt: gs);
            }
            case "DEL" when f.Length >= 1 && f[0] is string { Length: > 0 } cs:
                return new TargetRemoved(cs);
            case "DELALL" or "DISCON":
                return new AllTargetsRemoved();
            case "CONN" when f.Length >= 1 && f[0] is string { Length: > 0 } cs:
                return new LinkStatus("connected", Callsign: cs, Observer: f.Length > 2 && f[2] is true);
            default:
                return null;
        }
    }

    private static double? Num(object? o) => o switch { double d => d, float x => x, long l => l, ulong u => u, _ => null };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _cts.Dispose();
    }
}
