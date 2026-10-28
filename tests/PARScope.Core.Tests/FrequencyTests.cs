using PARScope.Core.Data;
using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

/// <summary>Controller frequencies from NASR FRQ.csv.</summary>
public class FrequencyTests
{
    // Lemoore (NLC), as NASR lists it: tower UHF split by runway, approach split north and south.
    private static readonly Frequency[] Lemoore =
    [
        new(128.3, "LCL", true), new(340.2, "LCL", true, "RWY 14L, 32L"), new(360.2, "LCL", true, "RWY 14R, 32R"),
        new(121.65, "GND", true), new(305.2, "GND", true),
        new(118.15, "APCH", true, "SOUTH"), new(124.1, "APCH", true, "NORTH"), new(269.025, "APCH", true),
    ];

    [Fact]
    public void Military_aircraft_get_UHF_and_civil_aircraft_VHF()
    {
        Assert.Equal(340.2, Frequencies.Pick(Lemoore, "LCL", military: true, runwayId: "32L"));
        Assert.Equal(360.2, Frequencies.Pick(Lemoore, "LCL", military: true, runwayId: "32R"));
        Assert.Equal(128.3, Frequencies.Pick(Lemoore, "LCL", military: false, runwayId: "32L"));
        Assert.Equal(305.2, Frequencies.Pick(Lemoore, "GND", military: true));
        Assert.Equal(269.025, Frequencies.Pick(Lemoore, "APCH", military: true));
    }

    [Fact]
    public void Sectors_are_picked_by_the_side_the_aircraft_is_on()
    {
        Assert.Equal(124.1, Frequencies.Pick(Lemoore, "APCH", military: false, bearingFromFieldDeg: 340));
        Assert.Equal(118.15, Frequencies.Pick(Lemoore, "APCH", military: false, bearingFromFieldDeg: 170));
        Frequency[] norfolk = [new(118.9, "APCH", true, "010-310"), new(125.7, "APCH", true, "311-009")];
        Assert.Equal(125.7, Frequencies.Pick(norfolk, "APCH", military: false, bearingFromFieldDeg: 330));
        Assert.Equal(125.7, Frequencies.Pick(norfolk, "APCH", military: false, bearingFromFieldDeg: 5));
        Assert.Equal(118.9, Frequencies.Pick(norfolk, "APCH", military: false, bearingFromFieldDeg: 200));
        Assert.Null(Frequencies.Pick(null, "LCL", true));
    }

    [Theory]
    [InlineData("VV1234", true)]
    [InlineData("BNSH21", true)]
    [InlineData("RCH481", true)]
    [InlineData("N123AB", false)]
    [InlineData("UAL1234", false)]
    public void Who_flies_military_radios(string callsign, bool military) =>
        Assert.Equal(military, Frequencies.IsMilitary(callsign));

    [Fact]
    public void Frequencies_are_spoken_digit_by_digit_and_still_understood()
    {
        Assert.Equal("three four zero point two", Frequencies.Spoken(340.2));
        Assert.Equal("two six niner point zero two five", Frequencies.Spoken(269.025));
        var t = Phraseology.Parse("Over landing threshold, on course, contact tower on three four zero point two after landing.");
        Assert.True(t.ContactTower);
        Assert.True(t.RangeInfo);
        var brief = Phraseology.Parse("In the event of missed approach, climb and maintain two thousand two hundred, fly runway heading, contact approach on two six niner point zero two five.");
        Assert.True(brief.MissedApproachInstructions);
        Assert.Equal(2200, brief.ClimbToFt);
        Assert.Null(brief.HeadingDeg);
        Assert.True(Phraseology.Parse("Reach four eight one, contact approach on two six niner point zero two five.").ContactApproach);
    }
}
