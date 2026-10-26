# vNAS target data over UDP (CRC)

Notes for VATSIM tool developers who want live traffic from a controller's CRC session without opening
a second network connection. vibe PAR's CRC feed is built on this; VATEject or any other tool can use the
same approach. Everything here was measured on 2026-10-26 with **CRC 2.18.3**, logged in as an observer;
vNAS can change it without notice.

## Summary

- CRC talks to vNAS in two ways:
  - **SignalR over TLS** (`wss://<env>.vnas.vatsim.net/hubs/client`), which is opaque to an outside tool.
  - **Display target updates over UDP**, from the vNAS server's port **6809** to a local port CRC opens.
- The UDP datagrams are **plain MessagePack**, with no encryption and no signature. Any program that can see
  the packets can read them.
- The rates measured per aircraft are:

| Stream | Rate | Altitude |
|---|---|---|
| STARS targets | **1 Hz** | Mode C, hundreds of feet |
| Tower Cab aircraft | **1 Hz** while moving | **true altitude to 0.01 ft** |
| ASDE-X | 1 Hz | — |
| ERAM targets | 12 s | — |

- vNAS only sends what CRC subscribes to: the STARS, ERAM, ASDE-X and Tower Cab displays CRC has open. In the
  capture, a Tower Cab window at DCA carried airborne aircraft out to about 70 NM.
- CRC owns the UDP port, so a second program can't bind it. It has to read the datagrams by packet capture.
  That is read-only: nothing is sent, and CRC and vNAS are not touched.

## Capturing

| Method | Needs | Notes |
|---|---|---|
| Raw socket, `SIO_RCVALL` (`IOControlCode.ReceiveAll`) on each local IPv4 address | Administrator | Built into Windows. Receives every IPv4 packet on the interface; keep UDP with source port 6809. |
| Npcap / libpcap (for example SharpPcap) | Npcap installed (admin once) | Filter `udp and src port 6809`. With Npcap's default install (not "admin only"), no elevation is needed afterwards. |
| `pktmon` (Windows) | Administrator | For one-off captures. `pktmon start --capture --comp nics --pkt-size 0`, then `pktmon etl2pcap`. |

To find the server, use the source address of the UDP traffic, or `Get-NetTCPConnection -OwningProcess <CRC pid>`.
The UDP comes from the same host as one of CRC's TLS connections. CRC's local ports are in
`Get-NetUDPEndpoint -OwningProcess <pid>`.

## Datagram format

Each datagram is one MessagePack value:

```
[kind, [topic, [unionTag, payload]]]
kind     3 = entity update (0 = keepalive, [0, []])
topic    [topicId, facilityId, subset?, sectorId?]   e.g. [22, "PCT", nil, nil]
payload  an array whose layout depends on unionTag (below)
```

Timestamps are MessagePack **timestamp extensions** (ext type −1, 8 bytes): the upper 30 bits are nanoseconds,
the lower 34 bits Unix seconds.

### Topics seen

| Topic | Union tag | Display | Facility example | Rate per aircraft |
|---|---|---|---|---|
| 22 | 22 | STARS targets | PCT | 1.00 s |
| 10 | 24 | Tower Cab aircraft | DCA, RDU, HEF, DOV | ~1.0 s (sparser when stationary) |
| 2 | 3 | ASDE-X targets | ADW, DCA, IAD | 1.0 s |
| 3 | 6 | ASDE-X tracks | ADW, DCA, IAD | ~1–3 s |
| 12 | 17 | ERAM aircraft (type, destination) | ZDC | 1.0 s |
| 14 | 12 | ERAM targets | ZDC | 12 s |
| 15 | 13 | ERAM target history | ZDC | 12 s |
| 16 | 14 | ERAM data blocks | ZDC | ~12 s |

### STARS target (topic 22, tag 22)

Fields 0–16 were confirmed by matching 12 aircraft against ERAM (beacon, altitude, ground speed and track all
agree). Later fields are unidentified.

| # | Field | Example |
|---|---|---|
| 0 | track id (ULID) | `"01M5WVVSH4QR3144A3ADD9FY03"` |
| 1 | callsign | `"AAL1062"` |
| 2 | bool (unknown) | `true` |
| 3 | trail: 10 × `[timestamp, [lat, lon]]`, newest first | |
| 4 | bool (unknown) | `false` |
| 5 | position `[lat, lon]` (degrees, float64) | `[37.91599, -75.56221]` |
| 6 | **Mode C altitude, hundreds of feet** (pressure altitude) | `370` = FL370 |
| 10 | beacon code (octal digits as an integer) | `1733` |
| 15 | ground speed, knots | `462` |
| 16 | track, degrees true | `34.68` |

For true altitude, correct Mode C with the altimeter setting: `pressure + (altimeter inHg − 29.92) × 1000`.

### Tower Cab aircraft (topic 10, tag 24)

| # | Field | Example |
|---|---|---|
| 0 | callsign | `"N4722P"` |
| 1 | int (unknown, usually 1) | `1` |
| 2 | aircraft type | `"C172"` |
| 3 | on ground | `false` |
| 4 | position `[lat, lon]` | `[39.58763, -75.56766]` |
| 5 | heading, degrees | `55.34` |
| 6 | unknown (nil or a number) | |
| 7 | **altitude, feet, 0.01 ft resolution** (true altitude, inferred) | `3463.0` |
| 8 | second altitude, feet; matches Mode C, likely pressure altitude (inferred) | `3531.6` |

An aircraft that stops changing is sent less often. YAAT notes a full resend every 10 s, and that new aircraft
arrive over SignalR first, so a UDP-only reader picks an aircraft up on its next UDP update.

### Other streams (layouts partly inferred)

- **ERAM target (14/12):** `[callsign, callsign, beacon, altitude100, ?, [lat, lon], groundSpeed, track, ?, bool, bool]`
- **ERAM history (15/13):** `[id, callsign, timestamp, beacon, [lat, lon], ?, ?, ?]`
- **ERAM aircraft (12/17):** `[callsign, ?, type, bool, [lat, lon], heading, track, ?, ?, destination]`
- **ASDE-X target (2/3):** `[callsign, [lat, lon], trail of 7 [lat, lon], heading, ?, ?, bool]`

## Test vectors

These are real datagrams (hex) from the capture, both for the same aircraft at the same moment.

Tower Cab, N4722P, C172, 39.58763 −75.56766, 3,463 ft:

```
920392940aa3444f56c0c0921899a64e343732325001a443313732c292cb4043cb3775b81301cbc052e4548a9bcfd5cb405b558000000000cb405c9dc73f9b7f44cb40ab0e0000000000cb40ab973333333333
```

STARS, N4722P, Mode C 035, beacon 1200, 124 kt (truncated after the fields above; the full vector is in
`tests/PARScope.Core.Tests/VnasUdpTests.cs`):

```
9203929416a3504354c0c09216dc0026ba30314d35575656534835564a4a5a4b54304a325a425439565248a64e3437323250c39a…
```

vibe PAR's decoder is `src/PARScope.Core/Traffic/VnasUdpDecoder.cs`. It includes a dependency-free MessagePack
reader of about 80 lines. The capture code is `src/PARScope.App/Services/CrcFeed.cs`, with raw socket and Npcap.

## Caveats

- **Unofficial.** This is not a published vNAS interface. Field positions were inferred from captures and can
  change with any vNAS or CRC update, so treat unknown layouts defensively.
- **Covers only what CRC shows.** Data arrives only for the facilities and displays the controller has open.
- **Terms.** Reading data your own client receives is passive, but it is still unsanctioned use of vNAS
  data. Check VATSIM's and vNAS's terms before distributing anything built on it.

## Sources

- Captures with Windows `pktmon` of CRC 2.18.3, observer session, 2026-10-26, about 4 minutes.
- [leftos/yaat](https://github.com/leftos/yaat) (MIT): `docs/crc-protocol-support.md` and
  `docs/crc-display-state.md`, which describe the hub methods and the Tower Cab UDP `EntityUpdate` (union tag 24).
