# vibe PAR

*Formerly VATSIM PAR Scope. Yes, it is vibe coded.*

A Precision Approach Radar (PAR) final-controller scope for VATSIM, modelled on real US military GCA
systems: AN/FPN-68, FPN-63, GPN-22, FPN-67, MPN-25 and TPN-31 ATNAVICS. It runs alongside CRC and gets
live traffic through vPilot, including VATSIM Velocity 5 Hz fast positions.

It also has a training mode: simulated arrivals you talk down by voice, with pilot, tower and approach
voices, standard phraseology, handoffs and missed approaches. With training off it is a plain scope:
no suggested calls, no simulated anything.

## What's in the release

| | |
|---|---|
| `PARScope-Setup-<version>.exe` | Installer: installs the scope and the vPilot plugin (recommended). |
| `PARScope.exe` | The scope. Windows 10/11, no .NET install needed. |
| `runtimes\win-x64\` | Speech-recognition libraries. Keep this folder next to `PARScope.exe`. |
| `Plugin\RadarStreamerPlugin.dll` + `.ini` | vPilot plugin that streams the traffic vPilot sees to the scope. |
| `TrafficProbe.exe` | Optional: shows the update rate you are actually getting, per aircraft. |

## Install

**With the installer** (recommended): run `PARScope-Setup-<version>.exe`. It installs for your user
only (no admin needed) and lets you change the install folder. It finds vPilot's `Plugins` folder by
itself and installs the plugin there; you can point it elsewhere or skip the plugin. Close vPilot
first. Uninstall from Windows Settings › Apps.

**Or from the zip**:

1. Unzip the release anywhere.
2. Close vPilot. Copy `RadarStreamerPlugin.dll` and `RadarStreamerPlugin.ini` into
   `%LOCALAPPDATA%\vPilot\Plugins\`. If Windows blocks the DLL, right-click it, open **Properties**
   and tick **Unblock**.
3. Run `PARScope.exe`. The first start downloads the airport database (FAA NASR plus OurAirports
   worldwide). Type an airport (`KNKX`, `NKX`, `EGQS`…) and pick the runway.

You only need vPilot for live VATSIM traffic. Training mode works without it.

**Updates**: at start-up the scope checks GitHub for a newer release and offers to install it; it
restarts itself when done. The vPilot plugin is never changed automatically. If a release updates it,
the new copy is in the `Plugin` folder next to the scope. Turn the check off in **Settings › Data**.
The title bar shows the version and the commit it was built from.

## Using it alongside CRC

CRC has no local data output, so the scope gets traffic from vPilot's **tower view** connection. The
VATSIM Code of Conduct (C7(b)) allows a controller one extra tower-view connection, `(CID)_TV`.

1. Connect to your position in CRC.
2. Start your simulator and vPilot with the plugin installed, and type `.towerview` in vPilot.
3. Run `PARScope.exe`. The status bar shows **vPilot tower view**.

The status bar tells you what you are getting:

* **5 Hz data**: Velocity fast positions are arriving.
* **5 s updates only**: vPilot is connected but only the normal slow updates are arriving.
* **VATSIM data feed (15 s)**: no vPilot; positions from the public data feed. Turn this on in
  **Settings › Data**. Fine for watching traffic, too slow for precision guidance.

Hover the status text for details, or open **Settings › Diagnostics** for live per-aircraft rates.

## The scope

* **Elevation** (top) and **azimuth** (bottom) share one range axis, touchdown on the left.
  * Elevation: glidepath, lower safe limit, decision height, threshold and touchdown.
  * Azimuth: final approach course, lateral safe limits and the runway. The antenna sits off to the
    side of the runway and its sector is angled toward the centreline, so the left and right limits
    are not symmetric.
* **Radar model** changes the scan rate, coverage, accuracy and display. FPN-63 is a monochrome
  analog indicator without a tracker; FPN-68 is a colour digital display with tracks and data blocks;
  ATNAVICS copies the USMC expeditionary screen.
* **Range** 5/10/20 NM (PgUp/PgDn). **Log** compresses the range axis the way real PAR displays do:
  miles are spread out close in and bunched up far out. **MTI** hides targets slower than 40 kt.
  **Safety** shows the safe limits. **Scan** shows the antenna sweep (on by default for analog models).
* Targets are painted when the simulated beam crosses them. Between network updates aircraft are
  dead-reckoned, so 5-second updates don't make targets jump. Radar noise and clutter are off unless
  you turn on **Settings › Display › Simulate radar errors**.
* Click a target or press **Tab** to select it. The side panel shows range from touchdown, feet
  above/below the glidepath, feet left/right of course and, in training, the recommended descent rate.
* **ASR**: press **F** (or the **ASR** button) to flip the scope to the surveillance picture, or use
  the pop-out button to put it in its own window, e.g. on another monitor. It shows the PAR coverage
  cone, the extended centreline and, for models whose ASR display had one, a compass rose.
* **METAR** is always in the status bar (VATSIM or NOAA, chosen in **Settings › Data**), and wind and
  altimeter are on the scope.
* **Settings** (gear button): PAR siting for each runway (glidepath, TCH, DH, antenna position — set
  them from the approach plate), display, data sources, training aids, audio and diagnostics.

## Training mode

Turn on **Training** in the toolbar. **Spawn arrival** adds an aircraft about 12–15 NM out at 2,000–3,000
ft, which leaves time for the radio check, the approach brief and missed approach instructions before
the glidepath.

* **Difficulty**: Easy, Normal, Hard, Expert. On Hard and Expert pilots slowly drift off their heading
  and descent rate until corrected, and the wind shifts as they descend.
* **Wind**: arrivals fly in the live METAR surface wind and gusts, so you correct for real drift (turn
  off in **Settings › Training** for a random wind).
* **Controller-directed** pilots hold heading and altitude until told otherwise (a true PAR talk-down).
  **Pilot flies the approach** pilots fly it themselves, with mistakes.
* **Callsigns and types** are realistic: service prefixes (Navy, Marine, Army, Air Force, Reach…) and
  squadron callsigns from the [Military Callsign List](https://henney.com/chm/callsign.htm). Approach
  speeds come from the FAA Aircraft Characteristics Database. You can enter your own callsign and ICAO
  type. Pilots, tower and approach say callsigns as on the radio: "Navy one two three four",
  "Banshee two one", "November one two three Alpha Bravo".

### Talking

Hold the push-to-talk key (**Space** by default; change it in **Settings › Audio**) and speak, or type
in the transmission box. The scope or ASR window needs to have focus. Standard 7110.65 phraseology works, e.g.
"turn left heading two four zero, slightly above glidepath and coming down".

* Pilots read back until you say "do not acknowledge further transmissions", then only answer a radio
  check or a missed approach.
* Glidepath calls change their descent rate; course calls are information only, so correct course
  with headings.
* "In the event of missed approach, climb and maintain two thousand, turn left heading one eight zero"
  is read back and remembered. "Execute missed approach" makes them fly it.

### Coordination

Start a call with **Tower**, **Local**, **Approach** or **Departure** and it goes to that controller
on the interphone instead of the pilot. Wording can be loose ("tower, final, 1234, touch and go"); the
aircraft is picked out by its callsign or number, otherwise the selected one is used.

* **Handoff in**: approach calls "handoff, one four mile final, Navy one two three four, two thousand
  five hundred, heading two four zero". Accept with "Navy one two three four, radar contact" plus your
  operating initials (any you like); approach answers with theirs and switches the pilot, who checks
  in. An unanswered handoff is repeated every 20 seconds.
* **Tower**: ask for a landing clearance (full stop, touch and go, low approach or option) and relay it.
  Without a clearance by 2 miles the suggested call is "tower clearance not received".
* **Missed approach**: hand the aircraft back to approach (or press **Coordinate missed approach**),
  then tell the pilot "contact approach".

### Keys (training)

| | |
|---|---|
| Space | Push to talk (configurable) |
| ← / → | Heading −1° / +1° (Shift: 5°) |
| D | Begin descent |
| Q / W / E | Slightly above / above / well above glidepath |
| A | On glidepath |
| Z / X / C | Slightly below / below / well below glidepath |
| G | Wheels should be down |
| M | Execute missed approach |
| T | Request tower clearance |
| P | Pause / resume |
| F | Flip PAR / ASR |
| Tab, Esc | Next target, deselect |

### Voices and speech recognition

In **Settings › Audio** pick the microphone, the output device and the pilot and tower voices.
Speech recognition can be:

* **Windows Speech**: offline, fast, but only understands standard phraseology. No coordination calls
  with callsigns it doesn't know.
* **Whisper** (tiny, base or small): a local OpenAI model, downloaded on first use (75–470 MB). Much
  more tolerant of accents and wording; use it for interphone calls.

**Suggested calls** (the computed transmission for the selected target) are in **Settings › Training**.
They are on for training traffic and off for VATSIM traffic.

### Session log

Every training session is logged to `%AppData%\VATSIM-PAR-Scope\diagnostics\training-*.log`: what
you said, how it was understood, what the pilot did, and every 2 seconds each aircraft's position
against the glidepath and course with the call the scope would have made. Useful for reviewing a
talk-down afterwards.

## VATSIM Velocity (5 Hz) data

vPilot's plugin API only raises an update every ~5 seconds, although vPilot receives Velocity fast
positions at up to 5 Hz. The plugin reads those fast positions from vPilot's internal event bus. It is
read-only and never sends anything to the network. If a vPilot update breaks it, the plugin falls back
to the 5-second updates. Details in [docs/VELOCITY.md](docs/VELOCITY.md).

The VATSIM servers only send fast positions to clients close enough to want them, so what a tower-view
connection gets depends on the servers. Close the scope and run `TrafficProbe.exe` to see the rates
you actually get.

## Sources

The models, symbology and phraseology follow FAA JO 7110.65, FAA Order 8260.3 (TERPS), ICAO Annex 10,
NAVAIR 00-80T-112/114, NAVEDTRA 14342A, AFMAN 13-204v3 and 11-217, UK MAA RA 3291, the MCAS Cherry
Point facility manual, and footage of in-service displays. See [docs/PAR-RESEARCH.md](docs/PAR-RESEARCH.md)
for what came from where.

## Building from source

Requires the .NET 10 SDK, and vPilot installed (the plugin compiles against vPilot's assemblies; set
`VPILOT_DIR` if vPilot is not in `%LOCALAPPDATA%\vPilot`).

```powershell
dotnet build VATSIM-PAR-Scope.sln
dotnet test tests\PARScope.Core.Tests
.\scripts\build-release.ps1 -Version 2.0.0   # zip in .\release
```

| Project | |
|---|---|
| `src/PARScope.Core` | Geodesy, traffic protocol and tracking, PAR geometry, radar and ASR models, phraseology, airport data, METAR, training simulation. No UI; unit tested. |
| `src/PARScope.App` | WPF on .NET 10 with [WPF UI](https://github.com/lepoco/wpfui) and SkiaSharp. |
| `src/RadarStreamerPlugin` | vPilot plugin (.NET Framework 4.7.2). |
| `tools/TrafficProbe` | Update-rate diagnostic. |

The plugin and scope talk NDJSON over UDP port 49090 (protocol v2, documented in `Plugin.cs`).

## Licence

MIT.
