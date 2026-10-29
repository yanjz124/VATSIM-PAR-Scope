using PARScope.Core.Par;
using PARScope.Core.Radar;
using PARScope.Core.Sim;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

/// <summary>AN/SPN-46 PALS: a lock-on tracker, and the carrier (CCA) wording accepted alongside the PAR wording.</summary>
public class Spn46Tests
{
    private static (TrainingTraffic Sim, TrackStore Store, RadarSimulator Radar) Setup()
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        var sim = new TrainingTraffic(1) { Geometry = geo };
        return (sim, new TrackStore(), new RadarSimulator(RadarPreset.Spn46, geo, seed: 1) { ClutterEnabled = false });
    }

    private static void Run(TrainingTraffic sim, TrackStore store, RadarSimulator radar, double from, double to)
    {
        for (double t = from; t <= to; t += 0.1)
        {
            foreach (var m in sim.Step(t)) store.Apply(m);
            radar.Advance(t, store.Snapshot(t));
        }
    }

    [Fact]
    public void Locks_on_inside_eight_miles_two_aircraft_at_a_time()
    {
        var (sim, store, radar) = Setup();
        foreach (var (cs, nm) in new[] { ("VV1001", 4.0), ("VV1002", 6.0), ("VV1003", 7.0), ("VV1004", 9.5) })
            sim.Spawn(new SpawnOptions { Callsign = cs, RangeNm = nm, Mode = PilotMode.SelfFlying, SpeedKt = 140 });
        Run(sim, store, radar, 0, 5);

        // Two channels: two of the three inside 8 miles are locked; the one at 9.5 miles is not seen at all.
        Assert.Equal(2, radar.Locked.Count);
        Assert.DoesNotContain("VV1004", radar.Locked);
        Assert.False(radar.Tracks.ContainsKey("VV1004"));
        Assert.All(radar.Tracks.Keys, k => Assert.Contains(k, radar.Locked));
    }

    [Fact]
    public void Holds_the_lock_and_frees_the_channel_when_an_aircraft_goes()
    {
        var (sim, store, radar) = Setup();
        sim.Spawn(new SpawnOptions { Callsign = "VV1001", RangeNm = 4, Mode = PilotMode.SelfFlying, SpeedKt = 140 });
        sim.Spawn(new SpawnOptions { Callsign = "VV1002", RangeNm = 5, Mode = PilotMode.SelfFlying, SpeedKt = 140 });
        sim.Spawn(new SpawnOptions { Callsign = "VV1003", RangeNm = 6, Mode = PilotMode.SelfFlying, SpeedKt = 140 });
        Run(sim, store, radar, 0, 5);
        Assert.Contains("VV1001", radar.Locked);
        string waiting = new[] { "VV1002", "VV1003" }.Single(c => !radar.Locked.Contains(c));

        radar.Drop("VV1001");
        sim.Remove("VV1001");
        store.Apply(new TargetRemoved("VV1001"));
        Run(sim, store, radar, 5.1, 7);
        Assert.Contains(waiting, radar.Locked);
    }

    [Theory]
    [InlineData("Navy three zero one, right five", 5)]
    [InlineData("three zero one, left ten", -10)]
    [InlineData("left five, slightly above glidepath", -5)]
    public void Soft_headings_are_turns_by_that_much(string call, double by) =>
        Assert.Equal(by, Phraseology.Parse(call).TurnByDeg);

    [Theory]
    [InlineData("runway three two left, cleared to land")]
    [InlineData("slightly left of course, five miles from touchdown")]
    [InlineData("left five miles from touchdown")]
    [InlineData("turn right heading three three zero")]
    public void Runways_ranges_and_headings_are_not_soft_headings(string call) =>
        Assert.Null(Phraseology.Parse(call).TurnByDeg);

    [Fact]
    public void Carrier_words_mean_the_same_as_par_words()
    {
        var on = Phraseology.Parse("on glide slope, on centerline");
        Assert.Equal(GlidepathCall.On, on.Glidepath);
        Assert.Equal("ON COURSE", on.CourseCall);
        Assert.Equal("SLIGHTLY LEFT OF COURSE", Phraseology.Parse("slightly left of centerline").CourseCall);
        Assert.Equal(GlidepathCall.SlightlyAbove, Phraseology.Parse("slightly above tipover").Glidepath);
        Assert.True(Phraseology.Parse("two zero one lock-on, five miles").RangeInfo);
        Assert.True(Phraseology.Parse("at minimums").RangeInfo);
    }
}
