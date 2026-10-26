using System.Text.Json;
using PARScope.Core.Geo;

namespace PARScope.Core.Traffic;

/// <summary>
/// Parses RadarStreamerPlugin NDJSON lines. Accepts protocol v2 and the legacy v1 messages so an
/// older plugin build keeps working (slow updates only).
/// </summary>
public sealed class ProtocolParser
{
    private readonly ClockSync _clock = new();

    public IEnumerable<TrafficMessage> ParseDatagram(string text, double receivedLocal)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            TrafficMessage? msg;
            try { msg = ParseLine(line, receivedLocal); }
            catch (JsonException) { continue; }
            catch (FormatException) { continue; }
            if (msg != null) yield return msg;
        }
    }

    public TrafficMessage? ParseLine(string line, double receivedLocal)
    {
        using var doc = JsonDocument.Parse(line);
        var o = doc.RootElement;
        if (o.ValueKind != JsonValueKind.Object) return null;

        string type = Str(o, "type") ?? "";
        double time = o.TryGetProperty("t", out var t) && t.TryGetInt64(out var ms)
            ? _clock.ToLocal(ms, receivedLocal)
            : receivedLocal;

        return Num(o, "v") >= 2 ? ParseV2(o, type, time) : ParseV1(o, type, time);
    }

    private static TrafficMessage? ParseV2(JsonElement o, string type, double time)
    {
        switch (type)
        {
            case "pos":
            case "add":
            {
                string? cs = Str(o, "cs");
                double? lat = Num(o, "lat"), lon = Num(o, "lon"), alt = Num(o, "alt");
                if (string.IsNullOrEmpty(cs) || lat is null || lon is null || alt is null) return null;

                var src = type == "add" ? ReportSource.Added : Str(o, "src") == "fast" ? ReportSource.Fast : ReportSource.Slow;
                Vec3? vel = null;
                if (Num(o, "ve") is double ve && Num(o, "vn") is double vn && Num(o, "vu") is double vu)
                    vel = new Vec3(ve, vn, vu);

                return new PositionReport(cs, time, src, GeoPoint.FromFeet(lat.Value, lon.Value, alt.Value), vel,
                    Str(o, "ac"), Num(o, "palt"), Num(o, "hdg"), Num(o, "gs"));
            }
            case "del":
                return Str(o, "cs") is { Length: > 0 } dcs ? new TargetRemoved(dcs) : null;
            case "net":
                string state = Str(o, "state") ?? "";
                if (state is "disconnected" or "ended") return new AllTargetsRemoved();
                return new LinkStatus("connected", Callsign: Str(o, "cs"), Observer: Bool(o, "observer"));
            case "hello":
                return new LinkStatus("hello", FastHook: Bool(o, "fastHook"), PluginVersion: Str(o, "plugin"));
            case "stats":
                return new LinkStatus("stats", FastHook: Bool(o, "fastHook"), FastPerMin: Num(o, "fastPerMin"), SlowPerMin: Num(o, "slowPerMin"));
            default:
                return null;
        }
    }

    private static TrafficMessage? ParseV1(JsonElement o, string type, double time)
    {
        switch (type)
        {
            case "add":
            case "update":
            {
                string? cs = Str(o, "callsign");
                double? lat = Num(o, "lat"), lon = Num(o, "lon"), alt = Num(o, "alt_ft");
                if (string.IsNullOrEmpty(cs) || lat is null || lon is null || alt is null) return null;
                return new PositionReport(cs, time, type == "add" ? ReportSource.Added : ReportSource.Slow,
                    GeoPoint.FromFeet(lat.Value, lon.Value, alt.Value), null,
                    Str(o, "typeCode"), Num(o, "pressAlt_ft"), Num(o, "heading_deg"), Num(o, "speed_kts"));
            }
            case "delete":
                return Str(o, "callsign") is { Length: > 0 } cs1 ? new TargetRemoved(cs1) : null;
            case "network_disconnected":
            case "session_ended":
                return new AllTargetsRemoved();
            case "network_connected":
                return new LinkStatus("connected", FastHook: false, Callsign: Str(o, "callsign"), Observer: Bool(o, "observer"));
            case "plugin_initialized":
                return new LinkStatus("hello", FastHook: false, PluginVersion: "1.x");
            default:
                return null;
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
