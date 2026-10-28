namespace PARScope.Core.Sim;

/// <summary>Landing intent passed to tower, as in the MCAS Cherry Point VISCOM groups (P3722.1S §6-311).</summary>
public enum LandingIntent { FullStop, TouchAndGo, LowApproach, Option }

public enum TowerAnswer { None, Cleared, Continue }

/// <summary>
/// Simulated local controller for final-controller coordination. The final controller requests a
/// clearance for the aircraft on final; tower answers with a landing clearance in 7110.65 tower wording,
/// or "continue" while the runway is occupied. The final controller then relays it to the pilot
/// (7110.65 §5-10-13).
/// </summary>
public sealed class TowerSim
{
    private readonly Dictionary<string, (TowerAnswer Answer, LandingIntent Intent)> _state = new(StringComparer.OrdinalIgnoreCase);

    public TowerAnswer AnswerFor(string callsign) => _state.TryGetValue(callsign, out var s) ? s.Answer : TowerAnswer.None;
    public LandingIntent IntentFor(string callsign) => _state.TryGetValue(callsign, out var s) ? s.Intent : LandingIntent.FullStop;

    /// <summary>Local controller's operating initials for this session (§2-1-14), said after a clearance.</summary>
    public string Initials { get; }
    public string SpokenInitials => RadioSpeech.Spell(Initials);

    public TowerSim(Random? rng = null)
    {
        rng ??= Random.Shared;
        Initials = $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}";
    }

    /// <summary>
    /// The final controller's interphone request, as shown/spoken, in one of two wordings: as a request, the way a
    /// US Army GCA controller does ("Local, final, request landing clearance, Army two zero one niner zero, five
    /// and a half miles"), or by position and intent ("Local, final, Army two zero one niner zero, six mile
    /// final, full stop"). On the landline tower is "local".
    /// </summary>
    public static string RequestText(string callsign, double rangeNm, LandingIntent intent, bool asRequest = true) => asRequest
        ? $"Local, final, request {RequestWords(intent)}, {RadioSpeech.Callsign(callsign)}, {Miles(rangeNm)}."
        : $"Local, final, {RadioSpeech.Callsign(callsign)}, {RadioSpeech.Digits(Math.Max(1, (int)Math.Round(rangeNm)).ToString())} mile final, {IntentWords(intent)}.";

    private static string IntentWords(LandingIntent intent) => intent switch
    {
        LandingIntent.TouchAndGo => "touch and go",
        LandingIntent.LowApproach => "low approach",
        LandingIntent.Option => "option",
        _ => "full stop",
    };

    /// <summary>"five miles", "five and a half miles", "one mile": to the nearest half mile.</summary>
    private static string Miles(double nm)
    {
        double half = Math.Max(1, Math.Round(nm * 2) / 2);
        int whole = (int)half;
        string n = RadioSpeech.Digits(whole.ToString());
        return half > whole ? $"{n} and a half miles" : whole == 1 ? "one mile" : $"{n} miles";
    }

    /// <summary>Tower's answer: cleared (with its initials), or continue if another aircraft is still on the runway.</summary>
    public string Request(string callsign, LandingIntent intent, string runwayId, bool runwayOccupied)
    {
        var answer = runwayOccupied ? TowerAnswer.Continue : TowerAnswer.Cleared;
        _state[callsign] = (answer, intent);
        string cs = RadioSpeech.Callsign(callsign);
        if (answer == TowerAnswer.Continue) return $"{cs}, continue, runway occupied, {SpokenInitials}.";
        return $"{cs}, runway {RadioSpeech.Runway(runwayId)}, {ClearanceWords(intent)}, {SpokenInitials}.";
    }

    public void Forget(string callsign) => _state.Remove(callsign);

    public static string ClearanceWords(LandingIntent intent) => intent switch
    {
        LandingIntent.TouchAndGo => "cleared touch and go",
        LandingIntent.LowApproach => "cleared low approach",
        LandingIntent.Option => "cleared for the option",
        _ => "cleared to land",
    };

    private static string RequestWords(LandingIntent intent) => intent switch
    {
        LandingIntent.TouchAndGo => "touch and go",
        LandingIntent.LowApproach => "low approach",
        LandingIntent.Option => "the option",
        _ => "landing clearance",
    };
}
