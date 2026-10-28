using PARScope.Core.Radar;
using SkiaSharp;

namespace PARScope.App.Rendering;

/// <summary>
/// Scope color schemes, taken from reference imagery (see docs/PAR-RESEARCH.md, "Display reference"):
/// Digital follows a current USMC expeditionary PAR screen and the ITT GCA-2000 patent drawings:
/// black, green range lines, olive cursors, tan coverage limits, white targets and text.
/// The phosphor schemes imitate analog AZ-EL indicators: monochrome, glow, afterglow, no text.
/// </summary>
public sealed record ScopeTheme
{
    public required SKColor Background { get; init; }
    public required SKColor RangeMinor { get; init; }
    public required SKColor RangeMajor { get; init; }
    public required SKColor RangeLabel { get; init; }
    public required SKColor AxisLabel { get; init; }
    public required SKColor Cursor { get; init; }
    public required SKColor CourseWedge { get; init; }
    public required SKColor Safety { get; init; }
    public required SKColor Coverage { get; init; }
    public required SKColor Ground { get; init; }
    public required SKColor Runway { get; init; }
    public required SKColor RawVideo { get; init; }
    public required SKColor Clutter { get; init; }
    public required SKColor Track { get; init; }
    public required SKColor History { get; init; }
    public required SKColor Selected { get; init; }
    public required SKColor Warning { get; init; }
    public required SKColor Scan { get; init; }
    /// <summary>Analog CRT: glow, afterglow, sweep trace, and no alphanumerics at all.</summary>
    public bool Analog { get; init; }
    /// <summary>Afterglow time constant for raw video, seconds.</summary>
    public double PersistenceSec { get; init; }
    public LineStyle CoverageStyle { get; init; } = LineStyle.Dashed;
    public LineStyle SafetyStyle { get; init; } = LineStyle.Dotted;
    /// <summary>Solid (major) range lines at 1 NM as well as every 5 NM.</summary>
    public bool MajorAtOneMile { get; init; }
    /// <summary>Decision height drawn as a short horizontal line at DH altitude with a tick where the glidepath
    /// crosses it (ATNAVICS), instead of a tick across the glidepath (FPN-63 "PAR minimums marker").</summary>
    public bool DhLine { get; init; }
    public SKColor DhColor { get; init; } = SKColors.White;
    /// <summary>Half-mile range lines (dotted); null = faded <see cref="RangeMinor"/>.</summary>
    public SKColor? RangeHalf { get; init; }
    /// <summary>Quarter-mile range lines inside the first mile, where the log axis spreads it out (ATNAVICS).</summary>
    public bool QuarterMilesInFirstMile { get; init; }
    /// <summary>A horizontal ground line on the elevation display.</summary>
    public bool GroundLine { get; init; } = true;
    /// <summary>Touchdown boxes, threshold mark and runway outline (GCA-2000 drawings).</summary>
    public bool RunwayMarks { get; init; } = true;
    /// <summary>A tick across the glidepath and course lines at every mile (GCA-2000 drawings).</summary>
    public bool CursorTicks { get; init; } = true;
    /// <summary>Data block of the transponder code only, with a leader line (ATNAVICS); else callsign / speed and
    /// range / offset.</summary>
    public bool SquawkBlock { get; init; }
    /// <summary>History as short dashes along the range axis instead of dots (ATNAVICS).</summary>
    public bool HistoryDashes { get; init; }
    /// <summary>Track symbol size relative to the default 4 px square.</summary>
    public float TargetScale { get; init; } = 1f;

    public static readonly ScopeTheme Digital = new()
    {
        Background = SKColors.Black,
        RangeMinor = new SKColor(0x2E, 0x9A, 0x46),
        RangeMajor = new SKColor(0x9A, 0xD8, 0xA2),
        RangeLabel = new SKColor(0x58, 0xD0, 0x6E),
        AxisLabel = new SKColor(0x8C, 0x94, 0x8E),
        Cursor = new SKColor(0xA8, 0xC8, 0x70),
        CourseWedge = new SKColor(0x4A, 0x7E, 0xC8),
        Safety = new SKColor(0xF2, 0xF2, 0xF2),
        Coverage = new SKColor(0xD8, 0xCC, 0xA0),
        Ground = new SKColor(0x8A, 0x80, 0x62),
        Runway = new SKColor(0xB8, 0xB8, 0xB8),
        RawVideo = new SKColor(0xE6, 0xE6, 0xE6),
        Clutter = new SKColor(0x9A, 0x9A, 0x9A),
        Track = SKColors.White,
        History = new SKColor(0xC8, 0xC8, 0xC8),
        Selected = new SKColor(0xFF, 0xE0, 0x40),
        Warning = new SKColor(0xFF, 0x40, 0x40),
        Scan = new SKColor(0x2E, 0x9A, 0x46, 0x50),
        Analog = false,
        PersistenceSec = 0.9,
    };

    public static readonly ScopeTheme GreenPhosphor = new()
    {
        Background = new SKColor(0x03, 0x08, 0x04),
        RangeMinor = new SKColor(0x1C, 0x6E, 0x34),
        RangeMajor = new SKColor(0x2F, 0xA8, 0x52),
        RangeLabel = SKColors.Transparent,
        AxisLabel = SKColors.Transparent,
        Cursor = new SKColor(0x5C, 0xFF, 0x8E),
        CourseWedge = new SKColor(0x2B, 0xB8, 0x5E),
        Safety = new SKColor(0x3F, 0xD8, 0x72),
        Coverage = new SKColor(0x2A, 0x9A, 0x4C),
        Ground = new SKColor(0x2A, 0x9A, 0x4C),
        Runway = new SKColor(0x2B, 0xB8, 0x5E),
        RawVideo = new SKColor(0x8C, 0xFF, 0xB0),
        Clutter = new SKColor(0x4C, 0xD0, 0x78),
        Track = new SKColor(0xC8, 0xFF, 0xD8),
        History = new SKColor(0x4D, 0xD8, 0x7F),
        Selected = new SKColor(0xE6, 0xFF, 0xEE),
        Warning = new SKColor(0xE6, 0xFF, 0xEE),
        Scan = new SKColor(0x5C, 0xFF, 0x95, 0x22),
        Analog = true,
        PersistenceSec = 3.0,
    };

    public static readonly ScopeTheme AmberPhosphor = GreenPhosphor with
    {
        Background = new SKColor(0x0C, 0x07, 0x02),
        RangeMinor = new SKColor(0x6E, 0x48, 0x10),
        RangeMajor = new SKColor(0xA8, 0x70, 0x18),
        Cursor = new SKColor(0xFF, 0xC0, 0x30),
        CourseWedge = new SKColor(0xC8, 0x8C, 0x20),
        Safety = new SKColor(0xE8, 0xA8, 0x28),
        Coverage = new SKColor(0x9A, 0x66, 0x16),
        Ground = new SKColor(0x9A, 0x66, 0x16),
        Runway = new SKColor(0xC8, 0x8C, 0x20),
        RawVideo = new SKColor(0xFF, 0xD0, 0x60),
        Clutter = new SKColor(0xD8, 0x9C, 0x30),
        Track = new SKColor(0xFF, 0xE4, 0xA0),
        History = new SKColor(0xD8, 0x9C, 0x30),
        Selected = new SKColor(0xFF, 0xF2, 0xD0),
        Warning = new SKColor(0xFF, 0xF2, 0xD0),
        Scan = new SKColor(0xFF, 0xC0, 0x30, 0x22),
    };

    /// <summary>
    /// AN/TPN-31 ATNAVICS, from Army radar-training-simulator and USMC expeditionary screen footage and a
    /// close-up of an ATNAVICS display (Rugged Portable Systems monitor): green dashed whole-mile lines, solid at
    /// 1 and 5 NM, cream dotted half (and, in the first mile, quarter) mile lines, green labels "1.00"; cream
    /// solid coverage limits; green glidepath and course with no mile ticks; a cream dotted lower safe limit;
    /// a cyan DH line with a tick; no ground line or runway marks; white filled targets with a dashed history
    /// trail and a leader line to the beacon code alone.
    /// </summary>
    public static readonly ScopeTheme Atnavics = Digital with
    {
        RangeMinor = new SKColor(0x6C, 0xD0, 0x88),
        RangeMajor = new SKColor(0x9C, 0xF0, 0xB0),
        RangeLabel = new SKColor(0x8C, 0xF5, 0x9E),
        RangeHalf = new SKColor(0xD8, 0xD8, 0xD2),
        Cursor = new SKColor(0xB0, 0xD8, 0x9C),
        Coverage = new SKColor(0xED, 0xE6, 0xCC),
        Safety = new SKColor(0xED, 0xE6, 0xCC),
        Ground = new SKColor(0xED, 0xE6, 0xCC),
        CoverageStyle = LineStyle.Solid,
        SafetyStyle = LineStyle.Dotted,
        MajorAtOneMile = true,
        DhLine = true,
        DhColor = new SKColor(0xB8, 0xE4, 0xF0),
        QuarterMilesInFirstMile = true,
        GroundLine = false,
        RunwayMarks = false,
        CursorTicks = false,
        SquawkBlock = true,
        HistoryDashes = true,
        TargetScale = 1.75f,
    };

    public static ScopeTheme For(RadarPreset preset) => preset.Style switch
    {
        _ when preset.Id == "tpn31" => Atnavics,
        DisplayStyle.Phosphor when preset.Id == "gpn22" => AmberPhosphor,
        DisplayStyle.Phosphor => GreenPhosphor,
        _ => Digital,
    };
}

/// <summary>Reusable paints and fonts; Skia objects are native, so they are not recreated every frame.</summary>
internal sealed class Ink : IDisposable
{
    public readonly SKPaint Stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt };
    public readonly SKPaint Fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    public readonly SKPaint Text = new() { IsAntialias = true };
    /// <summary>Plain terminal face, close to the X11 fixed/Courier text on real PAR consoles.</summary>
    public readonly SKFont Font;
    public readonly SKFont FontSmall;
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([6f, 4f], 0);
    private readonly SKPathEffect _dot = SKPathEffect.CreateDash([1.5f, 3f], 0);
    private readonly SKMaskFilter _glow = SKMaskFilter.CreateBlur(SKBlurStyle.Solid, 1.8f);

    /// <param name="fontScale">Scope text size (Settings › Display), 1 = default.</param>
    /// <param name="symbolScale">Target symbols, history dots and brackets (Settings › Display), 1 = default.</param>
    public Ink(string? family = null, float fontScale = 1f, float symbolScale = 1f)
    {
        var face = Resolve(family);
        FamilyName = face.FamilyName;
        FontScale = fontScale;
        Sym = symbolScale;
        // FixedDemiBold is an X11 terminal face drawn for small sizes; it reads best a little larger.
        bool fixedFace = FamilyName.StartsWith("Fixed", StringComparison.OrdinalIgnoreCase);
        Font = new SKFont(face, (fixedFace ? 13f : 11.5f) * fontScale) { Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Full };
        FontSmall = new SKFont(face, (fixedFace ? 12f : 10f) * fontScale) { Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Full };
        HasArrows = Font.ContainsGlyphs("↑↓");
        LineHeight = Font.Size + 1.5f;
    }

    public float FontScale { get; }
    /// <summary>Symbol scale: multiply symbol dimensions by this.</summary>
    public float Sym { get; }
    /// <summary>Data-block line spacing for the current text size.</summary>
    public float LineHeight { get; }

    /// <summary>True when this kit was made for the same face and sizes.</summary>
    public bool Matches(string? family, float fontScale, float symbolScale) =>
        (family == null || family.Equals(FamilyName, StringComparison.OrdinalIgnoreCase)) && FontScale == fontScale && Sym == symbolScale;

    /// <summary>Scope fonts in order of preference: the STARS/ERAM-style FixedDemiBold if installed, then
    /// plain terminal faces that ship with Windows.</summary>
    public static readonly string[] Candidates = ["FixedDemiBold", "Lucida Console", "Consolas", "Courier New"];

    public string FamilyName { get; }

    /// <summary>Installed candidates, for the font picker.</summary>
    public static IReadOnlyList<string> AvailableFamilies() =>
        Candidates.Where(f => SKFontManager.Default.MatchFamily(f) is { } tf && tf.FamilyName.Equals(f, StringComparison.OrdinalIgnoreCase)).ToList();

    private static SKTypeface Resolve(string? family)
    {
        foreach (var f in (family is null ? Candidates : [family, .. Candidates]))
        {
            var tf = SKFontManager.Default.MatchFamily(f);
            if (tf != null && tf.FamilyName.Equals(f, StringComparison.OrdinalIgnoreCase)) return tf;
        }
        return SKTypeface.Default;
    }

    /// <summary>Whether the face has arrow glyphs for data-block trend indicators.</summary>
    public bool HasArrows { get; }
    public bool Glow { get; set; }

    public SKPaint Line(SKColor c, float width = 1f, LineStyle style = LineStyle.Solid)
    {
        Stroke.Color = c;
        Stroke.StrokeWidth = width;
        Stroke.PathEffect = style switch { LineStyle.Dashed => _dash, LineStyle.Dotted => _dot, _ => null };
        Stroke.MaskFilter = Glow ? _glow : null;
        return Stroke;
    }

    public SKPaint Solid(SKColor c)
    {
        Fill.Color = c;
        Fill.MaskFilter = Glow ? _glow : null;
        return Fill;
    }

    public SKPaint TextPaint(SKColor c)
    {
        Text.Color = c;
        return Text;
    }

    public void Dispose()
    {
        Stroke.Dispose(); Fill.Dispose(); Text.Dispose();
        Font.Dispose(); FontSmall.Dispose();
        _dash.Dispose(); _dot.Dispose(); _glow.Dispose();
    }
}

public enum LineStyle { Solid, Dashed, Dotted }

internal static class SkExtensions
{
    public static SKColor Fade(this SKColor c, double alpha) => c.WithAlpha((byte)Math.Clamp(alpha * c.Alpha, 0, 255));
}
