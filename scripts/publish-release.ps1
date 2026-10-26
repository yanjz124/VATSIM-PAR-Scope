param(
  [Parameter(Mandatory)] [string]$Version,      # e.g. 2.0.0-beta.3 (a "-" suffix makes it a pre-release)
  [string]$NotesFile,                           # release notes (Markdown); default: the commit list since the last tag
  [int]$Keep = 3                                # releases of this major version to keep; older ones are deleted
)
# Builds the release from the current commit, tags it, publishes it on GitHub with the zip, installer and
# checksums, then deletes older releases of the same major version beyond -Keep. Tags are kept, so old
# builds can still be found and rebuilt. Requires a clean working tree and the gh CLI.

# Native tools (git, gh) report progress on stderr, so failures are detected by exit code.
$ErrorActionPreference = "Continue"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if (git -C $root status --porcelain) { throw "Commit or stash your changes first." }
$sha = git -C $root rev-parse --short HEAD
$tag = "v$Version"
$out = Join-Path $root "release"

& (Join-Path $PSScriptRoot "build-release.ps1") -Version $Version -OutDir $out
if (-not $?) { throw "Build failed" }

if (-not $NotesFile) {
  $prev = git -C $root describe --tags --abbrev=0 2>$null
  $NotesFile = Join-Path $out "notes-$Version.md"
  $log = if ($prev) { git -C $root log --pretty="- %s" "$prev..HEAD" } else { git -C $root log --pretty="- %s" -20 }
  ($log -join "`n") + "`n`nBuilt from $sha." | Set-Content -Encoding utf8 $NotesFile
}

git -C $root push origin HEAD 2>&1 | Out-Host
if ($LASTEXITCODE) { throw "git push failed" }
git -C $root tag -a $tag -m "vibe PAR $Version"
git -C $root push origin $tag 2>&1 | Out-Host
if ($LASTEXITCODE) { throw "pushing the tag failed" }

$assets = @("PARScope-$Version.zip", "PARScope-Setup-$Version.exe", "PARScope-$Version.sha256.txt") |
  ForEach-Object { Join-Path $out $_ } | Where-Object { Test-Path $_ }
$ghArgs = @("release", "create", $tag) + @($assets) + @("--title", "vibe PAR $Version ($sha)", "--notes-file", "$NotesFile")
if ($Version.Contains("-")) { $ghArgs += "--prerelease" }
& gh $ghArgs 2>&1 | Out-Host
if ($LASTEXITCODE) { throw "gh release create failed" }

# Prune: newest first; keep -Keep releases of this major version, delete the rest (not their tags).
$major = $Version.Split(".")[0]
# (Parenthesised so Windows PowerShell enumerates the JSON array instead of passing it on as one object.)
$old = (gh release list --limit 100 --json tagName,createdAt | ConvertFrom-Json) |
  Where-Object { $_.tagName -like "v$major.*" } |
  Sort-Object { [datetime]$_.createdAt } -Descending |
  Select-Object -Skip $Keep
foreach ($r in $old) {
  Write-Host "Deleting old release $($r.tagName)"
  gh release delete $r.tagName --yes
}
