namespace PARScope.Core.Radar;

public enum DisplayStyle
{
    /// <summary>Monochrome P7-style phosphor with afterglow (FPN-63 / MPN-14 era).</summary>
    Phosphor,
    /// <summary>Raster colour display with tracks and data blocks (FPN-68 / GCA-2020 era).</summary>
    Digital,
}

public enum ScanPattern
{
    /// <summary>Mechanical antennas sweeping back and forth (azimuth sweep, elevation "nodding").</summary>
    Mechanical,
    /// <summary>Electronically steered beams: the sector is revisited in one direction each frame.</summary>
    Electronic,
}

/// <summary>
/// Performance and presentation of a PAR model. Values come from the research brief in docs/PAR-RESEARCH.md;
/// where a public figure was not available the ICAO Annex 10 minimum is used.
/// </summary>
public sealed record RadarPreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    public double AzLeftDeg { get; init; } = 10;
    public double AzRightDeg { get; init; } = 10;
    public double ElMinDeg { get; init; } = -1;
    public double ElMaxDeg { get; init; } = 7;
    /// <summary>Upper elevation limit seen by the azimuth beam (it is a vertical fan, so it sees high traffic).</summary>
    public double AzBeamElMaxDeg { get; init; } = 20;
    public double MaxRangeNm { get; init; } = 20;
    /// <summary>Range limit with MTI selected, where the system has one (FPN-63: "When MTI is used, the RANGE
    /// SELECT switch will choose either 10 or 15 miles", MCAS Cherry Point ATCFacO P3722.1S §7-205).</summary>
    public double? MtiMaxRangeNm { get; init; }

    public double EffectiveMaxRangeNm(bool mti) => mti && MtiMaxRangeNm is double m ? m : MaxRangeNm;

    /// <summary>Selectable range scales, capped by the MTI range limit.</summary>
    public double[] ScalesFor(bool mti)
    {
        if (!mti || MtiMaxRangeNm is not double m) return RangeScalesNm;
        return RangeScalesNm.Where(s => s < m).Append(m).Distinct().ToArray();
    }

    /// <summary>Time for each antenna to cover its sector once.</summary>
    public double AzScanSec { get; init; } = 1.0;
    public double ElScanSec { get; init; } = 1.0;
    public ScanPattern Pattern { get; init; } = ScanPattern.Mechanical;

    public double AzBeamwidthDeg { get; init; } = 0.85;
    public double ElBeamwidthDeg { get; init; } = 0.55;

    /// <summary>Scales the ICAO-derived noise model (1 = one third of the ICAO error budget).</summary>
    public double NoiseFactor { get; init; } = 1.0;
    public double DetectionProbability { get; init; } = 0.98;

    public DisplayStyle Style { get; init; } = DisplayStyle.Digital;
    /// <summary>Whether the indicator shows the antenna sweep trace (analog B-scope). Default for the Scan toggle.</summary>
    public bool SweepTrace { get; init; }
    /// <summary>Whether the indicator's range axis is compressed (logarithmic). Default for the Log toggle.</summary>
    public bool LogRangeDefault { get; init; }
    /// <summary>
    /// In log mode, also scale heights and offsets with the range compression so constant-angle lines stay
    /// straight (ATNAVICS footage). Otherwise height stays linear in feet and those lines curve.
    /// </summary>
    public bool LogStraightLines { get; init; }
    /// <summary>Whether the system runs a track-while-scan tracker (track symbols, data blocks).</summary>
    public bool Tracking { get; init; } = true;
    public int MaxTracks { get; init; } = 6;
    public double[] RangeScalesNm { get; init; } = [5, 10, 20];

    /// <summary>
    /// Half the vertical extent of an elevation return at a given antenna range, at minimum usable gain.
    /// A return is roughly as tall as the beam is wide there; NAVEDTRA 14342A §10 has the controller set
    /// "the smallest possible usable target" and read deviations from its quarters.
    /// </summary>
    public double ElevationTargetHalfFt(double antennaRangeFt) =>
        Math.Max(8, 0.4 * antennaRangeFt * Math.Tan(ElBeamwidthDeg * Math.PI / 180));

    /// <summary>Half the lateral extent of an azimuth return at a given antenna range.</summary>
    public double AzimuthTargetHalfFt(double antennaRangeFt) =>
        Math.Max(10, 0.4 * antennaRangeFt * Math.Tan(AzBeamwidthDeg * Math.PI / 180));

    public static readonly RadarPreset Fpn63 = new()
    {
        Id = "fpn63",
        Name = "AN/FPN-63",
        Description = "USN/USMC shore PAR (1978–). Two mechanically scanned X-band antennas, 20° × 8° sector, "
                      + "10/20 NM, analog AZ-EL indicator with bright 5-mile marks and a decision-height marker. No tracker.",
        AzLeftDeg = 10, AzRightDeg = 10, ElMinDeg = -1, ElMaxDeg = 7, MaxRangeNm = 20, MtiMaxRangeNm = 15,
        AzScanSec = 1.0, ElScanSec = 1.0, Pattern = ScanPattern.Mechanical,
        AzBeamwidthDeg = 0.85, ElBeamwidthDeg = 0.55,
        NoiseFactor = 1.3, Style = DisplayStyle.Phosphor, SweepTrace = true, Tracking = false, RangeScalesNm = [10, 20],
    };

    public static readonly RadarPreset Gpn22 = new()
    {
        Id = "gpn22",
        Name = "AN/GPN-22 Hi-PAR",
        Description = "USAF fixed-base PAR. Phased-array feed, electronically scanned in both axes, 20° × 8°, "
                      + "tracks six aircraft to 15 NM.",
        AzLeftDeg = 10, AzRightDeg = 10, ElMinDeg = -1, ElMaxDeg = 7, MaxRangeNm = 15,
        AzScanSec = 1.0, ElScanSec = 1.0, Pattern = ScanPattern.Electronic,
        AzBeamwidthDeg = 0.75, ElBeamwidthDeg = 0.75,
        NoiseFactor = 1.0, Style = DisplayStyle.Phosphor, SweepTrace = true, Tracking = true, MaxTracks = 6, RangeScalesNm = [5, 10, 15],
    };

    public static readonly RadarPreset Fpn67 = new()
    {
        Id = "fpn67",
        Name = "AN/FPN-67 FBPAR",
        Description = "US Army fixed-base PAR (Raytheon). ±10° az, −1° to +6° el, 15 NM, track-while-scan with sub-second update.",
        AzLeftDeg = 10, AzRightDeg = 10, ElMinDeg = -1, ElMaxDeg = 6, MaxRangeNm = 15,
        AzScanSec = 0.8, ElScanSec = 0.8, Pattern = ScanPattern.Electronic,
        AzBeamwidthDeg = 0.8, ElBeamwidthDeg = 0.6,
        NoiseFactor = 0.8, Style = DisplayStyle.Digital, Tracking = true, MaxTracks = 6, RangeScalesNm = [5, 10, 15],
    };

    public static readonly RadarPreset Fpn68 = new()
    {
        Id = "fpn68",
        Name = "AN/FPN-68 (PAR-2020)",
        Description = "USN/USMC FPN-63 replacement (L3Harris). AESA, 30° az, −1° to +7° el, 20 NM (15 NM rain mode), "
                      + "update under 1 s, colour display with tracks and data blocks.",
        AzLeftDeg = 15, AzRightDeg = 15, ElMinDeg = -1, ElMaxDeg = 7, MaxRangeNm = 20,
        AzScanSec = 0.5, ElScanSec = 0.5, Pattern = ScanPattern.Electronic,
        AzBeamwidthDeg = 0.75, ElBeamwidthDeg = 0.6,
        NoiseFactor = 0.6, Style = DisplayStyle.Digital, Tracking = true, MaxTracks = 12, RangeScalesNm = [5, 10, 20],
    };

    public static readonly RadarPreset Mpn25 = new()
    {
        Id = "mpn25",
        Name = "AN/MPN-25 (GCA-2000)",
        Description = "USMC/Army transportable GCA (ITT/Exelis). Phased-array PAR to 20 NM, 1 s ICAO update, "
                      + "history dots and data blocks.",
        AzLeftDeg = 10, AzRightDeg = 10, ElMinDeg = -1, ElMaxDeg = 7, MaxRangeNm = 20,
        AzScanSec = 1.0, ElScanSec = 1.0, Pattern = ScanPattern.Electronic,
        AzBeamwidthDeg = 1.05, ElBeamwidthDeg = 0.6,
        NoiseFactor = 0.9, Style = DisplayStyle.Digital, Tracking = true, MaxTracks = 6, RangeScalesNm = [5, 10, 20],
    };

    public static readonly RadarPreset Icao = new()
    {
        Id = "icao",
        Name = "ICAO Annex 10 minimum",
        Description = "Generic PAR meeting ICAO Annex 10 Vol I §3.2: 20° × 7° sector, 9 NM, renewed once per second.",
        AzLeftDeg = 10, AzRightDeg = 10, ElMinDeg = -1, ElMaxDeg = 6, MaxRangeNm = 10,
        AzScanSec = 1.0, ElScanSec = 1.0, Pattern = ScanPattern.Mechanical,
        NoiseFactor = 1.5, Style = DisplayStyle.Digital, Tracking = true, RangeScalesNm = [5, 10],
    };

    public static readonly RadarPreset Tpn31 = new()
    {
        Id = "tpn31",
        Name = "AN/TPN-31 ATNAVICS",
        Description = "US Army/USMC tactical ATC system. Display as on the Army radar training simulator and USMC "
                      + "expeditionary screens: compressed range axis, green cursors, cream limit lines, DH line, "
                      + "white tracks with leader lines. Coverage and range are assumed (public figures not found).",
        AzLeftDeg = 10, AzRightDeg = 10, ElMinDeg = -1, ElMaxDeg = 7, MaxRangeNm = 10,
        AzScanSec = 1.0, ElScanSec = 1.0, Pattern = ScanPattern.Electronic,
        AzBeamwidthDeg = 1.0, ElBeamwidthDeg = 0.7,
        NoiseFactor = 1.0, Style = DisplayStyle.Digital, Tracking = true, MaxTracks = 6, RangeScalesNm = [5, 10],
        LogRangeDefault = true,
        LogStraightLines = true,
    };

    public static IReadOnlyList<RadarPreset> All { get; } = [Fpn68, Tpn31, Fpn63, Gpn22, Fpn67, Mpn25, Icao];

    public static RadarPreset ById(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Fpn68;
}
