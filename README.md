
VATSIM PAR Scope
=================

A small toolkit for experimenting with a Precision Approach Radar (PAR) style display on VATSIM.

This repo contains two things:

- A vPilot plugin (RadarStreamerPlugin) that streams aircraft position events as NDJSON over UDP.
- A WPF PAR-style scope app (PARScopeDisplay) that listens for those NDJSON messages and renders Vertical, Azimuth and Plan views.

Who made this
-------------
I vibe-coded the whole project.

Quick notes
-----------
- The plugin streams to 127.0.0.1:49090 by default.
- The scope app listens on UDP 127.0.0.1:49090 and draws three coordinated scopes.

Build & run
-----------
Requirements:
- Windows
- .NET Framework 4.7.2 (projects currently target 4.7.2)
- MSBuild (from Visual Studio or Build Tools)

Build (from repo root):

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe' .\VATSIM-PAR-Scope.sln /t:Rebuild /p:Configuration=Release
```

Run the scope app:

- After building, launch `PARScopeDisplay\bin\Release\PARScopeDisplay.exe`.
- The app listens for NDJSON on UDP 127.0.0.1:49090 by default.

Run the plugin in vPilot:

- Copy `RadarStreamerPlugin\bin\Release\RadarStreamerPlugin.dll` into your vPilot `Plugins` folder and restart vPilot.

NDJSON format
-------------
Each UDP line is a JSON object (NDJSON). Events include `type` (add/update/delete), `callsign`, `lat`, `lon`, `alt_ft`, `speed_kts` and other fields. See the code for the exact fields.

Files you’ll care about
-----------------------
- `PARScopeDisplay/MainWindow.xaml` + `MainWindow.xaml.cs` — WPF UI and drawing logic.
- `RadarStreamerPlugin/Plugin.cs` — vPilot plugin implementation.
- `RadarStreamerReceiver/Program.cs` — simple UDP receiver used for testing.

Notes & TODOs
-------------
- UDP host/port are currently hardcoded; making them configurable is a planned improvement.
- History, ground-filtering and visual tuning are implemented but may need refinement.

License
-------
MIT — feel free to use or modify.

Questions / Issues
-----------------
Open an issue on GitHub.
