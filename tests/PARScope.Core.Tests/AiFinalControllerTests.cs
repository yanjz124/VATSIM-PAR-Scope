using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Sim;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

/// <summary>The AI final controller working whole approaches through the radar, pilot, tower and approach models.</summary>
public class AiFinalControllerTests
{
    /// <summary>
    /// Runs one arrival from approach's handoff until it lands, goes missed or leaves, with the AI on final. Every
    /// call is fed back the way the app does: landline calls to tower or approach, radio calls to the pilot,
    /// each taking about as long to say as speech does. Returns the calls and results in order.
    /// </summary>
    private static (List<string> Log, SimAircraft Ac) Fly(HandoffMix handoffs, int seed, double? skill = 0.8, TrainingDifficulty difficulty = TrainingDifficulty.Normal, double maxSec = 900, bool navy = false, RadarPreset? radarPreset = null)
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        double course = GeometryTests.Runway.MagneticCourseDeg, elev = GeometryTests.Runway.ThresholdElevFt;
        var sim = new TrainingTraffic(seed) { Geometry = geo, Handoffs = handoffs, ReturnForAnotherApproach = false, Difficulty = difficulty };
        var store = new TrackStore();
        var preset = radarPreset ?? (navy ? RadarPreset.Spn46 : RadarPreset.Fpn68);
        var radar = new RadarSimulator(preset, geo, seed: 5) { ClutterEnabled = false };
        var approach = new ApproachSim(new Random(seed));
        var tower = new TowerSim(new Random(seed));
        var ai = new AiFinalController(new Random(seed))
        {
            Facility = "Miramar", RunwayId = GeometryTests.Runway.RunwayId, CourseMagDeg = course, ThresholdElevFt = elev,
            MissedClimbMslFt = Math.Round((elev + 2000) / 100) * 100, Geometry = geo,
            Navy = navy, LockOn = preset.Pattern == ScanPattern.Tracker,
        };
        var ac = sim.SpawnRandom(PilotMode.ControllerDirected, skill: skill);
        var log = new List<string>();
        approach.OfferHandoff(ac.Callsign, ac.RangeFt / Units.FeetPerNm, course, ac.AltitudeAboveThresholdFt + elev, 0, ac.HandoffLeg);

        // Speech takes time: about 2.8 words a second.
        static double Secs(string s) => s.Split(' ').Length / 2.8;
        double radioFree = 0, landlineFree = 1, aiFree = 0;
        var due = new List<(double At, Action Do)>();

        for (double t = 0; t <= maxSec && !ac.Finished; t += 0.1)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            radar.Advance(t, store.Snapshot(t));
            foreach (var (_, text) in sim.TakeResults()) log.Add("RESULT " + text);
            foreach (var (_, text) in sim.TakePilotCalls()) { log.Add($"{t,5:0} PILOT " + text); radioFree = Math.Max(radioFree, t) + Secs(text); }
            foreach (var d in due.Where(d => d.At <= t).ToList()) { due.Remove(d); d.Do(); }

            Advisory? adv = radar.Tracks.TryGetValue(ac.Callsign, out var tr) && tr.IsCorrelated && !tr.IsCoasting
                ? AdvisoryEngine.Evaluate(tr, geo, preset, new DeviationBands(), course,
                    Units.Norm360(course + ac.CommandedHeadingRelDeg), ac.GearReportedDown)
                : null;
            var traffic = new[] { new AiTraffic(ac, adv, approach.IsPending(ac.Callsign), tower.AnswerFor(ac.Callsign), tower.IntentFor(ac.Callsign), approach.IsCoordinated(ac.Callsign), tr) };
            if (ai.Next(traffic, t, radioFree > t, landlineFree > t, aiFree > t) is not { } call) continue;

            log.Add($"{t,5:0} " + (call.Landline ? "LANDLINE " : "FINAL ") + call.Text);
            double start = Math.Max(t, call.Landline ? landlineFree : radioFree), end = start + Secs(call.Text);
            aiFree = end;
            if (call.Landline) landlineFree = end; else radioFree = end;
            due.Add((end, () =>
            {
                double now = sim.SimTime;
                if (call.Landline)
                {
                    string reply;
                    if (call.Text.Contains("radar contact"))
                    {
                        reply = approach.Accept(ac.Callsign) ?? "Roger.";
                        due.Add((now + 7, () =>
                        {
                            string checkIn = ac.CheckInCall("Miramar", course, elev);
                            log.Add("PILOT " + checkIn);
                            radioFree = Math.Max(radioFree, sim.SimTime) + Secs(checkIn);
                        }));
                    }
                    else
                    {
                        var ic = Interphone.Parse(call.Text);
                        Assert.NotNull(ic);
                        Assert.Same(ac, Interphone.FindAircraft(call.Text, sim.Aircraft));
                        reply = ic.To == Facility.Tower
                            ? tower.Request(ac.Callsign, ic.Intent ?? LandingIntent.FullStop, GeometryTests.Runway.RunwayId, false)
                            : approach.Coordinate(ac.Callsign);
                    }
                    log.Add($"{now,5:0} LINE " + reply);
                    landlineFree = Math.Max(landlineFree, now) + 2 + Secs(reply);
                    return;
                }
                var heard = Phraseology.Parse(call.Text);
                Assert.False(heard.IsEmpty, $"not understood: {call.Text}");
                var response = ac.Receive(heard, now, course, adv?.RecommendedDescentFpm ?? 700, elev, start);
                if (response.Reply is { } r)
                {
                    log.Add($"{now,5:0} PILOT " + r);
                    radioFree = Math.Max(radioFree, now) + 1.5 + Secs(r);
                }
            }));
        }
        return (log, ac);
    }

    [Theory]
    [InlineData(HandoffMix.FinalOnly)]
    [InlineData(HandoffMix.BaseAndDogleg)]
    public void Talks_arrivals_down_to_a_landing(HandoffMix handoffs)
    {
        // Like a real talk-down, the odd one ends in a go-around; most land.
        int landed = 0;
        var failures = new List<string>();
        for (int seed = 1; seed <= 10; seed++)
        {
            var (log, _) = Fly(handoffs, seed);
            if (log.Any(l => l.StartsWith("RESULT landed"))) landed++;
            else failures.Add(string.Join("\n", log));
        }
        Assert.True(landed >= 8, $"{landed}/10 landed. First failure:\n{failures.FirstOrDefault()}");
    }

    [Fact]
    public void Carrier_wording_on_the_spn46_lock_on_soft_headings_and_minimums()
    {
        int landed = 0;
        bool soft = false;
        for (int seed = 1; seed <= 10; seed++)
        {
            var (log, _) = Fly(HandoffMix.FinalOnly, seed, navy: true);
            string all = string.Join("\n", log);
            if (log.Any(l => l.StartsWith("RESULT landed"))) landed++;
            var finals = log.Where(l => l.Contains(" FINAL ")).ToList();
            int lockOn = finals.FindIndex(l => l.Contains("lock-on, "));
            Assert.True(lockOn >= 0, all);
            Assert.Single(finals, l => l.Contains("lock-on"));
            // After lock-on, turns are soft headings ("right five"), never "turn right heading…".
            Assert.DoesNotContain(finals.Skip(lockOn), l => l.Contains("heading"));
            soft |= finals.Skip(lockOn).Any(l => System.Text.RegularExpressions.Regex.IsMatch(l.ToLowerInvariant(), @"\b(?:left|right) (?:one|two|three|four|five|six|seven|eight|nine|ten|fifteen|twenty|thirty)\b"));
            if (log.Any(l => l.StartsWith("RESULT landed"))) Assert.Contains(finals, l => l.Contains("at minimums"));
            Assert.DoesNotContain(finals, l => l.Contains("at decision altitude"));
        }
        Assert.True(soft, "no soft heading in ten approaches:\n" + string.Join("\n", Fly(HandoffMix.FinalOnly, 1, navy: true).Log));
        Assert.True(landed >= 8, $"{landed}/10 landed");
    }

    [Fact]
    public void Says_begin_descent_once()
    {
        for (int seed = 1; seed <= 10; seed++)
        {
            var (log, ac) = Fly(HandoffMix.FinalOnly, seed);
            var calls = log.Where(l => l.Contains("FINAL") && l.Contains("begin descent", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(calls.Count == 1, $"seed {seed}:\n{string.Join("\n", log)}");
        }
    }

    [Fact]
    public void Waits_for_the_landline_to_close_before_talking_on_frequency()
    {
        static double Secs(string s) => s.Split(' ').Length / 2.8;
        static (double T, string Text) Parse(string l) => (double.Parse(l[..5]), l[6..]);
        for (int seed = 1; seed <= 10; seed++)
        {
            var (log, _) = Fly(HandoffMix.FinalOnly, seed);
            var timed = log.Where(l => l.Length > 6 && char.IsDigit(l[4])).Select(Parse).ToList();
            for (int i = 0; i < timed.Count; i++)
            {
                if (!timed[i].Text.StartsWith("LANDLINE")) continue;
                // From the call until the other end's answer has been said.
                var answer = timed.Skip(i + 1).FirstOrDefault(x => x.Text.StartsWith("LINE "));
                if (answer.Text == null) continue;
                double closed = answer.T + 2 + Secs(answer.Text[5..]);
                Assert.DoesNotContain(timed, x => x.Text.StartsWith("FINAL") && x.T > timed[i].T && x.T < closed);
            }
        }
    }

    [Fact]
    public void Works_the_whole_approach_in_order()
    {
        var (log, ac) = Fly(HandoffMix.FinalOnly, seed: 3, skill: 1);
        string all = string.Join("\n", log);
        int Index(string s) { int i = log.FindIndex(l => l.Contains(s, StringComparison.OrdinalIgnoreCase)); Assert.True(i >= 0, $"no \"{s}\" in:\n{all}"); return i; }
        Assert.True(Index("radar contact") < Index("this will be a P A R approach"), all);
        Assert.True(Index("this will be a P A R approach") < Index("do not acknowledge further transmissions"), all);
        Assert.True(Index("do not acknowledge further transmissions") < Index("begin descent"), all);
        Assert.True(Index("LANDLINE Local, final") < Index("cleared to land"), all);
        Index("miles from touchdown");
        Index("at decision altitude");
        Index("contact tower after landing");
        Assert.True(ac.ClearedToLand);
        Assert.Contains(log, l => l.StartsWith("RESULT landed"));
    }

    [Fact]
    public void Coordinates_a_missed_approach_and_switches_the_pilot()
    {
        // Ceiling below the decision height: the pilot goes around at minimums.
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        var ai = new AiFinalController { CourseMagDeg = 243, Geometry = geo };
        var sim = new TrainingTraffic { Geometry = geo, FieldWeather = (50, 3) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 0.5, HeightOffsetFt = 0, Mode = PilotMode.ControllerDirected, GearDown = true });
        ac.Receive(Phraseology.Parse("BEGIN DESCENT"), 0, 243, 700);
        for (double t = 0.1; t < 20 && !ac.GoingAround; t += 0.1) sim.Step(t).ToList();
        Assert.True(ac.GoingAround);

        var line = new[] { new AiTraffic(ac, null, false, TowerAnswer.None, LandingIntent.FullStop, false) };
        AiCall? request = null;
        double at = 100;
        for (; request == null && at < 110; at += 0.5) request = ai.Next(line, at, false, false);
        Assert.NotNull(request);
        Assert.True(at > 103, "a few seconds to see the missed approach first");
        Assert.True(request.Landline);
        Assert.True(Interphone.Parse(request.Text) is { To: Facility.Approach, MissedApproach: true });

        var coordinated = new[] { new AiTraffic(ac, null, false, TowerAnswer.None, LandingIntent.FullStop, true) };
        var contact = ai.Next(coordinated, 115, false, false);
        Assert.NotNull(contact);
        Assert.False(contact.Landline);
        Assert.True(Phraseology.Parse(contact.Text).ContactApproach);
    }

    [Theory]
    [InlineData("TURN LEFT HEADING 240, SLIGHTLY ABOVE GLIDEPATH AND COMING DOWN, ON COURSE.", "Turn left heading two four zero, slightly above glidepath and coming down, on course.")]
    [InlineData("3 MILES FROM TOUCHDOWN", "Three miles from touchdown")]
    public void Scope_calls_are_worded_for_speech(string call, string spoken)
    {
        Assert.Equal(spoken, AiFinalController.Speakable(call));
        var a = Phraseology.Parse(call);
        var b = Phraseology.Parse(spoken);
        Assert.Equal(a.HeadingDeg, b.HeadingDeg);
        Assert.Equal(a.Glidepath, b.Glidepath);
        Assert.Equal(a.RangeInfo, b.RangeInfo);
    }

    [Fact]
    public void Approach_waits_for_a_gap_before_calling_a_handoff_again()
    {
        var approach = new ApproachSim(new Random(1));
        approach.OfferHandoff("VV123", 12, 302, 2500, now: 0);
        approach.Postpone(25); // final answering when the repeat fell due at 20 s
        Assert.Null(approach.DueRepeat(21));
        Assert.NotNull(approach.DueRepeat(25));
    }
    [Fact]
    public void Briefs_only_after_the_pilot_has_checked_in()
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        var sim = new TrainingTraffic(1) { Geometry = geo };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 12, Mode = PilotMode.ControllerDirected });
        var ai = new AiFinalController { CourseMagDeg = 302, Geometry = geo };
        AiTraffic T(bool pending) => new(ac, null, pending, TowerAnswer.None, LandingIntent.FullStop, false);

        AiCall? accept = null;
        for (double t = 0; accept == null && t < 10; t += 0.5) accept = ai.Next([T(true)], t, false, false);
        Assert.Contains("radar contact", accept!.Text);
        // Accepted, but the check-in is slow to come (a voice still generating): no brief over it.
        for (double t = 10; t < 25; t += 0.5) Assert.Null(ai.Next([T(false)], t, false, false));
        ac.CheckInCall("Miramar", 302, 0);
        AiCall? brief = null;
        for (double t = 25; brief == null && t < 30; t += 0.5) brief = ai.Next([T(false)], t, false, false);
        Assert.Contains("this will be a P A R approach", brief!.Text);
    }}
