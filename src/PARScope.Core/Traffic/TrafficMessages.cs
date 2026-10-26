using PARScope.Core.Geo;

namespace PARScope.Core.Traffic;

public enum ReportSource
{
    /// <summary>Public vPilot AircraftUpdated event, roughly every 5 s.</summary>
    Slow,
    /// <summary>VATSIM Velocity fast position (up to 5 Hz) with world velocities.</summary>
    Fast,
    /// <summary>Aircraft first added to the sim.</summary>
    Added,
    /// <summary>Built-in training traffic.</summary>
    Simulated,
    /// <summary>Public VATSIM data feed, about every 15 s (degraded fallback).</summary>
    Feed,
    /// <summary>vNAS target data CRC receives over UDP (Tower Cab or STARS), about 1 Hz, read by packet capture.</summary>
    Crc,
}

/// <summary>Where traffic comes from, for the source priority (Settings › Data).</summary>
public enum SourceGroup
{
    /// <summary>vNAS target data captured from CRC.</summary>
    Crc,
    /// <summary>The vPilot plugin (Velocity fast positions and 5 s updates).</summary>
    VPilot,
    /// <summary>The public VATSIM data feed (15 s).</summary>
    Feed,
}

public static class SourceGroups
{
    public static readonly IReadOnlyList<SourceGroup> DefaultPriority = [SourceGroup.Crc, SourceGroup.VPilot, SourceGroup.Feed];

    public static SourceGroup? Of(ReportSource s) => s switch
    {
        ReportSource.Crc => SourceGroup.Crc,
        ReportSource.Fast or ReportSource.Slow or ReportSource.Added => SourceGroup.VPilot,
        ReportSource.Feed => SourceGroup.Feed,
        _ => null,
    };
}

public abstract record TrafficMessage;

/// <param name="Time">Local monotonic time in seconds (see <see cref="IClock"/>).</param>
/// <param name="VelocityEnu">East/north/up m/s when the report carries velocities (Velocity fast packets).</param>
/// <param name="Frozen">Training aircraft held by a training pause: stationary but still shown as a moving target.</param>
/// <param name="Beacon">Transponder code as its four octal digits read as a decimal number (1200), when the source has it.</param>
/// <param name="CoarseAltitude">Altitude is Mode C in 100 ft steps (CRC STARS targets): fine for the ASR, useless for a glidepath.</param>
public sealed record PositionReport(
    string Callsign,
    double Time,
    ReportSource Source,
    GeoPoint Position,
    Vec3? VelocityEnu = null,
    string? TypeCode = null,
    double? PressureAltFt = null,
    double? HeadingDeg = null,
    double? GroundSpeedKt = null,
    bool Frozen = false,
    int? Beacon = null,
    bool CoarseAltitude = false) : TrafficMessage;

public sealed record TargetRemoved(string Callsign) : TrafficMessage;

public sealed record AllTargetsRemoved : TrafficMessage;

public sealed record LinkStatus(string Kind, bool? FastHook = null, string? Callsign = null, bool? Observer = null,
    double? FastPerMin = null, double? SlowPerMin = null, string? PluginVersion = null) : TrafficMessage;

public interface IClock
{
    /// <summary>Monotonic seconds.</summary>
    double Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();
    public double Now => System.Diagnostics.Stopwatch.GetElapsedTime(_start).TotalSeconds;
}

/// <summary>
/// Maps the plugin's wall-clock timestamps onto local monotonic time. UDP lines are batched, so
/// receive time alone would collapse several reports onto one instant; the sender's timestamp keeps
/// their true spacing. The offset tracks the minimum observed latency and follows slow drift.
/// </summary>
public sealed class ClockSync
{
    private double? _offset;

    public double ToLocal(long senderUnixMs, double receivedLocal)
    {
        double candidate = receivedLocal - senderUnixMs / 1000.0;
        if (_offset is null || candidate < _offset || candidate - _offset > 5.0)
            _offset = candidate;
        else
            _offset += (candidate - _offset.Value) * 0.002;
        return Math.Min(senderUnixMs / 1000.0 + _offset.Value, receivedLocal);
    }
}
