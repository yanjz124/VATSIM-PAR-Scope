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

    [Theory]
    // From a real session.
    [InlineData("CONVOY FOUR SEVEN TURN RIGHT HEADING ZERO NINE OR ZERO.", 90)]
    [InlineData("HEADING ZERO MINOR ZERO.", 90)]
    public void Niner_as_whisper_writes_it(string said, double heading) =>
        Assert.Equal(heading, Phraseology.Parse(said).HeadingDeg);

    [Theory]
    [InlineData("SEVEN MILES FROM TUXDOWNE.")]
    [InlineData("SIX MILES FROM TUCHAHOU.")]
    [InlineData("SEVEN MILES FROM A TOUCHDOWN.")]
    public void Range_calls_with_a_mangled_touchdown(string said) => Assert.True(Phraseology.Parse(said).RangeInfo, said);

    [Theory]
    [InlineData("WILD LEFT THE COURSE IN CORRECTING.", "WELL LEFT OF COURSE")]
    [InlineData("WHILE RED COURSE AND CORRECTING SLOWLY.", "WELL RIGHT OF COURSE")]
    [InlineData("WELL LEFT THE COURSE IN CORRECTING.", "WELL LEFT OF COURSE")]
    public void Well_left_and_right_as_whisper_writes_them(string said, string course) =>
        Assert.Equal(course, Phraseology.Parse(said).CourseCall);

    [Fact]
    public void Degrees_as_doers() => Assert.Equal(-2, Phraseology.Parse("TURN TWO DOERS LEFT.").TurnByDeg);
}
