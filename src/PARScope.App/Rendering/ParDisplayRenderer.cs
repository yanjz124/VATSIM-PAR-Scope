using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using SkiaSharp;

namespace PARScope.App.Rendering;

public sealed record ParRenderOptions
{
    public double RangeScaleNm { get; init; } = 10;
    public bool LogRange { get; init; }
    public bool ShowSafetyLimits { get; init; } = true;
    /// <summary>UK RA 3291 blue 2° and 5° azimuth lines (not on US displays).</summary>
    public bool ShowCourseWedge { get; init; }
    public bool ShowScan { get; init; } = true;
    public int HistoryDots { get; init; } = 6;
    public string? Selected { get; init; }
    public Advisory? SelectedAdvisory { get; init; }
    public bool MtiEnabled { get; init; } = true;
    /// <summary>Wind and altimeter line for the status window, e.g. "WIND 300/09  ALT 30.02".</summary>
    public string? Weather { get; init; }
}

/// <summary>Screen position of a drawn target, for click selection.</summary>
public readonly record struct HitTarget(string Callsign, float X, float Y);

/// <summary>
/// The AZ-EL indicator: elevation above, azimuth below, one shared range axis with touchdown at the
/// left. Laid out after a current USMC PAR screen and the ITT GCA-2000 patent (US5907568, US6600442):
/// full-height range lines, Cartesian feet about touchdown, glidepath and course cursors with mile
/// ticks, coverage limits from the antenna, and a data block of callsign / speed and range / offset.
///
/// With the compressed (log) range axis, heights and offsets are scaled by X(r)/r, the trick the real
/// display uses so that every constant-angle line (glidepath, coverage limits) stays straight.
/// </summary>
public sealed class ParDisplayRenderer : IDisposable
{
    private const float LabelRowH = 18;
    private Ink _ink = new();

    /// <summary>Switches the scope font; takes effect on the next frame.</summary>
    public void SetFont(string? family)
    {
        if (family != null && family.Equals(_ink.FamilyName, StringComparison.OrdinalIgnoreCase)) return;
        var old = _ink;
        _ink = new Ink(family);
        old.Dispose();
    }
    private readonly List<HitTarget> _hits = [];
    private readonly LabelPlacer _labels = new();

    public IReadOnlyList<HitTarget> Hits => _hits;

    // Frame layout and scales.
    private ScopeTheme _t = ScopeTheme.Digital;
    private float _left, _right, _elTop, _elBottom, _azTop, _azBottom, _elZeroY, _azZeroY;
    private double _rMinNm, _rMaxNm, _elPxPerFt, _azPxPerFt;
    private bool _log;

    public void Render(SKCanvas c, float width, float height, ParGeometry? geo, RadarSimulator? radar, double now, ParRenderOptions opt)
    {
        _t = radar != null ? ScopeTheme.For(radar.Preset) : ScopeTheme.Digital;
        _ink.Glow = false;
        c.Clear(_t.Background);
        _hits.Clear();

        if (geo == null || radar == null)
        {
            c.DrawText("SELECT AN AIRPORT AND RUNWAY", width / 2, height / 2, SKTextAlign.Center, _ink.Font, _ink.TextPaint(ScopeTheme.Digital.AxisLabel));
            return;
        }

        Layout(width, height, geo, opt);
        DrawRangeLines(c, opt);
        DrawElevationCursors(c, geo, radar, opt);
        DrawAzimuthCursors(c, geo, radar, opt);
        if (opt.ShowScan) DrawScan(c, geo, radar);
        DrawVideo(c, geo, radar, now, opt);
        if (!_t.Analog)
        {
            // In log mode the vertical scale varies with range, so a fixed feet scale would lie.
            if (!_log) DrawAxisLabels(c);
            DrawStatusBox(c, geo, radar, opt);
        }
    }

    // ---- Layout & mapping ------------------------------------------------------------------------

    private void Layout(float w, float h, ParGeometry geo, ParRenderOptions opt)
    {
        float marginX = _t.Analog ? 16 : 56;
        _left = marginX;
        _right = w - 14;
        float top = 10, bottom = h - (_t.Analog ? 10 : LabelRowH);
        float usable = bottom - top - LabelRowH;
        _elTop = top;
        _elBottom = top + usable * 0.52f;
        _azTop = _elBottom + LabelRowH;
        _azBottom = bottom;

        _log = opt.LogRange;
        _rMaxNm = opt.RangeScaleNm;
        _rMinNm = -Math.Max(0.04 * opt.RangeScaleNm, 0.2);

        double scaleFt = opt.RangeScaleNm * Units.FeetPerNm;
        double hTop = Math.Max(geo.GlidepathHeightFt(scaleFt) * 1.6, 300);
        double hBottom = -hTop * 0.10;
        _elPxPerFt = (_elBottom - _elTop) / (hTop - hBottom);
        _elZeroY = (float)(_elTop + hTop * _elPxPerFt);

        double crossHalf = Math.Max(ParGeometry.CourseLineFt(scaleFt, 5.0) * 1.15, 600);
        _azPxPerFt = (_azBottom - _azTop) / 2 / crossHalf;
        _azZeroY = (_azTop + _azBottom) / 2;
    }

    private float X(double rangeFt)
    {
        double r = rangeFt / Units.FeetPerNm;
        double f;
        if (_log)
        {
            // x ∝ ln(1 + r/k) measured off a USMC PAR screen (k ≈ 5 NM on its 10 NM scale).
            double k = _rMaxNm / 2.0;
            double span = Math.Log(1 + _rMaxNm / k) + (-_rMinNm) / k;
            f = r >= 0 ? (-_rMinNm / k + Math.Log(1 + r / k)) / span : (r - _rMinNm) / k / span;
        }
        else f = (r - _rMinNm) / (_rMaxNm - _rMinNm);
        return (float)(_left + f * (_right - _left));
    }

    /// <summary>Pixels per foot of height/offset at a range; constant in linear mode, X(r)/r-scaled in log mode.</summary>
    private double VerticalScale(double basePxPerFt, double rangeFt)
    {
        if (!_log || rangeFt <= 0) return basePxPerFt;
        double rMaxFt = _rMaxNm * Units.FeetPerNm;
        double x0 = X(0);
        return basePxPerFt * rMaxFt / (X(rMaxFt) - x0) * (X(rangeFt) - x0) / rangeFt;
    }

    private float YEl(double heightFt, double rangeFt) => (float)(_elZeroY - heightFt * VerticalScale(_elPxPerFt, rangeFt));

    /// <summary>Right of course is drawn up: a plan view seen from above, the aircraft flying toward touchdown at the left.</summary>
    private float YAz(double crossFt, double rangeFt) => (float)(_azZeroY - crossFt * VerticalScale(_azPxPerFt, rangeFt));

    private double RangeMinFt => _rMinNm * Units.FeetPerNm;
    private double RangeMaxFt => _rMaxNm * Units.FeetPerNm;

    private SKRect ElRect => new(_left, _elTop, _right, _elBottom);
    private SKRect AzRect => new(_left, _azTop, _right, _azBottom);

    /// <summary>Strokes value(range) as a polyline across the visible range.</summary>
    private void Curve(SKCanvas c, SKPaint paint, Func<double, double> value, Func<double, double, float> y, double fromFt, double toFt)
    {
        fromFt = Math.Max(fromFt, RangeMinFt);
        toFt = Math.Min(toFt, RangeMaxFt);
        if (toFt <= fromFt) return;
        using var b = new SKPathBuilder();
        const int n = 160;
        for (int i = 0; i <= n; i++)
        {
            double r = fromFt + (toFt - fromFt) * i / n;
            var p = new SKPoint(X(r), y(value(r), r));
            if (i == 0) b.MoveTo(p); else b.LineTo(p);
        }
        using var path = b.Detach();
        c.DrawPath(path, paint);
    }

    // ---- Static picture --------------------------------------------------------------------------

    private void DrawRangeLines(SKCanvas c, ParRenderOptions opt)
    {
        double minor = opt.RangeScaleNm <= 5 ? 0.25 : 0.5;
        float lastLabelRight = float.NegativeInfinity;
        for (double nm = 0; nm <= _rMaxNm + 1e-6; nm += minor)
        {
            bool whole = Math.Abs(nm - Math.Round(nm)) < 1e-6;
            bool major = whole && (int)Math.Round(nm) % 5 == 0;
            float x = X(nm * Units.FeetPerNm);
            var p = major ? _ink.Line(_t.RangeMajor, 1f)
                  : whole ? _ink.Line(_t.RangeMinor, 1f, LineStyle.Dashed)
                  : _ink.Line(_t.RangeMinor.Fade(0.6), 1f, LineStyle.Dotted);
            if (_t.Analog && !whole) continue;
            c.DrawLine(x, _elTop, x, _elBottom, p);
            c.DrawLine(x, _azTop, x, _azBottom, p);

            if (!_t.Analog && whole && (opt.RangeScaleNm <= 10 || (int)nm % 2 == 0))
            {
                string label = nm.ToString("0.00");
                float lw = _ink.FontSmall.MeasureText(label);
                bool last = x + lw + 2 > _right;
                float left = last ? x - 2 - lw : x + 2;
                if (left > lastLabelRight + 6)
                {
                    c.DrawText(label, left, _elBottom + 13, SKTextAlign.Left, _ink.FontSmall, _ink.TextPaint(_t.RangeLabel));
                    lastLabelRight = left + lw;
                }
            }
        }
    }

    private void DrawElevationCursors(SKCanvas c, ParGeometry geo, RadarSimulator radar, ParRenderOptions opt)
    {
        c.Save();
        c.ClipRect(ElRect);
        _ink.Glow = _t.Analog;

        c.DrawLine(_left, YEl(0, 1), _right, YEl(0, 1), _ink.Line(_t.Ground, 1f));

        // Elevation coverage limits from the antenna.
        var ant = geo.AntennaPoint;
        foreach (double el in new[] { radar.Preset.ElMinDeg, radar.Preset.ElMaxDeg })
        {
            double tan = Math.Tan(Units.DegToRad(el));
            Curve(c, _ink.Line(_t.Coverage, 1.2f, LineStyle.Dashed), r => ant.HeightFt + tan * (r - ant.RangeFt), YEl, Math.Max(ant.RangeFt, 1), RangeMaxFt);
        }

        // Lower safe limit ("B") cursor, glidepath angle less 0.5° from touchdown: dotted under the glidepath.
        if (opt.ShowSafetyLimits)
            Curve(c, _ink.Line(_t.Safety, 1.4f, LineStyle.Dotted), geo.LowerSafeLimitHeightFt, YEl, 1, RangeMaxFt);

        // Glidepath cursor from the GPI, with a tick every mile.
        Curve(c, _ink.Line(_t.Cursor, 1.6f), geo.GlidepathHeightFt, YEl, 1, RangeMaxFt);
        for (int nm = 1; nm <= (int)_rMaxNm; nm++)
        {
            double r = nm * Units.FeetPerNm;
            float x = X(r), y = YEl(geo.GlidepathHeightFt(r), r);
            c.DrawLine(x, y - 5, x, y + 5, _ink.Line(_t.Cursor, 1.2f));
        }

        // Decision height mark across the glidepath.
        double dhR = geo.DecisionHeightRangeFt;
        float dx = X(dhR), dy = YEl(geo.Site.DecisionHeightFt, dhR);
        c.DrawLine(dx, dy - 10, dx, dy + 10, _ink.Line(_t.Safety, 1.6f));

        // Touchdown box and threshold mark on the ground line.
        float gy = YEl(0, 1);
        c.DrawRect(X(0) - 4, gy - 4, 8, 8, _ink.Line(_t.Runway, 1f));
        float tx = X(geo.ThresholdRangeFt);
        c.DrawLine(tx, gy, tx, YEl(geo.Site.TchFt, geo.ThresholdRangeFt), _ink.Line(_t.Runway, 1.2f));
        _ink.Glow = false;
        c.Restore();
    }

    private void DrawAzimuthCursors(SKCanvas c, ParGeometry geo, RadarSimulator radar, ParRenderOptions opt)
    {
        c.Save();
        c.ClipRect(AzRect);
        _ink.Glow = _t.Analog;

        // Runway outline and touchdown box.
        double halfW = (geo.Runway.WidthFt ?? 150) / 2;
        double len = geo.Runway.LengthFt ?? 8000;
        float rx0 = X(geo.ThresholdRangeFt), rx1 = X(geo.ThresholdRangeFt - len);
        float ry0 = YAz(halfW, -1), ry1 = YAz(-halfW, -1);
        c.DrawRect(SKRect.Create(Math.Min(rx0, rx1), Math.Min(ry0, ry1), Math.Abs(rx0 - rx1), Math.Max(2, Math.Abs(ry1 - ry0))), _ink.Line(_t.Runway, 1f));
        c.DrawRect(X(0) - 4, _azZeroY - 4, 8, 8, _ink.Line(_t.Runway, 1f));

        // Antenna sector limits.
        var ant = geo.AntennaPoint;
        foreach (double az in new[] { -radar.Preset.AzLeftDeg, radar.Preset.AzRightDeg })
        {
            double tan = Math.Tan(Units.DegToRad(az));
            Curve(c, _ink.Line(_t.Coverage, 1.2f, LineStyle.Dashed), r => ant.CrossFt + tan * (r - ant.RangeFt), YAz, Math.Max(ant.RangeFt, 1), RangeMaxFt);
        }

        if (opt.ShowCourseWedge)
        {
            foreach (double deg in new[] { 2.0, 5.0 })
                foreach (int s in new[] { 1, -1 })
                    Curve(c, _ink.Line(_t.CourseWedge, 1f), r => s * ParGeometry.CourseLineFt(r, deg), YAz, 1, RangeMaxFt);
        }


        // Course cursor with mile ticks.
        c.DrawLine(X(0), _azZeroY, _right, _azZeroY, _ink.Line(_t.Cursor, 1.6f));
        for (int nm = 1; nm <= (int)_rMaxNm; nm++)
        {
            float x = X(nm * Units.FeetPerNm);
            c.DrawLine(x, _azZeroY - 5, x, _azZeroY + 5, _ink.Line(_t.Cursor, 1.2f));
        }
        _ink.Glow = false;
        c.Restore();
    }

    /// <summary>Analog indicators paint along the beam, so the current antenna angle shows as a faint trace.</summary>
    private void DrawScan(SKCanvas c, ParGeometry geo, RadarSimulator radar)
    {
        var ant = geo.AntennaPoint;
        var p = _ink.Line(_t.Scan, 2f);
        c.Save();
        c.ClipRect(ElRect);
        double tanEl = Math.Tan(Units.DegToRad(radar.ElBeamDeg));
        Curve(c, p, r => ant.HeightFt + tanEl * (r - ant.RangeFt), YEl, Math.Max(ant.RangeFt, 1), RangeMaxFt);
        c.Restore();
        c.Save();
        c.ClipRect(AzRect);
        double tanAz = Math.Tan(Units.DegToRad(radar.AzBeamDeg));
        Curve(c, p, r => ant.CrossFt + tanAz * (r - ant.RangeFt), YAz, Math.Max(ant.RangeFt, 1), RangeMaxFt);
        c.Restore();
    }

    // ---- Video and tracks ------------------------------------------------------------------------

    private void DrawVideo(SKCanvas c, ParGeometry geo, RadarSimulator radar, double now, ParRenderOptions opt)
    {
        double rawWindow = _t.Analog ? _t.PersistenceSec * 2.5 : Math.Max(radar.Preset.AzScanSec, radar.Preset.ElScanSec) * 1.15;

        foreach (var scope in new[] { ScopeKind.Elevation, ScopeKind.Azimuth })
        {
            var clip = scope == ScopeKind.Elevation ? ElRect : AzRect;
            c.Save();
            c.ClipRect(clip);

            // History dots: the last N paints of each tracked target, oldest faintest.
            if (opt.HistoryDots > 0 && radar.Preset.Tracking)
            {
                foreach (var g in radar.Plots.Where(p => p.Scope == scope && p.Callsign != null).GroupBy(p => p.Callsign))
                {
                    var list = g.ToList();
                    int start = Math.Max(0, list.Count - 1 - opt.HistoryDots);
                    for (int i = start; i < list.Count - 1; i++)
                    {
                        double f = (i - start + 1.0) / (list.Count - start);
                        c.DrawCircle(Project(scope, list[i]), 1.5f, _ink.Solid(_t.History.Fade(0.2 + 0.6 * f)));
                    }
                }
            }

            // Raw video. Analog returns smear along range and glow; digital plots are small dots.
            _ink.Glow = _t.Analog;
            foreach (var p in radar.Plots)
            {
                if (p.Scope != scope) continue;
                double age = now - p.Time;
                if (age > rawWindow || age < 0) continue;
                var color = p.IsClutter ? _t.Clutter : _t.RawVideo;
                var pt = Project(scope, p);
                if (_t.Analog)
                {
                    // The return is as tall (elevation) or wide (azimuth) as the beam at that range, which is
                    // what the controller divides into quarters to judge deviation (NAVEDTRA 14342A Table 10-1).
                    double alpha = Math.Exp(-age / _t.PersistenceSec);
                    double halfFt = scope == ScopeKind.Elevation
                        ? radar.Preset.ElevationTargetHalfFt(p.Polar.RangeFt)
                        : radar.Preset.AzimuthTargetHalfFt(p.Polar.RangeFt);
                    float halfPx = (float)Math.Clamp(halfFt * VerticalScale(scope == ScopeKind.Elevation ? _elPxPerFt : _azPxPerFt, p.Point.RangeFt), 1.5, 14);
                    c.DrawRoundRect(pt.X - 3, pt.Y - halfPx, 6, halfPx * 2, 2, 2, _ink.Solid(color.Fade(alpha)));
                }
                else if (p.IsClutter || !radar.Preset.Tracking)
                {
                    c.DrawCircle(pt, 1.6f, _ink.Solid(color.Fade(1 - age / rawWindow * 0.6)));
                }
            }
            _ink.Glow = false;

            // Tracks: symbol first for all, then data blocks placed so nothing overlaps.
            var shown = new List<(RadarTrack Track, float X, float Y, bool Selected)>();
            foreach (var tr in radar.Tracks.Values)
            {
                bool inThisScope = scope == ScopeKind.Elevation ? tr.HasElevation : tr.HasAzimuth;
                if (!inThisScope) continue;
                double since = now - (scope == ScopeKind.Elevation ? tr.LastElPaint : tr.LastAzPaint);
                if (since > 6) continue;
                float x = X(tr.RangeFt), y = scope == ScopeKind.Elevation ? YEl(tr.HeightFt, tr.RangeFt) : YAz(tr.CrossFt, tr.RangeFt);
                if (!clip.Contains(x, y)) continue;
                _hits.Add(new HitTarget(tr.Callsign, x, y));
                shown.Add((tr, x, y, tr.Callsign.Equals(opt.Selected, StringComparison.OrdinalIgnoreCase)));
            }

            _labels.Reset(clip);
            foreach (var s in shown) _labels.Reserve(new SKRect(s.X - 4, s.Y - 4, s.X + 4, s.Y + 4));
            foreach (var s in shown.OrderByDescending(s => s.Selected).ThenBy(s => s.Track.RangeFt))
            {
                // Every track is drawn alike, as on the reference displays; selection is only a bracket.
                var col = _t.Track;
                if (radar.Preset.Tracking)
                {
                    c.DrawRect(s.X - 2, s.Y - 2, 4, 4, _ink.Solid(col));
                    DrawDataBlock(c, geo, scope, s.Track, s.X, s.Y, col);
                }
                // Systems without a tracker (FPN-63) have no symbology; the bracket only marks which
                // return the side-panel readouts refer to.
                if (s.Selected) DrawBracket(c, s.X, s.Y, col);
            }
            c.Restore();
        }
    }

    /// <summary>GCA-2000 style block: callsign / ground speed and range / offset in feet with trend arrow.</summary>
    private void DrawDataBlock(SKCanvas c, ParGeometry geo, ScopeKind scope, RadarTrack tr, float x, float y, SKColor col)
    {
        var dev = geo.DeviationOf(tr.Point);
        double gpRate = tr.HeightRateFps - tr.RangeRateFps * Math.Tan(Units.DegToRad(geo.Site.GlidepathDeg));
        double holding = Math.Max(2.0, tr.RangeFt * 0.00008);
        string up = _ink.HasArrows ? "↑" : "^", down = _ink.HasArrows ? "↓" : "v";

        string line1 = tr.Callsign;
        string line2 = $"{tr.GroundSpeedKt:000} {Math.Max(0, tr.RangeFt) / Units.FeetPerNm:0.0}";
        string line3 = scope == ScopeKind.Elevation
            ? $"{Signed(dev.AboveGlidepathFt)}{Trend(gpRate, holding, up, down)}"
            : $"{(dev.RightOfCourseFt >= 0 ? "R" : "L")}{Math.Abs(dev.RightOfCourseFt):0}{Trend(tr.CrossRateFps, holding, up, down)}";

        float w = new[] { line1, line2, line3 }.Max(s => _ink.Font.MeasureText(s)) + 2;
        const float lineH = 13, h = lineH * 3;
        var box = _labels.Place(x, y, w, h);

        float lx = box.MidX < x ? box.Right : box.Left;
        float ly = box.MidY < y ? box.Bottom - 3 : box.Top + 3;
        c.DrawLine(x, y, lx, ly, _ink.Line(col, 1f));

        var tp = _ink.TextPaint(col);
        c.DrawText(line1, box.Left + 1, box.Top + 10, SKTextAlign.Left, _ink.Font, tp);
        c.DrawText(line2, box.Left + 1, box.Top + 10 + lineH, SKTextAlign.Left, _ink.Font, tp);
        c.DrawText(line3, box.Left + 1, box.Top + 10 + 2 * lineH, SKTextAlign.Left, _ink.Font, tp);
    }

    private static string Trend(double rate, double holding, string up, string down) =>
        rate > holding ? up : rate < -holding ? down : "";

    private void DrawBracket(SKCanvas c, float x, float y, SKColor col)
    {
        var p = _ink.Line(col, 1.2f);
        const float r = 9, k = 4;
        c.DrawLine(x - r, y - r, x - r + k, y - r, p); c.DrawLine(x - r, y - r, x - r, y - r + k, p);
        c.DrawLine(x + r, y - r, x + r - k, y - r, p); c.DrawLine(x + r, y - r, x + r, y - r + k, p);
        c.DrawLine(x - r, y + r, x - r + k, y + r, p); c.DrawLine(x - r, y + r, x - r, y + r - k, p);
        c.DrawLine(x + r, y + r, x + r - k, y + r, p); c.DrawLine(x + r, y + r, x + r, y + r - k, p);
    }

    private SKPoint Project(ScopeKind scope, Plot p) =>
        new(X(p.Point.RangeFt), scope == ScopeKind.Elevation ? YEl(p.Point.HeightFt, p.Point.RangeFt) : YAz(p.Point.CrossFt, p.Point.RangeFt));

    // ---- Digital chrome --------------------------------------------------------------------------

    /// <summary>Feet scales on the left edge, as in the GCA-2000 drawings.</summary>
    private void DrawAxisLabels(SKCanvas c)
    {
        var tp = _ink.TextPaint(_t.AxisLabel);
        var tick = _ink.Line(_t.AxisLabel, 1f);
        double hStep = NiceStep((_elZeroY - _elTop) / _elPxPerFt / 4);
        for (double h = 0; YEl(h, -1) > _elTop + 6; h += hStep)
        {
            float y = YEl(h, -1);
            c.DrawLine(_left - 4, y, _left, y, tick);
            c.DrawText(h.ToString("0"), _left - 6, y + 4, SKTextAlign.Right, _ink.FontSmall, tp);
        }
        double cStep = NiceStep((_azZeroY - _azTop) / _azPxPerFt / 2);
        for (double v = -2 * cStep; v <= 2 * cStep + 1; v += cStep)
        {
            float y = YAz(v, -1);
            if (y < _azTop + 6 || y > _azBottom - 4) continue;
            c.DrawLine(_left - 4, y, _left, y, tick);
            string label = v == 0 ? "0" : (v > 0 ? "R" : "L") + Math.Abs(v).ToString("0");
            c.DrawText(label, _left - 6, y + 4, SKTextAlign.Right, _ink.FontSmall, tp);
        }
    }

    /// <summary>White-bordered status window at the top left, as on the real console.</summary>
    private void DrawStatusBox(SKCanvas c, ParGeometry geo, RadarSimulator radar, ParRenderOptions opt)
    {
        var rw = geo.Runway;
        // Only what the reference screens show: time, PAR runway, "GS: 3.0° RWY: 248°" (GCA-2000 patent
        // fig. 4A: glidepath angle and magnetic runway course), range, wind/altimeter.
        string[] lines =
        [
            $"{DateTime.UtcNow:HHmmss}  PAR RWY-{rw.RunwayId}",
            $"GS: {geo.Site.GlidepathDeg:0.0}° RWY: {rw.MagneticCourseDeg:000}°",
            $"RNG:{opt.RangeScaleNm:0}NM",
            .. (opt.Weather is { Length: > 0 } wx ? new[] { wx } : []),
        ];
        float w = lines.Max(l => _ink.Font.MeasureText(l)) + 12, h = lines.Length * 14 + 8;
        var box = SKRect.Create(_left + 8, _elTop + 6, w, h);
        c.DrawRect(box, _ink.Solid(SKColors.Black));
        c.DrawRect(box, _ink.Line(SKColors.White, 1f));
        var tp = _ink.TextPaint(SKColors.White);
        for (int i = 0; i < lines.Length; i++)
            c.DrawText(lines[i], box.Left + 6, box.Top + 15 + i * 14, SKTextAlign.Left, _ink.Font, tp);
        _labels.Reserve(box);
    }

    private static string Signed(double v) => (v >= 0 ? "+" : "-") + Math.Abs(v).ToString("0");

    private static double NiceStep(double raw)
    {
        if (raw <= 0 || double.IsNaN(raw)) return 500;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3.5 ? 2.5 : n < 7.5 ? 5 : 10) * mag;
    }

    public void Dispose() => _ink.Dispose();
}
