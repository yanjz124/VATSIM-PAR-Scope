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
        Assert.StartsWith("Missed approach, climb and maintain two thousand five hundred, heading one eight zero", r1.Reply);
        Assert.Equal(0, ac.CommandedHeadingRelDeg); // not flown yet

        var r2 = ac.Receive(Phraseology.Parse("execute missed approach"), 1, 243, 700, thresholdElevFt: 100);
        Assert.StartsWith("Going missed", r2.Reply);
        for (double t = 0; t <= 120; t += 0.1) sim.Step(t).ToList();
        Assert.True(ac.GoingAround);
        Assert.Equal(-63, ac.CommandedHeadingRelDeg, 1);
        Assert.InRange(ac.HeightFt, 2300, 2450); // 2,500 MSL with a 100 ft threshold, levelling

        Assert.StartsWith("Switching", ac.Receive(Phraseology.Parse("contact approach"), sim.SimTime, 243, 700).Reply);
        for (double t = 120.1; t <= 130; t += 0.1) sim.Step(t).ToList();
        Assert.Empty(sim.Aircraft);
    }

    [Fact]
    public void Arrivals_are_handed_off_far_out_below_the_glidepath()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        for (int i = 0; i < 20; i++)
        {
            var ac = sim.SpawnRandom(PilotMode.ControllerDirected);
            Assert.InRange(ac.RangeFt / PARScope.Core.Geo.Units.FeetPerNm, 12, 15);
            Assert.InRange(ac.HeightFt, 1900, 3000);
        }
        Assert.Equal("Final, approach, handoff, one four mile final, Navy one two three four, two thousand five hundred, heading two four three.",
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
        Assert.Equal("Navy one two three four, runway two four right, cleared to land.", tower.Request("VV1234", LandingIntent.FullStop, "24R", false));
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
    public void Tactical_callwords_fly_types_with_faa_approach_speeds()
    {
        Assert.True(MilitaryCallsigns.All.Count > 50);
        Assert.All(MilitaryCallsigns.All, m => Assert.NotNull(AircraftPerformance.Find(m.TypeCode)?.ApproachSpeedKt));
        Assert.Equal("Banshee two one", RadioSpeech.Callsign("BANSHEE21"));

        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        Assert.Equal("C30J", sim.SpawnRandom(PilotMode.ControllerDirected, "BRADY31").TypeCode);
    }
}
