using System.Buffers.Binary;
using System.Text;
using PARScope.Core.Geo;

namespace PARScope.Core.Traffic;

/// <summary>
/// Decodes the target updates vNAS sends CRC over UDP (from the server's port 6809) into position reports.
/// The datagrams are plain MessagePack, <c>[3, [[topic, facility, null, null], [unionTag, payload]]]</c>;
/// see docs/CRC-VNAS-UDP.md. Two streams are used:
/// <list type="bullet">
/// <item>Tower Cab aircraft (topic 10, tag 24), about 1 Hz while moving, with altitude in feet to 0.01 ft:
/// the PAR source when a Tower Cab window is open at the field.</item>
/// <item>STARS targets (topic 22, tag 22), 1 Hz, with Mode C altitude in hundreds of feet, corrected to true
/// altitude with the altimeter setting. Skipped for an aircraft that is also on the Tower Cab stream.</item>
/// </list>
/// Field positions were identified from captures (see the document); unknown fields are ignored.
/// </summary>
public sealed class VnasUdpDecoder
{
    public const int ServerPort = 6809;
    public const int TopicStarsTargets = 22, TagStarsTarget = 22;
    public const int TopicTowerCab = 10, TagTowerCabAircraft = 24;

    /// <summary>An aircraft seen on the Tower Cab stream this recently is not also taken from STARS.</summary>
    public const double TowerCabPrecedenceSec = 3.0;

    private readonly Dictionary<string, double> _towerCabSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current altimeter setting, inches of mercury, to turn STARS Mode C into true altitude.</summary>
    public double AltimeterInHg { get; set; } = 29.92;

    public int Decoded { get; private set; }
    public int Undecodable { get; private set; }

    /// <summary>Position reports in one UDP payload, stamped with the local receive time.</summary>
    public IReadOnlyList<PositionReport> Decode(ReadOnlySpan<byte> payload, double now)
    {
        object? msg;
        try { msg = new MsgPackReader(payload).Read(); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException or FormatException)
        {
            Undecodable++;
            return [];
        }
        Decoded++;
        if (msg is not object?[] { Length: >= 2 } outer || outer[1] is not object?[] { Length: >= 2 } body
            || body[0] is not object?[] { Length: >= 1 } topic || body[1] is not object?[] { Length: >= 2 } inner
            || inner[1] is not object?[] p)
            return [];
        int topicId = AsInt(topic[0]), tag = AsInt(inner[0]);
        if (topicId == TopicTowerCab && tag == TagTowerCabAircraft && TowerCab(p, now) is { } tc)
        {
            _towerCabSeen[tc.Callsign] = now;
            return [tc];
        }
        if (topicId == TopicStarsTargets && tag == TagStarsTarget && Stars(p, now) is { } st
            && !(_towerCabSeen.TryGetValue(st.Callsign, out double seen) && now - seen < TowerCabPrecedenceSec))
            return [st];
        return [];
    }

    // [callsign, ?, type, onGround, [lat, lon], heading, ?, altitudeFt (true), pressureAltFt?]
    private static PositionReport? TowerCab(object?[] p, double now)
    {
        if (p.Length < 8 || p[0] is not string cs || LatLon(p[4]) is not var (lat, lon) || AsDouble(p[7]) is not double alt) return null;
        return new PositionReport(cs, now, ReportSource.Crc, new GeoPoint(lat, lon, alt * Units.MetersPerFoot),
            TypeCode: p[2] as string, PressureAltFt: p.Length > 8 ? AsDouble(p[8]) : null, HeadingDeg: AsDouble(p[5]));
    }

    // [trackId, callsign, ?, history, ?, [lat, lon], modeC100, ?, ?, ?, beacon, ?, ?, ?, ?, groundSpeedKt, trackDeg, …]
    private PositionReport? Stars(object?[] p, double now)
    {
        if (p.Length < 17 || p[1] is not string cs || cs.Length == 0 || LatLon(p[5]) is not var (lat, lon) || AsDouble(p[6]) is not double modeC100)
            return null;
        double pressureFt = modeC100 * 100;
        double trueFt = pressureFt + (AltimeterInHg - 29.92) * 1000;
        return new PositionReport(cs, now, ReportSource.Crc, new GeoPoint(lat, lon, trueFt * Units.MetersPerFoot),
            PressureAltFt: pressureFt, HeadingDeg: AsDouble(p[16]), GroundSpeedKt: AsDouble(p[15]));
    }

    private static (double, double)? LatLon(object? o) =>
        o is object?[] { Length: >= 2 } a && AsDouble(a[0]) is double lat && AsDouble(a[1]) is double lon && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180
            ? (lat, lon) : null;

    private static double? AsDouble(object? o) => o switch { double d => d, float f => f, long l => l, ulong u => u, _ => null };
    private static int AsInt(object? o) => o switch { long l => (int)l, ulong u => (int)u, _ => -1 };
}

/// <summary>Just enough MessagePack to read vNAS datagrams: maps, arrays, strings, numbers, nil, bool, bin, ext.</summary>
public ref struct MsgPackReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _d = data;
    private int _i;

    private ReadOnlySpan<byte> Take(int n) { var s = _d.Slice(_i, n); _i += n; return s; }

    public object? Read()
    {
        byte t = _d[_i++];
        if (t <= 0x7F) return (long)t;
        if (t >= 0xE0) return (long)(sbyte)t;
        if (t is >= 0x80 and <= 0x8F) return Map(t & 0x0F);
        if (t is >= 0x90 and <= 0x9F) return Array(t & 0x0F);
        if (t is >= 0xA0 and <= 0xBF) return Str(t & 0x1F);
        return t switch
        {
            0xC0 => null,
            0xC2 => false,
            0xC3 => true,
            0xC4 => Take(Take(1)[0]).ToArray(),
            0xC5 => Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))).ToArray(),
            0xC6 => Take(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))).ToArray(),
            0xCA => (double)BinaryPrimitives.ReadSingleBigEndian(Take(4)),
            0xCB => BinaryPrimitives.ReadDoubleBigEndian(Take(8)),
            0xCC => (long)Take(1)[0],
            0xCD => (long)BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
            0xCE => (long)BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
            0xCF => BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
            0xD0 => (long)(sbyte)Take(1)[0],
            0xD1 => (long)BinaryPrimitives.ReadInt16BigEndian(Take(2)),
            0xD2 => (long)BinaryPrimitives.ReadInt32BigEndian(Take(4)),
            0xD3 => BinaryPrimitives.ReadInt64BigEndian(Take(8)),
            0xD4 => Ext(1), 0xD5 => Ext(2), 0xD6 => Ext(4), 0xD7 => Ext(8), 0xD8 => Ext(16),
            0xC7 => Ext(Take(1)[0]),
            0xC8 => Ext(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xC9 => Ext(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
            0xD9 => Str(Take(1)[0]),
            0xDA => Str(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xDB => Str(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
            0xDC => Array(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xDD => Array(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
            0xDE => Map(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xDF => Map(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)))),
            _ => throw new InvalidDataException($"MessagePack type 0x{t:X2}"),
        };
    }

    private string Str(int n) => Encoding.UTF8.GetString(Take(n));

    private object?[] Array(int n)
    {
        if (n > _d.Length - _i) throw new InvalidDataException("array longer than the datagram");
        var a = new object?[n];
        for (int k = 0; k < n; k++) a[k] = Read();
        return a;
    }

    private Dictionary<object, object?> Map(int n)
    {
        if (n > _d.Length - _i) throw new InvalidDataException("map longer than the datagram");
        var m = new Dictionary<object, object?>(n);
        for (int k = 0; k < n; k++) { var key = Read() ?? "null"; m[key] = Read(); }
        return m;
    }

    /// <summary>Extension types (vNAS timestamps are ext −1); kept opaque.</summary>
    private (sbyte Type, byte[] Data) Ext(int n) { sbyte type = (sbyte)Take(1)[0]; return (type, Take(n).ToArray()); }
}
