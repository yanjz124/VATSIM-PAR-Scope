using System.Net.Http;
using PARScope.Core.Data;
using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Sim;
using PARScope.Core.Traffic;

namespace PARScope.App.Services;

/// <summary>State of the vPilot plugin link, updated from the UDP thread.</summary>
public sealed record LinkState(
    double LastDatagram = double.NegativeInfinity,
    bool? FastHook = null,
    string? PluginVersion = null,
    string? Callsign = null,
    double? FastPerMin = null,
    double? SlowPerMin = null,
    string? Error = null);

/// <summary>
/// Owns the live picture: traffic sources → track store → PAR and ASR radar models. Ticked once per
/// rendered frame on the UI thread; network input arrives on a background thread.
/// </summary>
public sealed class ScopeEngine : IDisposable
{
    private UdpTrafficSource? _udp;
    private VatsimDataFeed? _feed;

    /// <summary>Seconds without plugin data before the public data feed takes over.</summary>
    public const double FeedFallbackAfterSec = 20;
    private LinkState _link = new();

    public IClock Clock { get; } = SystemClock.Instance;
    public TrackStore Store { get; } = new();
    public TrainingTraffic Training { get; } = new();
    public TrafficRecorder Recorder { get; } = new();

    public RunwayEnd? Runway { get; private set; }
    public AirportRecord? Airport { get; private set; }
    public ParGeometry? Geometry { get; private set; }
    public RadarSimulator? Radar { get; private set; }
    public AsrSimulator? Asr { get; private set; }
    public RadarPreset Preset { get; private set; } = RadarPreset.Fpn68;
    public IReadOnlyList<TargetTrack> Targets { get; private set; } = [];
    public double Now { get; private set; }

    public LinkState Link => Volatile.Read(ref _link);

    public bool ClutterEnabled { get; set; } = true;
    /// <summary>Noise, multipath, clutter and missed paints on both radars. Off by default.</summary>
    public bool SimulateErrors { get; set; }
    public bool MtiEnabled { get; set; } = true;

    public void StartNetwork(int port)
    {
        _udp?.Dispose();
        _udp = new UdpTrafficSource(port, Clock, OnMessage);
        _udp.Error += e => Volatile.Write(ref _link, Link with { Error = e });
        _udp.Start();
    }

    /// <summary>Starts the 15 s public data feed as a fallback for when no plugin is streaming.</summary>
    public void StartDataFeed(HttpClient http, Func<bool> enabled)
    {
        _feed?.Dispose();
        _feed = new VatsimDataFeed(http, Clock, Ingest,
            () => enabled() && Clock.Now - Link.LastDatagram > FeedFallbackAfterSec && Clock.Now - XPilotLast > FeedFallbackAfterSec && !CrcLive,
            () => Runway is { } r ? GeoPoint.FromFeet(r.ThresholdLat, r.ThresholdLon, r.ThresholdElevFt) : null);
        _feed.Start();
    }

    public VatsimDataFeed? Feed => _feed;

    /// <summary>xPilot's visual-machine stream (xPilot command .visualip 127.0.0.1), or null when off.</summary>
    public XPilotVisualSource? XPilot { get; private set; }
    public string? XPilotError { get; private set; }

    /// <summary>Starts or stops listening for xPilot (Settings › Data).</summary>
    public void SetXPilot(bool on)
    {
        XPilot?.Dispose();
        XPilot = null;
        XPilotError = null;
        if (!on) return;
        var x = new XPilotVisualSource(XPilotVisualSource.DefaultPort, Clock, Ingest);
        x.Error += e => XPilotError = e;
        x.Start();
        XPilot = x;
    }

    /// <summary>Local time of the last xPilot message (positions or heartbeats), or -inf.</summary>
    public double XPilotLast => XPilot?.LastMessageTime ?? double.NegativeInfinity;
    public bool XPilotLive => Clock.Now - XPilotLast < 15;

    /// <summary>vNAS target data captured from CRC's UDP (Settings › Data). Off unless the user turns it on.</summary>
    public CrcFeed Crc => _crc ??= new CrcFeed(Clock, Ingest);
    private CrcFeed? _crc;

    /// <summary>
    /// Network traffic is on the scope but no source has delivered lately (vPilot, xPilot or the CRC feed within 6 s,
    /// the 15 s data feed within 30 s): the feed as a whole is lost, and tracks coast longer.
    /// </summary>
    public bool DataLost { get; private set; }

    private bool AnySourceFresh =>
        Clock.Now - Link.LastDatagram < 6
        || Clock.Now - XPilotLast < 6
        || (_crc != null && Clock.Now - _crc.LastPacket < 6)
        || (_feed?.LastUpdateUtc is DateTime u && (DateTime.UtcNow - u).TotalSeconds < 30);

    /// <summary>CRC feed data has arrived recently.</summary>
    public bool CrcLive => _crc != null && Clock.Now - _crc.LastPacket < 10;

    public void Configure(AirportRecord airport, RunwayEnd runway, ParSiteConfig site, RadarPreset preset)
    {
        Airport = airport;
        Runway = runway;
        Preset = preset;
        Geometry = new ParGeometry(runway, site);
        Training.Geometry = Geometry;
        Training.HomeAirportName = airport.Name;
        Radar = new RadarSimulator(preset, Geometry) { ClutterEnabled = ClutterEnabled, MtiEnabled = MtiEnabled };
        Asr = new AsrSimulator(GeoPoint.FromFeet(runway.ThresholdLat, runway.ThresholdLon, runway.ThresholdElevFt));
    }

    public void Tick()
    {
        Now = Clock.Now;
        foreach (var m in Training.Step(Now)) Store.Apply(m);
        Targets = Store.Snapshot(Now);
        DataLost = !AnySourceFresh && Targets.Any(t => !t.IsSimulated);
        if (Radar != null)
        {
            Radar.DataLost = DataLost;
            Radar.ClutterEnabled = ClutterEnabled;
            Radar.SimulateErrors = SimulateErrors;
            Radar.MtiEnabled = MtiEnabled;
            Radar.Advance(Now, Targets);
        }
        if (Asr != null)
        {
            Asr.SimulateErrors = SimulateErrors;
            Asr.Advance(Now, Targets);
        }
    }

    public void ClearTraining()
    {
        foreach (var a in Training.Aircraft) Store.Apply(new TargetRemoved(a.Callsign));
        Training.Clear();
    }

    private void OnMessage(TrafficMessage msg)
    {
        var link = Link with { LastDatagram = Clock.Now, Error = null };
        if (msg is LinkStatus s)
        {
            link = link with
            {
                FastHook = s.FastHook ?? link.FastHook,
                PluginVersion = s.PluginVersion ?? link.PluginVersion,
                Callsign = s.Callsign ?? link.Callsign,
                FastPerMin = s.FastPerMin ?? link.FastPerMin,
                SlowPerMin = s.SlowPerMin ?? link.SlowPerMin,
            };
        }
        else
        {
            Ingest(msg);
        }
        Volatile.Write(ref _link, link);
    }

    /// <summary>Network traffic (plugin or data feed): record for diagnostics, then track.</summary>
    private void Ingest(TrafficMessage msg)
    {
        if (msg is PositionReport r) Recorder.Record(r, Clock.Now);
        Store.Apply(msg);
    }

    public void Dispose()
    {
        Recorder.Dispose();
        _udp?.Dispose();
        _feed?.Dispose();
        _crc?.Dispose();
        XPilot?.Dispose();
    }
}
