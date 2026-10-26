using System.Globalization;
using PARScope.Core.Geo;

namespace PARScope.Core.Tests;

public class WmmTests
{
    /// <summary>NOAA's official WMM2025 test values: declination within the rounding of the published 0.01°.</summary>
    [Fact]
    public void Declination_matches_noaa_test_values()
    {
        int n = 0;
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "WMM2025_TestValues.txt")))
        {
            if (line.StartsWith('#') || line.Trim().Length == 0) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            double d = Wmm.DeclinationDeg(f[2], f[3], f[1], f[0]);
            // Published to 0.01°, so allow the rounding.
            Assert.True(Math.Abs(d - f[4]) < 0.0075, $"{f[0]} {f[1]} km {f[2]},{f[3]}: {d:0.000} vs {f[4]}");
            n++;
        }
        Assert.True(n >= 10);
    }
}
