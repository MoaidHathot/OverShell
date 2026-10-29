# OverShell 0.4.2

## Tab creation controls

The **+** and profile arrow now sit immediately after the last tab. When tabs
overflow, the controls stay pinned beside the scrolling tabs and remain reachable
at minimum window width. Side-list and rail layouts keep their controls below
the scrolling list.

## Single-click dashboard opening

A **single click on a dashboard card opens its terminal**. It dismisses an open
hover preview, cancels pending hover timers, clears the dashboard keyboard cursor,
and restores terminal input focus. Clicking the enlarged hover preview also opens
the terminal through the same path.

## Seamless styled previews

Fix faint lines between background rows and runs when styled previews are scaled
to fractional sizes. Cell backgrounds now meet cleanly while text retains its
antialiasing.

## Verification

- **521 C# tests** passed with warnings treated as errors.
- **34 overflow checks** passed across crowded, minimum-width and side layouts.
- **95 on-screen preview checks** passed: 48 composed and 47 opaque, with zero skips.
- Pixel regressions cover styled backgrounds at 0.37, 0.77 and 1.25 scales.

See the [preview report](DASHBOARD-PREVIEWS.md#interaction-and-scaling-follow-up-2026-10-29)
for the implementation details and evidence.

## Upgrade

Close the running OverShell instance, then launch the new release:

```powershell
dnx OverShell@0.4.2
```

For a globally installed tool:

```powershell
dotnet tool update -g OverShell --version 0.4.2
```

Signed framework-dependent and self-contained x64 zips are attached to the
[GitHub release](https://github.com/MoaidHathot/OverShell/releases/tag/v0.4.2),
along with the tool package and `SHA256SUMS.txt`.
