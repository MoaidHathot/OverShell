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
- Final audit: pending.
