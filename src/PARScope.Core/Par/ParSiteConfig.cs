namespace PARScope.Core.Par;

/// <summary>
/// Per-runway PAR installation parameters. Defaults follow ICAO Annex 10 Vol I §3.2 siting guidance
/// (antenna about 3,000 ft past touchdown, 400 ft to the side, scanning ±10°).
/// </summary>
public sealed record ParSiteConfig
{
    public double GlidepathDeg { get; init; } = 3.0;
    public double TchFt { get; init; } = 50.0;
    /// <summary>Decision height above touchdown (HAT), feet.</summary>
    public double DecisionHeightFt { get; init; } = 200.0;

    /// <summary>Antenna distance beyond the glidepath intercept point, along the runway, feet.</summary>
    public double AntennaSetbackFt { get; init; } = 3000.0;
    /// <summary>Antenna offset from the runway centreline, feet; positive = right side as seen by a landing pilot.</summary>
    public double AntennaOffsetFt { get; init; } = 400.0;
    public double AntennaHeightFt { get; init; } = 10.0;
}

public enum DeviationStandard
{
    /// <summary>NAVEDTRA 14342A Table 10-1 / NAVAIR 00-80T-114 ¶7.2.6.7.4: read from quarters of the target.</summary>
    UsNavyTargetQuarters,
    /// <summary>UK MAA RA 3291: fixed feet and the 2°/5° azimuth lines.</summary>
    UkRa3291,
}

/// <summary>Bands used to translate deviations into 7110.65 phraseology.</summary>
public sealed record DeviationBands
{
    public DeviationStandard Standard { get; init; } = DeviationStandard.UsNavyTargetQuarters;

    /// <summary>"On glidepath" half-width, feet. RA 3291: touching the GP cursor.</summary>
    public double OnGlidepathFt { get; init; } = 15;
    /// <summary>"Slightly" above/below limit, feet (RA 3291: within 60 ft).</summary>
    public double SlightlyFt { get; init; } = 60;
    /// <summary>Beyond this is "well" above/below (RA 3291: more than 100 ft).</summary>
    public double WellFt { get; init; } = 100;
    /// <summary>"On course" half-width, feet (RA 3291 draws the centreline 50 ft wide).</summary>
    public double OnCourseFt { get; init; } = 25;
    /// <summary>Azimuth line inside which deviations are "slightly" (RA 3291 blue 2° line from touchdown).</summary>
    public double SlightlyCourseDeg { get; init; } = 2.0;
    /// <summary>Azimuth line beyond which deviations are "well" (RA 3291 blue 5° line).</summary>
    public double WellCourseDeg { get; init; } = 5.0;
}
