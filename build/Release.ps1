<#
.SYNOPSIS
  Builds every release artifact of OverShell (DESIGN.md §13), in two phases so the
  binaries can be signed in between.

.DESCRIPTION
  Phase "build":   publish framework-dependent and self-contained win-x64, pack the .NET tool,
                   unpack the tool package - everything lands under artifacts/release/stage/ -
                   and write stage/signing-catalog.txt, the exact list of our own binaries
                   (OverShell*.exe, OverShell*.dll). The release workflow signs that catalog;
                   the Terminal/ConPTY binaries are already Microsoft-signed and are left alone.
  Phase "package": repack the tool package from the (signed) staging copy, zip both publish
                   flavours, write SHA-256 sums, render the winget manifests from winget/templates.
  Phase "all":     both, unsigned - what a local run does.

  Nothing here needs elevation or network beyond NuGet restore.

.PARAMETER Version
  Package version, e.g. 0.1.0. The release workflow passes the tag without its "v".

.PARAMETER RequireSigned
  Fail the package phase if any catalogued binary lacks a valid Authenticode signature.
  The release workflow passes it whenever signing ran, so a silent signing failure cannot
  ship unsigned bits under a signed-looking release.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')] [string] $Version,
    [ValidateSet('build', 'package', 'all')] [string] $Phase = 'all',
    [string] $Repository = 'MoaidHathot/OverShell',
    [switch] $RequireSigned
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\release'
$stage = Join-Path $out 'stage'
$catalog = Join-Path $stage 'signing-catalog.txt'
$project = Join-Path $root 'src\OverShell.App\OverShell.App.csproj'
$common = @('-c', 'Release', '-p:Platform=x64', '-r', 'win-x64', "-p:Version=$Version", '-nologo', '-v:m')
$utf8 = [Text.UTF8Encoding]::new($false)

function Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Invoke-Dotnet([string[]] $arguments) {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Get-Signable {
    Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -like 'OverShell*.exe' -or $_.Name -like 'OverShell*.dll' }
}

if ($Phase -in 'build', 'all') {
    Step "Clean $out"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Path $stage | Out-Null

    Step 'Publish framework-dependent (needs Microsoft.WindowsDesktop.App 10)'
    Invoke-Dotnet (@('publish', $project) + $common + @('--self-contained', 'false', '-o', (Join-Path $stage 'fd')))

    Step 'Publish self-contained (no prerequisites)'
    Invoke-Dotnet (@('publish', $project) + $common + @('--self-contained', 'true', '-o', (Join-Path $stage 'sc')))

    Step 'Pack the .NET tool'
    $packDir = Join-Path $out 'nupkg-unsigned'
    Invoke-Dotnet (@('pack', $project) + $common + @('-o', $packDir))
    $nupkg = Get-ChildItem $packDir -Filter "OverShell.$Version.nupkg" | Select-Object -First 1
    if (-not $nupkg) { throw "pack produced no OverShell.$Version.nupkg in $packDir" }

    Step 'Unpack the tool package for signing'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($nupkg.FullName, (Join-Path $stage 'nupkg'))

    Step 'Signing catalog'
    # Paths relative to the catalog's own directory, one per line - the shape
    # azure/artifact-signing-action's files-catalog input expects.
    $signable = @(Get-Signable)
    $lines = $signable | ForEach-Object { $_.FullName.Substring($stage.Length + 1) }
    [IO.File]::WriteAllText($catalog, (($lines -join "`n") + "`n"), $utf8)
    Write-Host "Catalogued $($signable.Count) file(s) for signing in $catalog"
    $lines | ForEach-Object { "  $_" }
}

if ($Phase -in 'package', 'all') {
    if (-not (Test-Path $stage)) { throw "Nothing staged: run -Phase build first ($stage is missing)" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $dist = Join-Path $out 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null

    Step 'Signature check'
    $states = Get-Signable | ForEach-Object {
        $s = Get-AuthenticodeSignature $_.FullName
        # SignerCertificate is null on an unsigned file; strict mode makes that a hard error.
        $signer = if ($null -ne $s.SignerCertificate) { $s.SignerCertificate.Subject -replace ',.*', '' } else { '-' }
        [pscustomobject]@{ File = $_.FullName.Substring($stage.Length + 1); Status = $s.Status; Signer = $signer }
    }
    $states | ForEach-Object { "  {0,-10} {1,-52} {2}" -f $_.Status, $_.File, $_.Signer }
    $unsigned = @($states | Where-Object Status -ne 'Valid')
    if ($RequireSigned -and $unsigned.Count -gt 0) {
        throw "RequireSigned: $($unsigned.Count) catalogued file(s) are not validly signed: $(($unsigned.File) -join ', ')"
    }
    if (-not $RequireSigned -and $unsigned.Count -gt 0) { Write-Host "  (unsigned build - fine locally; the release workflow signs before this phase)" }

    Step 'Repack the tool package'
    # Entry names must use forward slashes and the OPC parts must stay where they were; .NET's
    # ZipFile writes '/' separators on every platform since .NET Core 3.
    $nupkgOut = Join-Path $dist "OverShell.$Version.nupkg"
    if (Test-Path $nupkgOut) { Remove-Item $nupkgOut }
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $stage 'nupkg'), $nupkgOut, [IO.Compression.CompressionLevel]::Optimal, $false)

    Step 'Zip the publish flavours'
    $fdZip = Join-Path $dist "OverShell-$Version-win-x64.zip"
    $scZip = Join-Path $dist "OverShell-$Version-win-x64-selfcontained.zip"
    foreach ($pair in @(@{ Src = (Join-Path $stage 'fd'); Zip = $fdZip }, @{ Src = (Join-Path $stage 'sc'); Zip = $scZip })) {
        if (Test-Path $pair.Zip) { Remove-Item $pair.Zip }
        [IO.Compression.ZipFile]::CreateFromDirectory($pair.Src, $pair.Zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    }

    Step 'Checksums'
    $sums = Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.zip', '.nupkg' } | ForEach-Object {
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    }
    [IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), $utf8)

    Step 'winget manifests'
    $fdHash = (Get-FileHash $fdZip -Algorithm SHA256).Hash.ToUpperInvariant()
    $manifestDir = Join-Path $dist "winget\manifests\m\MoaidHathot\OverShell\$Version"
    New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
    $values = @{
        '{{VERSION}}'      = $Version
        '{{SHA256}}'       = $fdHash
        '{{RELEASE_DATE}}' = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
        '{{ZIP_URL}}'      = "https://github.com/$Repository/releases/download/v$Version/$([IO.Path]::GetFileName($fdZip))"
        '{{NOTES_URL}}'    = "https://github.com/$Repository/releases/tag/v$Version"
    }
    foreach ($template in Get-ChildItem (Join-Path $root 'winget\templates') -Filter '*.yaml') {
        $text = [IO.File]::ReadAllText($template.FullName)
        foreach ($key in $values.Keys) { $text = $text.Replace($key, $values[$key]) }
        if ($text -match '\{\{[A-Z_]+\}\}') { throw "Unrendered placeholder in $($template.Name): $($Matches[0])" }
        [IO.File]::WriteAllText((Join-Path $manifestDir $template.Name), $text, $utf8)
    }

    Step 'Done'
    Get-ChildItem $dist -File | ForEach-Object { "{0,10:N0} KB  {1}" -f ($_.Length / 1KB), $_.Name }
    Get-Content (Join-Path $dist 'SHA256SUMS.txt')
}
