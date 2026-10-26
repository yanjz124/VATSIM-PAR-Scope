namespace PARScope.Core.Sim;

/// <summary>
/// Simulated approach (arrival/departure radar) controller on the interphone. Approach hands each arrival
/// to final with position, heading and altitude; after a missed approach the final controller coordinates
/// the climb-out back to approach before switching the pilot. Receiving controllers acknowledge with
/// their operating initials (FAA JO 7110.65 §2-1-14, §5-4-5).
/// </summary>
public sealed class ApproachSim
{
    private readonly HashSet<string> _coordinated = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Approach controller's operating initials for this session.</summary>
    public string Initials { get; }

    public ApproachSim(Random? rng = null)
    {
        rng ??= Random.Shared;
        Initials = $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}";
    }

    /// <summary>"Final, approach, handoff, Navy one two three four, one four miles, heading two four zero, two thousand five hundred."</summary>
    public static string HandoffToFinal(string callsign, double rangeNm, double headingMagDeg, double altitudeFt) =>
        $"Final, approach, handoff, {RadioSpeech.Callsign(callsign)}, {RadioSpeech.Digits(((int)Math.Round(rangeNm)).ToString())} miles, "
        + $"heading {RadioSpeech.Heading(headingMagDeg)}, {RadioSpeech.Altitude(altitudeFt)}.";

    /// <summary>The final controller's coordination for a missed approach.</summary>
    public static string MissedApproachRequest(string callsign, double headingMagDeg, double climbToFt) =>
        $"Approach, final, {RadioSpeech.Callsign(callsign)}, missed approach, heading {RadioSpeech.Heading(headingMagDeg)}, climbing to {RadioSpeech.Altitude(climbToFt)}.";

    /// <summary>Approach accepts the aircraft.</summary>
    public string Coordinate(string callsign)
    {
        _coordinated.Add(callsign);
        return $"{RadioSpeech.Callsign(callsign)}, approved, {string.Join(' ', Initials.ToCharArray())}.";
    }

    public bool IsCoordinated(string callsign) => _coordinated.Contains(callsign);

    public void Forget(string callsign) => _coordinated.Remove(callsign);
}
