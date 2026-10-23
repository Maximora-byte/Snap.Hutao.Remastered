param(
    [string]$ArchiveUrl = $env:BUNDLED_IMAGES_URL,
    [string]$ResourceVersion = $env:BUNDLED_IMAGES_VERSION,
    [string]$Sha256 = $env:BUNDLED_IMAGES_SHA256
)
$ErrorActionPreference = 'Stop'
if (!$ArchiveUrl -and !$ResourceVersion -and !$Sha256) {
    Write-Host 'No bundled image snapshot configured; building without optional images.'
    return
}
if (!$ArchiveUrl -or !$ResourceVersion -or $Sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'Configure BUNDLED_IMAGES_URL, BUNDLED_IMAGES_VERSION and BUNDLED_IMAGES_SHA256 together.'
}
$uri = [Uri]$ArchiveUrl
if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https') { throw 'The snapshot must use an HTTPS archive URL.' }
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $repoRoot ('artifacts/image-snapshot-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $stage 'snapshot.7z'
$extract = Join-Path $stage 'extracted'
$output = Join-Path $stage 'prepared'
[IO.Directory]::CreateDirectory($extract) | Out-Null
Invoke-WebRequest -Uri $uri -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Sha256) { throw 'Snapshot SHA256 mismatch.' }
& 7z x $archive "-o$extract" -y | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Failed to extract bundled image snapshot.' }
$rawDirectories = @(Get-ChildItem -LiteralPath $extract -Directory -Recurse -Filter raw)
if ($rawDirectories.Count -gt 1) { throw 'Ambiguous archive layout: multiple raw directories.' }
$rawRoot = if ($rawDirectories.Count -eq 1) { $rawDirectories[0].FullName } else { $extract }
& (Join-Path $PSScriptRoot 'Prepare-BundledImages.ps1') -SourceDirectory $rawRoot -ResourceVersion $ResourceVersion -OutputDirectory $output
# MSBuild imports environment variables as properties. Every Cake entry point consumes this.
$env:BundledImagesSource = $output
if ($env:GITHUB_ENV) { "BundledImagesSource=$output" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8 }
