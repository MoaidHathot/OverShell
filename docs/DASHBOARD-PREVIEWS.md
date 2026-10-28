# Faithful dashboard previews

## Goal and acceptance criteria

Show the terminal's rendered content in dashboard cards, preserving its colours,
font, glyphs and layout. Previewing a tab must preserve its terminal grid and input
focus. Supply styled snapshots when live composition is unavailable, plus an enlarged
hover/keyboard preview. Bound preview work to visible consumers and release resources
when cards, tabs or windows close.

## Committed stages

1. **Baseline and repeatable measurements.** Add a scratch-state benchmark for terminal
   idle, dashboard idle, dashboard busy and hidden-dashboard busy; record CPU time,
   allocations, private/working-set bytes, handles, and available GPU-engine counters.
   Measure the existing text dashboard before changing its rendering.
2. **Native mirror proof.** Extend the local terminal-control fork with lifetime-safe
   composition previews. Prove that hidden terminals update a scaled visual without
   resizing the source or routing input into it. Build/package the dependency locally.
3. **Dashboard integration and styled fallback.** Add a preview surface with viewport
   clipping, aspect-fit geometry, visible-consumer scheduling and failure recovery.
   Exercise composed and opaque terminals and repeat the benchmark against stage 1.
4. **Enlargement, hardening and final audit.** Add hover/keyboard enlargement, test focus,
   DPI/resize/scroll/close transitions, verify coloured pixels on the input desktop, run
   regressions and compare measurements against both baseline and the preceding stage.

The native implementation was initially built and tested locally under the terminal
fork's contribution policy. On 2026-10-28, the fork's maintainer explicitly authorized
public publication with AI-use disclosure. That disclosure is included in the native
package documentation and release notes.

## Benchmark protocol

`OVERSHELL_SELFTEST=previewbench` creates isolated PowerShell tabs, paints a deterministic
ANSI fixture and animates it during the busy phases. `OVERSHELL_PREVIEW_STAGE` labels
the evidence, `OVERSHELL_PREVIEW_TABS` sets the tab count (default 8), and
`OVERSHELL_PREVIEW_MODE` selects the dashboard mode once that setting exists.
`OVERSHELL_PREVIEW_OPAQUE=1` exercises an opaque terminal. Use the same tab count,
rendering mode, window dimensions and desktop for comparisons. Run via
`tools/Invoke-SelfTest.ps1` with scratch config/state directories and an approved TEMP.

The JSON report is written under the scratch state directory. CPU percent is expressed
as a fraction of **one logical core**, alongside raw CPU milliseconds and wall time.
GPU counters are labelled unavailable when Windows exposes no matching counters; a
private-desktop run does not establish on-screen composition fidelity. Pixel checks
are a separate, short, isolated input-desktop test.

## Native preview dependency

Normal OverShell builds restore **OverShell.Terminal.Wpf 1.26.260930.4**, which carries
the preview ABI in signed x64 and ARM64 native binaries. The implementation originated
in terminal-fork commit `0f7770824`; its touched source files use LF line endings.
For a local rebuild with the fork's documented Visual Studio/SDK prerequisites:

```powershell
.\build\overshell\Build.ps1 -Platform x64 -Wpf -Version 1.26.260930.4
.\build\overshell\Pack.ps1 -Version 1.26.260930.4-local.1 -Platforms x64
dotnet nuget push .\artifacts\packages\OverShell.Terminal.Wpf.1.26.260930.4-local.1.nupkg --source C:\nuget\local-feed
```

Select a local rebuild explicitly in `Directory.Packages.props` when testing it.
Preview entrypoints are capability-detected; a renderer without them uses the text
fallback. The local example builds x64, matching OverShell's supported architecture;
the native release workflow builds both x64 and ARM64.

## Evidence

Baseline and per-stage results will be recorded here after each measured run. Raw
reports and screenshots are retained under `artifacts/selftest/dashboard-previews/`.

### Baseline (8 tabs, 123 × 36 grid, composed, private desktop)

| Phase | CPU, one core | Allocated during phase | Private bytes | GPU-engine sum |
|---|---:|---:|---:|---:|
| Terminal idle, 5 s | 2.47% | 2.59 MiB | 313.6 MiB | 0.209% |
| Dashboard idle, 5 s | 2.78% | 1.84 MiB | 323.0 MiB | 0.028% |
| Dashboard busy, 8 s | 19.63% | 38.42 MiB | 323.5 MiB | 0.203% |
| Dashboard hidden/busy, 8 s | 14.47% | 31.90 MiB | 326.9 MiB | 0.178% |

All eight source grids survived the view transitions. The busy text dashboard took
56 UIA snapshots in the measured interval. These are comparison measurements, not
on-screen rendering evidence; the GPU column sums the process's matching engine counters.

### Native proof

Terminal fork commit `0f7770824` provides a token-based composition mirror
and a bounded, versioned styled-viewport snapshot ABI. The original proof used the local
`OverShell.Terminal.Wpf 1.26.260930.4-preview.1` package (x64). Mirrors share the renderer's
surface, fit its rendered size, track swap-chain replacement and remove their visuals
on disposal/source teardown. Short-lived composition targets are pruned from the cache.

The proof passed **20 input-desktop checks**, including red-to-green pixels from a
hidden source, the fitted image's far edge, clipping, and unchanged native/managed
grids. The opaque path also passed its styled-snapshot checks. Native snapshots retain
foreground/background colours, bold/italic/underline and wide Unicode; the fixture
explicitly uses UTF-8 so Windows PowerShell's OEM encoding cannot replace CJK with '?'.

With the native API installed but the original text dashboard still in use, the next
8-tab run measured terminal-idle/dashboard-idle/dashboard-busy/hidden-busy CPU at
7.71% / 1.24% / 14.06% / 31.44% of one core. Busy allocations were 36.49 MiB and
private bytes 327.7 MiB; there were **zero preview leases, styled captures and geometry
updates**, as expected before integration. The CPU spread makes a single short timing
insufficient to claim improvement/regression; final comparisons will include matched
text/live runs and distinguish observer/system variability from preview work.

### Dashboard integration

Cards now own read-only preview consumers. Native mirrors have no capture timer once
attached. Styled consumers share one cancellable snapshot/drawing per tab, with at
most two background drawing jobs. Offscreen/hidden consumers release leases and stop
snapshot work. The terminal host remains laid out while hidden, fixing background
tabs starting with tiny grids when created directly on the dashboard.

Validation at this stage: **513 C# tests**; composed dashboard **15 checks**, opaque
dashboard **14 checks**. Card-level pixel verification was initially interrupted by
the Windows lock screen; it was completed in the final stage below. Pixel probes now
check that the sampled point actually belongs to the test window, so a lock screen or
another window cannot be mistaken for the application.

Matched 8-tab integration runs (private desktop, same build, 8-second busy phase):

| Mode | CPU, one core | Allocated | Private bytes | Preview work |
|---|---:|---:|---:|---|
| auto/live | 18.43% | 33.69 MiB | 323.5 MiB | 4 visible leases; 0 snapshots/drawings |
| styled, 500 ms | 26.29% | 45.96 MiB | 333.4 MiB | 4 sources; 64 captures |
| text | 44.55% | 36.22 MiB | 320.7 MiB | 56 UIA snapshots |

All three modes performed zero preview captures while the dashboard was hidden.
The timing variance and styled-mode cost motivate conservative snapshot intervals and
another matched comparison after enlargement/lifecycle hardening.

### Enlargement and lifecycle hardening

Hover previews are owned tool windows with no-activate behavior; keyboard previews
accept navigation/Open/Close keys and explicitly return focus. Both share the card's
preview source. A source-tab close, view change, minimize or owner close removes the
enlarged view. Placement uses physical monitor work areas, including negative monitor
coordinates. Keyboard navigation also scrolls the chosen card into view.

The styled default is now **1000 ms**. Placement updates are coalesced, and offscreen
controls discard retained drawings as well as native leases. Legacy enlarged previews
have reference-counted text readers so they continue refreshing outside the dashboard
and release that polling when closed. The integration checks cover native and styled
consumers, keyboard/hover focus, snapshot sharing, resize without reflow, close-tab
cleanup and twenty repeated enlarged-window lifecycles with bounded handle use.

The regression pass also exposed older harness assumptions about child TEMP and JSON
argument quoting. The full harness now uses explicit output paths and native argv for
the Codex hook; a focused process test verifies quotes and Unicode through the installed
script. Direct programmatic palette dismissal now restores owner activation without
stealing focus from a replacement picker.

For a reproducible final comparison (including an archived baseline when available):

```powershell
.\tools\Invoke-PreviewBenchmarks.ps1 `
  -Exe .\artifacts\previews-build\bin\OverShell.App\release_win-x64\OverShell.exe `
  -BaselineExe .\artifacts\baselines\dashboard-text-0.3.1\OverShell.exe `
  -ResultsDir .\artifacts\selftest\dashboard-previews\final-bench -Repeats 2
```

The helper retains raw JSON/logs and a CSV, alternates mode order, and adds a doubled-tab
auto-mode case. Hover is disabled for these timed workloads to keep pointer position
from changing the number of preview consumers.

### Final comparison and requirements audit

Two alternating-order runs per mode used eight tabs with the same deterministic
workload, 123 × 36 source grids and a private desktop. CPU is the measured range as a
percentage of one core; allocation/private columns are two-run means for the busy
8-second phase. These short samples include observer/system variability, so the CPU
numbers establish the measured envelope rather than a precise speedup.

| Build/mode | CPU range | Allocated | Private bytes | Visible preview work |
|---|---:|---:|---:|---|
| Archived original text baseline | 10.00–28.98% | 37.98 MiB | 324.5 MiB | 48–56 UIA reads per run |
| Final auto/live | 22.85–23.93% | 32.85 MiB | 327.1 MiB | 4 leases, zero captures/drawings |
| Final styled, 1000 ms | 15.17–22.40% | 40.10 MiB | 325.6 MiB | 4 sources, 32–36 captures |
| Final text reference | 16.32–26.87% | 35.44 MiB | 325.8 MiB | 48 UIA reads per run |

The live path allocated about **13.5% less** than the archived baseline in these
workloads, while retaining full visual fidelity. Its CPU range lies within the observed
baseline range; no exact CPU improvement is claimed. Relative to the previous stage,
the conservative styled interval reduced captures from 64 to 32–36 in eight seconds.
The 16-tab live run still had **four visible leases and zero captures**; its host used
479.0 MiB private memory and 56.56% of one core while busy. Every mode released all
preview consumers and performed zero preview captures in the hidden-dashboard phase.

Final validation:

- **518 C# tests**, **11 bridge tests**, and a warnings-as-errors Release publish passed.
- **43 input-desktop preview checks** passed with no skips, including actual styled,
  live-card and enlarged-window pixels. Native red/green/blue changes were observed
  without activating or resizing the source terminal.
- **39 opaque-mode checks** passed on the private desktop; its on-screen checks were
  intentionally skipped there. Styled card pixels were also verified with an offscreen
  WPF render.
- Existing UI regressions passed: full harness 100, keynav 17, MRU 19, polish 14,
  history 14, profiles 69, overflow 34 and settings 17.
- Twenty enlarged-window open/close cycles left zero native/styled consumers and
  bounded handle counts. Source-tab closure, owner/view changes, scrolling, and text
  fallback reader cleanup were exercised.

Evidence: `final-visible.log`, `final-opaque.log`, `regression-1-final.log`, the other
`regression-*.log` files, `final-bench/summary.csv` and its raw JSON reports under
`artifacts/selftest/dashboard-previews/`. Visual evidence is in
`overshell-dashboard-previews.png` and `overshell-enlarged-preview.png`. The runnable
publish output is `artifacts/dashboard-preview-package/OverShell.exe`.
