using System.Text.RegularExpressions;
using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;

namespace PARScope.Core.Sim;

/// <summary>What the AI final controller knows about one training arrival when it picks its next call.</summary>
/// <param name="Advisory">The call the scope would make from the PAR track; null when the PAR has no
/// correlated, painted track (the controller says nothing it cannot see).</param>
/// <param name="Track">The PAR track the advisory came from, for the closure and drift across the course.</param>
public sealed record AiTraffic(SimAircraft Aircraft, Advisory? Advisory, bool HandoffPending, TowerAnswer Tower,
    LandingIntent TowerIntent, bool MissedCoordinated, RadarTrack? Track = null);

/// <summary>A transmission by the AI final controller: on the final frequency, or on the landline to tower or approach.</summary>
public sealed record AiCall(string Callsign, string Text, bool Landline);

/// <summary>
/// An AI final controller that works the position the way the keys lay it out, so a student can watch a whole
/// approach: accepts approach's handoff ("radar contact", 7110.65 §5-4-5), gives the approach brief after the
/// pilot checks in (§5-10-2, §5-10-4), the course and glidepath calls from the scope at least every five seconds
/// on final (§5-12-4), the wheels check (§2-1-25), "do not acknowledge further transmissions" (§5-10-10), a range
/// call each mile and the decision altitude, approach lights and threshold calls (§5-12-5 to §5-12-7), requests
/// tower's clearance and relays it (§5-10-13), and after a missed approach coordinates with approach and
/// switches the pilot. Each call is the text the scope suggests, worded for speech; the caller transmits it
/// and feeds it to the pilot exactly as if the student had said it.
/// </summary>
public sealed partial class AiFinalController
{
    private sealed class Progress
    {
        public double PendingSince = double.NaN, AcceptedAt = double.NaN, TowerRequestedAt = double.NaN, MissedSeenAt = double.NaN;
        public bool Briefed, NoAckSaid, ApproachingSaid, NotReceivedSaid, ContactTowerSaid, MissedRequested, ContactApproachSaid;
        public bool DecisionSaid, LightsSaid, ThresholdSaid, WheelsSaid, SafetySaid;
        public int LastMile = int.MaxValue;
        /// <summary>Drift (track minus assigned heading), learned from the radar while the heading is steady.</summary>
        public double DriftDeg;
        public double HeadingSetAt = double.NegativeInfinity, LastHeadingRel = double.NaN, DriftAt;
        /// <summary>The heading (relative to the course) in the call being built, and the last one given and when.</summary>
        public double ProposedRel, GivenRel, GivenAt = double.NegativeInfinity;
    }

    private readonly Dictionary<string, Progress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private double _radioIdleSince = double.NaN, _landlineIdleSince = double.NaN, _lastRadioCall = double.NegativeInfinity;

    /// <summary>"Potomac" in "Potomac final".</summary>
    public string Facility { get; set; } = "Approach";
    public string RunwayId { get; set; } = "";
    public double CourseMagDeg { get; set; }
    public double ThresholdElevFt { get; set; }
    /// <summary>Missed approach altitude (MSL) given in the brief.</summary>
    public double MissedClimbMslFt { get; set; } = 2000;
    /// <summary>Surface wind for the landing clearance, spoken ("wind two four zero at eight"), or empty.</summary>
    public string WindPhrase { get; set; } = "";
    public LandingIntent Intent { get; set; } = LandingIntent.FullStop;
    public ParGeometry? Geometry { get; set; }

    /// <summary>Seconds a handoff is offered before final answers it.</summary>
    public const double AcceptDelaySec = 4;
    /// <summary>Range at which final asks tower for the clearance.</summary>
    public const double TowerRequestNm = 6;

    /// <summary>
    /// The next transmission, or null if nothing is due. Landline calls (handoffs, tower, missed approach) go
    /// first; on the frequency the closest arrival is served. <paramref name="radioBusy"/> and
    /// <paramref name="landlineBusy"/>: something is still queued or playing there, so nothing new is said over it.
    /// </summary>
    public AiCall? Next(IReadOnlyList<AiTraffic> traffic, double now, bool radioBusy, bool landlineBusy)
    {
        _radioIdleSince = radioBusy ? double.NaN : double.IsNaN(_radioIdleSince) ? now : _radioIdleSince;
        _landlineIdleSince = landlineBusy ? double.NaN : double.IsNaN(_landlineIdleSince) ? now : _landlineIdleSince;
        _landlineBusy = landlineBusy;
        foreach (var gone in _progress.Keys.Where(k => traffic.All(t => !t.Aircraft.Callsign.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            _progress.Remove(gone);

        if (!landlineBusy && now - _landlineIdleSince >= 1.5)
            foreach (var t in traffic)
                if (Landline(t, now) is { } call) return call;

        if (!radioBusy && now - _radioIdleSince >= 0.6)
            foreach (var t in traffic.OrderBy(t => t.Aircraft.RangeFt))
                if (Radio(t, now) is { } call)
                {
                    _lastRadioCall = now;
                    return call;
                }
        return null;
    }


    private bool _landlineBusy;

    private Progress For(SimAircraft ac)
    {
        if (!_progress.TryGetValue(ac.Callsign, out var p)) _progress[ac.Callsign] = p = new Progress();
        return p;
    }

    private AiCall? Landline(AiTraffic t, double now)
    {
        var ac = t.Aircraft;
        var p = For(ac);
        string cs = RadioSpeech.Callsign(ac.Callsign);
        if (t.HandoffPending)
        {
            // A new offer (or one back from approach's pattern) starts the approach over.
            if (double.IsNaN(p.PendingSince)) { _progress[ac.Callsign] = p = new Progress { PendingSince = now }; }
            if (now - p.PendingSince < AcceptDelaySec) return null;
            p.AcceptedAt = now;
            p.PendingSince = double.NaN;
            return new AiCall(ac.Callsign, $"{cs}, radar contact.", true);
        }
        if (ac.GoingAround && !ac.HandedOff && !ac.InPattern)
        {
            if (double.IsNaN(p.MissedSeenAt)) p.MissedSeenAt = now;
            if (t.MissedCoordinated || p.MissedRequested || now - p.MissedSeenAt < 3) return null;
            p.MissedRequested = true;
            return new AiCall(ac.Callsign, ApproachSim.MissedApproachRequest(ac.Callsign,
                Units.Norm360(CourseMagDeg + ac.CommandedHeadingRelDeg), ac.ClimbToFt + ThresholdElevFt), true);
        }
        if (t.Advisory is { } adv && ac.Descending && !ac.Landed && !ac.ClearedToLand && t.Tower != TowerAnswer.Cleared
            && adv.Deviation.RangeNm <= TowerRequestNm
            && (double.IsNaN(p.TowerRequestedAt) || t.Tower == TowerAnswer.Continue && now - p.TowerRequestedAt >= 15))
        {
            p.TowerRequestedAt = now;
            return new AiCall(ac.Callsign, TowerSim.RequestText(ac.Callsign, adv.Deviation.RangeNm, Intent), true);
        }
        return null;
    }

    private AiCall? Radio(AiTraffic t, double now)
    {
        var ac = t.Aircraft;
        if (t.HandoffPending || ac.InPattern || ac.Landed || ac.Finished || ac.HandedOff) return null;
        var p = For(ac);
        string cs = RadioSpeech.Callsign(ac.Callsign);

        if (ac.GoingAround)
        {
            // Once approach's "radar contact" has been heard on the landline.
            if (!t.MissedCoordinated || p.ContactApproachSaid || _landlineBusy) return null;
            p.ContactApproachSaid = true;
            return new AiCall(ac.Callsign, $"{cs}, contact approach.", false);
        }

        // Taken over part way: an arrival already accepted and talking to final.
        if (double.IsNaN(p.AcceptedAt)) p.AcceptedAt = double.NegativeInfinity;
        if (!p.Briefed)
        {
            // Only once the pilot has checked in (their call, not a timer: voices can take a moment to
            // generate), or after half a minute if the check-in never comes.
            if (ac.Descending) p.Briefed = true;
            else if (!ac.CheckedIn && now - p.AcceptedAt < 30) return null;
            else
            {
                p.Briefed = true;
                return new AiCall(ac.Callsign, Brief(cs), false);
            }
        }

        double since = now - _lastRadioCall;
        if (t.Advisory is null && !ac.Descending) return since >= 10 ? AsrVector(ac) : null;
        if (t.Advisory is not { } adv || Geometry is not { } geo) return null;
        // The scope's glidepath and course words; the heading is the controller's own (below).
        string guidance = SpacesRx().Replace(TurnRx().Replace(adv.Transmission.Replace("(ADVISORY) ", ""), ""), " ").Trim(' ', ',');
        // A "…execute missed approach if not in sight" safety call is made once: the pilot either goes or has the
        // runway, and then the talk-down carries on with plain glidepath and course calls.
        bool warning = adv.Level == AdvisoryLevel.Warning;
        bool safety = warning && guidance.Contains("NOT IN SIGHT");
        if (safety && p.SafetySaid)
        {
            guidance = $"{adv.GlidepathPhrase}, {adv.CoursePhrase}";
            warning = safety = false;
        }
        string? turn = warning || adv.Deviation.RangeFt < geo.ThresholdRangeFt ? null : Heading(ac, adv, t.Track, p, now);
        bool urgent = warning || guidance.Contains("BEGIN DESCENT") || turn != null && !ac.Descending
                      || guidance.Contains("APPROACHING GLIDEPATH") && !p.ApproachingSaid;
        // At least every five seconds on final; before the descent, course information about every ten.
        if (since < (ac.Descending ? 4 : urgent ? 3 : 10)) return null;

        // "Contact tower after landing" on its own, once the clearance is relayed (the pilot handles it apart).
        if (ac.ClearedToLand && !p.ContactTowerSaid && adv.Deviation.RangeNm < 2 && !warning)
        {
            p.ContactTowerSaid = true;
            return new AiCall(ac.Callsign, $"{cs}, contact tower after landing.", false);
        }

        // One extra item a call at most, so no transmission runs long enough to leave a gap over five seconds:
        // a safety call, the clearance or "do not acknowledge" first, then the heading and position, then range.
        string? extra = null;
        Action? commit = null;
        if (t.Tower == TowerAnswer.Cleared && !ac.ClearedToLand && !warning)
            extra = $"{(WindPhrase.Length > 0 ? WindPhrase + ", " : "")}runway {RadioSpeech.Runway(RunwayId)}, {TowerSim.ClearanceWords(t.TowerIntent)}";
        else if (t.Tower != TowerAnswer.Cleared && adv.Deviation.RangeNm < 2 && !adv.BelowDecisionHeight && !p.NotReceivedSaid && !warning)
        {
            // No clearance by 2 miles (MCAS Cherry Point P3722.1S §6-316b).
            extra = "tower clearance not received, if runway or approach lights not in sight, execute missed approach";
            commit = () => p.NotReceivedSaid = true;
        }
        else if (!p.NoAckSaid && !ac.NoAcknowledge
                 && (ac.Descending || Math.Abs(turn != null ? p.ProposedRel : ac.CommandedHeadingRelDeg) <= 15
                     && Math.Abs(adv.Deviation.RightOfCourseFt) < Units.FeetPerNm))
        {
            // Once roughly lined up on final (heading within 15° of the course, under a mile off the centerline),
            // and in any case by the descent: from here headings, the wheels check and "begin descent" are flown
            // without readbacks (§5-10-10).
            extra = "do not acknowledge further transmissions";
            commit = () => p.NoAckSaid = true;
        }
        // Already descending (the radar has not shown it yet): no second "begin descent".
        if (ac.Descending) guidance = SpacesRx().Replace(guidance.Replace("BEGIN DESCENT,", "").Replace("BEGIN DESCENT", ""), " ").Trim(' ', ',');
        bool approaching = guidance.Contains("APPROACHING GLIDEPATH");
        // Said once; until the descent starts the course call carries on alone.
        if (approaching && p.ApproachingSaid) guidance = SpacesRx().Replace(ApproachingRx().Replace(guidance, ""), " ").Trim(' ', ',', '.');
        // The USN/USA wheels check (§2-1-25) is owed even when the approach skips "approaching glidepath".
        if (!ac.GearReportedDown && !p.WheelsSaid && (ac.Descending || approaching) && !guidance.Contains("WHEELS"))
            guidance = (guidance.TrimEnd('.') + ", WHEELS SHOULD BE DOWN").TrimStart(',', ' ');
        guidance = guidance.TrimEnd('.');
        string main = turn != null ? turn + (guidance.Length > 0 ? ", " + guidance : "") : guidance;

        // The range call is short and owed once a mile: it goes in, and the course and glidepath words only if
        // there is room (with a turn or safety call they always go).
        string? position = ac.Descending ? PositionCall(adv, geo, p, commit: true) : null;
        var parts = new List<string>();
        if (extra != null) parts.Add(extra);
        int used = Words(string.Join(" ", parts)) + (position != null ? Words(position) : 0);
        // A long call loses the course trend first ("…left of course and holding" → "…left of course").
        if (used + Words(main) > 13) main = CourseTrendRx().Replace(main, "OF COURSE");
        if (main.Length > 0 && (turn != null || warning || used + Words(main) <= 16)) parts.Add(main);
        if (position != null) parts.Add(position);
        if (parts.Count == 0) return null;
        commit?.Invoke();
        if (parts.Contains(main))
        {
            if (turn != null) { p.HeadingSetAt = p.GivenAt = now; p.GivenRel = p.ProposedRel; }
            if (approaching) p.ApproachingSaid = true;
            if (main.Contains("WHEELS")) p.WheelsSaid = true;
            if (safety) p.SafetySaid = true;
        }
        return new AiCall(ac.Callsign, Speakable(string.Join(". ", parts)) + ".", false);
    }

    private static int Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// The heading to give, if one is worth giving: closes the centerline over about 40 seconds (intercepts of
    /// up to 30°, 10° inside 4 NM, 5° inside 2 NM), allowing for the drift the controller has seen between the
    /// assigned heading and the track on the radar while the heading was steady. Small changes wait until the
    /// last turn has had time to show.
    /// </summary>
    private string? Heading(SimAircraft ac, Advisory adv, RadarTrack? track, Progress p, double now)
    {
        // The heading it last gave, until the pilot has had time to take it (they react a few seconds later).
        double assigned = now - p.GivenAt < 10 ? p.GivenRel : ac.CommandedHeadingRelDeg;
        if (double.IsNaN(p.LastHeadingRel) || Math.Abs(Units.Norm180(assigned - p.LastHeadingRel)) > 0.5)
        {
            p.LastHeadingRel = assigned;
            p.HeadingSetAt = Math.Max(p.HeadingSetAt, now);
        }
        double closure = track != null ? -track.RangeRateFps : 0;
        // The drift is read once the turn has finished and the radar track has settled on the new heading.
        if (track != null && closure > 100 && now - p.HeadingSetAt > 8)
        {
            double sample = Units.Norm180(Units.RadToDeg(Math.Atan2(track.CrossRateFps, closure)) - assigned);
            p.DriftDeg = Math.Clamp(p.DriftDeg + (sample - p.DriftDeg) * Math.Min(1, (now - p.DriftAt) / 4), -20, 20);
        }
        p.DriftAt = now;

        double rangeNm = adv.Deviation.RangeNm;
        double limit = rangeNm < 2 ? 5 : rangeNm < 4 ? 10 : 30;
        double wantTrack = Math.Clamp(Units.RadToDeg(Math.Atan2(-adv.Deviation.RightOfCourseFt / 30.0, Math.Max(closure, 150))), -limit, limit);
        double want = Math.Round(wantTrack - p.DriftDeg);
        double change = Units.Norm180(want - assigned);
        if (Math.Abs(change) < 2) return null;
        if (now - p.HeadingSetAt < 8 && Math.Abs(change) < 10) return null;
        p.ProposedRel = want;
        double hdg = Units.Norm360(CourseMagDeg + want);
        return $"TURN {(change > 0 ? "RIGHT" : "LEFT")} HEADING {(hdg < 0.5 ? 360 : hdg):000}";
    }

    /// <summary>
    /// Not yet on the PAR (a base leg or dogleg outside its sector): vectors onto final from the ASR picture, in
    /// fives, closing at up to 30° and less as the centerline nears.
    /// </summary>
    private AiCall? AsrVector(SimAircraft ac)
    {
        double crossNm = ac.CrossFt / Units.FeetPerNm;
        double wantRel = Math.Round(Math.Clamp(-crossNm * 20, -30, 30) / 5) * 5;
        if (Math.Abs(Units.Norm180(wantRel - ac.CommandedHeadingRelDeg)) < 10) return null;
        double hdg = Units.Norm360(CourseMagDeg + wantRel);
        string side = Units.Norm180(wantRel - ac.CommandedHeadingRelDeg) > 0 ? "right" : "left";
        return new AiCall(ac.Callsign, $"Turn {side} heading {RadioSpeech.Heading(hdg)}.", false);
    }

    /// <summary>
    /// A range call passing each mile (§5-12-5), then "at decision altitude", "over approach lights" and "over
    /// landing threshold" (§5-12-6, §5-12-7). Each is said once, on the first call after it is reached;
    /// <paramref name="commit"/> marks it said.
    /// </summary>
    private static string? PositionCall(Advisory adv, ParGeometry geo, Progress p, bool commit)
    {
        double rangeFt = adv.Deviation.RangeFt, nm = adv.Deviation.RangeNm;
        if (rangeFt <= geo.ThresholdRangeFt + 300)
        {
            if (p.ThresholdSaid) return null;
            if (commit) p.ThresholdSaid = p.LightsSaid = p.DecisionSaid = true;
            return $"over landing threshold, {adv.CoursePhrase}";
        }
        // Said a moment early, so it is heard at the decision altitude rather than missed between calls.
        if (rangeFt <= geo.DecisionHeightRangeFt + 600 && !p.DecisionSaid)
        {
            if (commit) p.DecisionSaid = true;
            return "at decision altitude";
        }
        if (nm <= 0.55)
        {
            if (p.LightsSaid) return null;
            if (commit) p.LightsSaid = true;
            return "over approach lights";
        }
        if (rangeFt <= geo.DecisionHeightRangeFt + 600) return null;
        // Said a little before the mile, so it is still about that mile when the pilot hears it.
        int mile = (int)Math.Round(nm);
        if (mile >= 1 && mile < p.LastMile && nm >= mile - 0.1 && nm <= mile + 0.3)
        {
            if (commit) p.LastMile = mile;
            return AdvisoryEngine.RangePhrase(mile);
        }
        return null;
    }

    private string Brief(string cs)
    {
        string alt = RadioSpeech.Altitude(MissedClimbMslFt);
        return $"{cs}, {Facility} final, this will be a P A R approach to runway {RadioSpeech.Runway(RunwayId)}. "
            + $"In the event of missed approach, climb and maintain {alt}, fly runway heading. "
            + "If no transmissions are received for one minute in the pattern or five seconds on final approach, "
            + $"attempt contact with tower and proceed V F R. If unable, climb and maintain {alt} and execute missed approach.";
    }

    /// <summary>The scope's call worded for the speech engine: lower case, numbers as spoken digits ("heading two four zero").</summary>
    public static string Speakable(string text)
    {
        string s = NumberRx().Replace(text.ToLowerInvariant(), m => RadioSpeech.Digits(m.Value));
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    [GeneratedRegex(@"\d+")] private static partial Regex NumberRx();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex SpacesRx();
    [GeneratedRegex(@"TURN (?:LEFT|RIGHT) HEADING \d{3},?")] private static partial Regex TurnRx();
    [GeneratedRegex(@"OF COURSE AND (?:HOLDING|CORRECTING)")] private static partial Regex CourseTrendRx();
    [GeneratedRegex(@"APPROACHING GLIDEPATH\.?(?: WHEELS SHOULD BE DOWN\.?)?,?")] private static partial Regex ApproachingRx();
}
