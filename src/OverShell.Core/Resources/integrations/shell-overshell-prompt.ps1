# OverShell integration v1 - written by `OverShell integrations install shell`; safe to delete this block.
# Tells the terminal the current directory after every command (OSC 9;9, the sequence Windows
# Terminal documents for PowerShell), so an OverShell tab follows `cd` exactly and a restored tab
# reopens where you were. Wraps whatever prompt is defined above this block; does nothing outside
# OverShell and Windows Terminal.
if (($env:OVERSHELL_TAB_ID -or $env:WT_SESSION) -and -not $global:__OverShellPromptWrapped) {
    $global:__OverShellPromptWrapped = $true
    $global:__OverShellInnerPrompt = (Get-Command prompt -CommandType Function -ErrorAction SilentlyContinue).ScriptBlock
    function global:prompt {
        $location = $ExecutionContext.SessionState.Path.CurrentLocation
        $inner = if ($global:__OverShellInnerPrompt) { @(& $global:__OverShellInnerPrompt) -join '' } else { "PS $location> " }
        if ($location.Provider.Name -eq 'FileSystem') { "$([char]27)]9;9;$($location.ProviderPath)$([char]7)$inner" } else { $inner }
    }
}
# /OverShell integration
