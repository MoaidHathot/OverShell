<#
.SYNOPSIS
Compare dashboard preview modes with repeatable scratch-state workloads.
.DESCRIPTION
Runs text/auto/styled in alternating order, optionally alongside an archived baseline,
and a larger auto-mode case. Uses Invoke-SelfTest's private desktop. Keeps raw reports
and logs plus a CSV summary; measurements include the observer and normal process noise.
#>
[CmdletBinding()]
param(
    [string] $Exe = (Join-Path $PSScriptRoot '..\artifacts\bin\OverShell.App\release_win-x64\OverShell.exe'),
    [string] $BaselineExe,
    [ValidateRange(2, 32)] [int] $Tabs = 8,
    [ValidateRange(1, 5)] [int] $Repeats = 2,
    [string] $ResultsDir = (Join-Path $PSScriptRoot ('..\artifacts\selftest\dashboard-previews\bench-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [switch] $Opaque,
    [switch] $SkipScale
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if ($BaselineExe) { $BaselineExe = (Resolve-Path -LiteralPath $BaselineExe).Path }
$ResultsDir = [IO.Path]::GetFullPath($ResultsDir)
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$rows = [Collections.Generic.List[object]]::new()

function Measure-Case([string] $Name, [string] $Mode, [string] $Binary, [int] $Count, [int] $Repeat) {
    $directory = Join-Path $ResultsDir "$Name-$Mode-$Count-$Repeat"
    $environment = @{
        OVERSHELL_PREVIEW_STAGE = $Name
        OVERSHELL_PREVIEW_MODE = $Mode
        OVERSHELL_PREVIEW_TABS = [string]$Count
        OVERSHELL_PREVIEW_OPAQUE = $(if ($Opaque) { '1' } else { '0' })
    }
    & (Join-Path $PSScriptRoot 'Invoke-SelfTest.ps1') -Mode previewbench -Exe $Binary -ConfigDir (Join-Path $directory 'config') -StateDir (Join-Path $directory 'state') -Env $environment -MaxSeconds 150
    $code = $LASTEXITCODE
    $log = Join-Path ([IO.Path]::GetTempPath()) 'overshell-selftest.log'
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $directory 'selftest.log') }
    if ($code -ne 0) { throw "Benchmark failed: $Name, $Count tabs, repeat $Repeat" }
    $report = Get-Content -LiteralPath (Join-Path $directory 'state\preview-benchmark.json') -Raw | ConvertFrom-Json
    foreach ($phase in $report.phases) {
        $rows.Add([pscustomobject]@{
            Case = $Name; Mode = $Mode; Tabs = $Count; Repeat = $Repeat; Phase = $phase.phase
            WallMs = $phase.wallMilliseconds; CpuMs = $phase.cpuMilliseconds; CpuOneCorePercent = $phase.oneCorePercent
            AllocatedBytes = $phase.allocatedBytes; PrivateBytes = $phase.privateBytes; WorkingSetBytes = $phase.workingSetBytes
            Handles = $phase.handles; GdiObjects = $phase.gdiObjects; UserObjects = $phase.userObjects
            GpuEnginePercent = $phase.gpuEnginePercent; UiaSnapshots = $phase.snapshots
            LivePreviews = $phase.livePreviews; StyledSources = $phase.styledSources
            StyledCaptures = $phase.styledCaptures; StyledDrawings = $phase.styledDrawings
            SameGrids = $report.sameGrids; Report = (Join-Path $directory 'state\preview-benchmark.json')
        })
    }
    $rows | Export-Csv -LiteralPath (Join-Path $ResultsDir 'summary.csv') -NoTypeInformation
}

for ($repeat = 1; $repeat -le $Repeats; $repeat++) {
    if ($BaselineExe) { Measure-Case 'baseline' 'text' $BaselineExe $Tabs $repeat }
    $order = if ($repeat % 2 -eq 1) { @('text', 'auto', 'styled') } else { @('styled', 'auto', 'text') }
    foreach ($mode in $order) { Measure-Case 'final' $mode $Exe $Tabs $repeat }
}
if (-not $SkipScale) { Measure-Case 'scale' 'auto' $Exe ([Math]::Min(32, $Tabs * 2)) 1 }
$rows | Select-Object Case,Mode,Tabs,Repeat,Phase,CpuOneCorePercent,AllocatedBytes,PrivateBytes,LivePreviews,StyledCaptures | Format-Table -AutoSize
Write-Output "Benchmark evidence: $ResultsDir"
