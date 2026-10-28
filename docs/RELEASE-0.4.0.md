# OverShell 0.4.0

## Faithful dashboard previews

- Dashboard cards show **live GPU-rendered terminal mirrors** with the composed
  renderer. Colours, fonts, glyphs and application layout come from the actual terminal.
- Opaque terminals use **styled full-viewport snapshots**, retaining colours,
  formatting and wide-glyph cell widths. `dashboard.previewMode` selects `auto`,
  `styled`, or the legacy `text` view.
- Preview scaling preserves the source terminal's rows and columns. Background tabs
  created directly on the dashboard now start at full terminal dimensions.
- Visible cards own preview work; scrolled-out cards and hidden/minimized windows
  release it. Styled consumers of the same tab share a change-driven drawing.

## Enlarged previews

- Hover over a dashboard card to enlarge it without taking keyboard focus.
- **Ctrl+Shift+O** opens an enlarged preview of the current terminal.
- On the dashboard, press **Ctrl+Shift+3 again**, choose a card with arrows, then press
  **Space**. In the preview, arrows change tab, **Enter** opens the terminal, and
  **Esc** or **Space** returns.
- Resizing the enlarged view preserves terminal dimensions. Preview windows close
  with their source tab and fit the monitor work area.

```jsonc
{
  "dashboard": {
    "previewMode": "auto",
    "snapshotIntervalMs": 1000,
    "hoverPreview": true,
    "hoverDelayMs": 500
  }
}
```

The palette exposes these settings. Live mirrors share rendered frames; the snapshot
interval controls changed styled snapshots. The card footer identifies the active backend.

## Fixes and verification

This release also fixes stale dashboard keyboard cursors after view changes and owner
focus restoration after programmatic palette dismissal.

Validation passed **518 C# tests**, **11 bridge tests**, **43 on-screen preview checks**,
**39 opaque-mode checks**, and the existing UI regression suite. Repeated profiling
compared the original baseline and each implementation stage: the measured live path
allocated about **13.5% less** than the archived text baseline, and 8- and 16-tab runs
both used four visible mirror leases with zero captures. CPU ranges and full evidence
are recorded in [the preview report](DASHBOARD-PREVIEWS.md#final-comparison-and-requirements-audit).

The release uses the signed **OverShell.Terminal.Wpf 1.26.260930.4** package, with
x64/ARM64 native builds and a documented preview ABI.

## Upgrade

Close the running OverShell instance, update, and launch the new version:

```powershell
dotnet tool update -g OverShell --version 0.4.0
```

See the [user guide](GUIDE.md#faithful-and-enlarged-previews) and
[tutorial](TUTORIAL.md#7-four-views-of-the-same-tabs) for controls and configuration.
