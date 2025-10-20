# VATSIM PAR Scope

This repository contains a vPilot plugin that streams aircraft position data over UDP as NDJSON and a WPF PAR-style radar scope display that consumes those messages and renders vertical, azimuth and plan views useful for Precision Approach Radar (PAR) experimentation.

## Current status (Oct 2025)

Implemented

- RadarStreamerPlugin: vPilot plugin that streams aircraft add/update/delete events as NDJSON over UDP (default 127.0.0.1:49090).
- PAR Scope Display (WPF): a working Windows app that renders three coordinated scopes:
   - Vertical scope (altitude vs along-track) with history replay stored in physical units and reprojection on draw.

- History handling:
   - History is stored in physical units (NM/ft) per target and reprojected to each scope on redraw so resizing/range changes are handled.
   - History is update-driven (change-detection) and retains the most recent N entries (user-adjustable via slider).

- Known working binaries (after a successful build)
- `PARScopeDisplay\\bin\\Release\\PARScopeDisplay.exe` — the WPF scope application.
- Plugin: `RadarStreamerPlugin\\bin\\Release\\RadarStreamerPlugin.dll` (drop in vPilot Plugins folder).

## Quick start — build and run

Prerequisites

- Windows with .NET Framework 4.7.2+ (project targets .NET Framework 4.7.2)
- MSBuild (Visual Studio or Build Tools) available on PATH
- vPilot if you plan to run the plugin live

Build (from repo root)

```powershell

Or build the solution:

```powershell

Run the PAR scope app

- After a successful build, run the app:

- The app listens for NDJSON on UDP 127.0.0.1:49090 by default.

Run the plugin (for live vPilot use)

- Build the plugin project and copy `RadarStreamerPlugin.dll` into vPilot's `Plugins` folder. See plugin docs in the plugin folder for details.

## NDJSON message format (same as before)

Each UDP line is a JSON object (NDJSON). Example events:

Add event

Update event

```json

Fields are documented in the original README but include: type, t (ms epoch), callsign, typeCode, lat, lon, alt_ft, pressAlt_ft, pitch_deg, bank_deg, heading_deg, speed_kts.

## Configuration

- The UDP host/port are currently hardcoded in the plugin; the scope app listens on 127.0.0.1:49090 by default. Making these configurable via INI or command-line is a planned improvement.
- UI persistence: history-dots count and plan-alt-top are stored in AppData files in `\\%APPDATA%\\VATSIM-PAR-Scope`.

## Files of interest

- `PARScopeDisplay\\MainWindow.xaml` / `MainWindow.xaml.cs` — main WPF app UI and drawing logic.
- `RadarStreamerPlugin\\Plugin.cs` — plugin implementation for vPilot.
- `IniFile.cs` — small helper to read INI files (used by plugin if needed).

## Remaining TODOs (short)
- Standardize inline two-line datablocks across all scopes (callsign + alt/gs).
- Finalize centerline color and visual tuning.
- Make UDP host/port configurable (INI or UI).
- Optional: add NDJSON playback test harness for easier visual verification.

## Contributing
- PRs welcome. Please run the Release build and ensure no compile errors before submitting.

---

If you'd like, I can also:
- Run the built PAR scope app now and exercise it with a small NDJSON emitter to demonstrate the Plan ground filter and history behavior.
- Tidy the datablock formatting across scopes (I can implement and test a consistent two-line style).

# VATSIM PAR Scope

A vPilot plugin and radar scope system for streaming aircraft position data to simulate a **Precision Approach Radar (PAR)** display on VATSIM.

## Project Overview

This project consists of two parts:

### Part 1: RadarStreamerPlugin (Current Implementation)
A vPilot plugin that intercepts nearby aircraft position data and streams it over UDP as NDJSON.

- **When connected to VATSIM** via vPilot (as pilot, observer, or towerview), vPilot receives position updates for nearby traffic
- **The plugin subscribes** to `AircraftAdded`, `AircraftUpdated`, and `AircraftDeleted` events from the vPilot broker API
- **Streams NDJSON** over UDP (default 127.0.0.1:49090) with callsign, type code, lat/lon, altitude, pressure altitude, pitch, bank, heading, and speed
- **High refresh rate**: forwards all updates from vPilot's interpolated/smoothed aircraft positions

### Part 2: PAR Scope Display (Future)
A radar scope application that consumes the streamed data and renders:
- Vertical and lateral cross-section views modeling a real PAR scope
- 1 Hz refresh rate display
- Range/bearing calculations and target tracking

This README focuses on **Part 1** (the plugin).

---

## Quick Start

### Prerequisites
- **vPilot** installed (to obtain `RossCarlson.Vatsim.Vpilot.Plugins.dll`)
- **.NET Framework 4.8** (or higher)
- **MSBuild** (from Visual Studio, Build Tools, or Windows SDK)

### Build

1. **Set the vPilot plugin DLL path** (if not at the default location):
   ```powershell
   $Env:VPILOT_PLUGINS_DLL = "C:\Path\To\RossCarlson.Vatsim.Vpilot.Plugins.dll"
   ```
   Or the project will try to resolve from `C:\Users\$USERNAME\AppData\Local\vPilot\RossCarlson.Vatsim.Vpilot.Plugins.dll`.

2. **Build the solution**:
   ```powershell
   msbuild VATSIM-PAR-Scope.sln /p:Configuration=Release
   ```

   Or open `VATSIM-PAR-Scope.sln` in Visual Studio and build.

3. **Output**:
   - `RadarStreamerPlugin\bin\Release\RadarStreamerPlugin.dll`
   - `RadarStreamerReceiver\bin\Release\RadarStreamerReceiver.exe`

### Deploy

1. **Copy the plugin DLL** to vPilot's `Plugins` folder:
   ```powershell
   $vp = (Get-ItemProperty 'HKCU:\Software\vPilot').Install_Dir
   Copy-Item 'RadarStreamerPlugin\bin\Release\RadarStreamerPlugin.dll' "$vp\Plugins\" -Force
   ```

2. **Launch vPilot**. Check the debug window for:
   ```
   [RadarStreamer] Initialized. Streaming NDJSON over UDP to 127.0.0.1:49090
   ```

3. **Run the receiver** (optional, for testing):
   ```powershell
   .\RadarStreamerReceiver\bin\Release\RadarStreamerReceiver.exe
   ```

4. **Connect to VATSIM** in vPilot. As aircraft appear/move/disappear, you'll see NDJSON lines in the receiver console.

---

## Message Format (NDJSON)

Each line is a JSON object representing an event:

### Add Event
Emitted when a new aircraft is added to the simulation.
```json
{"type":"add","t":1729270123456,"callsign":"AAL123","typeCode":"B738","lat":40.6413,"lon":-73.7781,"alt_ft":1500,"pressAlt_ft":1520,"pitch_deg":3.2,"bank_deg":-1.5,"heading_deg":270,"speed_kts":180}
```

### Update Event
Emitted frequently with the latest position/orientation.
```json
{"type":"update","t":1729270123556,"callsign":"AAL123","typeCode":"B738","lat":40.6414,"lon":-73.7785,"alt_ft":1520,"pressAlt_ft":1540,"pitch_deg":3.1,"bank_deg":-1.4,"heading_deg":270,"speed_kts":181}
```

### Delete Event
Emitted when an aircraft is removed.
```json
{"type":"delete","t":1729270125000,"callsign":"AAL123"}
```

### Fields
- `type`: Event type (`add`, `update`, `delete`)
- `t`: Unix timestamp in milliseconds
- `callsign`: Aircraft callsign
- `typeCode`: ICAO aircraft type code (e.g., `B738`, `A320`)
- `lat`: Latitude (decimal degrees)
- `lon`: Longitude (decimal degrees)
- `alt_ft`: True altitude (feet MSL)
- `pressAlt_ft`: Pressure altitude (feet)
- `pitch_deg`: Pitch angle (degrees)
- `bank_deg`: Bank angle (degrees)
- `heading_deg`: Heading (degrees true)
- `speed_kts`: Ground speed (knots)

---

## Configuration

Currently the UDP host/port are hardcoded in `Plugin.cs`:
```csharp
private string _host = "127.0.0.1";
private int _port = 49090;
```

To make these configurable via INI file, you can:
1. Load settings from `vPilot\Plugins\RadarStreamerPlugin.ini` using the included `IniFile.cs` utility
2. Read `Host` and `Port` keys from an `[UDP]` section
3. Apply the values before initializing the UDP client

(This is left as a TODO for flexibility.)

---

## Project Structure

```
VATSIM-PAR-Scope/
├── .github/
│   └── copilot-instructions.md
├── RadarStreamerPlugin/
│   ├── Plugin.cs               # Main vPilot IPlugin implementation
│   ├── IniFile.cs              # INI file reader utility
│   ├── Properties/
│   │   └── AssemblyInfo.cs
│   └── RadarStreamerPlugin.csproj
├── RadarStreamerReceiver/
│   ├── Program.cs              # Simple UDP receiver for testing
│   ├── Properties/
│   │   └── AssemblyInfo.cs
│   └── RadarStreamerReceiver.csproj
├── VATSIM-PAR-Scope.sln
├── .gitignore
└── README.md
```

---

## Development Notes

- **Compatibility**: Targets .NET Framework 4.8 for compatibility with vPilot's runtime
- **Legacy MSBuild**: Code avoids C# 6+ features (string interpolation, null-conditional, expression bodies) to compile with older MSBuild
- **Observer/Towerview**: Plugin works in all vPilot connection modes (pilot, observer, towerview) as long as vPilot receives traffic data
- **Refresh rate**: The plugin streams all updates it receives from vPilot; downsample to 1 Hz in the PAR scope app if needed
- **Range**: Only nearby traffic known to vPilot is included (VATSIM's network range limit)

---

## Roadmap


- [ ] Integrate real-world PAR symbology
- [ ] Add log scale for x axis


---

## Contributing

This is an early-stage project. Contributions, feedback, and ideas are welcome!

---

## License

MIT License (or specify your preferred license)

---

## Credits

- **vPilot** by Ross Carlson - [https://www.vatsim.net/pilots/software](https://www.vatsim.net/pilots/software)
- **VATSIM Network** - [https://www.vatsim.net/](https://www.vatsim.net/)

---

## Support

For issues or questions, please open an issue on GitHub.
