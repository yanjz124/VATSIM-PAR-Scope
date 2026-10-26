using PARScope.Core.Data;
using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Traffic;
using SkiaSharp;

namespace PARScope.App.Rendering;

/// <summary>
/// North-up surveillance view in the style of an FAA STARS TCW: black field, grey video map, blue
/// history trails, beacon slashes, green full data blocks with a time-shared second line, and an SSA
/// clock/altimeter readout. Used to vector traffic onto the PAR final.
/// </summary>
public sealed class PlanRenderer : IDisposable
{
    private Ink _ink = new();
    private readonly List<HitTarget> _hits = [];
    private readonly LabelPlacer _labels = new();
    private float _cx, _cy, _pxPerM;

    private static readonly SKColor Map = new(0x8C, 0x8C, 0x8C);
    private static readonly SKColor MapDim = new(0x46, 0x46, 0x46);
    private static readonly SKColor Rings = new(0x2C, 0x2C, 0x2C);
    private static readonly SKColor DataBlock = new(0x12, 0xD0, 0x12);
    private static readonly SKColor Owned = SKColors.White;
    private static readonly SKColor Beacon = new(0x2A, 0x9A, 0xFF);
    private static readonly SKColor Ssa = new(0x12, 0xD0, 0x12);
    private static readonly SKColor[] History =
    [
        new(0x1E, 0x50, 0xE6), new(0x1A, 0x44, 0xC4), new(0x16, 0x3A, 0xA6), new(0x12, 0x30, 0x88), new(0x0E, 0x26, 0x6C),
    ];

    public IReadOnlyList<HitTarget> Hits => _hits;

    public void SetFont(string? family)
    {
        if (family != null && family.Equals(_ink.FamilyName, StringComparison.OrdinalIgnoreCase)) return;
        var old = _ink;
        _ink = new Ink(family);
        old.Dispose();
    }

    public void Render(SKCanvas c, float w, float h, ParGeometry? geo, AsrSimulator? asr, AirportRecord? airport,
        RadarPreset preset, IReadOnlyList<TargetTrack> targets, double now, double rangeNm, string? selected, bool showSweep, string? weather = null)
    {
        c.Clear(SKColors.Black);
        _hits.Clear();
        if (geo == null || asr == null) return;

        // Put the threshold off-centre so the final approach course runs across the view.
        double radiusM = rangeNm * Units.MetersPerNm;
        _pxPerM = (float)(Math.Min(w, h) / 2 / radiusM);
        double outbound = Units.DegToRad(geo.Runway.TrueHeadingDeg + 180);
        double shift = radiusM * 0.45;
        _cx = (float)(w / 2 - Math.Sin(outbound) * shift * _pxPerM);
        _cy = (float)(h / 2 + Math.Cos(outbound) * shift * _pxPerM);

        // Range rings every 5 NM.
        for (int nm = 5; nm <= rangeNm * 2; nm += 5)
            c.DrawCircle(_cx, _cy, (float)(nm * Units.MetersPerNm * _pxPerM), _ink.Line(Rings, 1f));

        var frame = asr.Frame;

        // Video map: runways, and the final approach course with a tick each mile.
        if (airport != null)
        {
            foreach (var r in airport.Runways)
            {
                var start = frame.ToEnu(GeoPoint.FromFeet(r.ThresholdLat, r.ThresholdLon, r.ThresholdElevFt));
                double len = (r.LengthFt ?? 6000) * Units.MetersPerFoot * 0.5; // each end draws half
                double hd = Units.DegToRad(r.TrueHeadingDeg);
                c.DrawLine(P(start.X, start.Y), P(start.X + Math.Sin(hd) * len, start.Y + Math.Cos(hd) * len), _ink.Line(Map, 2f));
            }
        }
        var tdEnu = frame.EcefToEnu(geo.ToEcef(new ApproachPoint(0, 0, 0)));
        var farEnu = frame.EcefToEnu(geo.ToEcef(new ApproachPoint(15 * Units.FeetPerNm, 0, 0)));
        c.DrawLine(P(tdEnu.X, tdEnu.Y), P(farEnu.X, farEnu.Y), _ink.Line(MapDim, 1f));
        for (int nm = 1; nm <= 15; nm++)
        {
            var e = frame.EcefToEnu(geo.ToEcef(new ApproachPoint(nm * Units.FeetPerNm, 0, 0)));
            double half = (nm % 5 == 0 ? 450 : 225) * Units.MetersPerFoot;
            double px = Math.Cos(outbound) * half, py = -Math.Sin(outbound) * half;
            c.DrawLine(P(e.X - px, e.Y - py), P(e.X + px, e.Y + py), _ink.Line(MapDim, 1f));
        }

        if (showSweep)
        {
            double sw = Units.DegToRad(asr.SweepDeg);
            c.DrawLine(_cx, _cy, (float)(_cx + Math.Sin(sw) * w * 2), (float)(_cy - Math.Cos(sw) * w * 2), _ink.Line(MapDim.Fade(0.6), 1f));
        }

        // Targets: history trail, beacon slash, data block.
        var byCs = targets.ToDictionary(x => x.Callsign, StringComparer.OrdinalIgnoreCase);
        var shown = new List<(string Callsign, AsrPlot Last, SKPoint Pt, bool Selected)>();
        foreach (var g in asr.Plots.GroupBy(p => p.Callsign))
        {
            var list = g.ToList();
            var last = list[^1];
            if (now - last.Time > AsrSimulator.SweepSec * 2.5) continue;
            for (int i = list.Count - 2, k = 0; i >= 0 && k < History.Length; i--, k++)
            {
                var hp = P(list[i].EastM, list[i].NorthM);
                c.DrawRect(hp.X - 2, hp.Y - 2, 4, 4, _ink.Solid(History[k]));
            }
            shown.Add((g.Key, last, P(last.EastM, last.NorthM), g.Key.Equals(selected, StringComparison.OrdinalIgnoreCase)));
        }

        _labels.Reset(new SKRect(0, 26, w, h - 22));
        foreach (var s in shown) _labels.Reserve(new SKRect(s.Pt.X - 5, s.Pt.Y - 5, s.Pt.X + 5, s.Pt.Y + 5));
        bool typePhase = (int)(now / 2.0) % 2 == 1; // STARS time-shares the second line
        foreach (var s in shown.OrderByDescending(s => s.Selected))
        {
            var col = s.Selected ? Owned : DataBlock;
            _hits.Add(new HitTarget(s.Callsign, s.Pt.X, s.Pt.Y));

            // Beacon slash, oriented across the radar's line of sight.
            double brg = Math.Atan2(s.Last.EastM, s.Last.NorthM);
            float dx = (float)(Math.Cos(brg) * 5), dy = (float)(Math.Sin(brg) * 5);
            c.DrawLine(s.Pt.X - dx, s.Pt.Y - dy, s.Pt.X + dx, s.Pt.Y + dy, _ink.Line(Beacon, 2f));

            byCs.TryGetValue(s.Callsign, out var track);
            double gs = track?.GroundSpeedKt ?? (track?.VelocityEcef.Length ?? 0) / Units.MpsPerKnot;
            string alt = $"{s.Last.AltFt / 100:000}";
            string line2 = typePhase && track?.TypeCode is { Length: > 0 } type ? $"{alt} {type}" : $"{alt} {gs / 10:00}";
            float bw = Math.Max(_ink.Font.MeasureText(s.Callsign), _ink.Font.MeasureText(line2)) + 2;
            var box = _labels.Place(s.Pt.X, s.Pt.Y, bw, 28);
            float lx = box.MidX < s.Pt.X ? box.Right : box.Left, ly = box.MidY < s.Pt.Y ? box.Bottom - 4 : box.Top + 4;
            c.DrawLine(s.Pt.X, s.Pt.Y, lx, ly, _ink.Line(col, 1f));
            c.DrawText(s.Callsign, box.Left + 1, box.Top + 12, SKTextAlign.Left, _ink.Font, _ink.TextPaint(col));
            c.DrawText(line2, box.Left + 1, box.Top + 26, SKTextAlign.Left, _ink.Font, _ink.TextPaint(col));
        }

        // SSA: clock and altimeter, top left.
        c.DrawText($"{DateTime.UtcNow:HHmm/ss}", 8, 16, SKTextAlign.Left, _ink.Font, _ink.TextPaint(Ssa));
        string station = airport?.MetarId ?? "";
        c.DrawText($"{station} {weather ?? ""}".Trim(), 8, 31, SKTextAlign.Left, _ink.Font, _ink.TextPaint(Ssa));
        c.DrawText($"{rangeNm:0}", 8, h - 8, SKTextAlign.Left, _ink.Font, _ink.TextPaint(Ssa.Fade(0.7)));
    }

    private SKPoint P(double eastM, double northM) => new((float)(_cx + eastM * _pxPerM), (float)(_cy - northM * _pxPerM));

    public void Dispose() => _ink.Dispose();
}
