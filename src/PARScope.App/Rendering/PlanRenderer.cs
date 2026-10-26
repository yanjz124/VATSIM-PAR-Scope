using PARScope.Core.Data;
using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Traffic;
using SkiaSharp;

namespace PARScope.App.Rendering;

/// <summary>North-up surveillance (ASR) view used to vector traffic onto the PAR final.</summary>
public sealed class PlanRenderer : IDisposable
{
    private readonly Ink _ink = new();
    private readonly List<HitTarget> _hits = [];
    private readonly LabelPlacer _labels = new();
    private float _cx, _cy, _pxPerM;

    public IReadOnlyList<HitTarget> Hits => _hits;

    public void Render(SKCanvas c, float w, float h, ParGeometry? geo, AsrSimulator? asr, AirportRecord? airport,
        RadarPreset preset, IReadOnlyList<TargetTrack> targets, double now, double rangeNm, string? selected, bool showSweep, string? weather = null)
    {
        var t = Palette;
        c.Clear(t.Background);
        _hits.Clear();
        if (geo == null || asr == null) return;

        // Put the threshold off-centre so the final approach course runs across the view.
        double radiusM = rangeNm * Units.MetersPerNm;
        _pxPerM = (float)(Math.Min(w, h) / 2 / radiusM);
        double outbound = Units.DegToRad(geo.Runway.TrueHeadingDeg + 180);
        double shift = radiusM * 0.45;
        _cx = (float)(w / 2 - Math.Sin(outbound) * shift * _pxPerM);
        _cy = (float)(h / 2 + Math.Cos(outbound) * shift * _pxPerM);

        // Range rings every 5 NM from the site.
        for (int nm = 5; nm <= rangeNm * 2; nm += 5)
        {
            c.DrawCircle(_cx, _cy, (float)(nm * Units.MetersPerNm * _pxPerM), _ink.Line(nm % 10 == 0 ? t.GridMajor : t.Grid, 1f));
            var lp = P(Math.Sin(outbound + 0.5) * nm * Units.MetersPerNm, Math.Cos(outbound + 0.5) * nm * Units.MetersPerNm);
            c.DrawText(nm.ToString(), lp.X, lp.Y, SKTextAlign.Center, _ink.FontSmall, _ink.TextPaint(t.TextDim));
        }

        // PAR coverage sector.
        var frame = asr.Frame;
        var ant = geo.AntennaPoint;
        var antEnu = frame.EcefToEnu(geo.ToEcef(ant));
        using (var b = new SKPathBuilder())
        {
            b.MoveTo(P(antEnu.X, antEnu.Y));
            double maxFt = preset.MaxRangeNm * Units.FeetPerNm;
            for (double az = -preset.AzLeftDeg; az <= preset.AzRightDeg + 1e-6; az += 1)
            {
                var pt = geo.FromPolar(new RadarPolar(maxFt, az, 2));
                var e = frame.EcefToEnu(geo.ToEcef(pt));
                b.LineTo(P(e.X, e.Y));
            }
            b.Close();
            using var wedge = b.Detach();
            c.DrawPath(wedge, _ink.Solid(t.Wedge.Fade(0.06)));
            c.DrawPath(wedge, _ink.Line(t.Wedge.Fade(0.5), 1f));
        }

        // Extended centreline with a tick every mile.
        var tdEnu = frame.EcefToEnu(geo.ToEcef(new ApproachPoint(0, 0, 0)));
        var farEnu = frame.EcefToEnu(geo.ToEcef(new ApproachPoint(20 * Units.FeetPerNm, 0, 0)));
        c.DrawLine(P(tdEnu.X, tdEnu.Y), P(farEnu.X, farEnu.Y), _ink.Line(t.Cursor.Fade(0.55), 1f, LineStyle.Dashed));
        for (int nm = 1; nm <= 20; nm++)
        {
            var e = frame.EcefToEnu(geo.ToEcef(new ApproachPoint(nm * Units.FeetPerNm, 0, 0)));
            double half = (nm % 5 == 0 ? 500 : 250) * Units.MetersPerFoot;
            double px = Math.Cos(outbound) * half, py = -Math.Sin(outbound) * half;
            c.DrawLine(P(e.X - px, e.Y - py), P(e.X + px, e.Y + py), _ink.Line(t.Cursor.Fade(0.55), 1f));
        }

        // Runways of the airport.
        if (airport != null)
        {
            foreach (var r in airport.Runways)
            {
                var start = frame.ToEnu(GeoPoint.FromFeet(r.ThresholdLat, r.ThresholdLon, r.ThresholdElevFt));
                double len = (r.LengthFt ?? 6000) * Units.MetersPerFoot * 0.5; // each end draws half
                double hd = Units.DegToRad(r.TrueHeadingDeg);
                c.DrawLine(P(start.X, start.Y), P(start.X + Math.Sin(hd) * len, start.Y + Math.Cos(hd) * len),
                    _ink.Line(r.RunwayId == geo.Runway.RunwayId ? t.Cursor : t.Runway, 3f));
            }
        }

        // Sweep (digital ASR displays such as STARS do not draw one; optional).
        if (showSweep)
        {
        double sw = Units.DegToRad(asr.SweepDeg);
        c.DrawLine(_cx, _cy, (float)(_cx + Math.Sin(sw) * w), (float)(_cy - Math.Cos(sw) * w), _ink.Line(t.Scan, 1.5f));
        }

        // Targets: history trail, current paint, data block.
        var byCs = targets.ToDictionary(x => x.Callsign, StringComparer.OrdinalIgnoreCase);
        var shown = new List<(string Callsign, AsrPlot Last, SKPoint Pt, bool Selected)>();
        foreach (var g in asr.Plots.GroupBy(p => p.Callsign))
        {
            var list = g.ToList();
            var last = list[^1];
            if (now - last.Time > AsrSimulator.SweepSec * 2.5) continue;
            for (int i = Math.Max(0, list.Count - 6); i < list.Count - 1; i++)
                c.DrawCircle(P(list[i].EastM, list[i].NorthM), 1.6f, _ink.Solid(t.History.Fade(0.25 + 0.1 * (i - list.Count + 6))));
            shown.Add((g.Key, last, P(last.EastM, last.NorthM), g.Key.Equals(selected, StringComparison.OrdinalIgnoreCase)));
        }

        _labels.Reset(new SKRect(0, 0, w, h));
        foreach (var s in shown) _labels.Reserve(new SKRect(s.Pt.X - 4, s.Pt.Y - 4, s.Pt.X + 4, s.Pt.Y + 4));
        foreach (var s in shown.OrderByDescending(s => s.Selected))
        {
            var col = s.Selected ? t.Selected : t.Track;
            _hits.Add(new HitTarget(s.Callsign, s.Pt.X, s.Pt.Y));
            c.DrawRect(s.Pt.X - 3, s.Pt.Y - 3, 6, 6, _ink.Solid(col));
            if (s.Selected) c.DrawCircle(s.Pt, 9, _ink.Line(col, 1.2f));

            byCs.TryGetValue(s.Callsign, out var track);
            double gs = track?.GroundSpeedKt ?? (track?.VelocityEcef.Length ?? 0) / Units.MpsPerKnot;
            string line2 = $"{s.Last.AltFt / 100:000} {gs / 10:00}";
            float bw = Math.Max(_ink.FontSmall.MeasureText(s.Callsign), _ink.FontSmall.MeasureText(line2)) + 2;
            var box = _labels.Place(s.Pt.X, s.Pt.Y, bw, 24);
            float lx = box.MidX < s.Pt.X ? box.Right : box.Left, ly = box.MidY < s.Pt.Y ? box.Bottom - 4 : box.Top + 4;
            c.DrawLine(s.Pt.X, s.Pt.Y, lx, ly, _ink.Line(col.Fade(0.55), 1f));
            c.DrawText(s.Callsign, box.Left + 1, box.Top + 10, SKTextAlign.Left, _ink.FontSmall, _ink.TextPaint(col));
            c.DrawText(line2, box.Left + 1, box.Top + 22, SKTextAlign.Left, _ink.FontSmall, _ink.TextPaint(s.Selected ? col : t.History));
        }

        c.DrawText($"ASR  {rangeNm:0} NM", 10, 18, SKTextAlign.Left, _ink.Font, _ink.TextPaint(t.Text));
        if (weather is { Length: > 0 })
            c.DrawText(weather, 10, h - 10, SKTextAlign.Left, _ink.Font, _ink.TextPaint(t.Text));
    }

    /// <summary>STARS-like surveillance colours: grey map, white targets, yellow selection.</summary>
    private sealed record PlanPalette(SKColor Background, SKColor Grid, SKColor GridMajor, SKColor Text, SKColor TextDim,
        SKColor Wedge, SKColor Cursor, SKColor Runway, SKColor Scan, SKColor History, SKColor Track, SKColor Selected);

    private static readonly PlanPalette Palette = new(
        Background: SKColors.Black,
        Grid: new SKColor(0x26, 0x2C, 0x31),
        GridMajor: new SKColor(0x38, 0x41, 0x48),
        Text: new SKColor(0xC8, 0xCC, 0xCF),
        TextDim: new SKColor(0x6E, 0x78, 0x80),
        Wedge: new SKColor(0x4A, 0x7E, 0xC8),
        Cursor: new SKColor(0xA8, 0xC8, 0x70),
        Runway: new SKColor(0x9A, 0x9A, 0x9A),
        Scan: new SKColor(0x6E, 0x78, 0x80, 0x40),
        History: new SKColor(0x9A, 0xA0, 0xA6),
        Track: SKColors.White,
        Selected: new SKColor(0xFF, 0xE0, 0x40));

    private SKPoint P(double eastM, double northM) => new((float)(_cx + eastM * _pxPerM), (float)(_cy - northM * _pxPerM));

    public void Dispose() => _ink.Dispose();
}
