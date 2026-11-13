# Tab details and automatic navigation hints

## Investigation and plan (2026-11-13)

The second, grey line is `TerminalTab.Detail` (the **tab detail/subtitle**).
Its project comes from the working directory; the main label can independently
come from saved or live session metadata. Correct titles therefore do not prove
that the directory beneath them is correct.

Evidence collected before implementation:

- The installed OpenCode V2 bridge matched the baseline repository bridge byte-for-byte.
- The last local shutdown archive was marked `sessionEnding`, which exercises the
  same restored-screen path as an interrupted run. Saved title/directory combinations
  were consistent with the reported symptom. No startup agent trace was available,
  so the exact historical ordering cannot be reconstructed.
- `ApplyReport` sends integration directories through the same setter as process
  probes. A subsequent nested-shell/tool probe can overwrite them. Reports are
  change-driven, explaining why a later interaction can appear to repair a detail.
- `ShellCwdTarget` chooses any deepest descendant shell, including a shell run by
  an agent's tool or a shared background service, without checking its ancestors.
- The bridge falls back to its plugin/default location while the selected
  session's cache entry is absent. That is not evidence of the session's location.
- OSC title/directory fields are mutated on the I/O thread before notification is
  dispatched. Asynchronous probes carry no directory generation, and Git details
  can retain the previous directory's branch until the next two-second refresh.
- Baseline: 567 C# tests and 11 Node bridge tests passed.

Relevant contracts were checked against the current WPF dispatcher/data-binding
documentation and the [OpenCode V2 TUI plugin API](https://opencode.ai/v2/docs/build/plugins/cli).
The latter exposes selected-session lookup/synchronization and distinguishes
session locations from plugin/default locations.

### Step 1: correct directory provenance and restoration

1. Reproduce the competing-source and incomplete-startup-metadata cases before
   changing behavior.
2. Keep shell/probe location separate from integration-owned session location;
   reject obsolete asynchronous probe results and marshal updates onto the UI
   thread. Hand ownership back when the integration releases or its harness exits.
3. Keep restored metadata useful until reliable live metadata arrives; do not
   publish a different project's fallback as a selected session's location.
4. Prevent probing through agent/service processes into tool shells. Immediately
   invalidate directory-dependent branch/dirty details and verify live updates.
5. Exercise the real restoration, OSC, report, save/reopen and rendered binding
   paths on an isolated private desktop.

### Step 2: tab hints through the extension layer

1. Call the short navigation markers **tab hints**, separate from user aliases,
   tab names and persistent/runtime IDs. Use subdued monospaced hints below the
   status dot; emphasize them only during selection. Support always-visible and
   on-demand display, live configuration, and a visibility toggle.
2. Assign hints automatically in grouped strip order: `1`–`9`, then letters;
   provide unambiguous multi-key hints for larger tab sets. Preserve the existing
   positional `Alt+1`–`Alt+9` shortcuts. Start selection with `Ctrl+Shift+Q` by
   default; allow rebinding and optional generated direct shortcuts.
3. Expose display order/change events, disposable non-persisted badge layers,
   replaceable extension-default key layers, and a disposable one-shot key mode.
   The first-party extension must use these public contracts.
4. Capture hint-to-runtime-ID mapping during selection, so reordering or a closed
   tab cannot redirect a pending choice. Escape, invalid input, timeout, settings
   reload and focus changes must clean up the mode without leaking hint text.
5. Cover strip/list/rail, sidebar/dashboard, overflow, detached/floating tabs,
   duplicate names, grouping, user key overrides, extension disposal and reload.

### Review and verification gates

After each step: review the diff against the plan, run the focused regressions,
and address observed failures before proceeding. Final verification includes the
full C# and bridge suites, a warnings-as-errors build, relevant existing private-
desktop self-tests, and a final requirements/cleanup review. Test configuration,
state and TEMP are isolated; verification does not drive the user's live instance.

## Implementation and verification

### Directory ownership

`TabDirectory` separates the shell's observed directory from a reported session
directory. A session directory wins until its source releases or its harness goes
away; shell announcements continue updating the fallback. Reports without a
directory do not disable normal probing. Session/source changes drop the previous
owner's directory. Every observation advances a revision; a probe begun before a
new report/announcement/release cannot apply its old result afterwards.

The host applies OSC model changes on its dispatcher, invalidates the previous
repository/branch/dirty decoration immediately, and follows only shell-to-shell
process ancestry when probing. The OpenCode bridge synchronizes an uncached
selected session and rechecks the route after the await. It never substitutes the
plugin/default location for an incompletely loaded selected session.

Review also fixed Git decoration not returning after toggling it off/on without a
HEAD modification, and the crash-loop guard counting the newly archived copy of
the previous run as a second crash. The latter was exposed by an actual abrupt-
termination/restart pair, then reproduced by a failing policy unit test. The guard
now counts distinct start timestamps, retaining its two-real-crash behavior.
History deduplication retains distinct interrupted runs and a clean close after a
crash, even with identical tabs, so a clean/long-lived run really breaks the chain.
Closing the final tab also preserves that boundary; empty lifecycle records are
excluded from the history picker and jump list because they have no tabs to reopen.

### Hint UX and settings

Hints are `1`–`9`, followed by letters excluding `I` and `O`. The first 33 are
single-key hints. Overflow uses the reserved prefix `0` and equal-length suffixes,
so `01` can never be confused with `1`; this remains prefix-free for larger sets.
Hints are assigned by grouped strip order. They are separate from selector numbers,
aliases and saved/runtime identifiers. A pending pick snapshots runtime IDs and
continues to show that mapping despite reordering, additions or removal.

The default is a 10-DIP monospaced marker below the status dot, at reduced opacity.
Selection uses full opacity and semibold text. The rail gives it a separate bottom
row so harness, detached, mute and unread marks retain their space. All four views
share the same hints; a bounded scrollable hint/name bar also covers off-screen
tabs, hidden strips and detached windows. No native terminal surface is recreated.

`Ctrl+Shift+Q` invokes `tab.hints.pick`; `tab.hints.toggle` is available in the palette.
`extensions["tabs.hints"]` supports `display: "always" | "onDemand"`,
`timeoutMs` (1–60 seconds, default 10), optional `directModifiers` (e.g. `"alt"`),
and the standard `enabled` switch. Generated direct shortcuts skip occupied chords
and conflicting leader prefixes. User overrides/unbinding retain priority. Settings
and effective shortcut help refresh live. Normal `Alt+1`–`Alt+9` retain their meaning.

### Public extension contracts

- `IShell.DisplayOrder` and `TabOrderChanged`: the grouped order after collection
  changes and group edits. The host synchronizes WPF's retained group ordering
  with this order; drag hit-testing also uses the actual grouped tab presenters.
- `IShell.Badges.CreateLayer(owner)`: a disposable `ITabBadgeLayer`. `Replace` swaps
  that layer's `tabId -> TabBadge(Text, Help, Emphasized)` contributions. Later layers
  take precedence; disposal reveals earlier contributions; tab closure removes
  every owner's contribution. Badges are observable through `ITab.Badge`, never
  serialized into `ITab.Properties`, tab names or session files.
- `IKeyBindings.CreateDefaultLayer(owner)`: replaceable/disposable default bindings,
  below user configuration and runtime shortcuts. `MapChanged` reports the
  effective result, including runtime assignments.
- `IKeyBindings.EnterMode(bindings, closed, timeout)`: a captured, one-shot key map
  ahead of ordinary shortcuts/interceptors. It closes before dispatching its
  selected command, consumes invalid input/Escape, expires from the host heartbeat,
  and cleans up on focus loss, mouse interaction, reload, replacement or disposal.
  `ModeEntered` lets older non-modal keyboard cursors relinquish their keys.
- `IHostUi.ShowBar/HideBar` now follows the invoking host, including tear-offs.

For example, an extension can contribute a temporary marker and capture a selection
without accessing `MainWindow`, `TerminalTab`, native HWNDs or a private tab registry:

```csharp
var badges = shell.Badges.CreateLayer("example.pick");
var tab = shell.DisplayOrder[0];
badges.Replace(new Dictionary<string, TabBadge>
{
    [tab.Id] = new("A", "Choose this tab", Emphasized: true),
});
var mode = shell.Keys.EnterMode(
    [new Keybinding("a", "tab.activate", Arguments: new JsonObject
    {
        ["target"] = new JsonObject { ["id"] = tab.Id },
    })],
    closed: badges.Dispose);
// Retain the mode/badge leases for the interaction; dispose them when the extension stops.
```

The built-in `TabHintsExtension` uses these contracts itself. WPF appears only in its
optional hint bar; allocation, badge ownership, binding ownership, mode dispatch and
directory provenance are UI-free Core types.

### Verification record

Baseline and failing evidence are retained under `artifacts/selftest/tab-hints-restore/`
and `tests/OverShell.Tests/TestResults/`. The focused regressions first reproduced
incorrect probe ownership, stale Git details, incomplete bridge metadata, grouped
ordering drift, and the duplicate crash strike. Tests were not relaxed to accept
those behaviors. Two unrelated fixture assumptions were corrected: the Windows
PowerShell test process explicitly permits its generated prompt script, and the
directory test names its isolated TEMP rather than assuming children inherit it
when environment rebuilding is enabled. Viewport captures now use explicit visual
bounds instead of scaling overflowing descendants into a misleading screenshot.

Final results: **594 C# tests**, **16 bridge tests**, and **550 application checks
across 22 modes**, all passing with zero failures/skips. The Release publish passed
with warnings treated as errors. The runnable development build is
`artifacts/tab-hints-restore-package/OverShell.exe`.

| Application coverage | Passed | Evidence below `artifacts/selftest/tab-hints-restore/` |
|---|---:|---|
| Hint UX/lifecycle, actual kill/restart pair, two-crash guard and close question, against the Release package | 71 | `release-acceptance/hints`, `history-boundaries/details-restart`, `history-boundaries/resilience` |
| Directory ownership, previous screens, session history and jump list | 41 | `final-features/details`, `final-features/ghost`, `history-boundaries/history`, `history-boundaries/jumplist` |
| Labels and two real OpenCode V2 clients, metadata-only operations | 41 | `integration` |
| Announcing/non-announcing/nested shells and injected integration | 26 | `directory-fixtures` |
| Full application harness, herd mode, sidebar/card cursor and MRU | 160 | `regressions/1`, `regressions/herdmode`, `regressions/keynav`, `regressions/mru` |
| Overflow, profiles, settings, navigation, aliases and floating terminals | 211 | `regressions-rest` |

The first wider matrix reached its runner's two-minute command timeout during
`overflow`; that incomplete run is retained, and the remaining modes were rerun with
an unlimited outer timeout and the existing per-mode limit. The earlier resilience
fixture disabled its own close-question setting; the runner now enables that setting
for this mode, and the full close-question check passes. Neither incomplete run is
included in the passing totals above.

WPF viewport captures are alongside the hint run's logs, including top/bottom strips,
side lists, rail, and light-theme single-line tabs. These capture the managed chrome
on a private desktop, without DWM's backdrop or native terminal pixels. All test-owned
OverShell processes were closed; source whitespace checks pass and changed/new text
files use LF line endings.

Reproduce the focused application acceptance checks:

```powershell
tools\Invoke-QuickAccessTests.ps1 -Modes hints,details,details1,details2,resilience `
  -Exe artifacts\tab-hints-restore-package\OverShell.exe `
  -Tag tab-hints -Output artifacts\selftest\tab-hints-restore\recheck `
  -TempRoot "$env:LOCALAPPDATA\Temp\opencode\overshell-hints-tests"
```

`details1` writes a real live checkpoint and the launcher kills that test process.
`details2` starts a new process with a deliberately unrelated profile directory and
checks both restored subtitles before any integration report or tab interaction.

### Final requirements review

| Requirement | Implementation and evidence |
|---|---|
| Correct restored subtitles immediately and in background tabs | Directory ownership/revision tests; incomplete-cache bridge regressions; real bindings and abrupt restart checks |
| Later live changes remain accurate | OSC, nested-shell, integration release/session-switch and Git invalidation checks; existing label and real-client tests |
| Small, discoverable navigation markers | Shared quiet badge style; single/multi-key hints; emphasis while picking; viewport captures and light/single-line checks |
| Toggleable display and automatic shortcuts | Always/on-demand setting, runtime toggle, rebinding/unbinding and conflict-aware direct defaults, live reload tests |
| Correct targeting through tab lifecycle | Captured runtime IDs, reorder/group/close/add tests, native repeat suppression, timeout/focus cleanup and restart regeneration |
| Dogfood extensibility | `TabHintsExtension` uses public display-order, badge, default-key, mode, command and presentation contracts; disposal tests |
| Fix additional observed bugs | Stale Git toggle/cache, grouped order and drag targeting, vertical visibility, duplicate crash strike, history lifecycle deduplication; regression evidence retained |

To use the updated OpenCode startup reporting, install the bridge from this build
(`OverShell.exe integrations install opencode`) and restart the clients. Run the new
OverShell executable after closing the current instance; ordinary startup restores
the existing saved session.
