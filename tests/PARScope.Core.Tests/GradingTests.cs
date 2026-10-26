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
}
