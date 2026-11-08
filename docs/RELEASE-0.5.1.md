# OverShell 0.5.1

## Floating terminals inside the existing window

**Ctrl+Shift+Y** now opens an inset panel inside the current OverShell window, including
detached windows. Windows tiling managers have no additional terminal window to tile.
The original terminal, process and scrollback move with the tab, which remains listed.

Floating preserves the terminal's font size, colours and configured transparency.
Floating the current tab leaves a matching tinted backdrop; a different background
terminal stays at its original grid and pixel scale. This fixes the enlarged duplicate
text and unwanted opaque backgrounds in the previous floating presentation.

The panel has a rounded frame and a compact header: **X hides** it and **…** offers
regular view, expansion and termination. Drag the caption to move it, resize from the
lower-right corner, or double-click the caption to expand/restore within the window.
**Ctrl+Shift+W terminates** the tab; **Ctrl+Shift+H** returns to the interrupted work.

Initial size is capped by `floating.maxWidth` and `floating.maxHeight`, defaulting to
1200 × 800 DIPs. Set either to `0` to remove that cap. Size ratios, position and
focus-loss dismissal remain configurable.

## Font-size shortcuts

| Shortcut | Action |
|---|---|
| **Ctrl+=**, **Ctrl++**, **Ctrl+NumPad+** | Increase terminal font size |
| **Ctrl+-**, **Ctrl+NumPad-** | Decrease terminal font size |
| **Ctrl+0** | Reset to this tab's profile font size |

Zoom changes the native renderer's font size and reflows the terminal. It belongs to
the open tab and survives floating, hiding and detaching. Configure the increment with
`terminal.fontSizeStep` (default: 1 point); all shortcuts can be rebound or unbound.
The `terminal.setFontSize` command and shared tab API support explicit sizes and targets.

## Cleaner tab labels

Duplicate tabs use human project context where useful. Runtime-ID prefixes are now
opt-in with `tabs.showDuplicateIds`. Tab selectors show small numbers in tab-strip
order; type `@number` to select one. `tabs.showPickerNumbers` controls those numbers,
and **Show this tab's number and ID** exposes its runtime address when needed.

## Fixes and verification

- Removed one-pixel backdrop gaps and overlaps at fractional display scaling.
- Restored the original native grid immediately when hiding a float.
- Preserved terminal focus, session identity, background input and independent find/prompt bars.
- Accepted integer JSON arguments for parameterized font-size commands.
- Source verification passed **567 C# tests**, **35 on-screen appearance checks** across
  both rendering modes with zero skips, and the floating/zoom lifecycle regressions.
- The release workflow builds with warnings treated as errors, runs the C# and bridge
  tests, and Authenticode-signs the release binaries before packaging.

See the [user guide](https://github.com/MoaidHathot/OverShell/blob/v0.5.1/docs/GUIDE.md)
and [verification record](https://github.com/MoaidHathot/OverShell/blob/v0.5.1/docs/TAB-QUICK-ACCESS.md).

## Upgrade

Close the running OverShell instance, then launch the new release:

```powershell
dnx OverShell@0.5.1
```

For a globally installed tool:

```powershell
dotnet tool update -g OverShell --version 0.5.1
```

The framework-dependent and self-contained Windows x64 zips, .NET tool package and
`SHA256SUMS.txt` are attached to the
[GitHub release](https://github.com/MoaidHathot/OverShell/releases/tag/v0.5.1).
