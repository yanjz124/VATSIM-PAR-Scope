using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

/// <summary>xPilot's visual-machine stream (nng pair1 over TCP, MessagePack bodies as in xPilot's dto.h).</summary>
public class XPilotVisualTests
{
    private sealed class Clock : IClock { public double Now => 42; }

    // A fast position as xPilot's FastPositionUpdateDto packs it.
    private static byte[] FastPos(string cs, double vEast = 50, double vUp = -3, double vNorth = 60) =>
        Pack(["FSTPOS", new object[] { cs, 38.95, -77.45, 1500.0, 1200.0, 190.0, 0.0, 2.5, vEast, vUp, vNorth, 0.0, 0.0, 0.0, 0.0, 140.0 }]);

    private static byte[] Add(string cs) =>
        Pack(["ADD", new object[] { cs, "AAL", "B738", 38.95, -77.45, 1500.0, 190.0, 0.0, 2.5 }]);

    [Fact]
    public void Fast_position_has_velocity_in_east_north_up()
    {
        var r = Assert.IsType<PositionReport>(XPilotVisualSource.Decode(FastPos("AAL1"), 5));
        Assert.Equal(ReportSource.Fast, r.Source);
        Assert.Equal(38.95, r.Position.LatDeg, 6);
        Assert.Equal(1500, r.Position.AltFt, 3);
        Assert.Equal(50, r.VelocityEnu!.Value.X);   // east
        Assert.Equal(60, r.VelocityEnu!.Value.Y);   // north (xPilot vz)
        Assert.Equal(-3, r.VelocityEnu!.Value.Z);   // up (xPilot vy)
        Assert.Equal(140, r.GroundSpeedKt);
        Assert.Equal(190, r.HeadingDeg);
    }

    [Fact]
    public void Add_delete_and_disconnect_are_understood_and_the_rest_ignored()
    {
        var add = Assert.IsType<PositionReport>(XPilotVisualSource.Decode(Add("AAL1"), 5));
        Assert.Equal(("AAL1", ReportSource.Added, "B738"), (add.Callsign, add.Source, add.TypeCode));
        Assert.Equal("AAL1", Assert.IsType<TargetRemoved>(XPilotVisualSource.Decode(Pack(["DEL", new object[] { "AAL1", "timeout" }]), 5)).Callsign);
        Assert.IsType<AllTargetsRemoved>(XPilotVisualSource.Decode(Pack(["DISCON", Array.Empty<object>()]), 5));
        Assert.Null(XPilotVisualSource.Decode(Pack(["HB", new object[] { "AAL1" }]), 5));
        Assert.Null(XPilotVisualSource.Decode([0xC1], 5));
    }

    [Fact]
    public async Task Receives_traffic_as_an_xpilot_visual_machine()
    {
        int port = FreePort();
        var got = new BlockingCollection<TrafficMessage>();
        using var source = new XPilotVisualSource(port, new Clock(), got.Add);
        source.Start();

        // What xPilot's nng pair1 dialer does: handshake, then length + hop count + body per message.
        using var xpilot = new TcpClient();
        await xpilot.ConnectAsync(IPAddress.Loopback, port);
        var s = xpilot.GetStream();
        await s.WriteAsync(XPilotVisualSource.Handshake());
        var theirs = new byte[8];
        await s.ReadExactlyAsync(theirs);
        Assert.True(XPilotVisualSource.IsPair1Handshake(theirs));

        await Send(s, Pack(["CONN", new object[] { "1234567_TV", "", true }]));
        await Send(s, Add("AAL1"));
        await Send(s, FastPos("AAL1"));

        Assert.True(got.TryTake(out var first, 3000));
        Assert.Equal(ReportSource.Added, Assert.IsType<PositionReport>(first).Source);
        Assert.True(got.TryTake(out var second, 3000));
        Assert.Equal(42, Assert.IsType<PositionReport>(second).Time); // stamped with the local receive time
        Assert.Equal("1234567_TV", source.Callsign);
        Assert.Equal(1, source.Connections);
    }

    private static async Task Send(NetworkStream s, byte[] body)
    {
        var frame = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt64BigEndian(frame, (ulong)(4 + body.Length));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), 0); // pair1 hop count
        body.CopyTo(frame, 12);
        await s.WriteAsync(frame);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // Minimal MessagePack writer: arrays, strings, doubles, bools.
    private static byte[] Pack(object[] value)
    {
        var o = new List<byte>();
        Write(o, value);
        return [.. o];
    }

    private static void Write(List<byte> o, object v)
    {
        switch (v)
        {
            case string str:
                var b = Encoding.UTF8.GetBytes(str);
                o.Add(0xD9); o.Add((byte)b.Length); o.AddRange(b);
                break;
            case double d:
                var buf = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(buf, d);
                o.Add(0xCB); o.AddRange(buf);
                break;
            case bool x:
                o.Add(x ? (byte)0xC3 : (byte)0xC2);
                break;
            case object[] a:
                if (a.Length <= 15) o.Add((byte)(0x90 | a.Length));
                else { o.Add(0xDC); o.Add((byte)(a.Length >> 8)); o.Add((byte)a.Length); }
                foreach (var e in a) Write(o, e);
                break;
        }
    }
}
