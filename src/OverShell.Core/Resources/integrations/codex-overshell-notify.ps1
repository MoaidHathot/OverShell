# OverShell integration v1 - written by `OverShell integrations install codex`. Safe to delete.
# Codex CLI's `notify` hook runs this with the event JSON as the last argument; it is posted
# to the OverShell tab that launched Codex, over loopback, so the tab knows the turn ended
# (and which thread to resume). Outside OverShell the variables are absent and nothing runs.
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Args)

if (-not $env:OVERSHELL_ENDPOINT -or -not $env:OVERSHELL_TOKEN -or -not $env:OVERSHELL_TAB_ID -or $Args.Count -eq 0) { exit 0 }

$payload = $Args[$Args.Count - 1]
$uri = "$env:OVERSHELL_ENDPOINT/v1/codex/$env:OVERSHELL_TAB_ID/notify?token=$env:OVERSHELL_TOKEN"
try {
    # Bytes, not a string: Windows PowerShell would otherwise re-encode the JSON as Latin-1.
    Invoke-RestMethod -Method Post -Uri $uri -ContentType 'application/json' -TimeoutSec 3 -Body ([Text.Encoding]::UTF8.GetBytes($payload)) | Out-Null
} catch {
    # OverShell is gone or busy; Codex must not care.
}
exit 0