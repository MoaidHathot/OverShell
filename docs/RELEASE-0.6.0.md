# OverShell 0.6.0

## Automatic tab hints

Small monospaced **tab hints** below the status dots give every tab a quick keyboard
address. Press **Ctrl+Shift+Q**, release the modifiers, then type the hint to jump.
**Esc** cancels. Digits `1`–`9` come first, followed by letters excluding `I` and `O`;
larger tab sets use unambiguous multi-key hints such as `01`. Keypad digits and
shifted number keys are supported.

Hints follow grouped tab-strip order. A pending selection keeps its original
runtime targets through reordering, tab closure and new tabs. The hint bar lists
off-screen tabs too, including in Zen and detached windows. Floating terminals
return through the shared presentation API with their process and scrollback intact.

- **Setting: Tab hints** selects always-visible or on-demand display.
- **Toggle tab hints** changes visibility for the current run.
- **Direct tab hint shortcuts** can generate bindings such as `Alt+A` on available
  keys. Existing commands, leader prefixes, user bindings and unbinding take priority.
- The picker itself is rebindable through `keybindings.jsonc`.

```jsonc
{
  "extensions": {
    "tabs.hints": {
      "display": "onDemand",
      "directModifiers": "alt",
      "timeoutMs": 10000
    }
  }
}
```

## Reliable restored tab details

The grey subtitle now keeps the selected agent session's directory authoritative.
Background tool processes cannot replace it, and late directory probes cannot undo
newer metadata. Shell announcements are remembered separately for returning to the
shell. Moving to another directory immediately clears obsolete branch/dirty details.

The OpenCode bridge waits for a selected session's own location to load, synchronizes
missing cached data, and rechecks selection after that await. This prevents a different
project's fallback directory from appearing during startup.

Additional fixes include:

- Crash-loop detection counts distinct runs, so archiving a crashed session does not
  turn one early crash into two. Clean and long-lived runs break the chain, including
  closing the final tab.
- Git decoration returns correctly after switching it off and back on.
- Grouped visual order agrees with keyboard navigation; drag hit-testing finds grouped tabs.
- Side-list and rail navigation scroll the selected tab into view.

## Extension APIs

The first-party `tabs.hints` extension uses public APIs for grouped display order and
change events, disposable runtime badge layers, replaceable default-key layers and
scoped one-shot key modes. Modes clean up on selection, cancellation, timeout, focus
loss, reload and disposal. Extension bars follow the invoking window, including tear-offs.

## Verification

- **594 C# tests** and **16 OpenCode bridge tests** passed.
- **550 application checks across 22 modes** passed with zero failures or skips,
  including an actual abrupt-termination/restart pair and two real OpenCode clients.
- Release builds and publication use warnings-as-errors checks and Authenticode signing.

See the [user guide](https://github.com/MoaidHathot/OverShell/blob/v0.6.0/docs/GUIDE.md#tab-hints-pick-a-tab-without-searching)
and [research, API and verification record](https://github.com/MoaidHathot/OverShell/blob/v0.6.0/docs/TAB-HINTS-AND-RESTORE.md).

## Upgrade

Close the running OverShell instance, then launch the new release:

```powershell
dnx OverShell@0.6.0
```

For a globally installed tool:

```powershell
dotnet tool update -g OverShell --version 0.6.0
```

Install the updated OpenCode bridge, then restart the OpenCode clients:

```powershell
dnx OverShell@0.6.0 -- integrations install opencode
```

The framework-dependent and self-contained Windows x64 zips, .NET tool package and
`SHA256SUMS.txt` are attached to the
[GitHub release](https://github.com/MoaidHathot/OverShell/releases/tag/v0.6.0).
