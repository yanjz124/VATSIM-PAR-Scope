param(
  [string]$Version = "2.0.0",
  [string]$OutDir = (Join-Path $PSScriptRoot "..\release")
)
# Builds a release zip:
#   PARScope.exe                 self-contained single file (no .NET install needed)
#   TrafficProbe.exe             diagnostic: measures plugin update rates
#   Plugin\RadarStreamerPlugin.dll + .ini   copy into %LOCALAPPDATA%\vPilot\Plugins
# Requires the .NET 10 SDK and a local vPilot install (for the plugin reference assemblies).

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$stage = Join-Path $OutDir "PARScope-$Version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage "Plugin") | Out-Null

Write-Host "Running tests..."
dotnet test (Join-Path $root "tests\PARScope.Core.Tests") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

$single = @("-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
            "-p:DebugType=none", "-p:Version=$Version")

Write-Host "Publishing PAR Scope..."
dotnet publish (Join-Path $root "src\PARScope.App") -c Release @single -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

Write-Host "Publishing TrafficProbe..."
dotnet publish (Join-Path $root "tools\TrafficProbe") -c Release @single -p:PublishTrimmed=true -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Probe publish failed" }

Write-Host "Building vPilot plugin..."
dotnet build (Join-Path $root "src\RadarStreamerPlugin") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed" }
$pluginBin = Join-Path $root "src\RadarStreamerPlugin\bin\Release\net472"
Copy-Item (Join-Path $pluginBin "RadarStreamerPlugin.dll"), (Join-Path $pluginBin "RadarStreamerPlugin.ini") (Join-Path $stage "Plugin")

# Keep only what users need.
Get-ChildItem $stage -File | Where-Object { $_.Extension -notin ".exe" } | Remove-Item -Force

$zip = Join-Path $OutDir "PARScope-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  PARScope-$Version.zip" | Set-Content -Encoding ascii (Join-Path $OutDir "PARScope-$Version.sha256.txt")
Write-Host "Done: $zip"
Write-Host "SHA256: $hash"
