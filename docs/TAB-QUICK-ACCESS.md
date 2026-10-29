# Tab quick access: implementation and evidence

## Contract

- A tab's runtime identity, display name, alias, presentation and retention are independent.
- Aliases are exact, case-insensitive, unique across this instance and optional. Reserved
  prompt selectors cannot be aliases. Runtime aliases/bindings survive hiding, moving,
  process exit and configuration reload; closing releases them. Duplicate, reopen and
  restart create fresh runtime identities without aliases or temporary bindings.
- Configured alias bindings resolve at invocation; runtime assignments bind to tab IDs.
  Operations retain their resolved ID across asynchronous work.
- Attention jumps remember the original work across a chain. Last-tab navigation remains
  a separate operation. Return restores the view and input context and tolerates closed tabs.
- Any tab can float (including the only tab and tear-offs). One visible quick float per
  owner; replacing its target retains the original return context. The live surface moves,
  preserving the process and scrollback. The tab remains listed. Mirrors remain read-only.
- Hide releases the floating presentation; terminate closes the tab. Escape belongs to
  terminal programs. Focused floats own terminal commands and count as viewed.
- Scratch tabs are normal tabs with explicit retention, initially floating. Defaults:
  invoking directory, configured/default profile, no restore/history, keep exited output.
  Keep-tab promotes retention. One-shot command completion follows process exit.
- Core contracts/policies are UI-free. First-party UI, shortcuts and control adapters use
  the same services. Settings and bindings remain live configurable; scripting is future work.

## Stages and review gates

1. Command arguments/context and navigation: unit tests plus private-desktop attention
   chains, last-tab switching, view/focus restoration and closed-target behavior.
2. Aliases/runtime bindings: collision and selector tests, assignment UI, discovery,
   prompt/control addressing, reload and lifecycle cleanup.
3. Interactive floats: same HWND/PID/scrollback, focused command routing, return, only-tab,
   tear-off, dashboard, repeated lifecycle, notifications, HTTP/MCP and settings.
4. Scratch/one-shot creation: real successful/failed processes, retention/history,
   explicit keep/hide/terminate and API creation.
5. Final audit: review every contract above, full C# and bridge tests, warnings-as-errors
   build and relevant existing private-desktop regressions; document actual evidence.

Each implementation stage is reviewed and committed before the next. UI evidence uses
`tools/Invoke-SelfTest.ps1` on a private Win32 desktop, with isolated config/state/TEMP.
Input-desktop pixel evidence is reported separately if needed.

## Extensibility surface

The UI-thread contracts live in `OverShell.Core`, with WPF/native hosting in
`OverShell.App`. `IShell.Addresses` assigns/resolves runtime aliases; `IShell.Presentations`
shows/hides/toggles regular and floating presentations; `IShell.Navigation` captures
and restores return contexts. `IShell.Keys.Assign/Remove` manages tab-owned runtime
registrations, including native global hotkeys. `IShell.OpenTab(TabRequest)` and
`IShell.SetLifetime` control creation and retention. First-party commands, pickers,
notifications and `ShellControl` consume those services.

`CommandRegistry` carries cloned arguments and a captured `CommandContext`; the host
applies its target during eligibility and execution, including legacy command handlers.
Typed selectors require one ID, alias or unique label. HTTP/MCP adapters marshal work
onto the UI thread; asynchronous operations retain the resolved runtime ID.
`ActiveTab` is the main selection; `FocusedTab`/`FocusedTabChanged` describe the viewed
interactive terminal. Presentation and alias changes have their own events.

The terminal's original profile remains separate from its one-shot process command line.
Aliases, runtime shortcuts, return contexts, float hosts and prompt drafts are runtime
state. Retention policy can be saved with a restorable tab. One-shot commands are not
replayed by duplication/restoration. A later scripting adapter can expose these same
contracts without owning native controls or inventing another tab registry.

## Reproducing verification

```powershell
dotnet test OverShell.slnx -c Debug -p:Platform=x64
node --experimental-vm-modules --test tests/OverShell.Tests/OpenCodeV2Bridge.test.mjs
tools\Invoke-QuickAccessTests.ps1
tools\Invoke-QuickAccessTests.ps1 -Tag regressions -Output artifacts\selftest\quick-access\regressions `
  -Modes herdmode,mru,tearoff,triage,profiles,settings,history,previews,transfers,promptattachments,mcp,1
```

The matrix runner isolates each mode's TEMP/config/state and records logs plus
`summary.json`. `quickrestore1` and `quickrestore2` share only their restart fixture.
It uses private Win32 desktops and never switches the user's input desktop.
Set `-Env @{ OVERSHELL_PREVIEW_OPAQUE = '1' }` for opaque quick-access fixtures.

## Evidence

- Baseline: 521 C# tests passed, zero failures/skips (Debug x64).
- Stage 1: 525 C# tests passed; `quicknav` passed 11 private-desktop checks with
  zero skips. Covered real terminal focus, a two-hop attention chain, return view,
  prompt input focus, ordinary navigation cancellation and independent last-tab bounce.
  Review moved last-tab ownership out of the optional herd extension and made close-driven
  neighbor selection preserve the attention origin. Malformed keybinding fields now
  produce diagnostics rather than exceptions or accidental unbinding.
- Stage 2: 533 C# tests and 20 private-desktop checks passed (zero skips), covering exact/duplicate
  addresses, picker discovery, local/global registrations, parameterized configured
  bindings, reload, HTTP mutation, asynchronous ID retention and close/reopen cleanup.
  The runtime key layer is applied last; registration rejects existing chord/prefix
  conflicts. Later configured conflicts are reported by key-map validation. Display
  preferences use `tabs.aliasDisplay` (badge/primary/hidden). Review fixed the preexisting
  control screen/wait behavior that re-resolved mutable labels after every await.
- Stage 3: 537 C# tests passed. `floats` passed 45 checks in composed mode and
  45 in opaque mode, both on the private desktop with zero failures/skips. The checks
  retain exact HWND/PID and scrollback, exercise only-tab/tear-off/dashboard presentation,
  native paste/find focus, independent prompt drafts, replacement/return, 20 rehost cycles,
  picker/protocol/toast/HTTP/MCP entry points, unread acknowledgement, live settings,
  focus-loss/owned-picker behavior, minimize, owner closure and explicit termination.
  Review fixed stale shortcut HWND registrations after window closure, isolated body
  opacity and link overlays to the actual terminal host, and made triage follow focused
  rather than merely selected tabs. The endpoint control switch now applies live.
  DELETE on a control subresource now returns 405 instead of accidentally closing a tab.
- Stage 4: 555 C# tests and 31 private-desktop scratch checks passed, with zero
  failures/skips. Real processes cover successful/nonzero/failed launches, output
  retained after exit, close-on-success/always, termination, literal argv (including
  empty/quoted/Unicode/environment-token arguments), creation without focus, Keep,
  history exclusion and restoration without command replay. The fixture's unsigned
  PowerShell script needed a process-local execution-policy override; the application
  does not change the user's policy. Review also fixed saved active indices when
  non-restorable/exited tabs precede the selected tab and relative navigation from
  secondary hosts. A corrupt generated reference assembly was regenerated by a clean
  warnings-as-errors rebuild; no source workaround was needed.
  Follow-up review passed 21 alias checks and 46 floating checks in parallel on separate
  private desktops/TEMP roots. The runner's leftover diagnostic now scopes to the tested
  process family instead of incorrectly reporting another concurrent test instance.
- Final audit: 556 C# tests and 11 bridge tests passed. A warnings-as-errors Release
  publish passed. The final, applicable passing UI results total 557 checks with zero
  failures; one existing dashboard input-desktop pixel comparison was skipped to keep
  the user's desktop undisturbed. Earlier failures and their diagnostic logs are retained
  beside the passing reruns under `artifacts/selftest/quick-access/`.

| Final UI coverage | Passed | Evidence directory under `artifacts/selftest/quick-access/` |
|---|---:|---|
| Navigation, aliases, scratch, two-process restart | 77 | `release-features`, `final-attention`, `focus-final` |
| Interactive floats, composed | 51 | `float-acceptance` |
| Interactive floats, opaque | 51 | `float-acceptance-opaque` |
| Full application harness, triage, herd mode | 137 | `final-verdict` |
| MCP, previous screen, profiles, MRU, tear-off, history, settings | 157 | `release-core` |
| Opaque previews, transfers, prompt attachments | 84 | `release-opaque` (one foreground pixel check skipped) |

The runnable package is `artifacts/quick-access-package/OverShell.exe`. The matrix's
TEMP roots contain only test-owned files. Default summon hotkeys are disabled in its
fixtures; runtime global-hotkey registration is tested explicitly and released. Native
thread-local activation is used when a private-desktop foreground request is refused.
Readiness and scrollback checks wait for observed shell output rather than fixed sleeps.

## Final requirements review

| Requirement | Implementation and evidence |
|---|---|
| Temporary addresses and shortcuts | `TabAddressing`, runtime key registrations; assignment, collision, reload, close/reopen and cross-process restart checks |
| Stable targeting for control operations | Typed selectors and captured command context; asynchronous screen/wait checks retain the original runtime ID |
| Any tab can float while remaining listed | Same-surface hosting; native HWND/PID/scrollback checks for active, background, only and detached tabs |
| Return to interrupted work | Scoped floating origins and attention history; chained returns, prompt focus, dashboard state and closed-origin recovery |
| Hide versus termination | Distinct presentation and tab-close APIs; X/Return preserve the process, Close/exit policy removes it |
| Scratch and one-shot creation | Tab lifetime policy and exact process argv; real exit codes, launch failures, retained output, Keep, history and restoration checks |
| Configuration | Embedded JSONC defaults, key bindings and setting catalog; live geometry/dismissal/alias/control tests, plus existing settings regressions |
| Extensibility baseline | UI-free Core contracts; first-party controllers, HTTP and twelve MCP tools use the same services |
| Testing without disrupting work | Private desktop matrix, isolated TEMP/config/state, default summon disabled in fixtures, process-family cleanup |

The final review added guards against stale focus-loss callbacks closing a replacement
float and background creation replacing the user's focused float. It restored the last
remaining detached tab to the main host when its last main-host neighbor closes, and
kept one-shot command lines separate from reusable profiles. Floating attention uses
Ctrl+Alt+Shift+J so the inbox retains Ctrl+Shift+I.

Focus-loss dismissal is evaluated after activation settles across the whole owned-window
family. A regression exposed why listening only to the float's Deactivated event was
insufficient: while an owned picker has focus, the float is already inactive, so leaving
that picker for the main window produces no second float-deactivation event. The check
now follows picker closure/deactivation and host activation too, considers only current
presentations, and leaves never-focused background floats alone.

### Startup cursor regression found during the audit

The MCP regression returned actual rows such as `mcp-typedift+P`: command output had
overwritten part of the first-run welcome text. The renderer had consumed a preamble
while ConPTY still believed its cursor was at the viewport origin. Preamble sessions
now request `PSEUDOCONSOLE_INHERIT_CURSOR`; the existing asynchronous output/input
connection answers the cursor query. The original exact-row MCP assertion then passed
(21 checks), as did the restored-screen/ghost regression (7 checks), including text
above the new prompt. No assertion was weakened to accept the corrupted output.

### Completion signal precedence regression

With the welcome preamble now preserved, the full harness exposed an older detector
precedence bug: a lingering screen match for `working` overrode OSC progress state 4
(explicit completion). A new focused unit test first failed with Expected Done / Actual
Working. Title/progress busy and completion signals now precede the lower-confidence
screen working/done fallback; blocked/error evidence still has priority. The regression
also checks that a subsequent explicit busy signal resumes Working and that a permission
prompt still wins over completion.
