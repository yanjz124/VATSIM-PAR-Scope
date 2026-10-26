using PARScope.Core.Par;
using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

/// <summary>Pilot reactions to "begin descent" and glidepath calls.</summary>
public class GlidepathResponseTests
{
    private const double Nominal = -700;

    private static (TrainingTraffic Sim, SimAircraft Ac) Arrival()
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 8, HeightOffsetFt = 0, SpeedKt = 130, Skill = 1, Mode = PilotMode.ControllerDirected });
        return (sim, ac);
    }

    private static void Run(TrainingTraffic sim, double seconds)
    {
        double end = sim.SimTime + seconds;
        for (double t = sim.SimTime + 0.1; t <= end; t += 0.1) sim.Step(t).ToList();
    }

    private static double Say(TrainingTraffic sim, SimAircraft ac, string call)
    {
        ac.Receive(Phraseology.Parse(call), sim.SimTime, 243, 700);
        Run(sim, 8);
        return ac.VerticalSpeedFpm;
    }

    [Fact]
    public void Begin_descent_descends_and_is_read_back()
    {
        var (sim, ac) = Arrival();
        Run(sim, 2);
        Assert.InRange(ac.VerticalSpeedFpm, -50, 50);
        var r = ac.Receive(Phraseology.Parse("BEGIN DESCENT"), sim.SimTime, 243, 700);
        Assert.Contains("descending", r.Reply, StringComparison.OrdinalIgnoreCase);
        Run(sim, 8);
        Assert.InRange(ac.VerticalSpeedFpm, -900, -500);
    }

    [Fact]
    public void Begin_descent_after_level_off_descends_again_and_takes_glidepath_calls()
    {
        var (sim, ac) = Arrival();
        Say(sim, ac, "BEGIN DESCENT");
        Assert.InRange(Say(sim, ac, "LEVEL OFF YOUR AIRCRAFT, ACKNOWLEDGE"), -50, 50);
        Assert.InRange(Say(sim, ac, "BEGIN DESCENT"), -900, -500);
        Assert.True(Say(sim, ac, "ABOVE GLIDEPATH") < -850);
    }

    [Theory]
    [InlineData("WELL ABOVE GLIDEPATH", -1100)]
    [InlineData("ABOVE GLIDEPATH", -950)]
    [InlineData("SLIGHTLY ABOVE GLIDEPATH", -850)]
    [InlineData("SLIGHTLY BELOW GLIDEPATH", -550)]
    [InlineData("BELOW GLIDEPATH", -450)]
    [InlineData("WELL BELOW GLIDEPATH", -300)]
    public void Plain_calls_correct_in_proportion(string call, double expected)
    {
        var (sim, ac) = Arrival();
        Say(sim, ac, "BEGIN DESCENT");
        Assert.InRange(Say(sim, ac, call), expected - 40, expected + 40);
    }

    [Fact]
    public void Trends_change_the_correction()
    {
        var p = (GlidepathCall c, CallTrend t, double cur) => SimAircraft.GlidepathResponse(c, t, Nominal, cur);
        // Correcting ("…and coming down"): keep the correction that is working.
        Assert.Equal(-1000, p(GlidepathCall.SlightlyAbove, CallTrend.Correcting, -1000));
        // Rapidly: over-correcting, ease back toward normal.
        Assert.InRange(p(GlidepathCall.SlightlyAbove, CallTrend.Rapidly, -1200), -1000, -800);
        // Holding: the correction isn't enough, add to it.
        Assert.True(p(GlidepathCall.Above, CallTrend.Holding, -950) < -950);
        // Going above: firmer than the plain call from the same rate.
        Assert.True(p(GlidepathCall.SlightlyAbove, CallTrend.Worsening, Nominal) < p(GlidepathCall.SlightlyAbove, CallTrend.None, Nominal));
        // A plain call never takes away a correction already being flown.
        Assert.Equal(-1100, p(GlidepathCall.SlightlyAbove, CallTrend.None, -1100));
        // Same the other way, below the glidepath.
        Assert.True(p(GlidepathCall.Below, CallTrend.Holding, -450) > -450);
        Assert.Equal(-500, p(GlidepathCall.SlightlyBelow, CallTrend.Correcting, -500));
        // On glidepath: normal rate.
        Assert.Equal(Nominal, p(GlidepathCall.On, CallTrend.None, -1100));
        // Never climbs or dives.
        Assert.InRange(p(GlidepathCall.WellBelow, CallTrend.Worsening, -100), Nominal, 0);
        Assert.InRange(p(GlidepathCall.WellAbove, CallTrend.Worsening, -1300), Nominal - 700, 0);
    }

    [Theory]
    [InlineData("ABOVE GLIDEPATH", true)]
    [InlineData("ON GLIDEPATH", true)]
    [InlineData("BELOW GLIDEPATH", false)] // level until they meet it
    public void A_level_pilot_told_on_or_above_the_glidepath_starts_down(string call, bool descends)
    {
        var (sim, ac) = Arrival();
        Run(sim, 2);
        Assert.False(ac.Descending);
        double vs = Say(sim, ac, call);
        Assert.Equal(descends, ac.Descending);
        if (descends) Assert.True(vs < -500, $"{vs:0}"); else Assert.InRange(vs, -50, 50);
    }

    [Fact]
    public void Lost_comms_instructions_are_not_a_missed_approach()
    {
        var t = Phraseology.Parse("IF NO TRANSMISSIONS ARE RECEIVED FOR ONE MINUTE IN THE PATTERN OR FIVE SECONDS ON FINAL APPROACH, "
            + "ATTEMPT CONTACT WITH TOWER AND PROCEED VFR. IF UNABLE, CLIMB AND MAINTAIN 2000 AND EXECUTE MISSED APPROACH");
        Assert.True(t.LostCommsInstructions);
        Assert.False(t.MissedApproach);
        var (sim, ac) = Arrival();
        ac.Receive(t, sim.SimTime, 243, 700);
        Run(sim, 10);
        Assert.False(ac.GoingAround);
        Assert.True(Phraseology.Parse("EXECUTE MISSED APPROACH").MissedApproach);
    }

    [Fact]
    public void Repeated_calls_while_still_high_keep_adding_correction()
    {
        var (sim, ac) = Arrival();
        Say(sim, ac, "BEGIN DESCENT");
        double first = Say(sim, ac, "ABOVE GLIDEPATH");
        double second = Say(sim, ac, "ABOVE GLIDEPATH AND HOLDING");
        Assert.True(second < first - 60, $"{first:0} then {second:0}");
    }
}
