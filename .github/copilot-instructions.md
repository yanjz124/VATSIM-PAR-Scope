<!-- Use this file to provide workspace-specific custom instructions to Copilot. For more details, visit https://code.visualstudio.com/docs/copilot/copilot-customization#_use-a-githubcopilotinstructionsmd-file -->
- [x] Verify that the copilot-instructions.md file in the .github directory is created. ✓

- [x] Clarify Project Requirements ✓
	VATSIM PAR Scope: vPilot plugin streaming aircraft position data over UDP as NDJSON for PAR (Precision Approach Radar) display. .NET Framework 4.8 C# projects.

- [x] Scaffold the Project ✓
	Created RadarStreamerPlugin (class library), RadarStreamerReceiver (console app), solution file, and supporting files.

- [x] Customize the Project ✓
	Added IniFile.cs utility, configured projects for vPilot plugin API, ensured legacy MSBuild compatibility.

- [x] Install Required Extensions ✓
	No additional extensions required for C# .NET Framework development.

- [x] Compile the Project ✓
	Successfully built both projects with MSBuild. Output: RadarStreamerPlugin.dll and RadarStreamerReceiver.exe.

- [x] Create and Run Task ✓
	Not required - direct MSBuild commands documented in README.md.

- [x] Launch the Project ✓
	Plugin deployment and receiver testing instructions provided in README.md. Deploy plugin to vPilot Plugins folder, run receiver, and connect to VATSIM.

- [x] Ensure Documentation is Complete ✓
	README.md covers build/deploy/message format/PAR scope goals. .gitignore added. Project ready for GitHub.
