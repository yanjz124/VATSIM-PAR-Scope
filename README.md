# vibe PAR

*Formerly VATSIM PAR Scope. Yes, it is vibe coded.*

**A Precision Approach Radar scope for VATSIM controllers**, modelled on the US military GCA systems
it imitates: AN/FPN-68, FPN-63, GPN-22, FPN-67, MPN-25 and TPN-31 ATNAVICS. It runs next to CRC, takes
live traffic from vPilot (including VATSIM Velocity 5 Hz positions), and has a training mode where you
talk simulated arrivals down by voice, with pilot, tower and approach on the other end.

![vibe PAR: FPN-68 display with three arrivals on final, the selected target's deviations and suggested call](docs/images/scope.png)

**[Download the latest release](https://github.com/yanjz124/VATSIM-PAR-Scope/releases)** — Windows 10/11.

## Features

**The scope**
* Elevation and azimuth on one range axis, with glidepath, lower safe limit, decision height and
  lateral safe limits; linear or log range, as on the real indicators.
* Seven radar models, each with its own coverage, scan rate, accuracy and display: from the analog
  FPN-63 B-scope to the FPN-68's colour tracks and data blocks and the ATNAVICS expeditionary screen.
* Targets are painted when the simulated beam crosses them, with MTI; real antenna siting off the
  runway; per-runway glidepath, TCH and DH from the approach plate.
* ASR surveillance picture as a flip view or a separate window, with the PAR coverage cone, extended
  centreline and, where the real display had one, a compass rose.
* Live METAR, wind and altimeter on the scope.

**Live VATSIM traffic**
* The vPilot plugin streams every aircraft vPilot sees, including Velocity fast positions at up to 5 Hz,
  over a tower-view connection next to CRC. The public data feed is a 15-second fallback.

**Training**
* Arrivals handed off by approach 12–15 NM out; you accept with "radar contact", run the approach and
  coordinate with tower and approach on the same push-to-talk, in your own words.
* Pilots read back, fly your headings and glidepath calls, drift on harder difficulties, fly in the
  live METAR wind, and at decision height land or go around (and tell you why) like real pilots.
* Speech recognition runs locally (Whisper on your graphics card or CPU, or Windows Speech). Pilot,
  tower and approach talk back in natural neural voices (Kokoro or Piper, run on your PC) or the
  Windows voices, each pilot with their own voice, through a radio effect.
* Every call and coordination has a hotkey and a button, so you can run a session without talking or
  typing. Keys can be changed.
* Every session is logged so you can review the talk-down afterwards.

With **Training** off it is a plain scope: no suggested calls and nothing simulated.

## Install

1. Download `PARScope-Setup-<version>.exe` from the [releases](https://github.com/yanjz124/VATSIM-PAR-Scope/releases)
   page and run it. It installs for your user only (no admin), lets you choose the folder, and finds
   vPilot's `Plugins` folder to install the plugin (you can change the folder or skip the plugin).
   Close vPilot first.
2. Start vibe PAR. The first start downloads the airport database (FAA NASR and OurAirports). Type an
   airport (`KNKX`, `NKX`, `EGQS`…) and pick the runway.

Prefer not to install? The zip on the same page has `PARScope.exe` (keep the `runtimes` folder next to
it) and the plugin in `Plugin\`, which you copy into `%LOCALAPPDATA%\vPilot\Plugins\` yourself.

**Updates**: vibe PAR checks for a new release at start-up and offers to install it (or check any time with **Check for updates now** in **Settings › Data**). With the
installer, the update includes the plugin; with the zip, copy a changed plugin by hand. The title bar
shows the version and the commit it was built from.

## Using it alongside CRC

CRC has no local data output, so live traffic comes from vPilot's **tower view**. The VATSIM Code of
Conduct (C7(b)) allows a controller one extra tower-view connection, `(CID)_TV`.

1. Connect to your position in CRC.
2. Start vPilot with the plugin installed and type `.towerview`.
3. Start vibe PAR. The status bar shows what is arriving:
   * **5 Hz data**: Velocity fast positions.
   * **5 s updates only**: vPilot connected, normal updates only.
   * **VATSIM data feed (15 s)**: no vPilot; public feed positions (turn on in **Settings › Data**).
     Fine for watching traffic, too slow for precision guidance.

Hover the status text for details; **Settings › Diagnostics** shows live rates per aircraft.

## Using the scope

| Control | |
|---|---|
| Airport, runway, radar model | Toolbar, left. Each model sets its own Log, Scan and compass-rose defaults; override them freely. |
| **ASR**, pop-out | Flip the scope to the ASR (**F**), or put the ASR in its own window. |
| Range 5/10/20 NM | **PgUp / PgDn**. |
| **MTI**, **Safety**, **2°/5°**, **Scan**, **Log** | Moving target indicator, safe limits, UK 2°/5° azimuth lines, antenna sweep, compressed range. |
| Target | Click, or **Tab** / **Esc**. The side panel shows range, glidepath and course deviation. |
| Gear button | **Settings**: per-runway PAR siting, display, data sources, training aids, audio, diagnostics. |

Radar noise, multipath and clutter are off unless you turn on **Settings › Display › Simulate radar
errors**; targets otherwise plot exactly where the aircraft is, once per scan.

## Training mode

Turn on **Training**, pick a **difficulty** and press **Spawn arrival**.

| Difficulty | |
|---|---|
| Easy | Light wind, precise pilots, close to the centreline. |
| Normal | Moderate wind, average pilots. |
| Hard | Pilots drift off heading and descent rate until corrected; the wind shifts on descent. |
| Expert | Gusts and shear, sloppy and slow pilots, steady drift. |

Arrivals fly in the live METAR wind (**Settings › Training** for a random one). Callsigns and types are
realistic (Navy, Marine, Reach, squadron callsigns from the
[Military Callsign List](https://henney.com/chm/callsign.htm)); approach speeds come from the FAA
Aircraft Characteristics Database. You can set your own callsign and ICAO type.

### Talking

Hold **push-to-talk** (**Space** by default, any key in **Settings › Audio**, or hold the **PTT**
button in the toolbar, which turns red while you transmit) and speak. You can also type. Standard
7110.65 phraseology works, and so do common variations ("well above glide and holding", "initiate
descent").

* Pilots read back until "do not acknowledge further transmissions", then only answer a radio check or
  a missed approach.
* Glidepath calls change the descent rate; course calls are information only, so correct with headings.
* "In the event of missed approach, climb and maintain two thousand, turn left heading one eight zero"
  is read back and remembered; "execute missed approach" makes them fly it.
* At **decision height** the pilot lands only with the runway in sight (live ceiling and visibility),
  a landing clearance, gear down and a stable approach. Otherwise they go around and say why, e.g.
  "Going around, no landing clearance". Touch and go, low approach and option clearances are flown.

### Coordination

Start a call with **Tower**, **Local**, **Approach** or **Departure** and it goes to that controller
instead of the pilot. Wording can be loose; the aircraft is found by its callsign or number.

* **Handoff in**: approach calls "handoff, one four mile final, Navy one two three four, two thousand
  five hundred, heading two four zero". Accept with "Navy one two three four, radar contact" and your
  initials. Approach answers with theirs and switches the pilot, who checks in. Unanswered handoffs are
  repeated every 20 seconds.
* **Tower**: request a clearance (full stop, touch and go, low approach, option) and relay it.
* **Missed approach**: hand the aircraft back to approach, then tell the pilot "contact approach".
  Aircraft that go missed, or fly a touch and go or low approach, are vectored round by approach and
  handed off to you again about two minutes later for another approach (pattern work). Full-stop
  landings end there. Turn this off in the Training traffic panel.

### Keys

Every call has a key, sent to the selected aircraft as if you had said it. **F1** (or **Key
assignments** in the training panel or **Settings › Audio**) lists them all; click one to change it.
The defaults:

| | |
|---|---|
| Space | Push to talk |
| Enter | Send the suggested call |
| ← / → | Heading −1° / +1° (Shift: 5°) |
| D | Begin descent |
| R | Range from touchdown |
| Q / W / E | Slightly above / above / well above glidepath |
| A | On glidepath |
| Z / X / C | Slightly below / below / well below glidepath |
| 1 2 3 · 4 · 5 6 7 (or numpad) | Well left / left / slightly left · on course · slightly right / right / well right |
| with Shift / Ctrl / Alt | "…and correcting" / "…and holding" / "going…" (glidepath and course keys) |
| H, B, L, K | Radio check, approach brief, lost communications, missed approach instructions |
| N, G | Do not acknowledge further transmissions, wheels should be down |
| V, Shift+V | Level off, resume normal rate of descent |
| M, Shift+M | Execute missed approach, if runway not in sight execute missed approach |
| Y, U | Relay tower's clearance, contact tower after landing |
| T | Request clearance from tower |
| Ctrl+R, J, Ctrl+M | Radar contact (accept handoff), contact approach, coordinate missed approach |
| PgUp / PgDn, Tab, Esc | Range scale, next target, deselect |
| P, F, F1 | Pause, flip PAR / ASR, key assignments |

### Voices

Chosen in **Settings › Audio**:

* **Kokoro** (downloaded on first use, 330 MB): the most natural; about half real time on a CPU, and
  each reply is synthesised while the pilot "thinks", so there is no extra wait.
* **Piper LibriTTS-R** (78 MB): very quick, with hundreds of speakers for variety.
* **Windows voices**: built in, robotic.

Each pilot gets their own voice (picked from the callsign) unless you turn that off, and the radio
effect band-limits the audio and adds the squelch.

### Speech recognition

Chosen in **Settings › Audio**, together with the microphone and output device.

* **Whisper** (tiny, base or small; downloaded on first use, 75–470 MB): understands accents and loose
  wording, needed for interphone calls. It transcribes each phrase while you are still talking, so the
  result is ready a fraction of a second after you let go. It uses your graphics card (any
  Vulkan-capable NVIDIA, AMD or Intel GPU) and falls back to the CPU; on a CPU, tiny.en is the quick
  choice and small.en the accurate one.
* **Windows Speech**: offline and instant, but only standard phraseology.

### Session log

Each session is written to `%AppData%\VATSIM-PAR-Scope\diagnostics\training-*.log`: what you said, how
it was understood, what the pilots did and why they went around, and every 2 seconds each aircraft's
position against the glidepath and course with the call the scope would have made.

## How the 5 Hz data works

vPilot's plugin API only raises an update every ~5 seconds, although vPilot receives Velocity fast
positions at up to 5 Hz. The plugin reads those from vPilot's internal event bus. It is read-only and
never sends anything to the network; if a vPilot update breaks it, it falls back to the 5-second
updates. VATSIM only sends fast positions to clients close enough to want them, so run
`TrafficProbe.exe` (with the scope closed) to see what you actually get. Details:
[docs/VELOCITY.md](docs/VELOCITY.md).

## Sources

FAA JO 7110.65, FAA Order 8260.3 (TERPS), ICAO Annex 10, NAVAIR 00-80T-112/114, NAVEDTRA 14342A,
AFMAN 13-204v3 and 11-217, UK MAA RA 3291, the MCAS Cherry Point facility manual, and footage of
in-service displays. [docs/PAR-RESEARCH.md](docs/PAR-RESEARCH.md) lists what came from where.

## Development

Requires the .NET 10 SDK and vPilot (the plugin compiles against vPilot's assemblies; set `VPILOT_DIR`
if it is not in `%LOCALAPPDATA%\vPilot`). The installer needs Inno Setup 6
(`winget install JRSoftware.InnoSetup`).

```powershell
dotnet build VATSIM-PAR-Scope.sln
dotnet test tests\PARScope.Core.Tests
.\scripts\build-release.ps1 -Version 2.0.0-beta.3            # zip and installer in .\release
.\scripts\publish-release.ps1 -Version 2.0.0-beta.3          # tag, GitHub release, prune old releases
```

| Project | |
|---|---|
| `src/PARScope.Core` | Geodesy, traffic protocol and tracking, PAR geometry, radar and ASR models, phraseology, airport data, METAR, training simulation. No UI; unit tested. |
| `src/PARScope.App` | The scope: WPF on .NET 10 with [WPF UI](https://github.com/lepoco/wpfui), SkiaSharp, NAudio, Whisper.net and sherpa-onnx. |
| `src/RadarStreamerPlugin` | vPilot plugin (.NET Framework 4.7.2). |
| `tools/TrafficProbe` | Update-rate diagnostic. |
| `installer/` | Inno Setup script. |

The plugin and scope talk NDJSON over UDP port 49090 (protocol v2, documented in `Plugin.cs`).

## Licence

MIT. Voices: [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) (Apache-2.0); Piper
[LibriTTS-R](https://www.openslr.org/141/) voice (trained on LibriTTS-R by Koizumi et al., CC BY 4.0),
both run with [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache-2.0).
