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
