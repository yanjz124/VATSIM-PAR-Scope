using PARScope.Core.Par;
using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

public class RadioTests
{
    [Theory]
    [InlineData("VV1234", "Navy one two three four")]
    [InlineData("VM4685", "Marine four six eight five")]
    [InlineData("R21456", "Army two one four five six")]
    [InlineData("REACH481", "Reach four eight one")]
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
        Assert.Contains("going missed", r4.Reply);
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
