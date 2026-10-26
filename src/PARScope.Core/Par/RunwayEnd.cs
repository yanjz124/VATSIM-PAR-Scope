namespace PARScope.Core.Par;

/// <summary>A landing runway end as published in airport data.</summary>
/// <param name="AirportId">Display identifier (FAA LID for US airports, otherwise ICAO).</param>
/// <param name="Icao">ICAO code used for METAR lookups, if known.</param>
/// <param name="ThresholdLat">Landing threshold (displaced threshold if any).</param>
/// <param name="ThresholdElevFt">Threshold elevation, feet MSL.</param>
/// <param name="TrueHeadingDeg">Runway true heading (direction of landing).</param>
/// <param name="VgsiTchFt">TCH of the visual glide slope indicator (PAPI/VASI) from NASR, not the PAR/ILS TCH.</param>
/// <param name="VgsiAngleDeg">PAPI/VASI visual glide path angle from NASR; not the PAR glidepath, which comes from the approach plate.</param>
/// <param name="MagVarEastDeg">Magnetic variation, east positive (magnetic = true − variation).</param>
public sealed record RunwayEnd(
    string AirportId,
    string? Icao,
    string RunwayId,
    double ThresholdLat,
    double ThresholdLon,
    double ThresholdElevFt,
    double TrueHeadingDeg,
    double? VgsiTchFt = null,
    double? VgsiAngleDeg = null,
    double? MagVarEastDeg = null,
    string? ApproachLights = null,
    double? LengthFt = null,
    double? WidthFt = null,
    string Source = "")
{
    public string DisplayName => $"{AirportId} RWY {RunwayId}";

    /// <summary>Magnetic final approach course, rounded the way it is spoken.</summary>
    public double MagneticCourseDeg => Geo.Units.Norm360(TrueHeadingDeg - (MagVarEastDeg ?? 0));
}
