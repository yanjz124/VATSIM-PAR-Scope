param(
  [string]$Version = "0.0.1-alpha2",
  [string]$ZipPath = "$(Join-Path (Get-Location) ('VATSIM-PAR-Scope-'+$Version+'.zip'))",
  [string]$ChecksumPath = "$(Join-Path (Get-Location) ('VATSIM-PAR-Scope-'+$Version+'.sha256.txt'))"
)

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  Write-Host "GitHub CLI 'gh' not found. Please install it from https://cli.github.com/ and authenticate with 'gh auth login'." -ForegroundColor Yellow
  Write-Host "Then re-run this script: .\scripts\publish-release.ps1 -Version $Version"
  exit 1
}

# Create release (draft) and upload assets
$tag = "v$Version"
$releaseName = "VATSIM-PAR-Scope $Version"
$body = "Release $Version\n\nUnsigned build - please verify checksums before use."

Write-Host "Creating release $tag (draft)..."
$release = gh release create $tag --title "$releaseName" --notes "$body" --draft --repo $env:GITHUB_REPOSITORY 2>&1
if ($LASTEXITCODE -ne 0) { Write-Host "gh release create failed:"; Write-Host $release; exit $LASTEXITCODE }

Write-Host "Uploading assets: $ZipPath and $ChecksumPath"
gh release upload $tag $ZipPath $ChecksumPath --repo $env:GITHUB_REPOSITORY
if ($LASTEXITCODE -ne 0) { Write-Host "gh release upload failed"; exit $LASTEXITCODE }

Write-Host "Draft release created. Visit the release to publish it." -ForegroundColor Green
