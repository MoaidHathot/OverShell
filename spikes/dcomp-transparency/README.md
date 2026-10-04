# Spike 8 - transparency: can a DirectX surface in a WPF window blend with the backdrop?

Standalone, not part of the solution or CI. See DESIGN.md §7.6 and §12.7 (spike 8) for
the result and what it means for a fork of the terminal control.

```powershell
dotnet build spikes/dcomp-transparency/DcompSpike.csproj
$exe = "spikes/dcomp-transparency/bin/Debug/net10.0-windows/DcompSpike.exe"
foreach ($mode in 'child-hwnd', 'child-dcomp', 'child-dcomp-noredir', 'parent-dcomp', 'parent-dcomp-topmost', 'parent-dcomp-mica') { & $exe $mode; Start-Sleep 5 }
Get-Content $env:TEMP/opencode/dcomp-spike.log
```

Each run opens a WPF window (frame extended over the client, acrylic backdrop, a
translucent WPF caption, a WPF banner over the terminal area), draws one 800x450 frame -
45 % black, opaque white bars, an opaque orange block, an alpha-0 hole - the way the mode
says, captures itself with BitBlt three seconds later, samples the pixels into
`%TEMP%\opencode\dcomp-spike.log` and `dcomp-<mode>.png`, and exits.

| Mode | How | Result |
|---|---|---|
| `child-hwnd` | `CreateSwapChainForHwnd` on a child HWND - what `HwndTerminal` does | alpha ignored: black |
| `child-dcomp` | composition swapchain + DComp target on the child HWND | non-deterministic: backdrop one run in four, white the rest |
| `child-dcomp-noredir` | same, `WS_EX_NOREDIRECTIONBITMAP` on the child | white |
| `parent-dcomp` | composition swapchain as a DComp visual on the top-level window, no child | backdrop every run; WPF content under it is covered |
| `parent-dcomp-topmost` | same, `topmost = true` | identical |
| `parent-dcomp-mica` | same with Mica | identical, Mica-tinted |

`result-active-window.png`: `parent-dcomp`, `parent-dcomp-mica`, `child-dcomp` (a run
where the child blended with the backdrop), left to right.
