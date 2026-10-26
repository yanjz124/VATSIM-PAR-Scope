using PARScope.Core.Par;

namespace PARScope.Core.Tests;

public class PublishedParTests
{
    [Fact]
    public void Miramar_and_cherry_point_have_their_published_par()
    {
        var nkx = PublishedPar.Find("KNKX", "24R");
        Assert.NotNull(nkx);
        Assert.Equal((3.2, 57, 1130, 897, 422), (nkx.GlidepathDeg, nkx.TchFt, nkx.RpiFt, nkx.DecisionAltitudeFt, nkx.HatFt));
        Assert.Same(nkx, PublishedPar.Find("NKX", "24R"));               // FAA LID
        Assert.Equal(55, PublishedPar.Find("KNKT", "5R")!.TchFt);         // "5R" → "05R"
        Assert.Equal(3.5, PublishedPar.Find("KNFL", "31L")!.GlidepathDeg);
        Assert.Equal(2.5, PublishedPar.Find("KPAM", "14L")!.GlidepathDeg);
        Assert.Null(PublishedPar.Find("KLAX", "24R"));
        Assert.Equal(102, PublishedPar.All.Count);
    }

    [Fact]
    public void Every_published_row_is_physically_consistent()
    {
        foreach (var p in PublishedPar.All)
        {
            double est = p.TchFt / Math.Tan(p.GlidepathDeg * Math.PI / 180);
            Assert.InRange(p.RpiFt, est * 0.75, est * 1.25);
            Assert.InRange(p.HatFt, 100, 500);
            Assert.InRange(p.GlidepathDeg, 2.5, 3.6);
        }
    }

    [Fact]
    public void Geometry_uses_rpi_and_measures_dh_from_the_touchdown_zone()
    {
        var rw = GeometryTests.Runway with { ThresholdElevFt = 460, TdzElevFt = 475 };
        var site = PublishedPar.Apply(GeometryTests.Site, PublishedPar.Find("KNKX", "24R")!);
        var geo = new ParGeometry(rw, site);
        Assert.Equal(1130, geo.GpiFromThresholdFt, 3);
        Assert.Equal(422 + 15, geo.DecisionHeightAboveThresholdFt, 3);
        Assert.Equal((422 + 15) / Math.Tan(3.2 * Math.PI / 180), geo.DecisionHeightRangeFt, 1);

        var noRpi = new ParGeometry(rw, site with { RpiFt = null });
        Assert.Equal(57 / Math.Tan(3.2 * Math.PI / 180), noRpi.GpiFromThresholdFt, 3);
    }
}
