using System.IO.Compression;
using System.Text;
using PARScope.Core.Data;

namespace PARScope.Core.Tests;

/// <summary>"(facility) final" uses the approach control's radio call from FAA NASR, not the airport's name.</summary>
public class FacilityCallTests
{
    private static AirportRecord Apt(string id, string name, string? apch = null, string? twr = null) =>
        new(id, null, name, 0, 0, 0, [], "NASR", apch, twr);

    [Fact]
    public void Approach_call_wins_then_tower_then_the_name()
    {
        Assert.Equal("Potomac", Apt("DCA", "RONALD REAGAN WASHINGTON NTL", "POTOMAC", "WASHINGTON").FacilityCall);
        Assert.Equal("Cherry Point", Apt("NKT", "CHERRY POINT MCAS (CUNNINGHAM FLD)", "CHERRY POINT").FacilityCall);
        Assert.Equal("Navy North Island", Apt("NZY", "NORTH ISLAND NAS", twr: "NAVY NORTH ISLAND").FacilityCall);
        Assert.Equal("Miramar", Apt("NKX", "MIRAMAR MCAS (JOE FOSS FLD)").FacilityCall);
    }

    [Fact]
    public void Atc_base_calls_are_added_to_nasr_airports()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(zip.CreateEntry("ATC_BASE.csv").Open(), Encoding.Latin1))
        {
            w.WriteLine("\"EFF_DATE\",\"FACILITY_ID\",\"TWR_CALL\",\"PRIMARY_APCH_RADIO_CALL\"");
            w.WriteLine("\"2026/10/01\",\"DCA\",\"WASHINGTON\",\"POTOMAC\"");
            w.WriteLine("\"2026/10/01\",\"DOV\",\"DOVER\",\"DOVER\"");
        }
        ms.Position = 0;
        var calls = NasrLoader.ParseAtcZip(ms);
        var airports = NasrLoader.WithAtc([Apt("DCA", "RONALD REAGAN WASHINGTON NTL"), Apt("XYZ", "SOMEWHERE MUNI")], calls);
        Assert.Equal(("POTOMAC", "WASHINGTON"), (airports[0].ApproachCall, airports[0].TowerCall));
        Assert.Equal("Potomac", airports[0].FacilityCall);
        Assert.Null(airports[1].ApproachCall);
        Assert.Equal("Somewhere", airports[1].FacilityCall);
    }
}
