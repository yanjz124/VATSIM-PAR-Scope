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
        _feed = new VatsimDataFeed(http, Clock, Store.Apply,
            () => enabled() && Clock.Now - Link.LastDatagram > FeedFallbackAfterSec,
            () => Runway is { } r ? GeoPoint.FromFeet(r.ThresholdLat, r.ThresholdLon, r.ThresholdElevFt) : null);
        _feed.Start();
    }

    public VatsimDataFeed? Feed => _feed;

    public void Configure(AirportRecord airport, RunwayEnd runway, ParSiteConfig site, RadarPreset preset)
    {
        Airport = airport;
        Runway = runway;
        Preset = preset;
        Geometry = new ParGeometry(runway, site);
        Training.Geometry = Geometry;
        Radar = new RadarSimulator(preset, Geometry) { ClutterEnabled = ClutterEnabled, MtiEnabled = MtiEnabled };
        Asr = new AsrSimulator(GeoPoint.FromFeet(runway.ThresholdLat, runway.ThresholdLon, runway.ThresholdElevFt));
    }

    public void Tick()
    {
        Now = Clock.Now;
        foreach (var m in Training.Step(Now)) Store.Apply(m);
        Targets = Store.Snapshot(Now);
        if (Radar != null)
        {
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
            Store.Apply(msg);
        }
        Volatile.Write(ref _link, link);
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _feed?.Dispose();
    }
}
