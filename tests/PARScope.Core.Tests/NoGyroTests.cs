using PARScope.Core.Par;
using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

/// <summary>Wordings Whisper produced in real sessions that must still be understood.</summary>
public class RecognizerVariantTests
{
    [Fact]
    public void Common_recognizer_variants_are_understood()
    {
        Assert.Equal(LandingIntent.FullStop, Phraseology.Parse("RUNWAY ZERO FIVE LEFT CLEAR TO LAND.").Clearance);
        Assert.True(Phraseology.Parse("GEAR SHOULD BE DOWN.").WheelsDown);
        var missed = Phraseology.Parse("YOUR MISSED APPROACH INSTRUCTIONS. FLY RUNWAY HEADING CLIMB MAINTAIN TWO THOUSAND.");
        Assert.Equal(2000, missed.ClimbToFt);
        Assert.Equal(1, Phraseology.Parse("ONE ONE MILES FROM TOUCHDOWN. TURN RIGHT.").NoGyroTurn);
    }
}

/// <summary>Generated callsigns fit the radar's callsign field; the radio still says the whole callword.</summary>
public class CallsignLengthTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Generated_callsigns_fit_and_are_spoken_in_full(int max)
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site), MaxCallsignLength = max };
        for (int i = 0; i < 300; i++)
        {
            var ac = sim.SpawnRandom(PilotMode.ControllerDirected);
            Assert.InRange(ac.Callsign.Length, 3, max);
            Assert.DoesNotContain("Alpha Bravo", RadioSpeech.Callsign(ac.Callsign)); // never spelled out letter by letter
            sim.Remove(ac.Callsign);
        }
        Assert.Equal("BNS", TrainingTraffic.Abbreviate("BANSHEE", 3));
        Assert.Equal("BNSH", TrainingTraffic.Abbreviate("BANSHEE", 4));
        Assert.Equal("RCH", TrainingTraffic.Abbreviate("REACH", 3));
    }

    [Fact]
    public void Abbreviated_callsigns_are_spoken_with_the_whole_word()
    {
        RadioSpeech.RegisterSpokenCallword("BNS21", "BANSHEE");
        Assert.Equal("Banshee two one", RadioSpeech.Callsign("BNS21"));
    }
}

/// <summary>No-gyro approach phraseology (FAA JO 7110.65 §5-10-3): turn until told to stop.</summary>
public class NoGyroTests
{
    [Theory]
    [InlineData("turn right", 1, false)]
    [InlineData("Navy one two three four, turn left", -1, false)]
    [InlineData("stop turn", 0, true)]
    [InlineData("turn right heading two four zero", 0, false)] // a heading is not a no-gyro turn
    public void Parses_no_gyro_turns(string text, int turn, bool stop)
    {
        var t = Phraseology.Parse(text);
        Assert.Equal(turn, t.NoGyroTurn);
        Assert.Equal(stop, t.StopTurn);
        Assert.False(t.IsEmpty);
    }

    [Fact]
    public void Parses_turn_rates_and_the_brief()
    {
        Assert.True(Phraseology.Parse("make half standard rate turns").HalfStandardRate);
        Assert.False(Phraseology.Parse("this will be a no-gyro approach, make all turns standard rate").HalfStandardRate);
        Assert.True(Phraseology.Parse("this will be a no gyro approach").NoGyroBrief);
    }

    [Fact]
    public void Turns_until_stop_turn_at_the_rate_given()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 10, Skill = 1, Mode = PilotMode.ControllerDirected });
        void Run(double s) { double end = sim.SimTime + s; for (double t = sim.SimTime + 0.1; t <= end; t += 0.1) sim.Step(t).ToList(); }
        Run(1);
        double h0 = ac.HeadingRelDeg;

        ac.Receive(Phraseology.Parse("turn right"), sim.SimTime, 243, 700);
        Run(10);
        double h1 = ac.HeadingRelDeg;
        Assert.True(h1 - h0 > 15, $"{h0:0} -> {h1:0}"); // standard rate, 3°/s, after the reaction delay

        ac.Receive(Phraseology.Parse("stop turn"), sim.SimTime, 243, 700);
        Run(10);
        double h2 = ac.HeadingRelDeg;
        Run(10);
        Assert.InRange(ac.HeadingRelDeg - h2, -1, 1); // holds the heading it rolled out on

        ac.Receive(Phraseology.Parse("make half standard rate turns"), sim.SimTime, 243, 700);
        ac.Receive(Phraseology.Parse("turn left"), sim.SimTime, 243, 700);
        Run(5);
        double h3 = ac.HeadingRelDeg;
        Run(10);
        Assert.InRange(h3 - ac.HeadingRelDeg, 13, 17); // 1.5°/s for 10 s

        ac.Receive(Phraseology.Parse("turn right heading 243"), sim.SimTime, 243, 700);
        Run(30);
        Assert.Equal(0, ac.NoGyroTurnDirection);
        Assert.InRange(ac.HeadingRelDeg, -1, 1);
    }
}
