using PARScope.Core.Geo;
using PARScope.Core.Par;
using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

/// <summary>Approach results and grades.</summary>
public class GradingTests
{
    private const string Brief = "THIS WILL BE A P-A-R APPROACH TO RUNWAY 24R. IN THE EVENT OF MISSED APPROACH, CLIMB AND MAINTAIN 2000, FLY RUNWAY HEADING. "
        + "IF NO TRANSMISSIONS ARE RECEIVED FOR ONE MINUTE IN THE PATTERN OR FIVE SECONDS ON FINAL APPROACH, ATTEMPT CONTACT WITH TOWER AND PROCEED VFR. "
        + "IF UNABLE, CLIMB AND MAINTAIN 2000 AND EXECUTE MISSED APPROACH";

    /// <summary>Flies a talk-down from 6 NM; <paramref name="talk"/> sends the controller's calls on final.</summary>
    private static List<string> FlyApproach(bool talk)
    {
        var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
        var ac = sim.Spawn(new SpawnOptions { RangeNm = 6.3, HeightOffsetFt = 0, SpeedKt = 130, Skill = 1, GearDown = true, Mode = PilotMode.ControllerDirected });
        void Say(string s) => ac.Receive(Phraseology.Parse(s), sim.SimTime, 243, 700);
        var results = new List<string>();
        if (talk) { Say(Brief); Say("APPROACHING GLIDEPATH, WHEELS SHOULD BE DOWN"); }
        Say("BEGIN DESCENT");
        int lastMile = 7;
        double lastCall = 0;
        for (double t = 0.1; t <= 400 && sim.Aircraft.Count > 0; t += 0.1)
        {
            sim.Step(t).ToList();
            results.AddRange(sim.TakeResults().Select(r => r.Text));
            if (!talk || ac.Landed || ac.GoingAround) continue;
            double nm = ac.RangeFt / Units.FeetPerNm;
            if ((int)Math.Floor(nm) < lastMile && nm > 0.5) { lastMile = (int)Math.Floor(nm); Say($"{Math.Round(nm)} MILES FROM TOUCHDOWN"); lastCall = t; }
            if (nm < 4 && !ac.ClearedToLand) { Say("RUNWAY 24R, CLEARED TO LAND"); lastCall = t; }
            if (t - lastCall >= 4) { Say("ON GLIDEPATH, ON COURSE"); lastCall = t; }
        }
        return results;
    }

    [Fact]
    public void A_well_run_talk_down_lands_and_grades_well()
    {
        var results = FlyApproach(talk: true);
        Assert.Contains(results, r => r.StartsWith("landed,") && r.Contains("ft past the threshold"));
        var grade = Assert.Single(results, r => r.StartsWith("GRADE"));
        Assert.Matches(@"^GRADE [AB] ", grade);
    }

    [Fact]
    public void A_silent_talk_down_fails_with_the_reasons()
    {
        var results = FlyApproach(talk: false);
        Assert.DoesNotContain(results, r => r.StartsWith("landed"));
        var grade = Assert.Single(results, r => r.StartsWith("GRADE"));
        Assert.Matches(@"^GRADE [DF] ", grade);
        Assert.Contains("longest silence", grade);
        Assert.Contains("no landing clearance", grade);
        Assert.Contains("no missed approach instructions", grade);
    }

    /// <summary>Feeds a grader a straight final from <paramref name="fromNm"/> to <paramref name="toNm"/> at 130 kt.</summary>
    private static string Grade(double fromNm, double toNm, Action<ApproachGrader, double, double> each)
    {
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        var g = new ApproachGrader();
        double speed = 130 * Units.FeetPerNm / 3600;
        for (double t = 0; ; t += 0.5)
        {
            double r = fromNm * Units.FeetPerNm - speed * t;
            if (r < toNm * Units.FeetPerNm) break;
            each(g, t, r);
            g.Sample(t, geo, r, 0, geo.GlidepathHeightFt(r));
        }
        return g.Finish(0, "went around", gearReportedDown: true)!;
    }

    [Fact]
    public void A_transmission_not_understood_still_breaks_the_silence()
    {
        var grade = Grade(3.2, 1.0, (g, t, r) => { if (t % 4 == 0) g.Keyed(t); });
        Assert.DoesNotContain("longest silence", grade);
        Assert.Contains("not understood", grade);
    }

    [Fact]
    public void Range_calls_are_owed_only_for_miles_the_aircraft_reached()
    {
        var said = new HashSet<int>();
        var grade = Grade(3.2, 1.5, (g, t, r) =>
        {
            int mile = (int)Math.Round(r / Units.FeetPerNm);
            if (Math.Abs(r / Units.FeetPerNm - mile) < 0.05 && said.Add(mile)) g.Heard(Phraseology.Parse($"{mile} MILES FROM TOUCHDOWN"), t, r);
            else if (t % 4 == 0) g.Keyed(t);
        });
        Assert.DoesNotContain("range given", grade); // 3 and 2 called; the go-around came before the 1-mile call
    }

    [Fact]
    public void Time_spent_talking_is_not_silence()
    {
        // Two transmissions on final, each 4 s long, the second starting 2 s after the first ended.
        var geo = new ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        var g = new ApproachGrader();
        var on = Phraseology.Parse("ON GLIDEPATH, ON COURSE");
        for (double t = 0; t <= 20; t += 0.5)
        {
            double r = (5 - t * 0.04) * Units.FeetPerNm;
            g.Sample(t, geo, r, 0, geo.GlidepathHeightFt(r));
            if (t == 4) g.Heard(on, 4, r, startedAt: 0);
            if (t == 10) g.Heard(on, 10, r, startedAt: 6);
            if (t == 14) g.Heard(on, 14, r, startedAt: 10);
            if (t == 18) g.Heard(on, 18, r, startedAt: 14);
        }
        var line = g.Finish(10, "landed", gearReportedDown: true);
        Assert.NotNull(line);
        Assert.DoesNotContain("longest silence", line);
    }}
