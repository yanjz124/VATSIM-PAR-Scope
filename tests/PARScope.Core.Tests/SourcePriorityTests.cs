using PARScope.Core.Geo;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

/// <summary>When several sources deliver the same aircraft, the user's priority decides; the next takes over when it stops.</summary>
public class SourcePriorityTests
{
    private static readonly GeoPoint CrcPos = new(38.90, -77.00, 1000), VPilotPos = new(38.91, -77.00, 1000);

    private static PositionReport Crc(double t, bool coarse = false) => new("AAL1", t, ReportSource.Crc, CrcPos, CoarseAltitude: coarse);
    private static PositionReport VPilot(double t) => new("AAL1", t, ReportSource.Slow, VPilotPos);

    [Fact]
    public void Default_priority_prefers_crc_over_vpilot_updates()
    {
        var store = new TrackStore();
        for (int t = 0; t <= 10; t++) { store.Apply(Crc(t)); if (t % 5 == 0) store.Apply(VPilot(t + 0.5)); }
        var tr = Assert.Single(store.Snapshot(10));
        Assert.Equal(ReportSource.Crc, tr.LastSource);
        Assert.Equal(CrcPos.LatDeg, tr.GeoAt(10).LatDeg, 3);
    }

    [Fact]
    public void Chosen_priority_wins_and_the_next_source_takes_over_when_it_stops()
    {
        var store = new TrackStore { Priority = [SourceGroup.VPilot, SourceGroup.Crc, SourceGroup.Feed] };
        for (int t = 0; t <= 10; t++) { store.Apply(Crc(t)); if (t % 5 == 0) store.Apply(VPilot(t + 0.5)); }
        Assert.Equal(ReportSource.Slow, Assert.Single(store.Snapshot(10.5)).LastSource);

        // vPilot goes quiet: after its 12 s freshness CRC is used again.
        for (int t = 11; t <= 25; t++) store.Apply(Crc(t));
        Assert.Equal(ReportSource.Crc, Assert.Single(store.Snapshot(25)).LastSource);
    }

    [Fact]
    public void Coarse_stars_data_never_outranks_an_exact_source()
    {
        var store = new TrackStore(); // CRC first, but this CRC data is STARS-only
        for (int t = 0; t <= 10; t++) { store.Apply(Crc(t, coarse: true)); if (t % 5 == 0) store.Apply(VPilot(t + 0.5)); }
        var tr = Assert.Single(store.Snapshot(10.5));
        Assert.Equal(ReportSource.Slow, tr.LastSource);
        Assert.False(tr.CoarseAltitude);
    }
}
