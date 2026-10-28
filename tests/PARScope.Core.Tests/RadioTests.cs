using PARScope.Core.Par;
using PARScope.Core.Sim;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

public class RadioTests
{
    [Theory]
    [InlineData("VV1234", "Navy one two three four")]
    [InlineData("VM4685", "Marine four six eight five")]
    [InlineData("R21456", "Army two one four five six")]
    [InlineData("REACH481", "Reach four eight one")]
    [InlineData("PAT123", "Pat one two three")]
    [InlineData("BANSHEE21", "Banshee two one")]
    [InlineData("N123AB", "November one two three Alpha Bravo")]
    [InlineData("N52TX", "November five two Tango X-ray")]
    [InlineData("GABCD", "Golf Alpha Bravo Charlie Delta")]
    [InlineData("G-ABCD", "Golf Alpha Bravo Charlie Delta")]
    // Airlines: telephony callsign and the flight number in group form (7110.65 §2-4-20).
    [InlineData("UAL1234", "United twelve thirty-four")]
    [InlineData("FFT211", "Frontier Flight two eleven")]
    [InlineData("AAL100", "American one hundred")]
    [InlineData("DAL1200", "Delta twelve hundred")]
    [InlineData("SWA3", "Southwest three")]
    [InlineData("BAW1103", "Speedbird eleven zero three")]
    [InlineData("SKW5051", "Skywest fifty fifty-one")]
    [InlineData("JBU2000", "Jetblue two thousand")]
    [InlineData("UAL51A", "United fifty-one Alpha")]
    [InlineData("RCH481", "Reach four eight one")] // military airlift keeps single digits
    [InlineData("BAT21", "Gherkin twenty-one")] // a typed callsign uses the FAA list (BAT is a civil designator)
    [InlineData("RPA4567", "Brickyard forty-five sixty-seven")]
    [InlineData("GJS3456", "Lindbergh thirty-four fifty-six")]
    [InlineData("PAT12", "Pat one two")] // US military operator: digits
    public void Military_callsigns_are_spoken_per_7110_65(string cs, string spoken)
    {
        Assert.Equal(spoken, RadioSpeech.Callsign(cs));
    }

    [Fact]
    public void Numbers_and_runways_are_spoken_digit_by_digit()
    {
        Assert.Equal("two four zero", RadioSpeech.Heading(240));
        Assert.Equal("three six zero", RadioSpeech.Heading(0));
        Assert.Equal("two thousand", RadioSpeech.Altitude(2010));
        Assert.Equal("one thousand six hundred", RadioSpeech.Altitude(1600));
        Assert.Equal("two four right", RadioSpeech.Runway("24R"));
        Assert.Equal("wind three zero zero at niner", RadioSpeech.Wind(300, 9, false));
        Assert.Equal(310, RadioSpeech.MagneticWindDeg(300, -10)); // 10°W: magnetic = true + 10
        Assert.Equal(360, RadioSpeech.MagneticWindDeg(4, 3));
        Assert.Equal(250, RadioSpeech.MagneticWindDeg(262, 12));
    }

    private static SimAircraft Spawn()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        return sim.Spawn(new SpawnOptions { RangeNm = 8, Mode = PilotMode.ControllerDirected });
    }

    [Fact]
    public void Pilot_reads_back_until_told_not_to_acknowledge()
    {
        var ac = Spawn();
        var r1 = ac.Receive(Phraseology.Parse("turn left heading two four zero"), 0, 243, 700);
        Assert.StartsWith("Heading two four zero", r1.Reply);

        var r2 = ac.Receive(Phraseology.Parse("do not acknowledge further transmissions"), 1, 243, 700);
        Assert.Null(r2.Reply);

        var r3 = ac.Receive(Phraseology.Parse("heading two four two, slightly above glidepath"), 2, 243, 700);
        Assert.Null(r3.Reply);

        var r4 = ac.Receive(Phraseology.Parse("too high for safe approach, if runway or approach lights not in sight execute missed approach"), 3, 243, 700);
        Assert.StartsWith("Going missed", r4.Reply);
    }

    [Theory]
    [InlineData("do not acknowledge further transmissions")]
    [InlineData("Do not acknowledge further instructions.")]
    [InlineData("don't acknowledge")]
    [InlineData("no need to acknowledge further calls")]
    [InlineData("acknowledgement not required")]
    [InlineData("no further readbacks")]
    public void Do_not_acknowledge_variants_are_understood(string text)
    {
        Assert.True(Phraseology.Parse(text).DoNotAcknowledge);
    }

    [Fact]
    public void Hard_pilots_drift_off_an_assigned_heading_until_reassigned()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 10, Skill = 1, HeadingDriftDegPerMin = 6, Mode = PilotMode.ControllerDirected });
        for (double t = 0; t <= 60; t += 0.1) sim.Step(t).ToList();
        Assert.InRange(Math.Abs(ac.HeadingRelDeg), 4, 8);

        ac.Receive(Phraseology.Parse("turn right heading two four three"), sim.SimTime, 243, 700);
        for (double t = 60.1; t <= 75; t += 0.1) sim.Step(t).ToList();
        Assert.InRange(Math.Abs(ac.HeadingRelDeg), 0, 2.5);
    }

    [Fact]
    public void Course_information_stops_the_drift_but_does_not_turn_the_pilot()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 10, Skill = 1, HeadingDriftDegPerMin = 6, Mode = PilotMode.ControllerDirected });
        for (double t = 0; t <= 60; t += 0.1) sim.Step(t).ToList();
        double drifted = ac.HeadingRelDeg;
        Assert.InRange(Math.Abs(drifted), 4, 8);
        string side = drifted > 0 ? "right" : "left";

        var t1 = Phraseology.Parse($"well {side} of course");
        Assert.NotNull(t1.CourseCall);
        ac.Receive(t1, sim.SimTime, 243, 700);
        for (double t = 60.1; t <= 75; t += 0.1) sim.Step(t).ToList();
        // Back on the assigned heading (the creep starts again from there), with no turn toward the centerline beyond it.
        Assert.InRange(ac.HeadingRelDeg, -2.5, 2.5);
        Assert.Equal(0, ac.CommandedHeadingRelDeg, 3);
    }

    [Fact]
    public void Missed_approach_instructions_are_remembered_then_flown()
    {
        var p = Phraseology.Parse("in the event of missed approach, climb and maintain two thousand five hundred, turn left heading one eight zero");
        Assert.True(p.MissedApproachInstructions);
        Assert.False(p.MissedApproach);
        Assert.Equal(2500, p.ClimbToFt);
        Assert.Equal(180, p.HeadingDeg);
        Assert.Equal(3000, Phraseology.Parse("climb and maintain 3,000").ClimbToFt);
        Assert.True(Phraseology.Parse("contact approach").ContactApproach);

        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 6, Skill = 1, Mode = PilotMode.ControllerDirected });
        var r1 = ac.Receive(p, 0, 243, 700, thresholdElevFt: 100);
        Assert.StartsWith("Roger, in the event of missed approach, climb and maintain two thousand five hundred, heading one eight zero", r1.Reply);
        Assert.Equal(0, ac.CommandedHeadingRelDeg); // not flown yet

        var r2 = ac.Receive(Phraseology.Parse("execute missed approach"), 1, 243, 700, thresholdElevFt: 100);
        Assert.StartsWith("Going missed", r2.Reply);
        for (double t = 0; t <= 120; t += 0.1) sim.Step(t).ToList();
        Assert.True(ac.GoingAround);
        Assert.Equal(-63, ac.CommandedHeadingRelDeg, 1);
        Assert.InRange(ac.AltitudeAboveThresholdFt, 2300, 2450); // 2,500 MSL with a 100 ft threshold, leveling

        Assert.StartsWith("Switching", ac.Receive(Phraseology.Parse("contact approach"), sim.SimTime, 243, 700).Reply);
        for (double t = 120.1; t <= 130; t += 0.1) sim.Step(t).ToList();
        Assert.True(Assert.Single(sim.Aircraft).InPattern); // with approach now, still on radar
    }

    [Fact]
    public void Arrivals_are_handed_off_far_out_below_the_glidepath()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        for (int i = 0; i < 20; i++)
        {
            var ac = sim.SpawnRandom(PilotMode.ControllerDirected);
            Assert.InRange(ac.RangeFt / PARScope.Core.Geo.Units.FeetPerNm, 12, 15);
            Assert.InRange(ac.AltitudeAboveThresholdFt, 1990, 3000);
        }
        Assert.Equal("Final, approach, handoff, one four mile final, Navy one two three four, two thousand five hundred, heading two four zero.",
            ApproachSim.HandoffToFinal("VV1234", 14.2, 243, 2500));
        var app = new ApproachSim();
        app.OfferHandoff("VV1234", 14, 243, 2500, now: 0);
        Assert.Null(app.DueRepeat(10));
        Assert.Equal(2, app.DueRepeat(21)!.Calls);
        Assert.NotNull(app.Accept("VV1234"));
        Assert.Null(app.DueRepeat(60));
        Assert.Equal($"Navy one two three four, radar contact, {RadioSpeech.Spell(app.Initials)}.", app.Coordinate("VV1234"));
        Assert.Matches("^[A-Z][a-z-]+ [A-Z][a-z-]+$", app.SpokenInitials);
    }

    [Theory]
    [InlineData("Tower, final, Navy one two three four, four mile final, full stop", Facility.Tower, LandingIntent.FullStop, false)]
    [InlineData("tower final 1234 touch n go", Facility.Tower, LandingIntent.TouchAndGo, false)]
    [InlineData("Local, got a Navy on a four mile final for the option", Facility.Tower, LandingIntent.Option, false)]
    [InlineData("Approach, final, Banshee two one went missed, runway heading, climbing to two thousand", Facility.Approach, null, true)]
    [InlineData("uh departure, missed approach on 1234", Facility.Approach, null, true)]
    public void Interphone_calls_are_read_loosely(string text, Facility to, LandingIntent? intent, bool missed)
    {
        var call = Interphone.Parse(text);
        Assert.NotNull(call);
        Assert.Equal(to, call.To);
        Assert.Equal(intent, call.Intent);
        Assert.Equal(missed, call.MissedApproach);
    }

    [Theory]
    [InlineData("tower clearance not received, execute missed approach")]
    [InlineData("approaching glidepath, wheels should be down")]
    [InlineData("if runway or approach lights not in sight, execute missed approach")]
    public void Pilot_calls_are_not_mistaken_for_interphone(string text) => Assert.Null(Interphone.Parse(text));

    [Fact]
    public void Interphone_finds_the_aircraft_named()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var navy = sim.SpawnRandom(PilotMode.ControllerDirected, "VV1234", "P8");
        var banshee = sim.SpawnRandom(PilotMode.ControllerDirected, "BANSHEE21", "F16");
        Assert.Same(navy, Interphone.FindAircraft("tower final navy one two three four full stop", sim.Aircraft));
        Assert.Same(navy, Interphone.FindAircraft("tower, 234, full stop", sim.Aircraft));
        Assert.Same(banshee, Interphone.FindAircraft("approach, banshee went missed", sim.Aircraft));
        Assert.Null(Interphone.FindAircraft("tower, full stop", sim.Aircraft));
    }

    /// <summary>A controller-directed arrival on the glidepath 3 NM out, descending at the right rate.</summary>
    private static (TrainingTraffic Sim, SimAircraft Ac) StableFinal(string? clearance, bool gear = true,
        (double? CeilingFt, double? VisibilitySm) wx = default)
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site), FieldWeather = wx };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 3, HeightOffsetFt = 0, SpeedKt = 130, Skill = 1, GearDown = gear, Mode = PilotMode.ControllerDirected });
        double fpm = 130 * PARScope.Core.Geo.Units.FeetPerNm / 60 * Math.Tan(3 * Math.PI / 180);
        ac.BeginDescent(-10, fpm);
        if (clearance != null) ac.Receive(Phraseology.Parse(clearance), 0, 243, fpm);
        for (double t = 0; t <= 150 && !ac.Finished && !ac.GoingAround; t += 0.1) sim.Step(t).ToList();
        return (sim, ac);
    }

    [Fact]
    public void Stable_cleared_approach_lands()
    {
        var (_, ac) = StableFinal("runway two four right, cleared to land");
        Assert.False(ac.GoingAround);
        Assert.True(ac.Landed || ac.Finished);
    }

    [Theory]
    [InlineData(null, true, null, null, "no landing clearance")]
    [InlineData("cleared to land", false, null, null, "gear not down")]
    [InlineData("cleared to land", true, 100.0, null, "no runway environment")]
    [InlineData("cleared to land", true, null, 0.25, "no runway environment")]
    public void Pilot_goes_around_at_minimums_and_says_why(string? clearance, bool gear, double? ceiling, double? vis, string reason)
    {
        var (sim, ac) = StableFinal(clearance, gear, (ceiling, vis));
        Assert.True(ac.GoingAround);
        Assert.Equal(reason, ac.GoAroundReason);
        Assert.Contains(sim.TakePilotCalls(), c => c.Text.StartsWith($"Going around, {reason}"));
    }

    [Fact]
    public void Touch_and_go_gets_airborne_again()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 3, HeightOffsetFt = 0, SpeedKt = 130, Skill = 1, GearDown = true, Mode = PilotMode.ControllerDirected });
        double fpm = 130 * PARScope.Core.Geo.Units.FeetPerNm / 60 * Math.Tan(3 * Math.PI / 180);
        ac.BeginDescent(-10, fpm);
        ac.Receive(Phraseology.Parse("runway two four right, cleared touch and go"), 0, 243, fpm);
        bool touched = false;
        for (double t = 0; t <= 200 && !ac.Finished; t += 0.1) { sim.Step(t).ToList(); touched |= ac.Landed; if (touched && ac.HeightFt > 300) break; }
        Assert.True(touched);
        Assert.True(ac.GoingAround);
        Assert.True(ac.HeightFt > 300);
    }

    // Wordings from a real session log that the parser used to miss.
    [Theory]
    [InlineData("well above glide and holding", GlidepathCall.WellAbove, CallTrend.Holding)]
    [InlineData("WELL ABOVE THE GLIDE AND HOLD.", GlidepathCall.WellAbove, CallTrend.Holding)]
    [InlineData("above glide slope and coming down", GlidepathCall.Above, CallTrend.Correcting)]
    [InlineData("slightly below glide path and correct", GlidepathCall.SlightlyBelow, CallTrend.Correcting)]
    public void Shortened_glidepath_calls_are_understood(string text, GlidepathCall call, CallTrend trend)
    {
        var t = Phraseology.Parse(text);
        Assert.Equal(call, t.Glidepath);
        Assert.Equal(trend, t.GlidepathTrend);
    }

    [Fact]
    public void Descent_and_no_ack_variants_are_understood()
    {
        Assert.True(Phraseology.Parse("INITIATE DESCENT.").BeginDescent);
        Assert.True(Phraseology.Parse("commence descent").BeginDescent);
        Assert.True(Phraseology.Parse("Bones six two, to not acknowledge further transmissions").DoNotAcknowledge);
        Assert.Equal("SLIGHTLY RIGHT OF COURSE", Phraseology.Parse("well above glide, slightly read of course and correcting").CourseCall);
    }
    // Canadian Forces GCA phraseology (vPAR, M. Oxner).
    [Fact]
    public void Level_off_acknowledge_is_answered_even_after_do_not_acknowledge()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 4, HeightOffsetFt = 0, SpeedKt = 130, Skill = 1, Mode = PilotMode.ControllerDirected });
        ac.BeginDescent(-10, 700);
        ac.Receive(Phraseology.Parse("do not acknowledge further transmissions"), 0, 243, 700);
        for (double t = 0; t <= 10; t += 0.1) sim.Step(t).ToList();
        Assert.True(ac.VerticalSpeedFpm < -500);

        var r = ac.Receive(Phraseology.Parse("3 miles from touchdown - dangerously below the glidepath - level off your aircraft - acknowledge"), sim.SimTime, 243, 700);
        Assert.StartsWith("Leveling off", r.Reply);
        for (double t = 10.1; t <= 20; t += 0.1) sim.Step(t).ToList();
        Assert.InRange(ac.VerticalSpeedFpm, -60, 60);

        Assert.Null(ac.Receive(Phraseology.Parse("back on glidepath - resume normal rate of descent"), sim.SimTime, 243, 700).Reply);
        for (double t = 20.1; t <= 28; t += 0.1) sim.Step(t).ToList();
        Assert.True(ac.VerticalSpeedFpm < -500);
    }

    [Fact]
    public void Cf_gca_course_wording_and_lost_comms_are_understood()
    {
        Assert.Equal("DRIFTING LEFT OF COURSE", Phraseology.Parse("drifting left of course, turn right heading 262").CourseCall);
        Assert.NotNull(Phraseology.Parse("well right of the on course correcting rapidly - fly heading 285").CourseCall);
        Assert.True(Phraseology.Parse("intercepting glidepath - commence descent for a 3 degree glidepath now").BeginDescent);
        var lc = Phraseology.Parse("if no transmissions received for a period of 5 seconds while on final, maintain last assigned heading and climb to 3000");
        Assert.True(lc.LostCommsInstructions);
        Assert.False(Phraseology.Parse("do not acknowledge further transmissions").AcknowledgeRequested);
    }

    /// <summary>Regression: a level arrival used to hold height above the threshold's tangent plane, so its
    /// reported (geodetic) altitude was ~200 ft high at 15 NM and descended as it came in.</summary>
    [Fact]
    public void Level_arrival_reports_a_constant_altitude()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 15, AltitudeFt = 2000, SpeedKt = 150, Skill = 1, Mode = PilotMode.ControllerDirected });
        double thr = GeometryTests.Runway.ThresholdElevFt;
        var alts = new List<double>();
        for (double t = 0; t <= 75; t += 0.1)
            foreach (var r in sim.Step(t).OfType<PositionReport>()) alts.Add(r.Position.AltFt - thr);
        Assert.InRange(alts.Min(), 1995, 2005);
        Assert.InRange(alts.Max(), 1995, 2005);
        Assert.Contains(RadioSpeech.Altitude(2000 + thr) + ",", ac.CheckInCall("Miramar", 243, thr)); // spoken MSL
    }

    // FAA JO 7110.65 §5-10-11/12 forms are instructions for later, not to fly now.
    [Theory]
    [InlineData("your missed approach procedure is climb and maintain three thousand, turn left heading one eight zero")]
    [InlineData("after completing low approach, climb and maintain two thousand, turn right heading zero six zero")]
    [InlineData("after completing touch and go climb and maintain 2000")]
    public void Faa_missed_approach_and_climbout_forms_are_remembered(string text)
    {
        var p = Phraseology.Parse(text);
        Assert.True(p.MissedApproachInstructions);
        Assert.False(p.MissedApproach);
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 8, Skill = 1, Mode = PilotMode.ControllerDirected });
        ac.Receive(p, 0, 243, 700);
        for (double t = 0; t <= 10; t += 0.1) sim.Step(t).ToList();
        Assert.Equal(0, ac.CommandedHeadingRelDeg);
        Assert.False(ac.GoingAround);
    }

    [Theory]
    [InlineData(null, false)]   // clear: runway in sight, continues
    [InlineData(150.0, true)]   // ceiling below the aircraft: goes around
    public void Conditional_missed_approach_depends_on_seeing_the_runway(double? ceiling, bool goesAround)
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site), FieldWeather = (ceiling, 3) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 1.2, HeightOffsetFt = -40, SpeedKt = 130, Skill = 1, GearDown = true, Mode = PilotMode.ControllerDirected });
        sim.Step(0).ToList(); sim.Step(0.1).ToList();
        var call = Phraseology.Parse("too low for safe approach, if runway or approach lights not in sight, execute missed approach");
        Assert.True(call.ConditionalMissedApproach);
        var r = ac.Receive(call, 0.1, 243, 700);
        for (double t = 0.2; t <= 6; t += 0.1) sim.Step(t).ToList();
        Assert.Equal(goesAround, ac.GoingAround);
        Assert.StartsWith(goesAround ? "Going missed" : "Runway in sight", r.Reply);
        Assert.False(Phraseology.Parse("tower clearance not received, if runway or approach lights not in sight, execute missed approach").ConditionalMissedApproach);
    }
    [Fact]
    public void Missed_approach_flies_approachs_pattern_on_radar_and_comes_back()
    {
        const double nm = PARScope.Core.Geo.Units.FeetPerNm;
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { Callsign = "VV1234", TypeCode = "P8", RangeNm = 3, HeightOffsetFt = 0, SpeedKt = 140, Skill = 1, Mode = PilotMode.ControllerDirected });
        ac.Receive(Phraseology.Parse("execute missed approach"), 0, 243, 700);
        for (double t = 0.1; t <= 20; t += 0.1) sim.Step(t).ToList();
        ac.Receive(Phraseology.Parse("contact approach"), sim.SimTime, 243, 700);

        // Stays on radar the whole way round, flying upwind, crosswind, downwind, base and intercept.
        var legs = new List<SimAircraft.PatternLeg>();
        double maxRange = 0, maxAbeam = 0, t1 = 20.1;
        for (; t1 <= 1500 && sim.TakeReturningAircraft().Count == 0; t1 += 0.1)
        {
            var reports = sim.Step(t1).ToList();
            Assert.DoesNotContain(reports, r => r is TargetRemoved);
            Assert.Single(sim.Aircraft);
            if (ac.InPattern && (legs.Count == 0 || legs[^1] != ac.Leg)) legs.Add(ac.Leg);
            maxRange = Math.Max(maxRange, ac.RangeFt);
            maxAbeam = Math.Max(maxAbeam, Math.Abs(ac.CrossFt));
        }
        Assert.Equal(Enum.GetValues<SimAircraft.PatternLeg>(), legs);
        Assert.InRange(maxAbeam / nm, 2.4, 3.5);
        Assert.InRange(maxRange / nm, 11.5, 15); // base turned 12-14 NM out, led by the turn radius
        Assert.InRange(t1, 200, 900); // a few minutes round, not instant

        // Back on final for another approach: same aircraft, fresh approach.
        Assert.Same(ac, Assert.Single(sim.Aircraft));
        Assert.False(ac.InPattern || ac.GoingAround || ac.HandedOff || ac.Descending);
        Assert.InRange(ac.RangeFt / nm, 10, 14);
        Assert.InRange(Math.Abs(ac.CrossFt) / nm, 0, 0.5);
        Assert.EndsWith("for another approach.", ac.CheckInCall("Miramar", 243, 0));
        Assert.Empty(sim.InPattern);
    }

    [Fact]
    public void Continuing_at_decision_height_the_pilot_lines_up_and_lands_visually()
    {
        // Off to the side and a little high at DH, but within the limits to continue.
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var geo = sim.Geometry!;
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 1.2, HeightOffsetFt = 30, CrossFt = 120, SpeedKt = 130, Skill = 1, GearDown = true, Mode = PilotMode.ControllerDirected });
        ac.Receive(Phraseology.Parse("begin descent"), 0, 243, 700);
        ac.Receive(Phraseology.Parse("runway two four right, cleared to land"), 0, 243, 700);
        for (double t = 0.1; t <= 120 && !ac.Landed; t += 0.1) sim.Step(t).ToList();
        Assert.True(ac.Visual);
        Assert.True(ac.Landed);
        Assert.False(ac.GoingAround);
        Assert.InRange(Math.Abs(ac.CrossFt), 0, 40);
    }

    [Fact]
    public void Full_stop_landing_does_not_come_back()
    {
        var (sim, ac) = StableFinal("runway two four right, cleared to land");
        for (double t = 150; t <= 400 && sim.Aircraft.Count > 0; t += 0.1) sim.Step(t).ToList();
        Assert.Empty(sim.Aircraft);
        Assert.Empty(sim.InPattern);
    }
    /// <summary>Every transmission the hotkeys produce must be understood by the pilot model.</summary>
    [Theory]
    [InlineData("SLIGHTLY ABOVE GLIDEPATH AND COMING DOWN")]
    [InlineData("WELL BELOW GLIDEPATH AND HOLDING")]
    [InlineData("GOING BELOW GLIDEPATH")]
    [InlineData("ON GLIDEPATH")]
    [InlineData("WELL LEFT OF COURSE AND CORRECTING")]
    [InlineData("GOING RIGHT OF COURSE")]
    [InlineData("ON COURSE")]
    [InlineData("HOW DO YOU HEAR ME")]
    [InlineData("THIS WILL BE A P-A-R APPROACH TO RUNWAY 24R")]
    [InlineData("IF NO TRANSMISSIONS ARE RECEIVED FOR ONE MINUTE IN THE PATTERN OR FIVE SECONDS ON FINAL APPROACH, ATTEMPT CONTACT WITH TOWER AND PROCEED VFR. IF UNABLE, CLIMB AND MAINTAIN 2500 AND EXECUTE MISSED APPROACH")]
    [InlineData("IN THE EVENT OF MISSED APPROACH, CLIMB AND MAINTAIN 2500, FLY RUNWAY HEADING")]
    [InlineData("DO NOT ACKNOWLEDGE FURTHER TRANSMISSIONS")]
    [InlineData("APPROACHING GLIDEPATH, WHEELS SHOULD BE DOWN")]
    [InlineData("4 MILES FROM TOUCHDOWN")]
    [InlineData("ONE MILE FROM TOUCHDOWN")]
    [InlineData("OVER LANDING THRESHOLD, SLIGHTLY LEFT OF COURSE")]
    [InlineData("LEVEL OFF YOUR AIRCRAFT, ACKNOWLEDGE")]
    [InlineData("RESUME NORMAL RATE OF DESCENT")]
    [InlineData("IF RUNWAY OR APPROACH LIGHTS NOT IN SIGHT, EXECUTE MISSED APPROACH")]
    [InlineData("WIND 300 AT 9, RUNWAY 24R, CLEARED TO LAND")]
    [InlineData("CONTACT APPROACH")]
    [InlineData("CONTACT TOWER AFTER LANDING")]
    [InlineData(CombinedBrief)]
    [InlineData("TURN LEFT")]
    [InlineData("TURN RIGHT")]
    [InlineData("STOP TURN")]
    [InlineData("SLIGHTLY ABOVE GLIDEPATH")]
    [InlineData("WELL LEFT OF COURSE")]
    public void Every_hotkey_call_is_understood(string text)
    {
        Assert.False(Phraseology.Parse(text).IsEmpty, text);

    }

    /// <summary>The approach brief key: approach, missed approach and lost communications in one transmission.</summary>
    private const string CombinedBrief = "THIS WILL BE A P-A-R APPROACH TO RUNWAY 24R. IN THE EVENT OF MISSED APPROACH, CLIMB AND MAINTAIN 2400, FLY RUNWAY HEADING. "
        + "IF NO TRANSMISSIONS ARE RECEIVED FOR ONE MINUTE IN THE PATTERN OR FIVE SECONDS ON FINAL APPROACH, ATTEMPT CONTACT WITH TOWER AND PROCEED VFR. "
        + "IF UNABLE, CLIMB AND MAINTAIN 2400 AND EXECUTE MISSED APPROACH";

    [Fact]
    public void Combined_brief_is_remembered_and_not_flown()
    {
        var t = Phraseology.Parse(CombinedBrief);
        Assert.True(t.ApproachBrief);
        Assert.True(t.MissedApproachInstructions);
        Assert.True(t.LostCommsInstructions);
        Assert.False(t.MissedApproach);
        Assert.Equal(2400, t.ClimbToFt);

        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 8, Skill = 1, Mode = PilotMode.ControllerDirected });
        var r = ac.Receive(t, 0, 243, 700);
        Assert.NotNull(r.Reply);
        for (double s = 0.1; s <= 10; s += 0.1) sim.Step(s).ToList();
        Assert.False(ac.GoingAround);
    }

    [Fact]
    public void Missed_approach_instructions_from_the_key_are_not_flown_and_lost_comms_is_read_back()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 6, Skill = 1, Mode = PilotMode.ControllerDirected });
        Assert.StartsWith("Roger, in the event of missed approach, climb and maintain two thousand five hundred, runway heading", ac.Receive(Phraseology.Parse("IN THE EVENT OF MISSED APPROACH, CLIMB AND MAINTAIN 2500, FLY RUNWAY HEADING"), 0, 243, 700).Reply);
        Assert.StartsWith("Roger", ac.Receive(Phraseology.Parse("IF NO TRANSMISSIONS ARE RECEIVED FOR ONE MINUTE IN THE PATTERN OR FIVE SECONDS ON FINAL APPROACH, ATTEMPT CONTACT WITH TOWER AND PROCEED VFR"), 0, 243, 700).Reply);
        Assert.StartsWith("Roger", ac.Receive(Phraseology.Parse("THIS WILL BE A P-A-R APPROACH TO RUNWAY 24R"), 0, 243, 700).Reply);
        Assert.False(ac.GoingAround);
    }
    [Fact]
    public void Pause_freezes_aircraft_and_holds_pilot_reactions()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 8, Mode = PilotMode.ControllerDirected });
        for (double t = 0; t <= 1; t += 0.1) sim.Step(t).ToList();

        sim.Paused = true;
        double range = ac.RangeFt, simTime = sim.SimTime;
        ac.Receive(Phraseology.Parse("turn left heading two three zero"), sim.SimTime, 243, 700);
        var reports = new List<TrafficMessage>();
        for (double t = 1.1; t <= 30; t += 0.1) reports.AddRange(sim.Step(t));

        Assert.Equal(range, ac.RangeFt);
        Assert.Equal(simTime, sim.SimTime);
        Assert.Equal(0, ac.CommandedHeadingRelDeg);
        Assert.All(reports.OfType<PositionReport>(), r => Assert.True(r.Frozen));

        sim.Paused = false;
        for (double t = 30.1; t <= 31; t += 0.1) sim.Step(t).ToList();
        Assert.True(ac.RangeFt < range);
        Assert.Equal(0, ac.CommandedHeadingRelDeg); // reaction delay still running in sim time
        for (double t = 31.1; t <= 40; t += 0.1) sim.Step(t).ToList();
        Assert.Equal(-13, ac.CommandedHeadingRelDeg, 1);
    }

    [Fact]
    public void Radio_check_and_check_in_follow_published_content()
    {
        var ac = Spawn();
        Assert.StartsWith("Loud and clear", ac.Receive(Phraseology.Parse("how do you hear me"), 0, 243, 700).Reply);
        string call = ac.CheckInCall("Lemoore", 322, 226);
        Assert.StartsWith("Lemoore final, ", call);
        Assert.Contains("heading", call);
        Assert.Contains(ac.GearDown ? "gear down" : "gear up", call);
    }

    [Fact]
    public void Tower_clears_or_says_continue()
    {
        var tower = new TowerSim();
        Assert.Equal($"Navy one two three four, runway two four right, cleared to land, {tower.SpokenInitials}.", tower.Request("VV1234", LandingIntent.FullStop, "24R", false));
        Assert.Equal("Local, final, request landing clearance, Navy one two three four, five and a half miles.", TowerSim.RequestText("VV1234", 5.4, LandingIntent.FullStop));
        Assert.Equal("Local, final, request touch and go, Navy one two three four, three miles.", TowerSim.RequestText("VV1234", 3.1, LandingIntent.TouchAndGo));
        Assert.Equal(LandingIntent.FullStop, Interphone.Parse(TowerSim.RequestText("VV1234", 5.4, LandingIntent.FullStop))!.Intent);
        Assert.Equal(LandingIntent.Option, Interphone.Parse(TowerSim.RequestText("VV1234", 5.4, LandingIntent.Option))!.Intent);
        // The other wording, by position and intent.
        Assert.Equal("Local, final, Navy one two three four, five mile final, full stop.", TowerSim.RequestText("VV1234", 5.4, LandingIntent.FullStop, asRequest: false));
        Assert.Equal(LandingIntent.TouchAndGo, Interphone.Parse(TowerSim.RequestText("VV1234", 5.4, LandingIntent.TouchAndGo, asRequest: false))!.Intent);
        Assert.Equal(TowerAnswer.Cleared, tower.AnswerFor("VV1234"));
        Assert.Contains("continue", tower.Request("VM4685", LandingIntent.TouchAndGo, "24R", true));
        Assert.True(Phraseology.Parse("wind 300 at 9, runway 24 right, cleared to land").ClearedToLand);
        Assert.True(Phraseology.Parse("tower clearance not received, execute missed approach").MissedApproach);
    }
}

public class AircraftPerformanceTests
{
    [Fact]
    public void Faa_database_supplies_military_approach_speeds()
    {
        Assert.Equal(128, AircraftPerformance.Find("C30J")!.ApproachSpeedKt);
        Assert.Equal(115, AircraftPerformance.Find("C17")!.ApproachSpeedKt);
        Assert.Equal("C", AircraftPerformance.Find("F18S")!.ApproachCategory);
        Assert.Null(AircraftPerformance.Find("ZZZZ"));
        Assert.True(AircraftPerformance.All.Count > 300);
    }

    [Fact]
    public void Custom_callsign_and_type_are_used_with_type_speed()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.SpawnRandom(PilotMode.ControllerDirected, "vm 4321", "C30J");
        Assert.Equal("VM4321", ac.Callsign);
        Assert.Equal("C30J", ac.TypeCode);
        Assert.InRange(ac.SpeedKt, 128, 136);
    }

    [Fact]
    public void Squadrons_based_at_the_field_are_found_by_name()
    {
        Assert.Contains(MilitaryCallsigns.BasedAt("MIRAMAR MCAS (JOSEPH J FOSS FLD)"), c => c.Callword == "BAT");
        var oceana = MilitaryCallsigns.BasedAt("OCEANA NAS (APOLLO SOUCEK FLD)");
        Assert.NotEmpty(oceana);
        Assert.All(oceana, c => Assert.Contains("OCEANA", c.Unit.ToUpperInvariant()));
        Assert.DoesNotContain(MilitaryCallsigns.BasedAt("MIRAMAR MCAS (JOSEPH J FOSS FLD)"), c => c.Unit.Contains("Joseph", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Aircat eleven", RadioSpeech.Callsign("CAT11")); // typed: the FAA list (CAT is a civil designator)
        Assert.Empty(MilitaryCallsigns.BasedAt("NAVAL AIR STATION"));

        // Most tactical arrivals at a field with based squadrons are theirs.
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site), HomeAirportName = "OCEANA NAS (APOLLO SOUCEK FLD)" };
        // Squadron callwords only (a service callsign such as "VV" or "PAT" is not a tactical arrival).
        var words = MilitaryCallsigns.All.Select(m => m.Callword).Where(w => w is not ("VV" or "VM" or "R" or "AF" or "PAT")).ToHashSet();
        int home = 0, tactical = 0;
        for (int i = 0; i < 400; i++)
        {
            var ac = sim.SpawnRandom(PilotMode.ControllerDirected);
            string word = new(ac.Callsign.TakeWhile(char.IsLetter).ToArray());
            if (words.Contains(word)) { tactical++; if (oceana.Any(o => o.Callword == word)) home++; }
            sim.Remove(ac.Callsign);
        }
        Assert.InRange(home / (double)tactical, 0.65, 0.9);
    }

    [Fact]
    public void Tactical_callwords_fly_types_with_faa_approach_speeds()
    {
        Assert.True(MilitaryCallsigns.All.Count > 50);
        Assert.All(MilitaryCallsigns.All, m => Assert.NotNull(AircraftPerformance.Find(m.TypeCode)?.ApproachSpeedKt));
        Assert.Equal("Banshee two one", RadioSpeech.Callsign("BANSHEE21"));

        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        // A custom callsign on a listed callword flies one of that callword's aircraft.
        var brady = MilitaryCallsigns.All.Where(m => m.Callword == "BRADY").Select(m => m.TypeCode).ToList();
        Assert.Contains(sim.SpawnRandom(PilotMode.ControllerDirected, "BRADY31").TypeCode, brady);
    }
}
