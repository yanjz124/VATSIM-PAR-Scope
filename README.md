
VATSIM PAR Scope
=================

A small toolkit for experimenting with a Precision Approach Radar (PAR) style display on VATSIM.

This repository contains:

- `RadarStreamerPlugin` — a vPilot plugin that streams aircraft position events as NDJSON over UDP.
- `PARScopeDisplay` — a WPF PAR-style scope app that listens for those NDJSON messages and renders Vertical, Azimuth and Plan views.

Quick release instructions
-------------------------
If you're using a pre-built release (recommended):

Installation
1. Make sure vPilot is not running.
2. Download the latest release — you only need `PARScopeDisplay.exe` (the scope) and `RadarStreamerPlugin.dll` (the vPilot plugin). The plugin also ships with `RossCarlson.Vatsim.Vpilot.Plugins.xml` (the vPilot manifest) — keep it next to the DLL.
3. Windows Defender or your antivirus may flag the unsigned `.dll` as suspicious. This project is hobby vibe-coding software and the DLL is not code-signed. If your Window Defender or Antivirus flags it, verify it and unblock if you trust it.
4. Copy the plugin files into vPilot's plugin folder, typically:

	 `C:\Users\<your username>\AppData\Local\vPilot\Plugins\`

	 Place both the `.dll` and the accompanying `.xml` manifest in that folder. You may leave `PARScopeDisplay.exe` anywhere you like.
5. Start a vPilot-compatible sim (MSFS, P3D, FSX). Launch `PARScopeDisplay.exe` whenever you like. Connect vPilot as a pilot/towerview/observer — the top-left of the display should show a green "connected" indicator when communication is established.

Troubleshooting
---------------
- I don't see a green connected in the `PARScopeDisplay.exe` even the vpilot is running
	- Make sure the plugin `.dll` and its `.xml` manifest are placed in vPilot's plugin folder (see the path above).
	- If Windows blocked the `.dll`, right-click the file, open Properties and click "Unblock" (after you have checked it with your antivirus software).
	- Restart vPilot after placing/unblocking the plugin. Open vPilot's debug window (type `.debug` in its console) to see plugin load messages.
	- If the plugin does not appear in the debug output, confirm the filename and manifest match the assembly name in the DLL.

Building from source
--------------------
If you prefer to build from source, you'll need Windows with MSBuild (Visual Studio or Build Tools) and .NET Framework 4.7.2.

From the repository root run (PowerShell):

```powershell
& 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe' .\VATSIM-PAR-Scope.sln /t:Rebuild /p:Configuration=Release
```

After building, copy `RadarStreamerPlugin\\bin\\Release\\RadarStreamerPlugin.dll` and the `RossCarlson.Vatsim.Vpilot.Plugins.xml` manifest to the vPilot plugin folder, and run `PARScopeDisplay\\bin\\Release\\PARScopeDisplay.exe`.

Files included in releases
--------------------------
- `PARScopeDisplay.exe` — the scope app (Windows WPF executable)
- `RadarStreamerPlugin.dll` — vPilot plugin (copy to vPilot Plugins folder)
- `RossCarlson.Vatsim.Vpilot.Plugins.xml` — vPilot plugin manifest (keep with the DLL)

License
-------
MIT — feel free to use or modify.

Issues
------
Open an issue on GitHub if you hit a problem or want to request a feature.
