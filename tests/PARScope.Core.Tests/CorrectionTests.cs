using PARScope.Core.Par;
using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

/// <summary>"Correction" replaces what it corrects; "disregard" cancels.</summary>
public class CorrectionTests
{
    [Theory]
    [InlineData("TURN LEFT HEADING TWO SEVEN ZERO, CORRECTION, TURN RIGHT HEADING TWO EIGHT ZERO", 280)]
    [InlineData("TURN LEFT HEADING TWO SEVEN ZERO, CORRECTION, TWO SIX FIVE", 265)]
    [InlineData("HEADING ZERO SEVEN ZERO DISREGARD HEADING ZERO EIGHT ZERO", 80)]
    public void Correction_and_disregard_leave_the_corrected_heading(string said, double heading) =>
        Assert.Equal(heading, Phraseology.Parse(said).HeadingDeg);

    [Fact]
    public void A_correction_replaces_only_what_it_mentions()
    {
        var t = Phraseology.Parse("TURN RIGHT HEADING ZERO EIGHT ZERO, SLIGHTLY ABOVE GLIDEPATH, CORRECTION, SLIGHTLY BELOW GLIDEPATH");
        Assert.Equal(80, t.HeadingDeg);
        Assert.Equal(GlidepathCall.SlightlyBelow, t.Glidepath);
    }

    [Fact]
    public void Disregard_after_an_instruction_cancels_that_transmission_only()
    {
        var t = Phraseology.Parse("GOING THROUGH THE FINAL. TURN LEFT HEADING ZERO SEVEN FIVE. CORRECTION DISREGARD.");
        Assert.True(t.Cancelled);
        Assert.False(t.Disregard);
        Assert.Null(t.HeadingDeg);
        Assert.False(t.IsEmpty); // understood, not "not understood"
        Assert.True(Phraseology.Parse("NAVY TWO FOUR TWO, DISREGARD.").Disregard);
    }

    [Fact]
    public void Disregard_on_its_own_puts_the_previous_heading_back()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 8, SpeedKt = 130, Skill = 1, Mode = PilotMode.ControllerDirected });
        double before = ac.CommandedHeadingRelDeg;
        ac.Receive(Phraseology.Parse("TURN RIGHT HEADING TWO SIX ZERO"), 0, 243, 700);
        for (double t = 0.1; t <= 10; t += 0.1) sim.Step(t).ToList();
        Assert.NotEqual(before, ac.CommandedHeadingRelDeg, 1);
        ac.Receive(Phraseology.Parse("DISREGARD"), 10, 243, 700);
        Assert.Equal(before, ac.CommandedHeadingRelDeg, 3);
    }
}
