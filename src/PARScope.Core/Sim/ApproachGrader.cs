using PARScope.Core.Geo;
using PARScope.Core.Par;

namespace PARScope.Core.Sim;

/// <summary>
/// Grades one controller-directed talk-down when it ends, out of 100, on things the orders require:
/// <list type="bullet">
/// <item>Precision (40): share of the final spent on glidepath and on course (the scope's deviation bands).</item>
/// <item>Transmissions (30): the longest silence on final against the five-second rule behind the lost
/// communications instruction (7110.65 §5-10-4), and range information at least once each mile the aircraft
/// reached (§5-12-7). A transmission the recognizer could not make out still counts as the controller talking.</item>
/// <item>Procedures (20): missed approach and lost communications instructions before descent (§5-10-2,
/// §5-10-4), the wheels check (§2-1-25) unless the pilot reported the gear down, and tower's clearance relayed
/// by 2 miles (MCAS Cherry Point P3722.1S §6-316b).</item>
/// <item>Outcome (10): where it landed, or why it went around (weather is not held against the controller).</item>
/// </list>
/// </summary>
public sealed class ApproachGrader
{
    private static readonly DeviationBands Bands = new();
    private const double Nm = Units.FeetPerNm;

    private double? _descentStart, _startRangeNm;
    private double _minRangeNm = double.PositiveInfinity;
    private int _unclear;
    private double _lastTx = double.NaN, _longestGap, _lastSample = double.NegativeInfinity;
    private int _samples;
    private double _gpScore, _courseScore;
    private bool _missedInstr, _missedInstrLate, _lostComms, _lostCommsLate, _wheels;
    private double? _clearanceAtNm;
    private readonly HashSet<int> _rangeCallMiles = [];
    private bool _graded;

    /// <summary>The aircraft reached final and started down: there is something to grade.</summary>
    public bool OnFinal => _descentStart != null;

    /// <summary>A controller transmission to this aircraft.</summary>
    /// <param name="startedAt">When the transmission began (push-to-talk pressed); the time spent talking is not
    /// silence. Null = it took no time (a key).</param>
    public void Heard(Transmission t, double now, double rangeFt, double? startedAt = null)
    {
        CloseGap(startedAt ?? now);
        _lastTx = now;
        double nm = rangeFt / Nm;
        if (t.MissedApproachInstructions) { if (OnFinal) _missedInstrLate = true; else _missedInstr = true; }
        if (t.LostCommsInstructions) { if (OnFinal) _lostCommsLate = true; else _lostComms = true; }
        if (t.WheelsDown) _wheels = true;
        if (t.Clearance != null) _clearanceAtNm ??= nm;
        if (t.RangeInfo) _rangeCallMiles.Add((int)Math.Round(nm));
    }

    /// <summary>A transmission to this aircraft that was not understood: the pilot heard the controller, so it
    /// breaks a silence, but it earns nothing else.</summary>
    public void Keyed(double now, double? startedAt = null)
    {
        CloseGap(startedAt ?? now);
        _lastTx = now;
        if (OnFinal) _unclear++;
    }

    /// <summary>A transmission began at <paramref name="start"/>: the silence before it on final is over.</summary>
    private void CloseGap(double start)
    {
        // Only the part of the silence on final, between the start of descent and decision height.
        if (OnFinal && !double.IsNaN(_lastTx))
            _longestGap = Math.Max(_longestGap, Math.Min(start, _sampledUntil) - Math.Max(_lastTx, _descentStart!.Value));
        _openGap = 0;
    }

    /// <summary>Silence since the last transmission that nothing has ended yet (counts if it is never broken).</summary>
    private double _openGap, _sampledUntil = double.PositiveInfinity;

    /// <summary>Once a second on final, from the start of descent to decision height.</summary>
    public void Sample(double now, ParGeometry geo, double rangeFt, double crossFt, double heightFt)
    {
        if (_descentStart == null)
        {
            _descentStart = now;
            _startRangeNm = rangeFt / Nm;
            if (double.IsNaN(_lastTx)) _lastTx = now;
        }
        // Provisional until the next transmission says when it began.
        _openGap = now - Math.Max(_lastTx, _descentStart.Value);
        _sampledUntil = now;
        _minRangeNm = Math.Min(_minRangeNm, rangeFt / Nm);
        if (now - _lastSample < 1) return;
        _lastSample = now;
        _samples++;
        double gp = Math.Abs(heightFt - geo.GlidepathHeightFt(Math.Max(rangeFt, 0)));
        _gpScore += gp <= Bands.OnGlidepathFt ? 1 : gp <= Bands.SlightlyFt ? 0.75 : gp <= Bands.WellFt ? 0.4 : 0;
        double courseDeg = Math.Abs(Units.RadToDeg(Math.Atan2(crossFt, Math.Max(rangeFt, 1))));
        _courseScore += Math.Abs(crossFt) <= Bands.OnCourseFt ? 1 : courseDeg <= Bands.SlightlyCourseDeg ? 0.75 : courseDeg <= Bands.WellCourseDeg ? 0.4 : 0;
    }

    /// <summary>
    /// The approach is over: the grade line, or null if it never reached final or was already graded.
    /// <paramref name="outcomePoints"/> is 0–10; <paramref name="fail"/> makes it an F whatever the score.
    /// </summary>
    public string? Finish(int outcomePoints, string outcome, bool gearReportedDown, bool fail = false)
    {
        if (_graded || !OnFinal) return null;
        _graded = true;
        var notes = new List<string>();

        double gpShare = _samples == 0 ? 1 : _gpScore / _samples, crsShare = _samples == 0 ? 1 : _courseScore / _samples;
        int precision = (int)Math.Round(20 * gpShare + 20 * crsShare);

        _longestGap = Math.Max(_longestGap, _openGap); // a silence nothing broke
        int silence = (int)Math.Clamp(15 - 3 * Math.Max(0, Math.Ceiling(_longestGap) - 5), 0, 15);
        if (_longestGap > 5.5) notes.Add($"longest silence on final {_longestGap:0} s (5 s or less)");
        // Each whole mile from where descent began down to the closest the aircraft got (a go-around at 1.5 NM
        // owes no 1-mile call).
        var miles = Enumerable.Range(1, Math.Max(0, (int)Math.Floor(_startRangeNm ?? 0))).Where(m => _minRangeNm <= m - 0.1).ToList();
        int milesNeeded = miles.Count;
        int milesCalled = miles.Count(_rangeCallMiles.Contains);
        int range = milesNeeded == 0 ? 15 : (int)Math.Round(15.0 * milesCalled / milesNeeded);
        if (milesCalled < milesNeeded) notes.Add($"range given at {milesCalled} of {milesNeeded} miles");

        int procedures = 0;
        procedures += _missedInstr ? 5 : _missedInstrLate ? 2 : 0;
        if (!_missedInstr) notes.Add(_missedInstrLate ? "missed approach instructions after descent began" : "no missed approach instructions");
        procedures += _lostComms ? 5 : _lostCommsLate ? 2 : 0;
        if (!_lostComms) notes.Add(_lostCommsLate ? "lost communications instructions after descent began" : "no lost communications instructions");
        if (_wheels || gearReportedDown) procedures += 5; else notes.Add("no wheels check");
        procedures += _clearanceAtNm is double c ? (c >= 1.9 ? 5 : 2) : 0;
        if (_clearanceAtNm is not double c2) notes.Add("no landing clearance relayed");
        else if (c2 < 1.9) notes.Add($"clearance relayed at {c2:0.0} NM (by 2 NM)");

        if (_unclear > 0) notes.Add($"{_unclear} transmission{(_unclear == 1 ? "" : "s")} on final not understood (counted as talking, nothing else)");
        int score = precision + silence + range + procedures + Math.Clamp(outcomePoints, 0, 10);
        string letter = fail ? "F" : score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "F";
        string detail = $"precision {precision}/40 (glidepath {gpShare:P0}, course {crsShare:P0}), transmissions {silence + range}/30, "
                        + $"procedures {procedures}/20, outcome {outcomePoints}/10 ({outcome})";
        return $"GRADE {letter} ({score}/100): {detail}" + (notes.Count > 0 ? ". " + string.Join("; ", notes) : "");
    }
}
