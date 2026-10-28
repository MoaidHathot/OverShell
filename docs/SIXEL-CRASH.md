# Sixel crash when running `dnx MoaidHathot`

## Cause

OverShell 0.3.1 and 0.4.0 could terminate when a terminal application displayed
a Sixel image. `MoaidHathot` uses Sixel for its portrait, so its normal startup
exercised the fault.

The recorded 0.4.0 crash was `0xC0000409`, subcode `FAST_FAIL_INVALID_ARG`, at
`Microsoft.Terminal.Control.dll + 0x5de09`. The release's matching symbols trace
it to `SixelParser::_colorFromIndex` during image decoding. Even a one-column
red Sixel image reproduces it through the public native API and the real ConPTY
path; neither DNX nor the application's 3D animation is required.

MSVC 19.51's optimized, hardened `std::array::operator[]` lookup with the
byte-sized palette index was compiled into an unconditional fail-fast. A small
C++ reproduction fails with `/O2` and hardening, but passes unoptimized, without
hardening, or with the checked `std::array::at` accessor. The fork's `Dev`
branding enables the hardening option; the regular Windows Terminal release
and preview branding do not. This explains the different behavior.

## Fix

The terminal fork now uses `_colorTable.at(static_cast<size_t>(tableIndex))`.
Palette bounds checking, all 256 colors, native Sixel images, and the rest of
STL hardening remain enabled. The native regression runs against the actual
optimized DLL in both opaque and composed hosts, before CI/release packaging.

The local dependency is **OverShell.Terminal.Wpf 1.26.260930.5-preview.1**,
built from terminal-fork commit `1d38fd8a0` and available in
`C:\nuget\local-feed`. It is a development package; the public OverShell 0.4.0
package uses the affected `1.26.260930.4` renderer.

## Reproduce the checks

From the terminal fork:

```powershell
.\build\overshell\Build.ps1 -Platform x64 -Wpf -SkipRestore -Version 1.26.260930.5
.\build\overshell\Test-Sixel.ps1
.\build\overshell\Pack.ps1 -Version 1.26.260930.5-preview.1 -Platforms x64
dotnet nuget push .\artifacts\packages\OverShell.Terminal.Wpf.1.26.260930.5-preview.1.nupkg --source C:\nuget\local-feed
```

Use a new preview version when rebuilding modified code; NuGet versions are
immutable. The consumer's normal restore selects the version in
`Directory.Packages.props`.

From OverShell:

```powershell
dotnet test OverShell.slnx -c Release -p:Platform=x64 -warnaserror
.\tools\Invoke-SelfTest.ps1 -Mode sixel -Visible `
  -Exe .\artifacts\bin\OverShell.App\release_win-x64\OverShell.exe `
  -Env @{ OVERSHELL_SIXEL_APP = 'dnx MoaidHathot -- --duration 6' }
```

The `sixel` self-test renders test images through a real shell, verifies all
256 palette entries can be decoded, checks actual red/blue pixels, resizes the
terminal, and verifies output after the optional real application exits.
`OVERSHELL_PREVIEW_OPAQUE=1` selects the opaque renderer in this test fixture.
The optional command runs as an interactive shell command so the test does not
depend on PowerShell's unsigned-script execution policy.

## Evidence

- The released native DLL fails the minimal-image probe with `0xC0000409`.
- The released DLL also terminates an isolated OverShell instance in the new
  real-ConPTY `sixel` regression before the image's completion marker is parsed.
- The corrected native DLL passes **16** scenarios across opaque/composed hosts:
  colored images, opaque backgrounds, transparency, the full palette,
  fragmented input and palette redefinition, aspect ratios/scrolling, the
  alternate screen, and resizing. Each scenario requires a subsequent native
  device-status reply.
- **518 C# tests** pass with the corrected package and warnings treated as errors.
- The real `dnx MoaidHathot -- --duration 6` command passes in both composed and
  opaque OverShell instances: **12 on-screen checks per mode, 24 total, with
  zero failures or skips**. The native portrait is visible, the command returns
  exit code 0, and the shell successfully renders another image afterward.
- Local crash analysis and raw test logs are under
  `artifacts/diagnostics/dnx-moaidhathot/`, including `fixed-final-app.log`,
  `fixed-opaque-app.log`, and `fixed-dnx.png`.

## Run the local fixed build

The runnable publish output is `artifacts/sixel-fix-package/OverShell.exe`.
Close the existing OverShell instance, then launch it from the repository:

```powershell
.\artifacts\sixel-fix-package\OverShell.exe
```

It uses the same native DLL as the validated local package. Public OverShell
0.4.0 remains the previously released build; these fixes are committed locally.
