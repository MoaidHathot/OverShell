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

The terminal fork's AGENTS.md permits local AI-assisted work and restricts larger public
contributions. Native changes and packages in this task are local; public submission
follows that repository's contribution rules.

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

The local terminal fork commit `0f7770824` provides a token-based composition mirror
and a bounded, versioned styled-viewport snapshot ABI. It is packaged locally as
`OverShell.Terminal.Wpf 1.26.260930.4-preview.1` (x64). Mirrors share the renderer's
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

Validation: **513 C# tests**; composed dashboard **15 checks**, opaque dashboard **14
checks**. Card-level pixel verification was interrupted by the Windows lock screen
and remains pending; the earlier native mirror pixel proof passed on an unlocked
desktop. The harness now rechecks desktop/foreground availability.

Matched 8-tab integration runs (private desktop, same build, 8-second busy phase):

| Mode | CPU, one core | Allocated | Private bytes | Preview work |
|---|---:|---:|---:|---|
| auto/live | 18.43% | 33.69 MiB | 323.5 MiB | 4 visible leases; 0 snapshots/drawings |
| styled, 500 ms | 26.29% | 45.96 MiB | 333.4 MiB | 4 sources; 64 captures |
| text | 44.55% | 36.22 MiB | 320.7 MiB | 56 UIA snapshots |

All three modes performed zero preview captures while the dashboard was hidden.
The timing variance and styled-mode cost motivate conservative snapshot intervals and
another matched comparison after enlargement/lifecycle hardening.
