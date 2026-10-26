using PARScope.Core.Geo;
using PARScope.Core.Radar;

namespace PARScope.Core.Par;

public enum AdvisoryLevel { Normal, Caution, Warning }

/// <summary>A suggested final-controller transmission for one target.</summary>
public sealed record Advisory(
    string Transmission,
    AdvisoryLevel Level,
    Deviation Deviation,
    double? SuggestedHeadingDeg,
    double RecommendedDescentFpm,
    string GlidepathPhrase,
    string CoursePhrase)
{
    public bool BelowDecisionHeight { get; init; }
    /// <summary>Level, below the glidepath, not yet told to begin descent.</summary>
    public bool PreDescent { get; init; }
}

/// <summary>
/// Builds precision-final phraseology from a radar track.
///
/// Phraseology: FAA JO 7110.65BB §5-12 and §5-10-14, plus the USN/USA wheels-down check (§2-1-25).
/// Deviation terms, US Navy standard (NAVEDTRA 14342A Table 10-1, NAVAIR 00-80T-114 ¶7.2.6.7.4): the
/// controller reads which quarter of the target the cursor cuts, so the bands grow with range as the
/// return grows with the beam. Safety limits (¶7.2.6.7.5): "too low" when well below and touching the
/// lower safe limit cursor; at one mile, any target not touching the cursor; lateral limits per
/// AFMAN 13-204v3 Table 7.1. The UK RA 3291 fixed-feet bands remain selectable.
/// </summary>
public static class AdvisoryEngine
{
    /// <summary>Feet per second of deviation change below which a deviation is "holding".</summary>
    private const double HoldingFps = 2.0;
    private const double RapidlyFps = 12.0;
    /// <summary>Descent this much faster than the glidepath is a "radical descent deviation".</summary>
    private const double RadicalDescentFps = 15.0;

    /// <param name="headingMagDeg">The aircraft's heading if the controller knows it (the last assigned heading, or
    /// the reported one), so the suggested heading keeps the wind correction; null = estimate from the track.</param>
    /// <param name="wheelsReportedDown">The pilot has reported the gear down on this approach, so the wheels check is not repeated (§2-1-25).</param>
    public static Advisory Evaluate(RadarTrack track, ParGeometry geo, RadarPreset preset, DeviationBands bands, double magneticCourseDeg,
        double? headingMagDeg = null, bool wheelsReportedDown = false)
    {
        var dev = geo.DeviationOf(track.Point);
        double rangeFt = Math.Max(dev.RangeFt, 1);
        bool belowDh = rangeFt <= geo.DecisionHeightRangeFt;

        double gpSlope = Math.Tan(Units.DegToRad(geo.Site.GlidepathDeg));
        // Rate of change of the glidepath deviation: height rate minus the glidepath's own descent rate.
        double devRateGp = track.HeightRateFps - track.RangeRateFps * gpSlope;
        double devRateCourse = track.CrossRateFps;
        double holding = Math.Max(HoldingFps, rangeFt * 0.00008);

        // Apparent target size at this antenna range (US Navy quarter method).
        double antennaRange = geo.ToPolar(track.Point).RangeFt;
        double elHalf = preset.ElevationTargetHalfFt(antennaRange);
        double azHalf = preset.AzimuthTargetHalfFt(antennaRange);

        string gp = GlidepathPhrase(dev.AboveGlidepathFt, devRateGp, bands, holding, elHalf);
        string course = CoursePhrase(dev.RightOfCourseFt, devRateCourse, rangeFt, bands, holding, azHalf);

        bool descending = track.HeightRateFps < -3;
        bool preDescent = !descending && dev.AboveGlidepathFt < -elHalf * 0.5 && !belowDh;

        var level = AdvisoryLevel.Normal;
        string? safety = null;
        if (!belowDh)
        {
            bool notTouchingGp = Math.Abs(dev.AboveGlidepathFt) > elHalf;
            bool notTouchingCl = Math.Abs(dev.RightOfCourseFt) > azHalf;
            bool touchingLsl = dev.AboveSafeLimitFt <= elHalf;

            if (descending && dev.AboveGlidepathFt < 0 && devRateGp < -RadicalDescentFps)
                (safety, level) = ("LOW ALTITUDE ALERT, CHECK YOUR ALTITUDE IMMEDIATELY", AdvisoryLevel.Warning);
            else if (descending && dev.AboveGlidepathFt < -elHalf && touchingLsl)
                (safety, level) = ("TOO LOW FOR SAFE APPROACH", AdvisoryLevel.Warning);
            else if (rangeFt <= 1.0 * Units.FeetPerNm && notTouchingGp)
                (safety, level) = (dev.AboveGlidepathFt > 0 ? "TOO HIGH FOR SAFE APPROACH" : "TOO LOW FOR SAFE APPROACH", AdvisoryLevel.Warning);
            else if (rangeFt <= 1.0 * Units.FeetPerNm && notTouchingCl
                     || rangeFt <= 6 * Units.FeetPerNm && Math.Abs(dev.RightOfCourseFt) > ParGeometry.LateralSafeLimitFt(rangeFt))
                (safety, level) = (dev.RightOfCourseFt > 0 ? "TOO FAR RIGHT FOR SAFE APPROACH" : "TOO FAR LEFT FOR SAFE APPROACH", AdvisoryLevel.Warning);
            else if (descending && notTouchingGp && dev.AboveGlidepathFt < 0)
                level = AdvisoryLevel.Caution;
        }

        double closureFps = Math.Max(-track.RangeRateFps, 1);
        double currentTrack = magneticCourseDeg + Units.RadToDeg(Math.Atan2(track.CrossRateFps, closureFps));
        double currentHeading = headingMagDeg ?? currentTrack;
        double? heading = SuggestHeading(track, dev, magneticCourseDeg, rangeFt, currentTrack, currentHeading);
        double gsKt = Math.Max(track.GroundSpeedKt, 60);
        double rod = gsKt * Units.FeetPerNm / 60.0 * gpSlope;

        string text;
        if (dev.RangeFt < geo.ThresholdRangeFt - 100)
        {
            // Guidance is discontinued once over the landing threshold (§5-12-8a3).
            text = "";
        }
        else if (safety != null)
        {
            text = safety.StartsWith("LOW ALTITUDE")
                ? safety + "."
                : $"{safety}. IF RUNWAY OR APPROACH LIGHTS NOT IN SIGHT, EXECUTE MISSED APPROACH.";
        }
        else
        {
            var parts = new List<string>();
            if (heading.HasValue) parts.Add($"TURN {(Units.Norm180(heading.Value - currentHeading) >= 0 ? "RIGHT" : "LEFT")} HEADING {heading.Value:000}");
            // Before glidepath intercept only course information is given; elevation starts with
            // "approaching glidepath" (§5-12-1, §5-12-4).
            if (!preDescent) parts.Add(gp);
            else if (PreDescentCall(dev, devRateGp, elHalf, wheelsReportedDown) is { } pre) parts.Add(pre);
            parts.Add(course);
            text = string.Join(", ", parts) + ".";
            if (belowDh) text = "(ADVISORY) " + text;
        }

        return new Advisory(text, level, dev, heading, rod, gp, course) { BelowDecisionHeight = belowDh, PreDescent = preDescent };
    }

    /// <summary>
    /// Before final descent: "APPROACHING GLIDEPATH" 10–30 s before intercept (§5-12-1), with the USN/USA
    /// wheels-down check (§2-1-25), then "BEGIN DESCENT" (§5-12-3).
    /// </summary>
    private static string? PreDescentCall(Deviation dev, double devRateGp, double elHalf, bool wheelsReportedDown)
    {
        double toIntercept = devRateGp > 0.5 ? -dev.AboveGlidepathFt / devRateGp : double.PositiveInfinity;
        if (toIntercept <= 10 || dev.AboveGlidepathFt > -elHalf) return "BEGIN DESCENT";
        if (toIntercept <= 30) return wheelsReportedDown ? "APPROACHING GLIDEPATH" : "APPROACHING GLIDEPATH. WHEELS SHOULD BE DOWN";
        return null;
    }

    public static string GlidepathPhrase(double aboveFt, double rateFps, DeviationBands b, double holdingFps = HoldingFps, double targetHalfFt = 60)
    {
        var mag = Magnitude(aboveFt, b, b.Standard == DeviationStandard.UkRa3291
            ? (b.OnGlidepathFt, b.SlightlyFt, b.WellFt)
            : QuarterBands(targetHalfFt));
        if (mag == null) return "ON GLIDEPATH";

        string side = aboveFt > 0 ? "ABOVE" : "BELOW";
        // 7110.65 §5-12-4 trend forms: "going above", "above and coming down", "below and holding".
        return Classify(aboveFt, rateFps, holdingFps) switch
        {
            Trend.Worsening when mag == "SLIGHTLY " => $"GOING {side} GLIDEPATH",
            Trend.Worsening when Math.Abs(rateFps) > RapidlyFps => $"{mag}{side} GLIDEPATH AND GOING {side} RAPIDLY",
            Trend.Worsening => $"{mag}{side} GLIDEPATH",
            Trend.Correcting => $"{mag}{side} GLIDEPATH AND COMING {(aboveFt > 0 ? "DOWN" : "UP")}",
            _ => $"{mag}{side} GLIDEPATH AND HOLDING",
        };
    }

    public static string CoursePhrase(double rightFt, double rateFps, double rangeFt, DeviationBands b, double holdingFps = HoldingFps, double targetHalfFt = 80)
    {
        var bands = b.Standard == DeviationStandard.UkRa3291
            ? (b.OnCourseFt,
               Math.Max(b.OnCourseFt * 2, ParGeometry.CourseLineFt(rangeFt, b.SlightlyCourseDeg)),
               ParGeometry.CourseLineFt(rangeFt, b.WellCourseDeg))
            : QuarterBands(targetHalfFt);
        var mag = Magnitude(rightFt, b, bands);
        if (mag == null) return "ON COURSE";

        string side = rightFt > 0 ? "RIGHT" : "LEFT";
        return Classify(rightFt, rateFps, holdingFps) switch
        {
            Trend.Worsening when mag == "SLIGHTLY " => $"GOING {side} OF COURSE",
            Trend.Worsening => $"{mag}{side} OF COURSE",
            Trend.Correcting => $"{mag}{side} OF COURSE AND CORRECTING",
            _ => $"{mag}{side} OF COURSE AND HOLDING",
        };
    }

    /// <summary>
    /// Target-quarter bands for a return of half-size h (NAVEDTRA 14342A Table 10-1): bisected = on;
    /// cursor through a middle quarter = slightly (|d| ≤ h/2); through an outer quarter = plain (≤ h);
    /// not touching = well.
    /// </summary>
    private static (double On, double Slightly, double Well) QuarterBands(double h) => (h / 8, h / 2, h);

    /// <summary>null = on; otherwise "SLIGHTLY ", "" or "WELL ".</summary>
    private static string? Magnitude(double deviation, DeviationBands _, (double On, double Slightly, double Well) bands)
    {
        double a = Math.Abs(deviation);
        if (a <= bands.On) return null;
        return a <= bands.Slightly ? "SLIGHTLY " : a > bands.Well ? "WELL " : "";
    }

    private enum Trend { Holding, Correcting, Worsening }

    /// <summary>Whether a signed deviation is shrinking or growing at <paramref name="rate"/> per second.</summary>
    private static Trend Classify(double deviation, double rate, double holdingFps)
    {
        if (Math.Abs(rate) < holdingFps) return Trend.Holding;
        return Math.Sign(rate) == Math.Sign(deviation) ? Trend.Worsening : Trend.Correcting;
    }

    /// <summary>
    /// Heading that returns the aircraft to the centerline within about 30 s, keeping the wind correction
    /// (heading − track): the controller works out the drift from the heading assigned against the track
    /// seen. Corrections are limited about the drift-corrected course, smaller close in.
    /// </summary>
    private static double? SuggestHeading(RadarTrack track, Deviation dev, double courseMag, double rangeFt,
        double currentTrack, double currentHeading)
    {
        double closure = -track.RangeRateFps;
        if (closure < 50) return null;

        double desiredCrossRate = -dev.RightOfCourseFt / 30.0;
        double crossRateError = desiredCrossRate - track.CrossRateFps;
        double correctionDeg = Units.RadToDeg(Math.Atan2(crossRateError, closure));

        double limit = rangeFt < 2 * Units.FeetPerNm ? 5 : rangeFt < 5 * Units.FeetPerNm ? 10 : 15;
        // Only advise a turn when the needed change is worth saying.
        if (Math.Abs(correctionDeg) < 1.5) return null;
        double windCorrection = Units.Norm180(currentHeading - currentTrack);
        double target = currentHeading + Math.Clamp(correctionDeg, -limit, limit);
        // Never more than the limit away from the course flown with the current drift correction.
        double corrected = courseMag + windCorrection;
        double hdg = Units.Norm360(Math.Round(corrected + Math.Clamp(Units.Norm180(target - corrected), -limit, limit)));
        if (Math.Abs(Units.Norm180(hdg - currentHeading)) < 1) return null; // already flying it
        return hdg == 0 ? 360 : hdg;
    }

    /// <summary>
    /// The position call due at this range, if any: "(n) MILES FROM TOUCHDOWN" passing each mile
    /// (7110.65BB §5-12-5), "AT DECISION ALTITUDE" (§5-12-6), "OVER APPROACH LIGHTS" at half a mile and
    /// "OVER LANDING THRESHOLD" (§5-12-7; MCAS Cherry Point P3722.1S §6-323).
    /// </summary>
    public static string? PositionCall(double rangeFt, ParGeometry geo, string? coursePhrase = null)
    {
        const double window = 250;
        // §5-12-7c: "OVER LANDING THRESHOLD, (position with respect to course)".
        if (Math.Abs(rangeFt - geo.ThresholdRangeFt) < window)
            return coursePhrase is { Length: > 0 } ? $"OVER LANDING THRESHOLD, {coursePhrase}" : "OVER LANDING THRESHOLD";
        if (Math.Abs(rangeFt - 0.5 * Units.FeetPerNm) < window) return "OVER APPROACH LIGHTS";
        if (Math.Abs(rangeFt - geo.DecisionHeightRangeFt) < window) return "AT DECISION ALTITUDE";
        double nm = rangeFt / Units.FeetPerNm, mile = Math.Round(nm);
        return mile >= 1 && Math.Abs(nm - mile) < 0.2 ? RangePhrase(mile) : null;
    }

    /// <summary>"(n) MILES FROM TOUCHDOWN" phrase, at each whole mile.</summary>
    public static string RangePhrase(double rangeNm)
    {
        int n = (int)Math.Round(rangeNm);
        return n <= 1 ? "ONE MILE FROM TOUCHDOWN" : $"{n} MILES FROM TOUCHDOWN";
    }
}
