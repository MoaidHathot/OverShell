# OverShell 0.4.1

## Fix: native crash when displaying Sixel images

Fix a native termination triggered by applications that display Sixel images,
including the portrait in **`dnx MoaidHathot`**. The fault affected OverShell
0.3.1 and 0.4.0.

The corrected renderer uses a checked palette lookup that avoids an optimized
MSVC hardening failure. Native image rendering, all 256 palette entries and
bounds checking are preserved. This release includes the signed
**OverShell.Terminal.Wpf 1.26.260930.5** package.

## Verification

- **518 C# tests** pass with warnings treated as errors.
- **16 native checks** exercise the built DLL in opaque and composed hosts.
- **24 on-screen checks** cover real Sixel pixels, resizing, the full palette,
  and `dnx MoaidHathot -- --duration 6` in both rendering modes, with zero skips.
- The application exits successfully and subsequent shell/image output works.
- Native regression checks now run before CI/release packaging.

See the [diagnostic report](SIXEL-CRASH.md) for the root cause and reproducible checks.

## Upgrade

Close the running OverShell instance, then launch the fixed release:

```powershell
dnx OverShell@0.4.1
```

For a globally installed tool:

```powershell
dotnet tool update -g OverShell --version 0.4.1
```

Signed framework-dependent and self-contained x64 zips are attached to the
[GitHub release](https://github.com/MoaidHathot/OverShell/releases/tag/v0.4.1),
along with the tool package and `SHA256SUMS.txt`.
