using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

public class PhraseologyTests
{
    [Fact]
    public void Spoken_heading_and_glidepath_call()
    {
        var t = Phraseology.Parse("turn right heading two four four slightly below glidepath and coming up");
        Assert.Equal(244, t.HeadingDeg);
        Assert.Equal(GlidepathCall.SlightlyBelow, t.Glidepath);
        Assert.Equal(CallTrend.Correcting, t.GlidepathTrend);
    }

    [Fact]
    public void Typed_numerals_and_niner()
    {
        Assert.Equal(90, Phraseology.Parse("HEADING 090, ON GLIDEPATH, ON COURSE").HeadingDeg);
        Assert.Equal(290, Phraseology.Parse("heading two niner zero").HeadingDeg);
        Assert.Equal(GlidepathCall.On, Phraseology.Parse("heading 090, on glide path").Glidepath);
    }

    [Theory]
    [InlineData("well above glidepath", GlidepathCall.WellAbove, CallTrend.None)]
    [InlineData("above glidepath and holding", GlidepathCall.Above, CallTrend.Holding)]
    [InlineData("going below glidepath", GlidepathCall.SlightlyBelow, CallTrend.Worsening)]
    [InlineData("below glidepath and going below rapidly", GlidepathCall.Below, CallTrend.Rapidly)]
    public void Glidepath_calls(string text, GlidepathCall call, CallTrend trend)
    {
        var t = Phraseology.Parse(text);
        Assert.Equal(call, t.Glidepath);
        Assert.Equal(trend, t.GlidepathTrend);
    }

    [Fact]
    public void Descent_missed_approach_and_noise()
    {
        Assert.True(Phraseology.Parse("approaching glidepath, begin descent").BeginDescent);
        var safety = Phraseology.Parse("too low for safe approach, if runway or approach lights not in sight execute missed approach");
        Assert.True(safety.MissedApproach);
        Assert.False(Phraseology.Parse("your missed approach procedure is climb to 2000").MissedApproach);
        Assert.True(Phraseology.Parse("four miles from touchdown").IsEmpty);
        Assert.Equal("SLIGHTLY LEFT OF COURSE", Phraseology.Parse("slightly left of course and correcting").CourseCall);
    }
}

public class FacilityProcedureTests
{
    [Fact]
    public void Waveoff_is_a_missed_approach()
    {
        Assert.True(Phraseology.Parse("tower directs waveoff, climb and maintain 1600").MissedApproach);
        Assert.True(Phraseology.Parse("wave off").MissedApproach);
    }

    [Fact]
    public void Fpn63_mti_limits_range_to_15nm()
    {
        var p = PARScope.Core.Radar.RadarPreset.Fpn63;
        Assert.Equal([10.0, 20.0], p.ScalesFor(mti: false));
        Assert.Equal([10.0, 15.0], p.ScalesFor(mti: true));
        Assert.Equal(15, p.EffectiveMaxRangeNm(true));
    }

    [Fact]
    public void Position_calls_on_short_final()
    {
        var geo = new PARScope.Core.Par.ParGeometry(GeometryTests.Runway, GeometryTests.Site);
        Assert.Equal("OVER LANDING THRESHOLD", PARScope.Core.Par.AdvisoryEngine.PositionCall(geo.ThresholdRangeFt, geo));
        Assert.Equal("OVER APPROACH LIGHTS", PARScope.Core.Par.AdvisoryEngine.PositionCall(0.5 * PARScope.Core.Geo.Units.FeetPerNm, geo));
        Assert.Equal("AT DECISION ALTITUDE", PARScope.Core.Par.AdvisoryEngine.PositionCall(geo.DecisionHeightRangeFt, geo));
        Assert.Equal("3 MILES FROM TOUCHDOWN", PARScope.Core.Par.AdvisoryEngine.PositionCall(3.05 * PARScope.Core.Geo.Units.FeetPerNm, geo));
        Assert.Null(PARScope.Core.Par.AdvisoryEngine.PositionCall(3.5 * PARScope.Core.Geo.Units.FeetPerNm, geo));
    }
}
