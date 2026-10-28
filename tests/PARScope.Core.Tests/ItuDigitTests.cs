using PARScope.Core.Sim;

namespace PARScope.Core.Tests;

/// <summary>Radiotelephony digits: "niner", "fife", "tree", and how recognizers spell them.</summary>
public class ItuDigitTests
{
    [Theory]
    [InlineData("TURN LEFT HEADING ZERO NINER ZERO", 90)]
    [InlineData("TURN RIGHT HEADING TREE FIFE ZERO", 350)]
    [InlineData("TURN LEFT HEADING TWO FYFE NYNER", 259)]
    [InlineData("TURN RIGHT HEADING ONE NAINER ZERO", 190)]
    [InlineData("TURN LEFT HEADING 2 FIFE 5", 255)]
    public void Radiotelephony_digits_make_headings(string said, double heading) =>
        Assert.Equal(heading, Phraseology.Parse(said).HeadingDeg);

    [Fact]
    public void Radiotelephony_digits_in_ranges_and_climbs()
    {
        Assert.True(Phraseology.Parse("TREE MILES FROM TOUCHDOWN").RangeInfo);
        Assert.True(Phraseology.Parse("FIFE MILES FROM TOUCHDOWN").RangeInfo);
        Assert.True(Phraseology.Parse("NINER MILES FROM TOUCHDOWN").RangeInfo);
        Assert.Equal(3000, Phraseology.Parse("CLIMB AND MAINTAIN TREE THOUSAND").ClimbToFt);
    }
}
