using System.Text.Json;
using PARScope.Core.Geo;

namespace PARScope.Core.Traffic;

/// <summary>
/// Degraded fallback source: the public VATSIM v3 data feed (regenerated about every 15 s). It opens
/// no network connection of its own, so it is always within VATSIM policy, but 15-second positions are
/// far too coarse for real precision guidance. It is used only while no vPilot plugin is streaming.
/// </summary>
public sealed class VatsimDataFeed : IDisposable
{
    public const string Url = "https://data.vatsim.net/v3/vatsim-data.json";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly Action<TrafficMessage> _sink;
    private readonly Func<bool> _wanted;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <param name="wanted">Polled before each download; return false to skip (e.g. the plugin is live).</param>
    /// <param name="area">Only pilots within <see cref="RadiusNm"/> of this point are reported.</param>
    public VatsimDataFeed(HttpClient http, IClock clock, Action<TrafficMessage> sink, Func<bool> wanted, Func<GeoPoint?> area)
    {
        _http = http;
        _clock = clock;
        _sink = sink;
        _wanted = wanted;
        Area = area;
    }

    public Func<GeoPoint?> Area { get; }
    public double RadiusNm { get; set; } = 40;
    public bool Active { get; private set; }
    public DateTime? LastUpdateUtc { get; private set; }
    public string? LastError { get; private set; }

    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!_cts.IsCancellationRequested)
        {
            if (_wanted() && Area() is { } centre)
            {
                Active = true;
                try
                {
                    await using var stream = await _http.GetStreamAsync(Url, _cts.Token);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: _cts.Token);
                    var current = Parse(doc.RootElement, centre);
                    foreach (var r in current) _sink(r);
                    foreach (var gone in seen.Except(current.Select(r => r.Callsign), StringComparer.OrdinalIgnoreCase).ToList())
                        _sink(new TargetRemoved(gone));
                    seen = current.Select(r => r.Callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    LastError = null;
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException
                                               or TaskCanceledException && !_cts.IsCancellationRequested)
                {
                    LastError = ex.Message;
                }
            }
            else if (Active)
            {
                // The plugin came back: hand the picture over to it.
                Active = false;
                foreach (var cs in seen) _sink(new TargetRemoved(cs));
                seen.Clear();
            }

            try { await Task.Delay(Interval, _cts.Token); }
            catch (TaskCanceledException) { break; }
        }
    }

    private List<PositionReport> Parse(JsonElement root, GeoPoint centre)
    {
        var list = new List<PositionReport>();
        if (root.TryGetProperty("general", out var g) && g.TryGetProperty("update_timestamp", out var ts) && ts.TryGetDateTime(out var upd))
            LastUpdateUtc = upd;
        if (!root.TryGetProperty("pilots", out var pilots)) return list;

        var frame = new LocalFrame(centre);
        double limitM = RadiusNm * Units.MetersPerNm;
        double now = _clock.Now;
        var utcNow = DateTime.UtcNow;

        foreach (var p in pilots.EnumerateArray())
        {
            double lat = p.GetProperty("latitude").GetDouble(), lon = p.GetProperty("longitude").GetDouble();
            double altFt = p.GetProperty("altitude").GetDouble();
            var pos = GeoPoint.FromFeet(lat, lon, altFt);
            if (frame.ToEnu(pos).HorizontalLength > limitM) continue;

            // Place the report at the time the server last heard from the pilot, not when we downloaded it.
            double age = p.TryGetProperty("last_updated", out var lu) && lu.TryGetDateTime(out var luTime)
                ? Math.Clamp((utcNow - luTime.ToUniversalTime()).TotalSeconds, 0, 60) : 0;
            string? type = p.TryGetProperty("flight_plan", out var fp) && fp.ValueKind == JsonValueKind.Object
                           && fp.TryGetProperty("aircraft_short", out var ac) ? ac.GetString() : null;

            list.Add(new PositionReport(
                p.GetProperty("callsign").GetString() ?? "?",
                now - age,
                ReportSource.Feed,
                pos,
                TypeCode: type,
                HeadingDeg: p.GetProperty("heading").GetDouble(),
                GroundSpeedKt: p.GetProperty("groundspeed").GetDouble()));
        }
        return list;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
