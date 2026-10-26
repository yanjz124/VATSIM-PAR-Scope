using PARScope.Core.Geo;
using PARScope.Core.Traffic;

namespace PARScope.Core.Tests;

/// <summary>vNAS UDP target datagrams, from a real CRC 2.18.3 capture (2026-10-26), decoded to position reports.</summary>
public class VnasUdpTests
{
    // Tower Cab aircraft (topic 10 "DOV", tag 24): N4722P airborne at 3,463 ft.
    private const string TowerCab =
        "920392940aa3444f56c0c0921899a64e343732325001a443313732c292cb4043cb3775b81301cbc052e4548a9bcfd5cb405b558000000000cb405c9dc73f9b7f44cb40ab0e0000000000cb40ab973333333333";

    // STARS target (topic 22 "PCT", tag 22): the same aircraft, Mode C 035 (3,500 ft), 10-point trail.
    private const string Stars =
        "9203929416a3504354c0c09216dc0026ba30314d35575656534835564a4a5a4b54304a325a425439565248a64e3437323250c39a92d7ff98c653c06adfb10b92cb4043cb46dc5d6388cbc052e46ad2dcb14692d7ff8ff184906adfb10692cb4043cb6ddaceee0fcbc052e4a2b1704ff492d7ff918139906adfb10192cb4043cb94317acc4fcbc052e4da9003eea292d7ff8a16a7306adfb0fc92cb4043cbba8826aa8fcbc052e51244a6223e92d7ff8df001306adfb0f792cb4043cbe08aefb2abcbc052e549f94855da92d7ff8c4082b06adfb0f292cb4043cc0639d5e4a4cbc052e58201cd5f9a92d7ff8a550b106adfb0ed92cb4043cc2b94d94079cbc052e5ba0a52695992d7ff919b36b06adfb0e892cb4043cc504816f007cbc052e5f1bef49cf592d7ff846442c06adfb0e392cb4043cc74538ef34dcbc052e629f16b11c792d7ffd250ff506adfb0de92cb4043cc980b242071cbc052e66223e18698c292cb4043cb3775b81301cbc052e4548a9bcfd523c0c0c2cd04b0c2c200067ccb405ca6dcc5a41d7801c0c2c0c0c0c0c000c2c2c2c2c0c0c09090c09080";

    [Fact]
    public void Tower_cab_aircraft_has_exact_altitude()
    {
        var r = Assert.Single(new VnasUdpDecoder().Decode(Convert.FromHexString(TowerCab), 10));
        Assert.Equal("N4722P", r.Callsign);
        Assert.Equal(ReportSource.Crc, r.Source);
        Assert.Equal("C172", r.TypeCode);
        Assert.Equal(39.58763, r.Position.LatDeg, 4);
        Assert.Equal(-75.56766, r.Position.LonDeg, 4);
        Assert.Equal(3463.0, r.Position.AltFt, 1);
        Assert.Equal(10, r.Time);
    }

    [Fact]
    public void Stars_target_uses_mode_c_corrected_by_the_altimeter()
    {
        var d = new VnasUdpDecoder { AltimeterInHg = 30.07 };
        var r = Assert.Single(d.Decode(Convert.FromHexString(Stars), 10));
        Assert.Equal("N4722P", r.Callsign);
        Assert.Equal(39.58763, r.Position.LatDeg, 4);
        Assert.Equal(3500, r.PressureAltFt);
        Assert.Equal(3650, r.Position.AltFt, 0); // 3,500 + (30.07 − 29.92) × 1,000
        Assert.Equal(124, r.GroundSpeedKt);
        Assert.Equal(1200, r.Beacon);
        Assert.True(r.CoarseAltitude); // 100 ft steps: ASR only
        Assert.False(Assert.Single(new VnasUdpDecoder().Decode(Convert.FromHexString(TowerCab), 10)).CoarseAltitude);
        Assert.InRange(r.HeadingDeg!.Value, 100, 130);
    }

    [Fact]
    public void Tower_cab_takes_precedence_over_stars_for_the_same_aircraft()
    {
        var d = new VnasUdpDecoder();
        Assert.Single(d.Decode(Convert.FromHexString(TowerCab), 10));
        Assert.Empty(d.Decode(Convert.FromHexString(Stars), 11));      // on the Tower Cab stream: STARS skipped
        Assert.Single(d.Decode(Convert.FromHexString(Stars), 14));      // Tower Cab gone quiet: STARS again
    }

    [Fact]
    public void Garbage_and_other_topics_are_ignored()
    {
        var d = new VnasUdpDecoder();
        Assert.Empty(d.Decode([0x92, 0x00, 0x90], 0));                  // keepalive
        Assert.Empty(d.Decode([0xC1, 0xFF], 0));                         // not MessagePack
        Assert.Empty(d.Decode([0x92, 0x03, 0x92, 0x94, 0x0E], 0));       // truncated
        Assert.Equal(2, d.Undecodable);
    }
}
