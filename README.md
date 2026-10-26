# PAR Scope for VATSIM

A Precision Approach Radar (PAR) final-controller display for VATSIM, modelled on real military GCA
systems (AN/FPN-68, FPN-63, GPN-22, FPN-67, MPN-25). It shows network traffic through vPilot,
including VATSIM Velocity 5 Hz fast positions, and has built-in training traffic you can talk down
by voice.

## What's in the box

| | |
|---|---|
| `PARScope.exe` | The scope: AZ-EL indicator, ASR plan view, METAR, training traffic. Windows 10/11; no .NET install needed. |
| `Plugin\RadarStreamerPlugin.dll` + `.ini` | vPilot plugin that streams the traffic vPilot sees to the scope over UDP. |
| `TrafficProbe.exe` | Optional diagnostic that shows the fast/slow update rate you are actually getting, per aircraft. |

## Install

1. Close vPilot.
2. Copy `RadarStreamerPlugin.dll` and `RadarStreamerPlugin.ini` into `%LOCALAPPDATA%\vPilot\Plugins\`.
   Windows may block an unsigned DLL. If so, right-click it, open Properties, and tick **Unblock**.
3. Start vPilot and connect (as pilot, observer or tower view). Type `.debug` in vPilot to see the
   plugin's messages. With Velocity data flowing it reports "Streaming … with Velocity fast positions".
4. Run `PARScope.exe`. The first start downloads the airport database (OurAirports worldwide plus the
   current FAA NASR cycle). Type an airport (`KNKX`, `NKX`, `EGQS`…) and pick the runway.

The status bar shows the link: **green** means fast positions are streaming, **amber** means slow
5-second updates only, **grey** means nothing has been received.

## Using it alongside CRC

CRC has no local data output, so the scope gets live traffic through vPilot's **tower view**
connection. VATSIM Code of Conduct C7(b) allows a controller one extra tower-view observer connection,
`(CID)_TV`, from an approved pilot client.

1. Connect to your position in CRC.
2. Start your simulator and vPilot (with the plugin installed). In vPilot, type `.towerview`.
3. Run `PARScope.exe`. The status bar shows **vPilot tower view**.

If no plugin is streaming, the scope falls back to the public VATSIM data feed. It uses no extra
connection, but it only updates about every 15 s. The status bar shows **VATSIM data feed (15 s)**
while that's active. It's fine for following traffic, but not for precision guidance.

Whether Velocity fast positions reach a tower-view connection, and over what range, depends on the
VATSIM servers. Run `TrafficProbe.exe` (with the scope closed) to see the rates you actually get.

## Using the scope

* **Elevation** (top) and **azimuth** (bottom) share one range axis, with touchdown on the left.
  * Elevation shows the glidepath cursor, the lower safe limit (TERPS obstacle clearance surface,
    dashed amber), the DH marker, and the threshold and touchdown marks.
  * Azimuth shows the course line, the 2° and 5° lines from touchdown, and the runway.
* **Radar model** selector: each preset changes the scan rate, coverage, accuracy and display style.
  The FPN-63 preset is a monochrome phosphor indicator with afterglow and no tracker. The FPN-68
  preset is a colour digital display with tracks, history dots and data blocks.
* **Range** 5/10/20 NM (PgUp/PgDn). **Log** compresses the range axis. **MTI** rejects fixed returns
  and targets slower than 40 kt. **Clutter** shows ground returns from the approach lights and terrain
  near touchdown.
* Click a target, or press **Tab**, to select it. The side panel shows its range from touchdown,
  feet above or below the glidepath, feet left or right of course, and the recommended rate of descent.
* **Approach and site setup** sets the glidepath angle, TCH, DH and antenna position for each runway.
  Defaults come from the published data.

Everything you see comes from the simulated radar, not directly from the network:

* Each target is painted only when the simulated beam crosses it.
* Each paint has realistic measurement noise: one third of the ICAO Annex 10 accuracy limits, plus
  ground multipath near the horizon.
* Between network updates, aircraft are dead-reckoned and smoothed, so a 5-second update rate doesn't
  make targets jump.

See [docs/PAR-RESEARCH.md](docs/PAR-RESEARCH.md) for the sources behind each choice.

## Training traffic

**Training traffic › Spawn arrival** adds a simulated aircraft on final. There are two pilot types:

* **Controller-directed** pilots hold heading and altitude until you talk to them.
* **Pilot flies the approach** pilots fly an ILS-style approach with human-like errors.

Talk to them with **push-to-talk**: hold **Space** and speak standard phraseology, e.g. "turn left
heading two four zero, slightly above glidepath and coming down". This uses the offline Windows speech
recogniser (English) with a PAR grammar. You can also type the same words in the transmission box.

Keyboard shortcuts: **D** begin descent, **←/→** heading ±1° (Shift ±5°).

**Suggested calls** (the computed 7110.65 transmission) are a training aid. They are on for training
traffic and off for VATSIM traffic unless you enable them.

Run `PARScope.exe --demo NKX 24R` to open straight into a runway with three self-flying arrivals.

## VATSIM Velocity (5 Hz) data

vPilot's public plugin API only raises an update every ~5 seconds, even though vPilot receives
Velocity fast positions at up to 5 Hz. The plugin taps vPilot's internal event bus to stream those fast
positions, with their velocity vectors. It is read-only and never talks to the network. If a vPilot
update breaks it, the plugin falls back to the 5-second events automatically. Details:
[docs/VELOCITY.md](docs/VELOCITY.md).

The VATSIM server only sends fast positions when a client nearby wants them. To see what you actually
get, close the scope and run `TrafficProbe.exe`.

## Building from source

Requires the .NET 10 SDK and vPilot installed. The plugin compiles against vPilot's own assemblies;
set `VPILOT_DIR` if vPilot is not in `%LOCALAPPDATA%\vPilot`.

```powershell
dotnet build VATSIM-PAR-Scope.sln
dotnet test tests\PARScope.Core.Tests
.\scripts\build-release.ps1 -Version 2.0.0   # release zip in .\release
```

| Project | |
|---|---|
| `src/PARScope.Core` | Geodesy (WGS-84/ECEF), traffic protocol and tracker, PAR geometry and TERPS surfaces, radar and ASR models, advisory and phraseology, airport data (NASR and OurAirports), METAR, training traffic. No UI dependencies; unit tested. |
| `src/PARScope.App` | WPF on .NET 10, using [WPF UI](https://github.com/lepoco/wpfui) (Fluent design) and SkiaSharp for the scopes. |
| `src/RadarStreamerPlugin` | vPilot plugin (.NET Framework 4.7.2, since vPilot loads it in-process). |
| `tools/TrafficProbe` | Rate and velocity diagnostic. |

The plugin and scope talk NDJSON over UDP port 49090 (protocol v2, documented in `Plugin.cs`). The
scope still accepts the v1 messages from older plugin builds.

## Licence

MIT.
