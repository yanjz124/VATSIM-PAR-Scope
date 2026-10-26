using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using PARScope.Core.Traffic;
using SharpPcap;

namespace PARScope.App.Services;

/// <summary>How the CRC feed captures vNAS's UDP datagrams to CRC.</summary>
public enum CrcCaptureMode
{
    Off,
    /// <summary>Npcap if it is installed, otherwise the raw socket when running as administrator.</summary>
    Auto,
    /// <summary>Windows raw socket: nothing to install; vibe PAR must run as administrator.</summary>
    RawSocket,
    /// <summary>Npcap (npcap.com): install once (as administrator); then no admin rights needed.</summary>
    Npcap,
}

/// <summary>
/// Read-only listener for the target data vNAS sends CRC over UDP (server port 6809): Tower Cab aircraft with
/// exact altitude and STARS targets, about 1 Hz each, plain MessagePack (docs/CRC-VNAS-UDP.md). CRC owns the
/// UDP port, so the datagrams are read by packet capture, either a raw socket or Npcap. Nothing is sent and
/// CRC is not touched.
/// </summary>
public sealed class CrcFeed : IDisposable
{
    private readonly Core.Traffic.IClock _clock;
    private readonly Action<TrafficMessage> _sink;
    private readonly Lock _lock = new();
    private readonly List<Socket> _sockets = [];
    private readonly List<ICaptureDevice> _devices = [];
    private CancellationTokenSource _cts = new();
    private readonly Dictionary<string, double> _seen = new(StringComparer.OrdinalIgnoreCase);

    public CrcFeed(Core.Traffic.IClock clock, Action<TrafficMessage> sink)
    {
        _clock = clock;
        _sink = sink;
    }

    public VnasUdpDecoder Decoder { get; } = new();
    public CrcCaptureMode Mode { get; private set; }
    /// <summary>Why the feed is not running, or null when it is.</summary>
    public string? Problem { get; private set; }
    /// <summary>The capture method actually in use (Auto resolves to Npcap or the raw socket).</summary>
    public CrcCaptureMode Method { get; private set; }
    public double LastPacket { get; private set; } = double.NegativeInfinity;

    /// <summary>Aircraft heard in the last 10 s.</summary>
    public int AircraftCount(double now)
    {
        lock (_lock) return _seen.Count(kv => now - kv.Value < 10);
    }

    public static bool IsAdministrator
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void Start(CrcCaptureMode mode)
    {
        Stop();
        Mode = mode;
        Problem = null;
        _cts = new CancellationTokenSource();
        try
        {
            switch (mode)
            {
                case CrcCaptureMode.RawSocket: StartRawSockets(); Method = mode; break;
                case CrcCaptureMode.Npcap: StartNpcap(); Method = mode; break;
                case CrcCaptureMode.Auto: StartAuto(); break;
            }
        }
        catch (Exception ex) when (ex is SocketException or UnauthorizedAccessException or DllNotFoundException or PcapException or TypeInitializationException or InvalidOperationException)
        {
            Stop();
            Mode = mode;
            Problem = ex switch
            {
                DllNotFoundException or TypeInitializationException => "Npcap is not installed (npcap.com).",
                UnauthorizedAccessException { Message: var m } when mode == CrcCaptureMode.Auto => m,
                SocketException { SocketErrorCode: SocketError.AccessDenied } or UnauthorizedAccessException => "Needs administrator rights.",
                _ => ex.Message,
            };
        }
    }

    /// <summary>Npcap when installed (no rights needed); otherwise the raw socket if running as administrator.</summary>
    private void StartAuto()
    {
        try
        {
            StartNpcap();
            Method = CrcCaptureMode.Npcap;
            return;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PcapException or InvalidOperationException)
        {
            foreach (var d in _devices) { try { d.Close(); } catch (PcapException) { } }
            _devices.Clear();
        }
        if (!IsAdministrator) throw new UnauthorizedAccessException("Install Npcap (npcap.com), or run vibe PAR as administrator.");
        StartRawSockets();
        Method = CrcCaptureMode.RawSocket;
    }

    // ---- Raw socket (SIO_RCVALL): every IPv4 packet on an interface, administrator only. ----

    private void StartRawSockets()
    {
        if (!IsAdministrator) throw new UnauthorizedAccessException("administrator");
        foreach (var address in LocalIPv4Addresses())
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
            s.Bind(new IPEndPoint(address, 0));
            s.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
            _sockets.Add(s);
            var token = _cts.Token;
            _ = Task.Run(() => RawLoop(s, token), token);
        }
        if (_sockets.Count == 0) throw new InvalidOperationException("No network interface with an IPv4 address.");
    }

    private void RawLoop(Socket s, CancellationToken ct)
    {
        var buffer = new byte[65535];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = s.Receive(buffer); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return; }
            if (UdpFromServer(buffer.AsSpan(0, n), 0) is (int start, int length)) Handle(buffer.AsSpan(start, length));
        }
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    // ---- Npcap (libpcap API through SharpPcap): no administrator rights once Npcap is installed. ----

    private void StartNpcap()
    {
        foreach (var dev in CaptureDeviceList.Instance)
        {
            if (dev is SharpPcap.LibPcap.LibPcapLiveDevice live && (live.Loopback || live.Addresses.Count == 0)) continue;
            dev.OnPacketArrival += OnPcapPacket;
            dev.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 100 });
            dev.Filter = $"udp and src port {VnasUdpDecoder.ServerPort}";
            dev.StartCapture();
            _devices.Add(dev);
        }
        if (_devices.Count == 0) throw new InvalidOperationException("Npcap found no network adapters.");
    }

    private void OnPcapPacket(object sender, PacketCapture e)
    {
        var raw = e.GetPacket();
        int offset = raw.LinkLayerType switch
        {
            PacketDotNet.LinkLayers.Ethernet => EthernetPayload(raw.Data),
            PacketDotNet.LinkLayers.Null or PacketDotNet.LinkLayers.Loop => 4,
            PacketDotNet.LinkLayers.Raw or PacketDotNet.LinkLayers.RawLegacy => 0,
            _ => -1,
        };
        if (offset < 0) return;
        if (UdpFromServer(raw.Data, offset) is (int start, int length)) Handle(raw.Data.AsSpan(start, length));
    }

    private static int EthernetPayload(byte[] frame)
    {
        if (frame.Length < 14) return -1;
        int type = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12));
        return type == 0x8100 && frame.Length >= 18 ? 18 : 14;
    }

    // ---- Shared: IPv4/IPv6 → UDP from port 6809 → payload. ----

    /// <summary>Start and length of the UDP payload when the IP packet at <paramref name="ip"/> is UDP from the vNAS port.</summary>
    internal static (int Start, int Length)? UdpFromServer(ReadOnlySpan<byte> packet, int ip)
    {
        if (packet.Length < ip + 28) return null;
        int version = packet[ip] >> 4, l4, proto;
        if (version == 4) { l4 = ip + (packet[ip] & 0x0F) * 4; proto = packet[ip + 9]; }
        else if (version == 6) { l4 = ip + 40; proto = packet[ip + 6]; }
        else return null;
        if (proto != 17 || packet.Length < l4 + 8) return null;
        if (BinaryPrimitives.ReadUInt16BigEndian(packet[l4..]) != VnasUdpDecoder.ServerPort) return null;
        int udpLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(l4 + 4)..]);
        int length = Math.Min(udpLength - 8, packet.Length - l4 - 8);
        return length > 0 ? (l4 + 8, length) : null;
    }

    private void Handle(ReadOnlySpan<byte> payload)
    {
        double now = _clock.Now;
        IReadOnlyList<PositionReport> reports;
        lock (_lock)
        {
            LastPacket = now;
            reports = Decoder.Decode(payload, now);
            foreach (var r in reports) _seen[r.Callsign] = now;
        }
        foreach (var r in reports) _sink(r);
    }

    public void Stop()
    {
        _cts.Cancel();
        foreach (var s in _sockets) { try { s.Close(); } catch (SocketException) { } }
        _sockets.Clear();
        foreach (var d in _devices)
        {
            try { d.StopCapture(); d.Close(); } catch (PcapException) { }
            d.OnPacketArrival -= OnPcapPacket;
        }
        _devices.Clear();
        Mode = Method = CrcCaptureMode.Off;
    }

    public void Dispose() => Stop();
}
