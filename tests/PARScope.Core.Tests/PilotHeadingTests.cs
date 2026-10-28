using PARScope.Core.Par;
using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

/// <summary>Sloppy pilots wobble around an assigned heading but do not settle on a steady error.</summary>
public class PilotHeadingTests
{
    [Fact]
    public void A_sloppy_pilot_averages_out_to_the_assigned_heading()
    {
        for (int i = 0; i < 4; i++)
        {
            var sim = new TrainingTraffic { Geometry = new ParGeometry(GeometryTests.Runway, GeometryTests.Site) };
            var ac = sim.Spawn(new SpawnOptions { RangeNm = 25, HeightOffsetFt = -800, SpeedKt = 130, Skill = 0.2, Mode = PilotMode.ControllerDirected });
            double sum = 0, worst = 0;
            int n = 0;
            for (double t = 0.1; t <= 400; t += 0.1)
            {
                sim.Step(t).ToList();
                if (t < 40) continue;
                double err = PARScope.Core.Geo.Units.Norm180(ac.HeadingRelDeg - ac.CommandedHeadingRelDeg);
                sum += err; n++;
                worst = Math.Max(worst, Math.Abs(err));
            }
            Assert.InRange(sum / n, -1.5, 1.5); // no standing error (the old random walk sat at 3–4°)
            Assert.True(worst <= 5, $"worst {worst:0.0}°");
        }
    }
}
