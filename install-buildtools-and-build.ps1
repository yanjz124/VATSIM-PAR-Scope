<#
install-buildtools-and-build.ps1

Usage: Run this script from an elevated (Administrator) PowerShell window.
It will download the Visual Studio Build Tools bootstrapper, install the
Managed Desktop + MSBuild workloads (for building WPF/.NET Framework apps),
and then attempt to locate msbuild and rebuild the solution.

IMPORTANT: This script performs an unattended install and requires an
Internet connection. It may take several minutes and may require a reboot.
Run as Administrator.
#>

Set-StrictMode -Version Latest

function Ensure-Admin {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "This script must be run as Administrator. Please restart PowerShell as Administrator and re-run."
        exit 2
    }
}

function Download-Bootstrapper($url, $dest) {
    Write-Host "Downloading $url to $dest ..."
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
}

function Run-Installer($exe, $args) {
    Write-Host "Running installer: $exe $args"
    $p = Start-Process -FilePath $exe -ArgumentList $args -Wait -Passthru -NoNewWindow
    return $p.ExitCode
}

function Find-MSBuild {
    $candidates = @(
        'C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    # Try to find any msbuild.exe under Program Files
    $found = Get-ChildItem 'C:\Program Files' -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    $found2 = Get-ChildItem 'C:\Program Files (x86)' -Filter msbuild.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found2) { return $found2.FullName }

    # Try PATH
    $cmd = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Path }

    return $null
}

Ensure-Admin

$solution = Join-Path (Get-Location) 'VATSIM-PAR-Scope.sln'
if (-not (Test-Path $solution)) {
    Write-Error "Solution file not found at $solution"
    exit 3
}

$bootstrapper = Join-Path $env:TEMP 'vs_BuildTools.exe'
$vsUrl = 'https://aka.ms/vs/17/release/vs_BuildTools.exe'

if (-not (Test-Path $bootstrapper)) {
    Download-Bootstrapper -url $vsUrl -dest $bootstrapper
}
else {
    Write-Host "Bootstrapper already downloaded: $bootstrapper"
}

# Workloads to install: Managed Desktop + MSBuild Tools
$installArgs = '--quiet --wait --norestart --nocache --installPath "C:\BuildTools" --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools --add Microsoft.VisualStudio.Workload.MSBuildTools'

Write-Host "Starting unattended install of Visual Studio Build Tools (this will take a while)..."
$exit = Run-Installer -exe $bootstrapper -args $installArgs
if ($exit -ne 0) {
    Write-Warning "Installer exited with code $exit. You may need to run the bootstrapper interactively to see errors."
}

Write-Host "Searching for msbuild.exe..."
$msbuild = Find-MSBuild
if (-not $msbuild) {
    Write-Error "msbuild.exe was not found after install. Try rebooting and re-run this script, or run the bootstrapper interactively to ensure workloads were selected."
    exit 4
}

Write-Host "Found msbuild: $msbuild"

Write-Host "Running msbuild to rebuild solution (Release)..."
& $msbuild $solution /t:Rebuild /p:Configuration=Release
$buildExit = $LASTEXITCODE

if ($buildExit -eq 0) {
    Write-Host "Build succeeded. You can now run PARScopeDisplay from the project's bin\Release folder."
    exit 0
} else {
    Write-Error "Build failed with exit code $buildExit. Inspect the output above for errors and paste here if you want me to fix them."
    exit $buildExit
}
