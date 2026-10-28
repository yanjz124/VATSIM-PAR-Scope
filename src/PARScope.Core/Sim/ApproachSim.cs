namespace PARScope.Core.Sim;

/// <summary>
/// Simulated approach (arrival/departure radar) controller on the interphone, using the radar handoff
/// phraseology of FAA JO 7110.65 §5-4-5: the transferring controller says "HANDOFF (position), (aircraft
/// ID), (altitude, restrictions)"; the receiving controller accepts with "(aircraft ID) RADAR CONTACT"
/// and both identify themselves by operating initials (§2-1-14). Approach only switches an arrival to
/// final once final has accepted it, and repeats an unanswered handoff.
/// </summary>
public sealed class ApproachSim
{
    private readonly HashSet<string> _coordinated = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingHandoff> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An arrival approach has offered to final and not yet had accepted.</summary>
    public sealed record PendingHandoff(string Callsign, string Text, double NextRepeat, int Calls);

    /// <summary>Seconds between repeats of an unanswered handoff.</summary>
    public const double RepeatSec = 20;

    /// <summary>Approach controller's operating initials for this session.</summary>
    public string Initials { get; }

    /// <summary>Operating initials in the phonetic alphabet: "Juliett Yankee".</summary>
    public string SpokenInitials => RadioSpeech.Spell(Initials);

    public ApproachSim(Random? rng = null)
    {
        rng ??= Random.Shared;
        Initials = $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}";
    }

    public IReadOnlyCollection<PendingHandoff> Pending => _pending.Values;

    /// <summary>"Final, approach, handoff, one four miles east, Navy one two three four, two thousand five hundred, heading two four zero."</summary>
    public static string HandoffToFinal(string callsign, double rangeNm, double headingMagDeg, double altitudeFt, HandoffLeg leg = HandoffLeg.Final)
    {
        string miles = RadioSpeech.Digits(((int)Math.Round(rangeNm)).ToString());
        string where = leg switch
        {
            HandoffLeg.Base => $"{miles} miles from touchdown, on base leg",
            HandoffLeg.Dogleg => $"{miles} miles from touchdown",
            _ => $"{miles} mile final",
        };
        // Approach assigns headings in tens.
        double heading = Math.Round(headingMagDeg / 10) * 10 is var h && h <= 0 ? 360 : h;
        return $"Final, approach, handoff, {where}, {RadioSpeech.Callsign(callsign)}, "
               + $"{RadioSpeech.Altitude(altitudeFt)}, heading {RadioSpeech.Heading(heading)}.";
    }

    /// <summary>Approach offers an arrival to final; returns the call.</summary>
    public string OfferHandoff(string callsign, double rangeNm, double headingMagDeg, double altitudeFt, double now, HandoffLeg leg = HandoffLeg.Final)
    {
        string text = HandoffToFinal(callsign, rangeNm, headingMagDeg, altitudeFt, leg);
        _pending[callsign] = new PendingHandoff(callsign, text, now + RepeatSec, 1);
        return text;
    }

    /// <summary>A handoff due to be repeated because final has not answered, if any.</summary>
    public PendingHandoff? DueRepeat(double now)
    {
        var due = _pending.Values.FirstOrDefault(p => now >= p.NextRepeat);
        if (due == null) return null;
        return _pending[due.Callsign] = due with { NextRepeat = now + RepeatSec, Calls = due.Calls + 1 };
    }

    /// <summary>Final accepted ("radar contact"): approach acknowledges with initials and switches the pilot.</summary>
    public string? Accept(string callsign)
    {
        if (!_pending.Remove(callsign)) return null;
        return $"{SpokenInitials}.";
    }

    public bool IsPending(string callsign) => _pending.ContainsKey(callsign);

    /// <summary>Final hands a missed approach back to approach.</summary>
    public static string MissedApproachRequest(string callsign, double headingMagDeg, double climbToFt) =>
        $"Approach, final, handoff, missed approach, {RadioSpeech.Callsign(callsign)}, climbing to {RadioSpeech.Altitude(climbToFt)}, heading {RadioSpeech.Heading(headingMagDeg)}.";

    /// <summary>Approach accepts the missed approach: "(aircraft ID) radar contact, (initials)".</summary>
    public string Coordinate(string callsign)
    {
        _coordinated.Add(callsign);
        return $"{RadioSpeech.Callsign(callsign)}, radar contact, {SpokenInitials}.";
    }

    public bool IsCoordinated(string callsign) => _coordinated.Contains(callsign);

    public void Forget(string callsign)
    {
        _coordinated.Remove(callsign);
        _pending.Remove(callsign);
    }
}
