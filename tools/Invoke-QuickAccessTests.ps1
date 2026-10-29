<#
.SYNOPSIS
Runs quick-access and related regressions on isolated private Win32 desktops.
.DESCRIPTION
Each mode has its own TEMP, config and state. quickrestore1/quickrestore2 share only
their restart fixture. Logs and a machine-readable summary are retained under Output.
Separate invocations with distinct Output/Tag can run concurrently without taking focus.
#>
[CmdletBinding()]
param(
    [string[]] $Modes = @('quicknav', 'aliases', 'floats', 'scratch', 'quickrestore1', 'quickrestore2'),
    [string] $Exe = (Join-Path $PSScriptRoot '..\artifacts\bin\OverShell.App\debug_win-x64\OverShell.exe'),
    [string] $Output = (Join-Path $PSScriptRoot '..\artifacts\selftest\quick-access\final'),
    [string] $TempRoot = (Join-Path ([IO.Path]::GetTempPath()) 'overshell-quick-access'),
    [string] $Tag = 'final',
    [hashtable] $Env = @{}
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$Exe = [IO.Path]::GetFullPath($Exe)
[IO.Directory]::CreateDirectory($Output) | Out-Null
$fixture = Join-Path $Output 'profiles.json'
[IO.File]::WriteAllText($fixture, '{"defaultProfile":"QA PowerShell","profiles":{"list":[{"guid":"302a0b80-f71f-49ed-83d2-057c26d389db","name":"QA PowerShell","commandline":"powershell.exe -NoLogo -NoProfile"}]}}')
$results = @()
$originalTemp = $env:TEMP
$originalTmp = $env:TMP
try {
    foreach ($mode in $Modes) {
        $group = if ($mode -in @('quickrestore1', 'quickrestore2')) { 'restart' } else { $mode }
        $root = Join-Path $Output $group
        $state = Join-Path $root 'state'
        $temp = Join-Path $TempRoot "$Tag-$group"
        $config = Join-Path $temp 'config'
        foreach ($directory in @($root, $config, $temp)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
        $keep = $mode -eq 'quickrestore2'
        if (-not $keep) {
            $settings = @{ protocol = @{ register = $false }; session = @{ confirmCloseWithAgents = $false }; extensions = @{ summon = @{ enabled = ($mode -eq 'summon') } }; notifications = @{ sinks = @{ sound = @{ enabled = $false }; toast = @{ enabled = $false }; palantir = @{ enabled = $false } } } }
            [IO.File]::WriteAllText((Join-Path $config 'settings.jsonc'), (ConvertTo-Json $settings -Depth 6).Replace("`r`n", "`n"))
        }
        $env:TEMP = $temp
        $env:TMP = $temp
        $environment = @{ OVERSHELL_WT_SETTINGS = $fixture }
        foreach ($key in $Env.Keys) { $environment[$key] = $Env[$key] }
        & (Join-Path $PSScriptRoot 'Invoke-SelfTest.ps1') -Mode $mode -Exe $Exe -ConfigDir $config -StateDir $state -KeepState:$keep -Desktop "overshell-$Tag-$group" -Env $environment
        $passed = $LASTEXITCODE -eq 0
        $log = Join-Path $temp 'overshell-selftest.log'
        $tally = if (Test-Path -LiteralPath $log) { (Select-String -LiteralPath $log -Pattern '=== selftest tally:' | Select-Object -Last 1).Line } else { $null }
        foreach ($name in @('overshell-selftest.log', 'overshell-agents.log', 'overshell-crash.log')) {
            $path = Join-Path $temp $name
            if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $root "$mode-$name") }
        }
        $savedConfig = Join-Path $root "$mode-configuration"
        [IO.Directory]::CreateDirectory($savedConfig) | Out-Null
        Copy-Item -Path (Join-Path $config '*') -Destination $savedConfig -Recurse -Force
        $results += [pscustomobject]@{ mode = $mode; passed = $passed; tally = $tally; directory = $root }
    }
}
finally {
    $env:TEMP = $originalTemp
    $env:TMP = $originalTmp
    [IO.File]::WriteAllText((Join-Path $Output 'summary.json'), (ConvertTo-Json -InputObject @($results) -Depth 5).Replace("`r`n", "`n"))
}
if (@($results | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 }
exit 0
