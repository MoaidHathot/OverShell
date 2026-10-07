# OverShell — Design & Development Notes

> **OverShell** = *Overseer Shell*. A Windows terminal **shell** (chrome, tabs, layout)
> wrapped around the real Windows Terminal rendering engine.

Last updated: 2026-10-03

How to *use* it - configuration, keys, views, agents, integrations, notifications - is
in [docs/GUIDE.md](docs/GUIDE.md). This document is about why it is built the way it is.

---

## 1. What it is

OverShell is a WPF application that hosts Microsoft's actual Windows Terminal control
and puts **our own** window chrome, tab strip and layout engine on top of it.

The terminal emulation — VT/ANSI parsing, the text buffer, the GPU-accelerated
DirectWrite renderer, selection, scrollback — is Microsoft's, unmodified. Everything
around it is ours.

```
┌──────────────────────────────────────────────────────┐
│  ● tab   ● tab   ● tab   +  ⌄            ─  □  ✕     │  ← ours (WPF)
├──────────────────────────────────────────────────────┤
│                                                      │
│   Microsoft.Terminal.Control.dll (native child HWND) │  ← Microsoft's
│                                                      │
├──────────────────────────────────────────────────────┤
│  ● PowerShell   C:\Users\…            132×37 · 4 tabs│  ← ours (WPF)
└──────────────────────────────────────────────────────┘
```

### Goals

- Full control over tab presentation: placement (top / left / bottom / right),
  multiple independent tab groups, drag-and-drop between them, custom layouts.
- Reuse the user's **existing Windows Terminal configuration** — profiles, colour
  schemes, fonts — with no separate config to maintain.
- Terminal fidelity that matches Windows Terminal, because it *is* Windows Terminal's
  renderer and parser.
- Access to the raw ANSI byte stream in both directions.
- **Session management above the terminal**: observe and drive what runs inside a tab —
  for example several AI coding agents, each in its own tab — rather than only display it.
  See §11.6.

### Non-goals

- Replacing Windows Terminal. OverShell reads WT's config; it never writes to it.
- Being a profile editor. Profiles are edited in Windows Terminal's own settings UI.
- Writing a terminal emulator. We host one — and must be able to host a *different* one
  without rewriting the shell (§11).

---

## 2. Why this shape — the core decision

Windows Terminal **cannot be embedded**. There is no supported API.
[microsoft/terminal#6999](https://github.com/microsoft/terminal/issues/6999)
("Productize the WPF, UWP Terminal Controls") has been open since 2020 and sits in the
icebox: *"we don't expect the core team to ever have the resources to get around to
this."*

Three viable approaches were evaluated in depth; a fourth (D) was added in 2026-09 after
Devolutions changed course.

### Option A — embed the real Windows Terminal process

Devolutions shipped this commercially in Remote Desktop Manager via
[`Devolutions/wt-distro`](https://github.com/Devolutions/wt-distro). The whole mechanism
is a **31-line patch** to `IslandWindow::MakeWindow()`:

```c
// reads the WT_PARENT_WINDOW_HANDLE environment variable
if (hWndParent) {
    dwExStyle &= ~WS_EX_TOPMOST;
    dwStyle = WS_CHILD | WS_BORDER;   // instead of WS_OVERLAPPEDWINDOW
}
CreateWindowEx(..., hWndParent, ...);
```

WT creates its window as a `WS_CHILD` of *your* HWND at creation time — no `SetParent`
reparenting. Post-hoc `SetParent` on stock WT **does not work**; that is precisely why
Devolutions forked.

`IslandWindow::MakeWindow` in today's `main` is **byte-identical** to the 2023 patch
context, so rebase cost is effectively zero.

- ✅ Acrylic, opacity, background images, retro CRT — everything, free
- ✅ Reads the real `settings.json`, including dynamic profiles, with zero re-implementation
- ❌ Must build Windows Terminal from source per release
- ❌ No access to the ANSI byte stream (WT owns the ConPTY end to end)
- ❌ Keyboard focus lives in another process → needs a `WH_KEYBOARD_LL` hook
- ❌ No clean way to move a live session between panes

> **Status 2026-09:** Devolutions no longer invests in this path. Their current work is
> Option D below. The patch still applies to `main`, but nobody is maintaining a
> distribution built on it.

### Option B — host the WPF control in-process ← **chosen (default surface)**

[`EasyWindowsTerminalControl`](https://github.com/mitchcapper/EasyWindowsTerminalControl)
(MIT, actively maintained) wraps `CI.Microsoft.Terminal.Wpf`, which P/Invokes the genuine
`Microsoft.Terminal.Control.dll`.

- ✅ In-process: keyboard focus, drag-and-drop and layout are ordinary WPF
- ✅ Full ANSI stream interception, both directions
- ✅ Live sessions can be detached from one control and re-attached to another
- ❌ **No transparency of any kind** in the terminal body (see §7.6)
- ❌ No built-in search, shell-integration marks, command palette (hyperlinks: solved
  on top, §11.8)
- ❌ Dynamic profiles must be re-implemented (see §6)

### Option C — write our own emulator

Rejected. The whole point is Windows Terminal fidelity.

### Option D — a managed terminal surface with no HWND

Added 2026-09. This is what Devolutions is now building:
[`awakecoding/terminal`](https://github.com/awakecoding/terminal/tree/copilot/dotnet-avalonia-port),
branch `copilot/dotnet-avalonia-port` — product name **Devolutions Terminal**. A complete
re-implementation of Windows Terminal in C# on .NET 10, published NativeAOT, rendered with
Avalonia 12 + Skia + HarfBuzz, cross-platform (ConPTY on Windows, `forkpty` on Linux).
Own VT parser and text buffer, own renderer, a full port of the `settings.json` /
actions / keybindings model, a `dt` CLI, a multi-window broker, MSIX/deb/rpm/AppImage
packaging. It is a fork of `microsoft/terminal` only because the C++ tree is their
**compatibility oracle**: `tools/Devolutions.Terminal.PortInventory` scans it and emits
`compat/windows-terminal.json` (120 settings keys, 92 actions, 123 `ITermDispatch`
methods) that their tests assert against. Nothing from Microsoft's binaries is shipped.

Their `docs/decisions/0001-skia-renderer.md` gives the same reason we gave in §7.6 for not
reusing AtlasEngine: *"Embedding Atlas would retain a large C++/WinRT boundary."*

Two facts about it matter to us:

- It **proves the shape**: a terminal surface that is an ordinary framework visual — no
  child HWND — gets transparency, overlays, airspace-free composition and normal keyboard
  routing for free. Every item in §7.2, §7.5 and §7.6 disappears for such a surface.
- It **proves the cost**: read their `dotnet/docs/parity-status.md` before being tempted.
  Grapheme clusters, reflow, images, Kitty keyboard, IME, UIA, search across reflow — it
  took a company plus a coding agent, and the long tail is still documented as open. This
  is exactly what Option C predicted.

Option D is therefore not "write our own" — it is **"make the surface pluggable, and pick
a second surface that someone else maintains"**. Candidates and their real costs are in
§11.4. The engine they made swappable, `libghostty-vt`, is discussed there too: it is a
parser and screen-state library only, *not* a control — whoever adopts it still writes
the renderer.

### The decision

**Option B stays the default surface.** Layout control and configuration reuse were the
primary goals, and B keeps those in one process where they are simple. Transparency in the
terminal body is the price on *this* surface, and it is a real one — see §7.6 for exactly
why it cannot be recovered without a different surface.

**But the surface becomes a seam, not a foundation.** §11 formalises what `TerminalTab`
already is informally — the only type that touches the session and the control — into
`ITerminalSession` and `ITerminalSurface`, so that a second surface (§11.4) can be added
behind a profile flag without touching the layout engine. Option A remains documented as
one possible surface; it is no longer the escape hatch.

---

## 3. Architecture

```
OverShell.slnx
├── Directory.Build.props          net10.0-windows · x64 · win-x64 RID · artifacts output
├── Directory.Packages.props       central package management
├── tools/
│   └── Show-LinkTestCard.ps1      the manual test card (§7.8) — run it inside a tab
└── src/
    ├── OverShell.Config/          no UI dependency — unit-testable in isolation
    │   ├── Models.cs                TerminalProfile, ColorScheme, TerminalCursorShape
    │   ├── Json.cs                  JSONC helpers (WT settings allow comments + trailing commas)
    │   ├── WindowsTerminalSettings  locates & parses settings.json, applies profiles.defaults
    │   ├── FragmentLoader.cs        fragment extensions (Git Bash, VS Debug Console, …)
    │   ├── Generators/              re-implemented dynamic profile generators
    │   │   ├── PowerShellCoreGenerator.cs
    │   │   └── VisualStudioGenerator.cs
    │   ├── DefaultSchemes.cs        WT's 16 built-in colour schemes (embedded resource)
    │   └── ProfileCatalog.cs        merges all of the above into one resolved list
    └── OverShell.App/             WPF
        ├── MainWindow              chrome, tab strip, status bar, tab lifecycle, link hover/click
        ├── TerminalTab             one profile + one session + one surface (the only type that touches either)
        ├── ShortcutRouter          pre-dispatch chord + terminal mouse interception (see §7.2)
        ├── WindowChromeInterop     DWM: dark mode, rounded corners, system backdrop
        ├── TerminalThemeMapper     ColorScheme → TerminalTheme (COLORREF)
        ├── TabAccent               deterministic per-profile accent colour (FNV-1a)
        ├── Theme/                  Palette.xaml, Controls.xaml
        ├── Overlay/
        │   └── OverlayHost           the one way to draw over the terminal body (§11.5)
        └── Terminal/               the seam (§11)
            ├── ITerminalSession, ITerminalSurface, SessionDescriptor, SurfaceCapabilities
            ├── TerminalFactory       which session/surface pair a tab gets
            ├── TerminalStreamState   DECSET 2004/1000-1003 tracking, OSC 8 ledger
            ├── ConPty/               ConPtySession + ConPtyNative — our own pseudoconsole
            ├── WindowsTerminal/      WindowsTerminalSurface + SessionConnection — the default surface;
            │                         AtlasFontMetrics — the renderer's underline geometry, re-derived (§7.13)
            └── Hyperlinks/           HyperlinkDetector (text), TerminalTextProbe (UIA: text, search, geometry, colour)
```

### Dependency graph

```
OverShell.Terminal.Wpf 1.26.260930.2        (our fork of microsoft/terminal, signed, §12.20; was CI.Microsoft.Terminal.Wpf 1.25.260303002)
├── Microsoft.Terminal.Wpf.dll          (managed HwndHost wrapper, ITerminalConnection)
└── Microsoft.Terminal.Control.dll      (native — the real WT control, 1.6 MB)
Microsoft.Windows.Console.ConPTY 1.24.260710001
├── conpty.dll                          (pseudoconsole API; prefers a sibling OpenConsole)
└── x64\OpenConsole.exe                 (the console host Windows Terminal ships)
```

`EasyWindowsTerminalControl` was the original glue between the two and is gone: the
pseudoconsole is driven directly (`ConPtySession`) and the control is hosted directly
(`WindowsTerminalSurface`). See §11.7 for why.

### Process model

Identical to Windows Terminal's — including the console host, since 2026-09:

```
OverShell.exe        ~156 MB    1 UI process
├── OpenConsole.exe    ~9 MB    ┐ one pair per tab
├── pwsh.exe         ~114 MB    ┘
└── …
```

Measured Windows Terminal for comparison: `WindowsTerminal.exe` 185 MB + 6 ×
`OpenConsole.exe` + 7 × `pwsh.exe`. **Opening a tab does not spawn a new UI process** in
either.

### One surface per tab — and why

Each tab owns its own `ITerminalSurface`. Inactive tabs are `Visibility.Hidden`
(not `Collapsed`, which would resize the grid to zero and reflow the shell's output).

This matters because **the scrollback buffer lives in the surface, not in the session**.
Sharing one surface and swapping sessions would discard history on every tab switch.

---

## 4. Build & run

```powershell
dotnet build OverShell.slnx -c Debug -p:Platform=x64
.\artifacts\bin\OverShell.App\debug_win-x64\OverShell.exe
```

Requires .NET 10 SDK, Windows 10 19041+ (Windows 11 22621+ for the system backdrop).
**x64 only** — `Microsoft.Terminal.Control.dll` is not published for AnyCPU, and the
solution pins x64 so only that copy is shipped.

### Environment variables

| Variable | Values | Purpose |
|---|---|---|
| `OVERSHELL_BACKDROP` | `acrylic` (default), `mica`, `micaalt`, `none` | System backdrop behind the chrome |
| `OVERSHELL_CONFIG_DIR` | a directory | Configuration root override (see *Roots* below) |
| `OVERSHELL_TRACE_KEYS` | `1` | Log every chord the router sees to `%TEMP%\overshell-keys.log` |
| `OVERSHELL_TRACE_LINKS` | `1` | Log link hover/click resolution to `%TEMP%\overshell-links.log`, and run one UIA self-probe after the first tab is ready |
| `OVERSHELL_TRACE_AGENTS` | `1` | Log harness detection, state transitions with their evidence, endpoint traffic and notification dispatch to `%TEMP%\overshell-agents.log` |
| `OVERSHELL_PROFILE_ROOT` | a directory | Stands in for *Documents* when `integrations install shell` looks for the PowerShell profiles (tests) |
| `OVERSHELL_SPIKES` | `1`, `find` | Run the §12.7 spikes in-process (`find`: spike 7, the UIA text provider as a search engine), log to `%TEMP%\overshell-spikes.log` |
| `OVERSHELL_STATE_DIR` | a directory | State root override (`state.json`, `session.json`) |
| `OVERSHELL_SELFTEST` | `1`, `opencode`, `opencode-resume`, `session1`/`session2`, `sessionend`, `history`, `icons`, `polish`, `cwd`, `resilience`, `ghost`, `workspaces`, `overflow`, `jumplist`, `theme`, `tearoff`, `find`, `inject`, `env`, `herdmode`, `mru`, `summon`, `address`, `keynav`, `inbox`, `triage`, `opencode-reply`, `spawn`, `spawn-opencode`, `mcp`, `herdlog`, `changes`, `transparency` | Run the end-to-end self-test in-process (§12.9–12.17): stream signals, endpoint, hook shims, process probe, palette focus, labels, views, layouts, reload, prompt bar, groups, explain, protocol handoff, skins; `opencode` runs the real `opencode run` with the installed plugin, `opencode-resume` resumes a session by id in a second process; `session1` then `session2` check restore across a restart (placement, tear-off, archive); `sessionend` sends WM_QUERYENDSESSION; `history` closes, reopens and lists sessions; `icons` renders the harness icons; the P5 modes (§12.14) each cover one batch - polish, the directory probe, the restore hold and close question, the previous screen, workspaces, strip overflow, the jump list, themes, tear-off chrome, find; `inject` the shell integration on the command line and `env` the registry-built environment (§12.15); the P6 modes one keyboard feature each (§12.16); `inbox` and `triage` the reply channels, inbox, toasts, mute and watch, `opencode-reply` the channel against the real OpenCode (§12.17). Log: `%TEMP%\overshell-selftest.log`. Run through `tools\Invoke-SelfTest.ps1 -Mode <mode>`: a private desktop, so nothing takes the focus (§12.21); `-Visible` for the pixel checks |
| `OVERSHELL_WT_SETTINGS` | a file | Read this Windows Terminal `settings.json` instead of the installed one (a portable Terminal; tests that need profiles the machine lacks) |

Every child process additionally receives `OVERSHELL_ENDPOINT`, `OVERSHELL_TOKEN`,
`OVERSHELL_TAB_ID` and `COPILOT_HOOK_ALLOW_LOCALHOST=1` (§12.4).

### Roots

Configuration (edited, worth syncing) and state (machine-local) live in two roots, each
the first that applies — an explicit override, then the XDG variable plus `\overshell`,
then the Windows default:

| | Configuration | State |
|---|---|---|
| 1 | `OVERSHELL_CONFIG_DIR` | `OVERSHELL_STATE_DIR` |
| 2 | `$XDG_CONFIG_HOME\overshell` | `$XDG_STATE_HOME\overshell` |
| 3 | `%APPDATA%\OverShell` | `%LOCALAPPDATA%\OverShell` |

XDG is honoured because people who keep dotfiles in one directory already point
OpenCode, Neovim and git at it through `XDG_CONFIG_HOME`; OverShell should land there
too without a second variable. A relative XDG value is ignored, as the specification
says. `AppPaths.ResolveConfigRoot`/`ResolveStateRoot` are pure over an environment
lookup (unit-tested); `OverShell settings path` prints the outcome and the variable
that decided it. Only the two roots are created at start — the optional sub-folders
(`agents\`, `layouts\`, `skins\`) appear when something is put in them, so a
dotfiles repository is not littered with empty directories.

### Command line

```
OverShell                                   # the window; a second start hands over and exits
OverShell --fresh                           # the window without the last session (it stays in the history, §12.13)
OverShell overshell://focus/<tabId>         # handed to the running window (§12.11); also view/<id>, new?profile=&cwd=, workspace/<name>, reopen, history[/<archive>] (§12.14)
OverShell integrations status
OverShell integrations install   <opencode|copilot|claude|codex|shell|all>   # shell: the prompt hook in both PowerShell profiles (§12.14)
OverShell integrations uninstall <opencode|copilot|claude|codex|shell|all>
OverShell integrations show      <claude|codex>   # the entries, for adding by hand
OverShell protocol status|register|unregister
OverShell settings path|init|open           # roots and files; starter files from the defaults
OverShell version                           # also --version, -v: version+sha and the image path
OverShell help                              # also --help, -h, -?, /?
```

A GUI process has no console; the parent's is attached — unless stdout is redirected,
in which case the redirected handle is used as-is (`AttachConsole` would replace it).
Started through a tool wrapper (`overshell` from `dotnet tool install`, `dnx OverShell`)
the window detaches itself so the wrapper returns at once; `--no-detach` keeps it in the
foreground (§13.2).

### Diagnostics

- `%TEMP%\overshell-crash.log` — every unhandled exception, always written
- `%TEMP%\overshell-keys.log` — key trace, only when `OVERSHELL_TRACE_KEYS=1`
- `%TEMP%\overshell-links.log` — link trace, only when `OVERSHELL_TRACE_LINKS=1`
- `%TEMP%\overshell-agents.log` — agent trace, only when `OVERSHELL_TRACE_AGENTS=1` (also the directory probe, themes and find)
- `GET %OVERSHELL_ENDPOINT%/v1/tabs?token=%OVERSHELL_TOKEN%` from inside any tab — every tab's harness, state and explain trail

### Build layout

`UseArtifactsOutput` puts everything under `artifacts/` (already git-ignored).
`Directory.Build.props` strips any native PDBs a package carries (`CI.Microsoft.Terminal.Wpf`
shipped ~82 MB **per architecture** — build output went from **246 MB → 2 MB**; the fork's
package keeps them out and attaches them to its GitHub release instead, §12.20). Set
`-p:OverShellKeepNativePdbs=true` to keep them when debugging Windows Terminal itself.

### ⚠ NuGet on this machine

`api.nuget.org` fails the TLS handshake (`SEC_E_ILLEGAL_MESSAGE`) across **every**
resolved edge IP, while `www.nuget.org` and `globalcdn.nuget.org` return 200 — SNI-based
blocking, consistent with `nuget.org` being disabled in the machine's global NuGet config
in favour of internal Microsoft feeds.

Restore works fine through those feeds. **The repo deliberately ships no `NuGet.config`**:
pinning the private feed would leak an internal URL into a public repository, and forcing
`nuget.org` would break restore locally. `NU1507` is suppressed in
`Directory.Build.props` with that reasoning recorded inline.

---

## 5. Coding conventions

- Comments explain **why**, never what. If a line looks wrong but is deliberate, the
  comment says what breaks without it.
- Zero build warnings. Not "few" — zero.
- `OverShell.Config` and `OverShell.Core` must stay free of any WPF reference. `Core`
  holds everything testable by plain xunit: commands, keybindings, agent rules and the
  state machine, search, notifications policy, settings, the integration protocol.
- Anything touching a session assumes it may already be dead (see §7.3).
- Only `TerminalTab` and the `Terminal/` folder may name a session or surface
  implementation. The chrome sees `ITerminalSession` / `ITerminalSurface` and nothing else.
- Every chord goes through `keybindings.jsonc` → command id → `CommandRegistry`. Nothing
  in the window binds a key directly.

---

## 6. Configuration pipeline

`settings.json` only stores a **stub** for dynamic profiles — guid, name, source, and no
commandline. The commandline lives in C++ generators inside Windows Terminal and is
computed at runtime. On the reference machine, 9 of 12 profiles were such stubs,
**including the default profile**.

`ProfileCatalog.Load()` reassembles them:

1. Parse `settings.json` (JSONC), applying `profiles.defaults` inheritance.
2. Load fragment extensions from `%ProgramData%` and `%LocalAppData%`
   `\Microsoft\Windows Terminal\Fragments`. These carry a full commandline.
3. Run re-implemented generators for `Windows.Terminal.PowershellCore` and
   `Windows.Terminal.VisualStudio`.
4. Merge stubs with resolvers, matched on **`(source, name)`** — user settings win
   wherever they specify anything.
5. Append generated profiles not yet present in `settings.json`.
6. Overlay user `schemes` on WT's 16 built-ins.

### Why match on `(source, name)` and not GUID

Windows Terminal derives dynamic profile GUIDs as
`CreateV5Uuid(CreateV5Uuid(RUNTIME_GENERATED_PROFILE_NAMESPACE_GUID, source), name)`
with namespace `{f65ddb7e-706b-4499-8a50-40313caf510a}` — SHA-1 over RFC-4122 byte order
plus UTF-16LE string bytes.

This was implemented and tested against real data. Result:

| Profile source | Derived GUID matches? |
|---|---|
| Fragment-supplied (`Git`, `VSDebugConsole`) | ✅ yes |
| `Windows.Terminal.PowershellCore` | ❌ no |
| `Windows.Terminal.VisualStudio` | ❌ no |
| `Windows.Terminal.Azure` | ❌ no |

The built-in generators use **their own GUID schemes** (derived from install paths and
instance ids), not the documented name-based derivation. Reproducing them faithfully is
not worth it — `(source, name)` is stable, simple and sufficient. Fragments still match
by GUID first, since they declare one explicitly.

### Generator details

Commandlines are copied verbatim from Windows Terminal's source so behaviour matches:

- **PowerShell Core** (`PowershellCoreProfileGenerator.cpp`): quoted full path to
  `pwsh.exe`; highest version wins the bare name `"PowerShell"`.
- **VS Developer Command Prompt** (`VsDevCmdGenerator.cpp`):
  `cmd.exe /k "<install>\Common7\Tools\VsDevCmd.bat" -startdir=none -arch=x64 -host_arch=x64`
- **VS Developer PowerShell** (`VsDevShellGenerator.cpp`): `Enter-VsDevShell <instanceId>`
  with `-SkipAutomaticLocation`, triple-quote escaped.
- Name suffix is `catalog.productLineVersion` from `vswhere` (`2022`, `18`) — this is what
  produces names matching `settings.json` exactly.

**Not implemented:** `Windows.Terminal.Azure` (Azure Cloud Shell) — needs an auth flow.
It is reported through `ProfileCatalog.Diagnostics` rather than failing silently.

---

## 7. Field notes

Hard-won findings. Each one cost real time to discover; none is obvious from the outside.

### 7.1 Sessions can move between controls — with a caveat

Verified experimentally: detaching a live `TermPTY` from one control and attaching it to
another **keeps the shell alive**, PIDs cross over exactly, and output keeps flowing.

But what transfers is the **visible screen, not the scrollback** — ConPTY repaints the
current viewport on resize, and that repaint is what populates the new control. History
above the fold is lost.

Consequence: this is viable for drag-and-drop *between groups*, but **must not** be used
for ordinary tab switching. Hence one control per tab.

### 7.2 Keyboard input is genuinely hostile

Three separate traps, in order of discovery:

**(a) The control forwards every key to the terminal.**
`TerminalContainer` hooks `WM_KEYDOWN` and calls `TerminalSendKeyEvent` unconditionally,
so WPF `InputBinding`s never fire — the shell swallows `Ctrl+T` first. `ShortcutRouter`
hooks `ComponentDispatcher.ThreadPreprocessMessage`, which runs before `DispatchMessage`,
and claims a small set of reserved chords there.

**(b) `Keyboard.Modifiers` is stale.**
It is derived from WPF's input stack, which never sees these keystrokes. Shift is
invisible, so `Ctrl+Shift+X` silently collapses into `Ctrl+X`. Read modifier state from
Win32 `GetKeyState` instead.

**(c) No `KeyboardNavigation` mode lets Tab through.**
`Contained` and `Cycle` shuffle focus among the control's children (the terminal HWND and
its ScrollBar); `None` and `Once` move focus *past* the container. **All of them mark the
key handled**, so the message is never dispatched and the shell never receives Tab.

The only fix is to claim the key and deliver it manually:

```csharp
SendMessageW(msg.hwnd, msg.message, msg.wParam, msg.lParam);
handled = true;
```

`SendMessageW` invokes the child's window procedure directly rather than posting, so the
message bypasses the queue and the hook does not observe it again — no recursion.
Applies to Tab **and the arrow keys**, which use the same mechanism.

### 7.3 Late input must never throw

Historical origin: the `TermPTY` class OverShell used until 2026-09 threw from
`WriteToTerm` once its pipe writers were gone —

```csharp
void ITerminalConnection.WriteInput(string data) {
    if (span.Length > 0 && !_ReadOnly)     // ← the only guard
        WriteToTerm(span);
}
public void WriteToTerm(ReadOnlySpan<char> input) {
    if (_consoleInputWriter == null && _consoleInputWriterB == null)
        throw new InvalidOperationException("There is no writer attached to a pseudoconsole…");
```

— while the control kept its `Connection` reference and kept delivering focus and key
messages after a session ended or while the `HwndHost` unloaded. The next one threw on
the dispatcher.

The lesson outlived the class and is now the contract of `ITerminalSession`:

- `WriteInput` **silently drops** input after `CloseInput()`, after exit, and after
  disposal. `ConPtySession` implements it as a flag plus a guarded pipe write; it cannot
  throw for a dead session.
- `CloseInput()` is a flag only. Closing the input *pipe* makes the console host tear the
  session down, and this call has to be safe while the surface is still alive.
- Teardown order is still: input off → session disposed → surface disposed → view
  removed from the tree, because unloading the `HwndHost` generates focus traffic.
- On shutdown, all tabs stop accepting input before any of them is disposed.

### 7.4 `HasStarted` has a startup race

The session starts on its own thread, so the child process does not exist for the first
moments of every tab. A naive "not running ⇒ died" check is therefore **true on every
healthy new tab**. `ITerminalSession` splits it into `HasStarted` and `IsRunning`; exit
detection requires `HasStarted && !IsRunning`, and `Exited` is the authoritative signal.

### 7.5 Window chrome: two traps

**`GlassFrameThickness="-1"` is a trap.** It hands the non-client frame to DWM, which then
(a) draws **its own caption buttons** over the custom ones, and (b) makes the whole client
area glass, so every repaint gap flashes through to the desktop. Keep it at `0` and call
`DwmExtendFrameIntoClientArea` manually with margins covering **only the chrome bands**.
Those margins are physical pixels — recompute them on `DpiChanged`.

**An unpresented swapchain composites as transparent.** A freshly created terminal owns a
child HWND whose swapchain has not presented a frame yet, so revealing it immediately
flashes the desktop through. Reveal only after the surface's `Ready` plus one turn at
`DispatcherPriority.Render`, with a timeout fallback so a bad commandline cannot leave a
permanently blank pane.

### 7.6 Transparency was structurally impossible here (until the fork, §12.20)

Asked and investigated properly. The `Microsoft.Terminal.Control.dll` C API exports
19 functions and **none** touch opacity:

```
AvoidBuggyTSFConsoleFlags  CreateTerminal  DestroyTerminal  TerminalCalculateResize
TerminalDpiChanged  TerminalGetSelection  TerminalIsSelectionActive
TerminalRegisterScrollCallback  TerminalRegisterWriteCallback  TerminalSendCharEvent
TerminalSendKeyEvent  TerminalSendOutput  TerminalSetFocused  TerminalSetTheme
TerminalTriggerResize  TerminalTriggerResizeWithDimension  TerminalUserScroll
DllCanUnloadNow  DllGetActivationFactory
```

`TerminalSetTheme` takes `COLORREF` — 24-bit, no alpha by definition.

`HwndTerminal.cpp` creates its window with **`dwExStyle = 0`** — no
`WS_EX_NOREDIRECTIONBITMAP`, no layering — and calls `AtlasEngine::SetHwnd`, an
HWND-attached swapchain. **DXGI's `CreateSwapChainForHwnd` ignores alpha mode**: such a
swapchain physically cannot blend with what is behind the window. Real Windows Terminal
transparency comes from `CreateSwapChainForComposition` (DirectComposition) plus
`WS_EX_NOREDIRECTIONBITMAP`. The WPF control has neither.

The renderer itself is perfectly capable — `AtlasEngine::EnableTransparentBackground`
exists and sets `s->target->useAlpha` — but `HwndTerminal` never calls it and never
exports it.

Two further dead ends: the package ships **no `.winmd`**, so the full WinRT `TermControl`
inside that DLL (it does export `DllGetActivationFactory`) has no projectable metadata;
and `SetTheme`'s `externalBackground` parameter is unrelated — it only paints the sliver
where the WPF control is larger than the character grid.

**What we did instead, until §12.20:** a Windows 11 system backdrop behind the title bar and
status bar only, with the terminal body opaque — `useAcrylicInTabRow: true` but *not* per-profile
`useAcrylic` / `opacity`. **Now** (2026-10-07): with the control built by our own fork
(`OverShell.Terminal.Wpf`, §12.20), `window.terminalOpacity` below 1 renders the body through a
composition visual on the top-level window and the backdrop shows through it — measured on
screen, `#0C6106` for a half-opaque `#0C0C0C` over acrylic-blurred lime. The rest of this
section stands as the analysis of the control *as Microsoft ships it*.

**What would work** (spike 8, §12.7, 2026-10-04): the terminal drawn into a composition
swapchain with premultiplied alpha and composed as a DirectComposition visual on the
**top-level** window blends with the acrylic every time — Windows Terminal's look —
whereas the same visual bound to a child HWND blends with white three runs in four. That
fixes the shape of a fork: no child HWND in the rendering path, the swapchain handle and
input exported through the C API, OverShell composing on its own window.

A transparent composition target also makes WPF **drop ClearType**; chrome text is
explicitly set to `Grayscale` so it degrades predictably instead of fringing.

### 7.7 Windows Terminal's single-instance key

For unpackaged builds, the mutex/window-class name is
`branding + " " + hash(exePath) + " " + hash(userSID)`, with `WM_COPYDATA` handoff
(`WindowEmperor.cpp`). The old monarch/peasant `Remoting` layer and `isolatedMode` are
**gone** from `main`. Relevant only if Option A is ever revisited: N copies in N
directories give N independent instances for free.

### 7.8 Testing rule — no synthetic input. Ever.

`SendKeys`, `keybd_event`, `SendInput`, `mouse_event` and `SetForegroundWindow` must
never be used to drive this app.

This is not stylistic caution. It caused real damage: synthetic `Ctrl+Shift+W` landed in
the user's other applications and closed windows, losing data.

It was also **useless as a test**: `SendKeys` injects printable characters as
`VK_PACKET` (0xE7) via `KEYEVENTF_UNICODE`, so the router correctly reported a key it
could not identify. The "failure" was an artifact of the test method.

**Instead:** build, hand the binary to a human, and diagnose from
`%TEMP%\overshell-crash.log` and `OVERSHELL_TRACE_KEYS=1`.

What *is* allowed, and used: launching the built exe, reading its window through UI
Automation (read-only), killing a child process to exercise the exit path, and
`CloseMainWindow()` — none of that injects input into anything. Redelivering a message the
user's own mouse produced, to the window it was addressed to, is likewise not synthetic
input (§11.8).

The human's side of this is scripted: `tools/Show-LinkTestCard.ps1`, run inside a tab,
prints every case the link code has to handle with what should happen, plus the shortcut
checklist. With `OVERSHELL_TRACE_LINKS=1`, `%TEMP%\overshell-links.log` records the
decision taken for every hover and click, with timings, so a report is "line N: what I
saw" plus that file.

### 7.9 ConPTY, owned directly — what the API actually does today

Learned while replacing the wrapper (`ConPtySession`, `winconpty.cpp` on `main`):

- **`ClosePseudoConsole` no longer waits.** It closes the signal and reference handles
  and returns. The host sees the signal pipe break, delivers `CTRL_CLOSE_EVENT` to every
  attached client (same path as closing a console window, 5 s grace) and exits; the
  output pipe then reports `ERROR_BROKEN_PIPE`, which .NET surfaces as a zero-length
  read. Nothing about teardown has to block the UI thread.
- **`ReleasePseudoConsole` right after `CreateProcess`.** Without it the host stays alive
  for as long as we hold the reference handle, even after the shell has exited. With it,
  the host exits when the last client detaches — so the pipe breaking *is* the exit
  signal, and no output can be lost between "process gone" and "pipe drained".
- **Exit is reported in order.** Windows Terminal's sequence is copied: the process wait
  fires → close the pseudoconsole (covers background children that keep the console
  alive) → the I/O thread drains to EOF → print `[process exited with code N]` → raise
  `Exited`. Verified live by killing `pwsh.exe` under a running tab.
- **`conpty.dll` looks for `<dir>\OpenConsole.exe`, then `<dir>\<arch>\OpenConsole.exe`,
  then falls back to `System32\conhost.exe`.** The NuGet package copies
  `x64\OpenConsole.exe` when `PlatformTarget=x64`; `ConptyRequiresARM64Host` is switched
  off in the csproj so the ARM64 copy is not shipped as well.
- **The environment block is ours.** `WT_SESSION` and `WT_PROFILE_ID` are set as Windows
  Terminal does, and `SessionDescriptor.Environment` overlays on top. This is the hook
  the agent-session work needs (§11.6).
- **Win32-input-mode** is still requested by writing `ESC[?9001h` *to the terminal* once
  the child exists — the same trick the wrapper used; the host decodes those records
  unconditionally.

### 7.10 The native control's text is readable through UI Automation — in-process

`HwndTerminal` answers `WM_GETOBJECT` with the same `ITextProvider` Windows Terminal
uses for screen readers, and the WPF wrapper deliberately lets that message through. So
the buffer *is* reachable, without any native change:

- `AutomationElement.FromHandle(terminalHwnd)` → `TextPattern`. First call ~45 ms
  (assembly load), afterwards `RangeFromPoint` + `ExpandToEnclosingUnit(Line)` +
  `GetText` is ~2 ms.
- **Call it from a worker thread.** A UIA client call on the provider's own UI thread is
  the documented deadlock. `Task.Run` is enough; results are awaited back on the UI thread.
- `TextUnit.Line` is one **buffer row** (`_expandToEnclosingUnit`: `_start.x = 0;
  _end = {0, y+1}`), not a logical line. Every row's text comes back **padded to the full
  column count** (`trimTrailingWhitespace=false`), **one character per glyph** — a row
  holding wide glyphs (CJK, emoji) has fewer characters than cells, one with combining
  marks more. Never derive a column count from text length; the surface knows its grid.
- **Wrap state is encoded in the line breaks.** `_getTextValue` uses
  `CopyRequest{includeLineBreak=true, formatWrappedRows=false}`, and `_RowCopyHelper`
  appends CR LF only when `!row.WasWrapForced()`. Reading row by row (`Move(Line, ±1)`
  on a row range) and stopping at the first row whose text ends in CR LF yields the
  logical line with the buffer's own wrap flags, no inference. ConPTY preserves wrapping
  when it repaints, so the flags match what the shell produced.
- **`FindText` searches wrapped rows as continuous text.** `TextBuffer::SearchText` runs
  ICU over a `UText` that joins rows (`UTextAdapter.cpp`) and inserts `\n` only after
  rows that did not wrap. So searching a logical-line range for the exact text of a
  wrapped URL finds it, and the returned range's `GetBoundingRectangles` are computed by
  the terminal from its own cell layout — wide glyphs, wrapping and all. This is what
  makes cell geometry exact without a width table on our side.
- **`FindText`'s range is one glyph too long.** `BufferRangeFromMatch` returns a half-open
  span; `UiaTextRangeBase::FindText` then increments the end again "because it's
  exclusive" (a comment left over from the inclusive `Search` era). The extra glyph shows
  up as the found range's text being longer than the needle; `MoveEndpointByUnit(End,
  Character, -1)` corrects it — after stripping the CR LF a row end carries from the
  comparison, because at a row end the "extra glyph" *is* that CR LF and pulling back
  would drop the last real one. A match ending in the last column of a non-wrapped row
  may instead be pushed past the search range by that increment and come back as *not
  found* — the approximate geometry covers that case.
- `GetAttributeValue(ForegroundColorAttribute)` returns the **rendered** foreground as a
  COLORREF (`_RemoveAlpha(GetAttributeColors(attr).first)`), or the mixed sentinel when
  the range is not uniform; `FontNameAttribute` returns the family AtlasEngine actually
  resolved (`fontInfo.SetFromEngine(primaryFontName, …)`) — "Cascadia Mono", not the
  comma list the profile asked for.
- `GetBoundingRectangles` on a row range returns exactly `columns × cellWidth` by
  `cellHeight` in physical pixels. A point below the last written row is snapped to that
  row by the provider, which shows up as a row rectangle that no longer contains the
  pointer.
- Coordinates are physical screen pixels, exactly what `MSG.pt` carries.

This is how Ctrl+click on a URL works on the default surface (§11.8).

### 7.11 The pointer over the native window is set from inside `WM_SETCURSOR`

The terminal's window class carries an I-beam and `DefWindowProc` re-applies it on every
mouse move, so `SetCursor` from outside is overwritten immediately. `HwndHost.MessageHook`
runs *before* the hosted window's own procedure, and marking `WM_SETCURSOR` handled there
after calling `SetCursor(IDC_HAND)` is all it takes. `WindowsTerminalSurface.Pointer`
wraps this; a managed surface would just set `FrameworkElement.Cursor`.

### 7.12 Drawing over the terminal: what the overlay window needs to be

`OverlayHost` is the layered-window workaround from §11.5, built. The details that
matter, each of which cost a wrong first attempt somewhere in the WebView2 world:

- **Owned** by the main window (`Window.Owner`), so it sits above it in the z-order
  without being topmost, minimises with it, and closes with it.
- **`WS_EX_LAYERED`** (WPF `AllowsTransparency`) for per-pixel alpha, plus
  **`WS_EX_TRANSPARENT`** so hit-testing skips it entirely and the terminal keeps
  receiving mouse messages underneath — hover keeps working while the underline is up.
  `WS_EX_TRANSPARENT` only has that meaning on layered windows.
- **`WS_EX_NOACTIVATE`** + `ShowActivated=false`. Verified: showing it leaves `GetFocus()`
  unchanged, so the shell keeps every keystroke.
- **Positioned with `SetWindowPos` in physical pixels**, never via `Left`/`Top`/`Width`/
  `Height`: those are DIPs whose scale depends on which monitor the window is on, and this
  window follows the terminal across monitors. If the move changes the window's DPI, WPF
  re-applies the OS-suggested size once; a second `SetWindowPos` on the new monitor sticks.
- **Sized to what it draws**, not to the terminal. A layered window costs an
  `UpdateLayeredWindow` of its whole bitmap on every change; an underline is 224×2 px,
  the terminal is 1862×1990.
- `DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND`, or Windows 11 rounds the corners of a
  two-pixel-high window into nothing.
- Anything positioned in screen pixels is stale the moment the main window moves,
  resizes or deactivates; the hover it belongs to is cleared on all three.

### 7.13 Putting the underline where AtlasEngine would

The renderer never tells anyone where it draws underlines, but it derives that from the
font deterministically (`AtlasEngine::_resolveFontMetrics`), so it can be re-derived:

```
fontSizeInPx    = pt / 72 · dpi
designUnitsPerPx = fontSizeInPx / designUnitsPerEm
advanceHeight   = (ascent + descent + lineGap) · designUnitsPerPx
cellWidth       = roundf(advance of "0" · designUnitsPerPx)      cellHeight = roundf(advanceHeight)
baseline        = roundf(ascent·dupp + (lineGap·dupp + cellHeight − advanceHeight) / 2)
underlineTop    = roundf(baseline − underlinePosition·dupp)      thickness = max(1, roundf(underlineThickness·dupp))
```

`AtlasFontMetrics` runs exactly this over `DWRITE_FONT_METRICS` (via a 60-line DirectWrite
COM shim), resolving the family the way the renderer does — first existing entry of the
comma list, else Consolas — or, better, the family the terminal *reports* through UIA
(§7.10). Two traps: `roundf` rounds halves away from zero and `MathF.Round` does not; and
arrays in `ComImport` interfaces marshal as `SAFEARRAY` unless told `LPArray`, which
silently hands DirectWrite garbage (the "0" advance came back as the 0.5 em fallback).

The result is only trusted when it also predicts the cell size the terminal reports —
`14×28` for Cascadia Mono 12 pt at 150 %, versus `13×28` for Consolas. Same height,
different width, which is exactly why both are checked. Mismatch means "wrong font" and
the overlay falls back to a bar near the cell's bottom.

Verified live: predicted `14×28` = observed, underline top 23 / thickness 1 against a
baseline of 22, and the overlay window placed at `y = 110 + 23`.

### 7.14 A pseudoconsole child inherits *our* redirected stdio unless told otherwise

Found by launching OverShell from a tool with stdin/stdout piped: the tab's `pwsh` printed
its banner into that pipe, complained that "the console output doesn't support virtual
terminal processing or it's redirected", read EOF from the pipe and exited 0 within
1.4 s. The pseudoconsole never saw it.

Without `STARTF_USESTDHANDLES`, the console subsystem gives a console child the parent's
standard handles whenever those are not console handles — a pipeline, a CI runner, a
launcher that captures output. `bInheritHandles = FALSE` does not prevent it. Setting the
flag with all three handles `NULL` makes the child take the pseudoconsole's own. Windows
Terminal never hits this because a packaged GUI app has no standard handles at all.

Verified: same launch, shell alive after 6 s, nothing leaked to the pipe, clean exit.

### 7.15 What ConPTY forwards from the application to us

Measured with the self-test (§12.9), one `pwsh` command line emitting each sequence:

| Sequence | Forwarded by the bundled OpenConsole? |
|---|---|
| OSC 0 / 2 title | yes (already relied on) |
| OSC 9;4 progress (`state;percent`) | yes |
| OSC 777;notify;title;body | **yes** — the rule files' `notification` patterns do fire |
| bare BEL | yes |
| OSC 133 A/B/C/D marks | yes |
| DECSET 2004 / 1000–1003 / 1004 | yes (the private-mode tracker sees them) |

`TerminalStreamState` decodes them on the I/O thread and queues them; the tab drains the
queue on the UI thread into `AgentStateMachine`, one dispatcher operation per burst.

---

## 8. Status

### Closed

| Item | Notes |
|---|---|
| Feasibility gate (7 criteria) | Render, resize, DPI, selection/copy, paste, scrollback, independence, **live session detach/reattach** — all verified |
| `OverShell.Config` | 10 profiles resolved with real commandlines on the reference machine |
| Custom window chrome | Dark mode, rounded corners, custom caption buttons |
| Tab strip | Accent dots, active indicator, hover, close, middle-click |
| Single terminal by default | No panes unless asked for |
| Live tab titles | OSC 0/2; working directory via OSC 7 and 9;9 |
| Status bar | Profile, working directory, grid size, tab count |
| Theming | WT colour schemes → `TerminalTheme`, 16 built-ins embedded |
| System backdrop | Acrylic/Mica on chrome, graceful fallback, `WM_SETTINGCHANGE` aware |
| Build hygiene | 0 warnings; 246 MB → 2 MB output |
| Crash resilience | Dispatcher exceptions logged, app survives, dialog shown at most once |
| **Session / surface seam** (§11.7 phases 1–2) | `ITerminalSession` + `ITerminalSurface`; `ConPtySession` owns the pseudoconsole; `WindowsTerminalSurface` hosts `Microsoft.Terminal.Wpf` directly; `EasyWindowsTerminalControl` removed; `OpenConsole.exe` bundled. Verified live: start, title, kill-shell → exit banner, graceful close tears down host + shell, no crash log |
| **Hyperlinks** (§11.8) | Hovering underlines the link under the pointer in its own colour at the renderer's underline position and shows it in the status bar; Ctrl turns the pointer into a hand; Ctrl+click opens it; OSC 8 links resolved by visible text; wrapped URLs and wide glyphs handled by asking the terminal (§7.10). Text logic and metric reconstruction verified by script (34 checks); UIA walk, `FindText` geometry, colour/font attributes and the overlay verified in-process by the trace-mode self-test; **confirmed by a human 2026-09-13** (below) |
| **Overlay host** (§7.12, §11.5) | Click-through, never-activated owned window for drawing over the terminal body. Carries the link underline today; the drag preview, toasts and badges go here next |
| **Paste parity** | Line endings → CR, other C0 dropped, bracketed when DECSET 2004 is on |
| **Herd overseer P0** (§12.8) | `OverShell.Core` (WPF-free, 99 xunit tests): commands, `keybindings.jsonc`, agent rule files + `AgentStateMachine`, fuzzy search, notification policy, `settings.jsonc`, integration protocol + installer. App: every chord through `keybindings.jsonc` → `CommandRegistry`; palette / tab switcher / rename as an owned window (§12.7 spike 3); `TerminalStreamState` decodes BEL, OSC 9;4, 9/99/777, 133, DECSET 1004; detector fed by title, screen snapshot (UIA, 300 ms debounce), process tree probe (toolhelp), output activity; loopback endpoint with per-run token, environment injected into every child; Copilot hook shim + OpenCode plugin written by `integrations install`; two-line tab item with state dot / ring / pulse, unread badge, progress bar; `tab.jumpToAttention`; status-bar counts; sinks `overlay` (non-activating owned toast window), `taskbar` (badge + progress + flash), `sound`, `command` (Palantir recipe, flags verified); labels persisted per profile + directory. **Verified in-process** (§12.9) including the real OpenCode plugin end-to-end; ConPTY stdio bug found and fixed (§7.14) |
| **Herd overseer P1 — views** (§12.10) | Layouts as JSONC regions (`layouts/*.jsonc`, 8 presets, user files replace by name); four views (`terminal`, `herd`, `dashboard`, `zen`) = layout + content, retunable in `settings.jsonc`; the tab strip in three shapes (strip / list / rail) moved between caption, bottom and side hosts — the terminal never re-parents; the **Herd sidebar** (grouped by project, attention → recency, rollups, activity age, context menu); the **Dashboard** (one card per tab, body = UIA screen text in the tab's colours, refreshed once a second while showing); view switch with attention badge; `view.*`, `view.toggle`, `layout.*` (per-session override), `settings.reload` commands; **hot reload** of `settings.jsonc`, `keybindings.jsonc`, `agents\`, `layouts\` (watcher, 400 ms debounce); switcher screen preview; git branch from `.git/HEAD` (+ optional dirty marker via `git status`); right-click tab menu. 110 unit tests; 60+ in-process checks incl. every preset rendered (§12.10) |
| **Herd overseer P2 — depth** (§12.11) | **Session restore**: `session.json` written on close and every 30 s, tabs (profile, directory, label, group), view and layout overrides reopened at start, an agent that was running gets its **resume command typed** once the shell is quiet (`opencode --session <id>` etc., from the integration's report or the rule file). **`overshell://`** registered per user (HKCU) at start; a second instance hands its URL to the running one over a named pipe and exits, granting it the foreground — so a Palantir toast click (`--launch overshell://focus/{tab.id}`, now in the shipped recipe) lands on its tab. **Prompt bar** (`Ctrl+Shift+Enter`): send to the active tab, every agent, the agents needing you, or every tab; history; **snippets** from `snippets.jsonc` as commands and a menu. **Tab groups** (`tab.moveToGroup`, headers in strip and list) and **drag reorder** (live move as the pointer crosses neighbours; crossing a group joins it). **Explain panel** (`Ctrl+Shift+E`): harness, authority, session, processes, transitions. **XAML skins** (`skins\<name>.xaml`, `settings.skin`) with live recolour. **Claude Code hooks** merged into `~/.claude/settings.json` under a marker (refused, not rewritten, when the file has comments), `/v1/claude/{tab}/{event}`. 138 unit tests; 75 in-process checks + a restart pair; ConPTY-safe throughout |
| **Herd overseer P3 — reach** (§12.12) | **Codex CLI `notify`**: `integrations install codex` writes a PowerShell shim next to `config.toml` and a marked `notify` line among its top-level keys (a `notify` of the user's is never replaced); the payload becomes an **advisory** report — the turn's end, the thread id for `codex resume`, the last message as summary — without taking authority, since Codex never says `working`. **Native toast sink** (`type: toast`): WinRT over hand-written COM, no CsWinRT (the output stays at 2 MB); AUMID under HKCU; click → `overshell://focus/<tab>`; verified by reading the shell's notification history back with Windows PowerShell. **Tear-off windows** (`Ctrl+Shift+X` / `Ctrl+Shift+A`): the live surface moves into a window of its own and back — same HWND, session alive — while the tab stays in the collection for detection, sidebar, dashboard, notifications and the session file; chords, right-click and link hover follow the window they happen in. 145 unit tests; 84 in-process checks + restart pair + OpenCode e2e |
| **Distribution** (13) | Three channels from one tag: **winget** `MoaidHathot.OverShell` (framework-dependent zip as a portable, alias `overshell`, `Microsoft.DotNet.DesktopRuntime.10` as a dependency), the **.NET tool** `OverShell` (`dotnet tool install -g OverShell`, `dnx OverShell`; the window detaches from the wrapper), and the **GitHub Release** with both zips (self-contained too), the tool package and `SHA256SUMS.txt`. `build/Release.ps1` builds everything in two phases with a signing catalogue in between; `release.yml` signs the four OverShell assemblies in every layout with Azure Artifact Signing over OIDC, publishes, pushes to nuget.org and opens the winget-pkgs pull request. Verified locally: pack, install from the repacked package, shim and `dnx` return at once, `overshell://` handoff through the shim, self-contained zip with no shared runtime, `winget validate`. Not yet exercised: a signed run and the first winget review |
| **Herd overseer P4 — resilience and history** (§12.13) | The session file is written every two seconds and says how the run ended; a start after a crash or power cut restores everything and says so; the main window and tear-offs come back where they were (clamped to the desktop that exists now); a restored agent resumes by id, or by the harness's "most recent session" form when no id is known, relaunched when the profile's program is the agent; **history**: closed tabs reopen (`Ctrl+Shift+Z`), earlier sessions are archived and a picker (`session.history`) brings a tab or a whole session back; opt-in restart with Windows; vector harness icons. 169 unit tests; 85 + 16 + 16 + 17 + 5 in-process checks, OpenCode resume e2e |
| **Herd overseer P5 — polish, honesty, depth** (§12.14) | Chords follow Windows Terminal (`Ctrl+T` back to the shell); `tab.duplicate`; atomic `state.json`; app icon + AUMID; the **working directory** probed from the shell process (cmd, bash) or read from the prompt line (PowerShell) with `integrations install shell` for the OSC hook; restore **held** after two interrupted runs in a minute; a **close question** when agents are working; the **previous screen** as a dim preamble after a crash; **workspaces** (`workspaces\*.jsonc`, `workspace.save`, `workspace.open.*`) and four more `overshell://` actions; tab-strip **overflow** (width band, wheel, fades, overflow button); taskbar **jump list**; **themes** `system`/`dark`/`light` with the Windows accent, live; **tear-offs** with the main window's chrome; **find in the buffer** (`Ctrl+Shift+F`, scrollback included, the current match as the terminal's selection, the rest tinted). Two pre-existing bugs fixed on the way (§12.14: the endpoint's disposed-listener retry, the `FindText` no-hit exception that hid the link probe's fallback). 207 unit tests; ten new self-test modes, 85-check still 85/85 |
| **Terminal parity** (§12.15) | **Shell integration on the command line**: a plain PowerShell profile gets `-NoExit -Command ". '<script>'"` (VS Code's way), so the directory is announced exactly with no profile edit; profiles that run their own command, and other shells, are left alone; `detection.injectShellIntegration`. **Environment rebuilt per tab** from the registry with `CreateEnvironmentBlock`, as Terminal does (`compatibility.reloadEnvironmentVariables`), with `WT_SESSION`/`WT_PROFILE_ID`/`OVERSHELL_*` named in `WSLENV` - a tool installed after the launcher is on PATH in the next tab. One pre-existing bug fixed (a shell announcing its starting directory was not counted as announcing). 250 unit tests; `inject` 14/14, `env` 13/13 |
| **P6 — an engine with an API, and the keyboard on top** (§12.16) | `OverShell.Core/Extensibility`: `IExtension`, `IShell`, `ITab` (with a per-tab `Properties` bag saved in the session), `IKeyBindings`, `IHostUi`; `ShellHost` adapts the window, `ExtensionHost` loads the built-ins (each disable-able, a failing one skipped). **Key sequences** (`ctrl+shift+k j`, `stay`, shadowing reported, a pure dispatcher that swallows strays). Built on the API: **herd mode** (`Ctrl+Shift+K` + a which-key bar), **MRU switching** on `Ctrl+Tab` (hold, tap, release), **global summon** (`` Win+` ``, on the tab that waited longest), **prompt addressing** (`@3 #group @label @blocked ...` with a live echo), **keyboard cursor** in the sidebar and the dashboard, waiting tabs **by age**, `tab.last`. One bug fixed (a summon of a minimized window minimized it again: `IsActive` vs the foreground window). 281 unit tests; five new self-test modes |
| **P7 — triage** (§12.17) | **Reply channels** per tab: the harness's integration (a per-tab command queue the OpenCode plugin v2 long-polls and executes through OpenCode's own permission / question / prompt API), the rule file's `answers` keys, or typed text. The **inbox** (`Ctrl+Shift+I`): waiting tabs oldest first with the line that asked, `y`/`n`, a reply box, Enter jumps, approve-all with a question. **Toast actions**: Allow / Deny on in-window and native toasts (`overshell://reply`, nonce-gated). **Mute** (`tab.mute`, survives restart), **watch** (`tab.watch`, a regex over the screen), **auto-advance**. Two bugs fixed (a background tab's first Done unmarked; a forgotten queue's poll waiting out its hold). 291 unit tests; `inbox` 22/22, `triage` 13/13, `opencode-reply` live |
| **P8 — orchestration** (§12.18) | **Spawn**: `agent.new` / `agent.newWorktree` open a tab with a harness, a directory (a fresh `git worktree` beside the repository) and a **first prompt**, delivered once the agent shows its composer (the rule file's idle marker on the whole screen - not quiet output, not "the plugin is listening": both measured wrong against the OpenCode TUI). **Control API** under `/v1/tabs` (describe, screen, send, reply, open, wait, close) = `ShellControl` on `IShell`/`ITab`; `endpoint.json` for the window's lifetime. **`OverShell mcp`**: an MCP server over stdio with seven tools, each one control call against the running window. Two detection bugs fixed (a quiet Unknown never re-evaluated; a Done flash when the plugin's first report ended a guessed Working). 309 unit tests; `spawn` 12/12, `spawn-opencode` 5/5 live, `mcp` 21/21 |
| **P9 — oversight** (§12.19) | **The herd log**: every tab opened or closed, state change and call for attention as a JSON line in `state\logs\<stamp>.jsonl` (one file per run, flushed per line, the newest 30 kept), fed from the API's events; `herd.log` (herd mode `L`) lists this run's entries newest first, Enter goes to the tab. **Changes while away**: when a turn ends in a tab out of view, `git status` is compared with the one from your last look; the files changed in between are counted on the item (`2 changed`) and listed by `tab.changes` (`D`), Enter opens one; count and list survive a restart. A P7 slip fixed (the mute glyph sat over the harness icon). 313 unit tests; `herdlog` 12/12, `changes` 15/15 |
| **The control, built by us** (§12.20) | **`OverShell.Terminal.Wpf`**: the Windows Terminal WPF control built from source by our fork of microsoft/terminal on GitHub Actions and published to nuget.org through Trusted Publishing; a distributable OverShell bumps by version, synced to upstream by a weekly rebase PR. **Terminal-body transparency** (`window.terminalOpacity`): the fork's composed rendering mode draws the terminal through a DirectComposition visual on the top-level window so the backdrop shows through the body — the thing §7.6 had ruled out with the control as shipped. Two upstream-grade fixes found on the way (`dcomp.dll` never loaded under WPF; WPF cannot repaint under a child HWND). `transparency` 12/12 on real screen pixels; every earlier mode green with the fork's package |
| **Self-tests on a private desktop** (§12.21) | `tools/Invoke-SelfTest.ps1` runs any mode on a private Win32 desktop: nothing on the user's screen, no focus change, the window manager blind to it; foreground checks judged there by thread-local activation (`CanJudgeActivation`), the acrylic reads and screenshots skipped unless `-Visible`; a tally line per run; the seeded modes (`history`, `jumplist`, `resilience`, `ghost`, `transparency`) seed themselves (`SeedState`). All 33 modes green hidden. |

### Confirmed by a human — 2026-09-13

Two sessions with a mouse, using `tools/Show-LinkTestCard.ps1` and
`OVERSHELL_TRACE_LINKS=1`:

| What | Result |
|---|---|
| Hover on a link: underline, hand cursor, URL in the status bar | Works (was Ctrl-gated at the time; now plain hover, see §11.8) |
| Case 8 — hover a URL the terminal already underlines (SGR 4) | **One line, not two**: `AtlasFontMetrics` placed ours exactly on the renderer's |
| OSC 8 link with text ≠ target | Log: `span=18+26 rects=[2194,782 364x28]` — 26 glyphs × 14 px, start 1942 + 18 × 14; the `FindText` off-by-one correction is right (uncorrected would read 378) |
| Probe cost | 0.9–2.4 ms per hover, on the worker thread |
| Ctrl+click on plain text, incl. "Ctrl and click in the same instant" | Selection starts, nothing opens — the hold-and-redeliver path works |
| Ctrl+double-click on a link | Opened once |
| Plain hover; Ctrl press/release while hovering (case 20) | Underline and URL on hover alone; the hand comes and goes with Ctrl |
| Pointer leaves onto the tab strip (case 23) | Underline and URL clear |
| Text scrolls under a resting pointer (case 24) | Underline follows the text / clears — the `OutputVersion` re-probe works |
| Fast sweep across the card (case 25) | No lag in the shell; underlines keep up — the cell-lattice throttle works |

Not reported on: the keyboard shortcuts and Tab/arrow forwarding (§8 "Fixed but not
yet confirmed"). They are on the card.

### Fixed but not yet confirmed by a human

Everything below is on `tools/Show-LinkTestCard.ps1` (§7.8), last sections; run it in a tab.

| Item | Fix |
|---|---|
| Tab stolen from the shell | Claim + `SendMessageW` forward (§7.2c) |
| Arrow keys | Same mechanism, fixed pre-emptively |
| Transparent flash on new tab | Reveal after `Ready` + render turn (§7.5) |
| Doubled caption icons | `GlassFrameThickness` back to `0` (§7.5) |
| `InvalidOperationException` on tab/app close | Gone by construction: `WriteInput` never throws (§7.3) |
| Palette / switcher / rename take the keyboard and give it back | Owned activated window; focus in/out verified in-process (§12.9), typing not yet by a human |
| Toasts are readable and clickable over the terminal | Non-activating owned window at the terminal's top-right; rendered and counted in-process, click-to-focus not yet by a human |
| Two-line tabs, badge, counts, taskbar badge/flash | Rendered in-process; look-and-feel is a human call |
| Views, layouts, sidebar, dashboard, view switch | Every preset applied and rendered in-process (§12.10); clicking through them, the sidebar rows, the cards and the right-click menu is a human call |
| Prompt bar, drag reorder, groups, explain panel, session restore, toast click → tab | Prompt bar sends and broadcast verified in-process; drag is verified through the same move path (	ab.moveLeft/Right), the mouse gesture itself is not; restore verified across a real restart (§12.11); overshell:// handoff verified from a second process — clicking a Palantir toast is a human call |
| Native toasts, tear-off windows | The toast is in the shell's history with its `launch` URL (§12.12); how it looks in Action Center and what a click does is a human call. Detach/attach verified in-process (same HWND, output flows); typing and resizing in the tear-off is a human call |

### Open — near term

- [ ] **Verify shortcuts on real hardware.** `Ctrl+Shift+T`, `Ctrl+Shift+W`, `Ctrl+Tab` (hold / tap / release),
      `Ctrl+Shift+K` + a key (herd mode), `` Win+` `` (summon),
      `Alt+1..9`, `Ctrl+C`/`Ctrl+V`, `Ctrl+Shift+C`/`V`, right-click copy-or-paste, and now
      `Ctrl+Shift+P` (commands), `Ctrl+Shift+Space` (tabs), `Ctrl+Shift+J` (jump),
      `Ctrl+Shift+R` (rename), `Ctrl+Shift+1..4` (views), ``Ctrl+Shift+` `` (toggle),
      `Ctrl+Shift+F` (find) with Enter / Shift+Enter / F3 / Esc inside the bar.
      Chord → command resolution is verified in-process; the Win32 pre-dispatch path with
      a real keyboard is not. The test card lists them all.
- [ ] **Watch for double-Tab.** If one press yields two tabs, the terminal is receiving
      both the forwarded `WM_KEYDOWN` and a `WM_CHAR`; narrow the forward.
- [ ] `closeOnExit` semantics. Today a dead tab stays open, dimmed and italic, with the
      exit banner in the buffer.
- [ ] Copilot CLI end-to-end with the real `copilot` (the shim is verified verbatim against
      the endpoint; the harness firing it is not — `~/.copilot/hooks` did not exist on the
      reference machine before `integrations install copilot`).
- [ ] `OverShell mcp` from a real MCP host (the protocol is verified by unit test and by
      the verb as a child process on pipes, §12.18; a host's `initialize` handshake and tool
      calls against it are not - the host side is the host's).
- [ ] A spawned Copilot / Claude / Codex tab's first prompt (the paste path): verified with a
      fake agent and live with OpenCode through its plugin; their composer markers
      (`screen.idle`) are what decides readiness, and only Copilot's is measured here.

### Open — the actual feature work

- [ ] Drag a tab *out* of the strip to detach it (today: `Ctrl+Shift+D` or the menu); the
      drag preview would draw in `OverlayHost`.
- [ ] Codex `notify` against a live Codex CLI (not installed on the reference machine; the
      script, translator and `config.toml` edit are verified without it).
- [ ] Link underline over a tear-off (that overlay is owned by the main window; the find
      highlights already have one per window, §12.14).
- [ ] Claude Code hooks against a live Claude Code (not installed on the reference
      machine; the shim, translator and settings.json merge are verified without it).
- [ ] Split panes (our own splitter tree, independent of WT's panes).

### Open — later

- [ ] Find: regular expressions (the UIA provider offers plain text only, §12.14) and a
      results list; both come free with an xterm.js surface (§11.4a).
- [x] Terminal-body transparency through a fork of the control (§12.20): the fork builds
      `OverShell.Terminal.Wpf` on GitHub Actions, the composed mode renders through a
      composition visual on the top-level window, `window.terminalOpacity` opts in.
- [ ] Upstream the additive API (`CreateTerminalEx`, `TerminalSetBackgroundOpacity`,
      `TerminalUpdateComposition`, the engine's `undoXamlScale` and the `dcomp.dll` load) to
      microsoft/terminal, so the fork shrinks to its workflows.
- [ ] Import Windows Terminal's `actions` / `keybindings` into `keybindings.jsonc` (the
      object shape is already accepted).
- [ ] Profile icons in the tab strip and new-tab menu (paths are already parsed).
- [ ] Azure Cloud Shell generator.
- [ ] Unit tests for `OverShell.Config`, `HyperlinkDetector` and `TerminalStreamState` —
      the latter two are checked by a script and the in-process self-test today.
- [ ] `ReplaySession` (§11.3) so the tab pipeline can be tested without synthetic input.
- [ ] Second surface prototype: xterm.js / WebView2 (§11.7 phase 4).

---

## 9. Known limitations

| Limitation | Cause | Recoverable? |
|---|---|---|
| Terminal-body transparency needs `window.terminalOpacity` < 1 and a restart to switch mode | A terminal's HWND is created once, in the mode chosen at start (§12.20); the opacity itself follows live | By design |
| No background image / retro effect / per-profile acrylic | The composed mode gives the body an opacity over the window's backdrop (§12.20); the engine's image and shader paths are not wired through the C API | Additive exports in the fork, the same way |
| Scrollback lost when moving a session | ConPTY repaints viewport only (§7.1) | No |
| WPF cannot draw over the terminal in-tree | Airspace — same as WebView2 | `OverlayHost` (§7.12); HTML overlays inside an xterm.js surface |
| Chrome text is grayscale-antialiased | Transparent composition target disables ClearType | `OVERSHELL_BACKDROP=none` |
| No shell-integration marks in the control; find is plain text, no regex | Not in the Hwnd C API; find is built over the UIA text provider (§12.14), which searches literal text | Marks: `TerminalStreamState` (§11.6). Regex: a second surface |
| Find highlights disappear while another window is in front | They are a separate click-through window over the terminal and would float over whatever covers it | By design; they return when OverShell is the foreground window again |
| A PowerShell profile that runs its own command (`-Command`, `-File`) has its directory read from the prompt line on screen unless the shell emits OSC 9;9 / 7 | The integration is only injected into a plain launch (§12.15); `Set-Location` does not change the process directory, so the PEB probe that serves cmd and bash is useless for pwsh; a prompt that does not show the path hides the directory | `integrations install shell` adds the OSC hook to both PowerShell profiles |
| A PowerShell tab starts at its prompt, without the version banner | `-Command` suppresses the banner; the shell integration rides on `-Command` (§12.15) | `detection.injectShellIntegration: false` (the probe follows `cd` instead), or the profile block |
| A variable set only in the shell that started OverShell is not in its tabs | The environment is rebuilt from the registry per tab, as Windows Terminal does (§12.15) | `compatibility.reloadEnvironmentVariables: false` |
| A URL ending in the last column of a non-wrapped row, or longer than 9 rows, gets approximate or no geometry | `FindText` off-by-one pushes such a match out of range (§7.10); the row walk is capped | Cosmetic; the link still opens |
| A link whose text is not uniformly coloured is underlined in the scheme foreground | The colour attribute reports "mixed" for the range | Split by colour run if it ever matters |
| x64 only | Native control not published AnyCPU | No |
| The terminal control is not signed by Microsoft | `OverShell.Terminal.Wpf` is built from source by our fork on GitHub Actions (§12.20); upstream signs its CI output with internal tooling, and there is still no release of the control ([microsoft/terminal#15404](https://github.com/microsoft/terminal/issues/15404)) | Since 1.26.260930.2 the fork's release signs the two binaries with the owner's Azure Artifact Signing certificate (the same one OverShell's release uses), so the whole shipped tree carries one signer; OverShell's release leaves signed files alone |
| Restored window placement can be a few pixels off on a mixed-DPI desktop | Placement is saved and clamped in the primary monitor's DIPs; a window on a monitor with another scale is converted by WPF at show time (§12.13) | Cosmetic; the window is always on a visible monitor |
| A tiling window manager re-tiles restored windows | It moves every new window; OverShell asked for the saved rectangle (traced at `SourceInitialized`) and the manager overrode it | Expected with such a manager; nothing to do |

---

## 10. Useful references

- [microsoft/terminal#6999](https://github.com/microsoft/terminal/issues/6999) — embedding, iceboxed
- [microsoft/terminal#20488](https://github.com/microsoft/terminal/pull/20488) — community PR for a UWP control NuGet
- [microsoft/terminal#15061](https://github.com/microsoft/terminal/issues/15061) — WPF control crashes on a null `Connection`
- [Devolutions/wt-distro](https://github.com/Devolutions/wt-distro) — the Option A patches (no longer developed)
- [EasyWindowsTerminalControl](https://github.com/mitchcapper/EasyWindowsTerminalControl) — the glue we started on; its `TermPTY.cs` and `EasyTerminalControl.cs` are the reference for what the wrapper used to do
- `src/cascadia/TerminalControl/HwndTerminal.cpp` — the native side we talk to (mouse handling: no `CS_DBLCLKS`, timestamp multi-click — §11.8)
- `src/cascadia/WpfTerminalControl/` — the managed wrapper (`TerminalContainer.cs` is the one to read: the `Connection` setter, the message hook, `WM_GETOBJECT`)
- `src/types/UiaTextRangeBase.cpp` — what UIA text ranges return: `_getTextValue`, `FindText`, `GetAttributeValue` (§7.10)
- `src/buffer/out/UTextAdapter.cpp` — how search sees wrapped rows (§7.10)
- `src/renderer/atlas/AtlasEngine.api.cpp` — `_resolveFontMetrics`, the underline arithmetic (§7.13)
- `src/winconpty/winconpty.cpp` — what `conpty.dll` actually does (§7.9)
- `src/cascadia/TerminalConnection/ConptyConnection.cpp` — the model for `ConPtySession`
- `src/cascadia/TerminalSettingsModel/defaults.json` — source of the embedded schemes

### Option D / §11 references

- [awakecoding/terminal @ `copilot/dotnet-avalonia-port`](https://github.com/awakecoding/terminal/tree/copilot/dotnet-avalonia-port) —
  Devolutions Terminal source tree, under `dotnet/`. Read in this order:
  `dotnet/README.md` (architecture), `dotnet/docs/parity-status.md` (the cost),
  `dotnet/docs/decisions/0001-skia-renderer.md` (why not AtlasEngine),
  `dotnet/docs/history/porting.md` (their C++ → C# mapping table),
  `dotnet/src/Devolutions.Terminal.Ghostty/GhosttyTerminalEngine.cs` (libghostty-vt P/Invoke reference).
- [ghostty-org/ghostty](https://github.com/ghostty-org/ghostty) — `libghostty-vt`:
  `include/ghostty/vt.h`, `zig build lib-vt`. Parser and screen state only; C ABI still
  unstable. Mitchell Hashimoto's announcement: [libghostty is coming](https://mitchellh.com/writing/libghostty-is-coming).
- [xtermjs/xterm.js](https://github.com/xtermjs/xterm.js) — candidate surface (a); VS Code's terminal emulator.
- [`CoreWebView2Controller.DefaultBackgroundColor`](https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2controller.defaultbackgroundcolor) —
  transparent WebView2; demonstrated in the WebView2 WPF sample.

### Handy

Set a tab title from inside PowerShell (emits OSC 2 through ConPTY, which OverShell
parses into the tab label):

```powershell
$Host.UI.RawUI.WindowTitle = 'OverShell'
```

---

## 11. Sessions and surfaces

Status: proposed 2026-09-12; **phases 1 and 2 built the same day** (§11.7), plus
hyperlinks on the default surface (§11.8). §11.1–11.6 are kept as the rationale.

### 11.1 Why revisit the coupling

Two things changed since §2 was first written.

1. **Devolutions changed course** (§2, Option D). The company behind Option A now ships a
   complete managed re-implementation and keeps the `microsoft/terminal` fork only as a
   test oracle. Their parity document is the most honest available measurement of what
   "write our own" costs — and it confirms we should not.
2. **The goals widened.** OverShell is meant to *manage* sessions — several AI coding
   agents (Copilot CLI, OpenCode, Claude Code, …) each in a tab, with OverShell observing
   their state and driving them — not only to put nicer chrome around a shell. And
   terminal-body transparency and overlays are wanted, which §7.6 proves the current
   surface can never provide.

The first point says: keep hosting someone else's emulator. The second says: the emulator
we host must be replaceable. Both are satisfied by one seam.

### 11.2 What was coupled — and what is now

Before the seam, `MainWindow` only ever saw `tab.View` as a `UIElement`, and everything
that knew about the wrapper stack lived in five places: `TerminalTab` (construction,
start/exit state, input, teardown ordering), `TerminalThemeMapper`, `ShortcutRouter`'s
assumption of a native child HWND, one exception filter in `App.xaml.cs`, and the
`ScrollBar` restyle in `Controls.xaml`.

After it, the only files that name an implementation are `TerminalFactory` and the
implementations themselves under `Terminal/ConPty/` and `Terminal/WindowsTerminal/`.
`TerminalThemeMapper` stays because it *is* part of the default surface (it produces the
native `TerminalTheme`), and the `ScrollBar` style is an app-wide implicit style that
happens to reach the control's scrollbar. `ShortcutRouter` asks the active surface's
capabilities before hand-delivering keys.

### 11.3 The right cut: sessions and surfaces — not engines and renderers

The tempting abstraction is the three-layer one Devolutions and Ghostty use — transport /
engine (VT parser + buffer) / renderer. **It is the wrong cut for us.** Our default
implementation cannot be sliced that way: `Microsoft.Terminal.Control.dll` fuses parser,
buffer, renderer and HWND into one opaque unit behind 19 C exports (§7.6). An
engine/renderer interface would have exactly one implementation, and it would ignore
half of the interface.

Two interfaces instead (the real ones live in `Terminal/`; this is the shape):

```
ITerminalSession   "a byte stream with a lifecycle"          knows nothing about pixels
ITerminalSurface   "turns a session into pixels and          engine + renderer fused;
                    input into bytes"                         knows nothing about processes
```

```csharp
public interface ITerminalSession : IDisposable
{
    SessionDescriptor Descriptor { get; }         // commandline, cwd, environment, profile id, kind
    bool HasStarted { get; }
    bool IsRunning { get; }
    int? ExitCode { get; }

    event EventHandler? Started;                  // background thread
    event EventHandler? Exited;                   // background thread, once, after output drained
    /// Raised on the I/O thread. Observers must be cheap and must never block.
    event EventHandler<string>? OutputReceived;

    void Start(int columns, int rows);
    void WriteInput(ReadOnlySpan<char> text);     // silently ignored after CloseInput — never throws (§7.3)
    void Resize(int columns, int rows);
    void CloseInput();                            // flag only; the session stays alive
}

public interface ITerminalSurface : IDisposable
{
    FrameworkElement View { get; }
    SurfaceCapabilities Capabilities { get; }     // NativeHwnd | LiveReattach | Transparency
                                                  // | Overlays | Search | Hyperlinks
    (int Columns, int Rows) Grid { get; }
    TerminalPointer Pointer { get; set; }         // Default | Hand — set by the chrome over a link

    /// UI thread, once the surface has something to draw. The reveal gate from §7.5 waits for this.
    event EventHandler? Ready;

    void Attach(ITerminalSession session);        // starts the session once the grid is known
    void Detach();
    void ApplyTheme(ColorScheme scheme, TerminalProfile profile);
    void Focus();
    string GetSelectedText();
}
```

Output is delivered as decoded text rather than bytes: the session decodes UTF-8 once
(split sequences across reads included), and both the native control and the OSC
observers want strings. A surface that wants bytes re-encodes; that is cheaper than
every observer decoding.

`TerminalTab` is: one profile, one `ITerminalSession`, one `ITerminalSurface`, plus the
stream observers (`TerminalStreamState` for modes and OSC 8; the OSC 0/2/7/9;9 tail scan
for title and cwd). `SurfaceCapabilities` is how the chrome decides what to offer:

- a profile's `opacity` / `useAcrylic` is honoured only when the surface reports
  `Transparency`;
- the drag preview draws in-tree only when `Overlays` is set, else through `OverlayHost`
  (§7.12);
- `ShortcutRouter`'s `SendMessageW` forwarding runs only for `NativeHwnd` surfaces.

Implementations:

| | Session | Surface |
|---|---|---|
| **Default (built)** | `ConPtySession` — our own `CreatePseudoConsole` + `CreateProcessW` through `conpty.dll`/`OpenConsole.exe`, with a private environment block, `ReleasePseudoConsole` after launch, and in-order exit reporting (§7.9) | `WindowsTerminalSurface` — hosts `Microsoft.Terminal.Wpf.TerminalControl` directly and hands it `SessionConnection`, an `ITerminalConnection` over the session. Capabilities: `NativeHwnd`, `LiveReattach` |
| Future | `ProcessPipeSession` (non-PTY agents), `SshSession`, `ReplaySession` — replays a recorded byte stream, which is the **only** way to exercise the pipeline in tests given §7.8 | §11.4 |

`LiveReattach` keeps §7.1's caveat: moving a session between surfaces transfers the
viewport, not the scrollback. The interface allows it; the chrome must still never use it
for tab switching.

### 11.4 Candidate second surfaces — with honest costs

Ranked by cost to reach *usable*, not by elegance.

**(a) xterm.js in WebView2.** The emulator behind VS Code's terminal — after Windows
Terminal itself, the most-exercised ConPTY client in existence. One WebView2 per tab
hosting `xterm.js` + `@xterm/addon-webgl`; the byte stream bridged with
`PostWebMessage` / `WebMessageReceived` (or a loopback WebSocket if throughput demands).
`CoreWebView2Controller.DefaultBackgroundColor = Transparent` is supported and shown in
the WebView2 WPF sample, so the WPF chrome behind the terminal shows through —
**terminal-body transparency for the first time**. Search, hyperlinks, Unicode 11 widths,
ligatures and image protocols are existing addons. Overlays are HTML *inside* the surface
(badges, agent status, inline panels) — for the AI-session UI that is arguably better than
WPF overlays.
Costs: still a child HWND, so the airspace limitation remains for *WPF* content; a
Chromium renderer per WebView2 (the browser process is shared — **measure per-tab memory
before committing**); a JS↔C# bridge to own; the WebView2 Runtime dependency (in-box on
Windows 11). Fidelity is xterm.js's, not Windows Terminal's — very good, and in places
(Kitty keyboard, images) ahead.

**(b) `libghostty-vt` + our own WPF renderer.** `libghostty-vt`
([ghostty-org/ghostty](https://github.com/ghostty-org/ghostty), MIT, zero dependencies;
`zig build lib-vt -Dtarget=x86_64-windows-gnu` yields a `.dll`) is **only the parser and
screen state**: `ghostty_terminal_vt_write` in, a render-state iterator of rows / cells /
styles out, plus key encoding, OSC/SGR parsers and paste-safety checks. No rendering, no
PTY, no input widget. Devolutions' `GhosttyTerminalEngine.cs` is a good reference for the
P/Invoke shape (`LibraryImport`, `UnmanagedCallersOnly` callbacks for write-PTY / bell /
title / pwd / clipboard / notification) — and a warning: the C ABI is explicitly unstable,
they pin a commit, validate the ABI at start-up, and pre-scan the byte stream for Sixel /
OSC 1337 to emit "unsupported" diagnostics because the pinned ABI exposes no image
resources.
What we would still own: glyph rendering (DirectWrite through WPF `GlyphRun`, or
SkiaSharp via `SkiaSharp.Views.WPF`), font fallback, wide and combining cells, cursor
styles, selection, scrollbar, IME, UIA. That is a renderer project — smaller than Option C
because the VT long pole is someone else's, but §7.6 already showed that AtlasEngine's
renderer is a large part of what makes Windows Terminal look right.
Upside if done: a pure WPF visual — transparency, overlays, `RenderTransform`, everything —
with no HWND and no airspace, and `ShortcutRouter`'s hacks become unnecessary for it.

**(c) `Devolutions.Terminal.Control`.** Already a managed surface with no HWND, a
WT-compatible settings model and a Skia renderer with parity docs. But it is Avalonia;
WPF↔Avalonia in-process mixing is not a supported scenario in either direction, and it is
not published as a package. **Track it.** If it ships on NuGet and OverShell ever moves to
Avalonia, it becomes (b) for free.

**(d) Option A — embed the WT process.** Unchanged from §2: WT's own acrylic, but no
stream access, a *worse* airspace problem (a foreign process's window), and no one
maintaining it.

**Recommendation.** (a) is the only second surface that costs a weekend rather than a
quarter. Build the seam, prove it by porting the default onto it, then prototype (a)
behind a profile flag (`"overshell.surface": "xterm"`). Do (b) only if (a)'s HWND or
memory footprint turns out to be the actual blocker.

### 11.5 Overlays without waiting for a second surface

Overlays over the default surface are a solved — if inelegant — problem, the same one
every WebView2/Chromium-in-WPF application has: a **separate top-level
`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` popup**, `AllowsTransparency=True`,
owned by the main window, positioned over the terminal's screen rectangle and repositioned
on `LocationChanged` / `SizeChanged` / `DpiChanged`, hidden while the main window is
inactive or minimised. It carries ordinary WPF content: toasts, agent-status badges, a
command palette, the drag preview from §8. Its text is grayscale-antialiased for the same
reason as §7.6.

**Built** as `OverlayHost` (§7.12 has the details that matter). Today it draws the link
underline; it is the place for the drag preview and toasts when those arrive.

What does **not** work and must not be retried: making the terminal's own child HWND
layered (`WS_EX_LAYERED` on `HwndTerminalClass`). Flip-model presentation and layered
windows are not a supported combination; `LWA_ALPHA` would fade the glyphs together with
the background; `LWA_COLORKEY` leaves anti-aliased halos around every glyph.

### 11.6 The AI-session use case needs the *session* seam, not a new surface

Everything "several agents in tabs, managed by OverShell" needs is available on the default
surface today, because we own the byte stream in both directions:

| Need | Mechanism | Surface-dependent? |
|---|---|---|
| Spawn with a specific commandline, cwd and **environment** (`TERM`, `COLORTERM`, API keys, `NO_COLOR`, …) | `SessionDescriptor.Environment` → `ConPtySession`'s environment block. **Built.** | No |
| Know when an agent is idle, waiting for approval, or finished | Stream observers: OSC 133 A/B/C/D prompt marks, OSC 9;4 progress (ConEmu — WT paints it on the taskbar), OSC 9 / OSC 777 notifications, BEL, plus per-agent heuristics. `TerminalStreamState` is the place; it already tracks modes incrementally | No |
| Show that state | Tab badge / accent pulse (chrome — trivial), overlay toast (§11.5), taskbar progress via `ITaskbarList3` | No |
| Drive an agent | `WriteInput` (exists as `SendText`); broadcast to all; scripted prompts | No |
| Persist and restart a session | `SessionDescriptor` is serialisable; `ITerminalSession` is restartable by construction | No |
| Different UI for agent tabs vs. shell tabs | `SessionDescriptor.Kind` selects a different tab template | No |

The order of work is therefore **session seam → stream observers → agent UI**, all on the
existing surface. The seam is done; the observers are the next step.

### 11.7 Phasing

1. **Seam.** ✅ 2026-09-12. `ITerminalSession` / `ITerminalSurface` introduced; the
   wrapper adapted behind them with zero behaviour change; `TerminalTab` stopped naming
   any wrapper type. Built and verified as a checkpoint before phase 2.
2. **Own the connection.** ✅ 2026-09-12. `WindowsTerminalSurface` over
   `Microsoft.Terminal.Wpf.TerminalControl` + `SessionConnection`; `ConPtySession` with
   its own `CreatePseudoConsole`; `EasyWindowsTerminalControl` out of the dependency
   graph; `OpenConsole.exe` bundled. §7.3's throw-after-close disappeared by
   construction. What changed for the user: the console host is Windows Terminal's
   `OpenConsole.exe` instead of the OS's `conhost.exe`; `WT_SESSION`/`WT_PROFILE_ID`
   are set; a dead shell prints `[process exited with code N]`; paste is bracketed.
3. **Observers and overlay host.** OSC 133 / 9;4 / notifications; `OverlayHost`; tab
   badges. The first agent-management features ship here.
4. **Second surface prototype.** xterm.js / WebView2 behind a profile flag. Measure memory
   and input latency against the default before deciding whether it becomes first-class.
5. **Only if 4 fails** on HWND or footprint grounds: `libghostty-vt` + a WPF renderer.

### 11.8 Hyperlinks on the default surface — built out of order, because it was cheap

Not in the original plan: the Hwnd C API has no hyperlink support, and links were listed
under "second surface". §7.10 changed that — the control's UIA `ITextProvider` gives the
text under any screen point in ~2 ms, in-process, from a worker thread, with wrap flags
and cell geometry included.

How it works:

- `ShortcutRouter` sees the mouse messages posted to the terminal HWND (class
  `HwndTerminalClass`) in the same pre-dispatch hook it uses for keys, and reports Ctrl
  going down and up.
- **Plain hover shows the link; Ctrl only follows it** — the same split as Windows
  Terminal. Every pointer move over the terminal may run a probe (serialised, coalesced to
  one in flight). `TerminalTextProbe` (UIA, `Task.Run`) walks row ranges outward from the
  hit row until the buffer's own wrap flags say the logical line ends (§7.10), giving exact
  per-row text and the pointer's text offset. `HyperlinkDetector` matches Windows
  Terminal's URL pattern on that text, trims trailing punctuation while keeping a
  Wikipedia-style balanced `)`, and falls back to the OSC 8 ledger by visible text. The
  probe then asks the **terminal** where that text is: `FindText` restricted to the
  logical line (the occurrence containing the pointer, if the text repeats), its bounding
  rectangles, its foreground colour, and the resolved font family. No cell arithmetic
  happens on our side unless the terminal declines.
- **Most moves do not probe.** The probe also returns the terminal's cell lattice (origin
  and cell size from the row rectangle — known even for the empty area below the last
  row, where the provider snaps to that row). A move is looked up only if the pointer
  crossed into another cell that is not part of the link it was on, or the last answer is
  older than 150 ms, or the session printed something since (`TerminalTab.OutputVersion`,
  a counter bumped on the I/O thread). The status timer re-probes a resting pointer on the
  same condition, so an underline follows text that scrolls beneath it — after checking
  with `GetCursorPos` that the pointer really is still there, because a pointer that left
  the window without a `WM_MOUSEMOVE` to our chrome must not grow a phantom underline.
  Nothing is shown while a selection is being dragged or while the application owns the
  mouse.
- A hit underlines the cells through `OverlayHost` — in the text's own colour, at the
  position and thickness AtlasEngine uses for that font (§7.13), or a plain bar if the
  metrics cannot be confirmed — and shows the URL in the status bar's detail slot. With
  Ctrl down the surface's `Pointer` becomes a hand (§7.11); Ctrl is read from
  `MK_CONTROL` on every mouse message as well as from the key messages, so a Ctrl that
  went down while the pointer was over another window is still seen.
- **Ctrl+`WM_LBUTTONDOWN` is decided without guessing.** If a hover probe answered within
  the last 300 ms, its answer is used synchronously: a press inside the **terminal's own
  rectangles** for the link it found opens that link (the down and its up are swallowed);
  a press at exactly the point where it found nothing passes through and a selection
  starts as always. Anything else — no recent probe, pointer drifted off the known
  rectangles, output may have scrolled — **holds** every left-button message while a probe
  runs at the click point, then either drops them (link opened) or hands them back to the
  terminal in order with `SendMessageW` (`ShortcutRouter.Redeliver`), so the selection
  simply starts a few milliseconds late. A 300 ms timeout redelivers regardless. This is
  the user's own message reaching its own window, the same mechanism as Tab forwarding —
  not synthetic input in the §7.8 sense. Safe because `HwndTerminal` has no `CS_DBLCLKS`
  (multi-click is detected by timestamp at processing time) and ignores moves when no
  selection is active. Two consequences of that missing `CS_DBLCLKS` are handled: the
  second press of a double-click arrives as a plain down, so the same link is not opened
  twice within `GetDoubleClickTime()`; and a drag that began with a swallowed press stays
  swallowed, since the terminal never saw the press. Applications that enabled mouse
  tracking (DECSET 1000/1002/1003, tracked by `TerminalStreamState`) get every click
  untouched.
- Leaving the terminal, switching tabs, or moving/resizing/deactivating the window clears
  all of it; releasing Ctrl only drops the hand.

What remains approximate is in §9: a URL ending in the very last column of a non-wrapped
row, one longer than nine rows, and links whose text is not one colour.

---

## 12. The herd overseer — architecture and plan

Decided 2026-09-13, after studying herdr, sidekick.nvim, Conductor/Claude Squad/Vibe
Kanban/Crystal/Superset/AgentsRoom, and what the agents themselves emit (§12.2). The
goal from §1 restated: OverShell manages a **herd** of AI coding agents from *outside*
their terminals — rich chrome, not a richer TUI.

### 12.1 Principles

1. **Attention-first.** Every surface — tab, sidebar, dashboard, taskbar, toast — ranks
   by "needs you now". AgentsRoom put it best: *the number of agents was never the hard
   part; knowing which one needs you, right now, always was.*
2. **Harness-agnostic core, integrations additive.** Detection works with nothing
   installed; a hook or plugin makes it exact. herdr's rule: one **state authority** per
   tab — an integration that is reporting wins, otherwise the detector decides. Never two
   competing sources of truth.
3. **Chrome outside the terminal.** We never inject into or restyle a TUI.
4. **Terminals are immortal across UI changes.** Layout and view switches re-parent
   surfaces; only closing a tab disposes one (§3, one surface per tab). `HwndHost`
   re-parents rather than destroys when its `PresentationSource` changes.
5. **Everything is a command** — named, palette-searchable, bindable from a file.
6. **Cheap by default.** Screen snapshots only when `OutputVersion` changes, debounced;
   no new polling loops; the status timer stays the single 500 ms heartbeat.

### 12.2 What agents actually emit — the evidence the detector is built on

| Harness | Without any integration | With integration |
|---|---|---|
| **OpenCode** | Title carries a status icon; `attention` (off by default) → OSC notification only when the terminal is *blurred* | Plugin API: `session.idle`, `session.status`, `session.error`, `session.created`, `permission.asked/replied`; plugin gets `directory`, `worktree`, can `fetch` |
| **Copilot CLI** | Nothing documented; screen rules only | Hooks: `userPromptSubmitted` (→ working), `agentStop` (→ idle), `notification` with `notification_type: permission_prompt \| elicitation_dialog` (→ blocked), `sessionStart/sessionEnd` with `sessionId`, `preToolUse toolName=ask_user`; hooks may be **`type: "http"` to localhost** when `COPILOT_HOOK_ALLOW_LOCALHOST=1` — no script needed |
| **Claude Code** | OSC 0/2 title prefix is the *only* reliable turn signal: `✳` idle, braille spinner working. OSC 9/99/777 are **idle-for-60 s timers**, and the channel is chosen from the detected terminal — **`WT_SESSION` → "windows-terminal" → `no_method_available` → silent** | Hooks (`Notification`, `Stop`, `SessionStart`); OSC 9;4 progress when it believes it is in iTerm2 |
| **Codex** | `[tui] notification_method = osc9 \| bel`; events `agent-turn-complete`, `approval-requested`; suppressed while focused unless `focused_notifications` | `notify = [cmd]` with JSON (`agent-turn-complete` only) |

Consequences: title transitions + screen-bottom rules + BEL/OSC 9/99/777 + OSC 9;4 +
OSC 133 + output activity + exit is the agnostic core. Because we set `WT_SESSION`
(§7.9), Claude Code sends us titles but never a notification — `TERM_PROGRAM` spoofing is
a spike (§12.7), not a plan. OpenCode and Codex gate notifications on terminal *blur*, so
DECSET 1004 focus reporting is answered honestly: an inactive tab is blurred.

### 12.3 Model

```
Workspace
└── TabGroup                       rollup: worst state of its tabs
    └── Tab  (TerminalTab grows)   Label · Kind (Shell | Agent:<harness>) · Project (repo root or cwd)
                                   Branch · State · Attention (+since) · Unread · LastActivity
                                   Progress (OSC 9;4) · Summary · Pinned · Color
```

States: `Unknown | Idle | Working | Blocked | Done | Exited`. `Done` is "the turn ended
while you were not looking" and stays until viewed; `Blocked` is a strict match on
approval/question UI or an integration saying so — herdr's "fall back to idle, never to
blocked" rule, because a false blocked is a false alarm every time.

`AgentStateTracker` per tab: inputs are the stream signals above plus a **screen-bottom
snapshot** (UIA, last N rows, on `OutputVersion` change, 300 ms debounce) matched against
**rule files** — JSONC per harness under `%APPDATA%\OverShell\agents\` with bundled
defaults for OpenCode, Copilot, Claude, Codex: patterns for *blocked*, *idle prompt*,
*working*. Output: state + confidence + an **explain** trail (`why is this tab blocked?`).

### 12.4 Integration endpoint — one transport for every harness

An in-process `HttpListener` on `127.0.0.1:<random>` with a per-run bearer token. The
child environment gets `OVERSHELL_ENDPOINT`, `OVERSHELL_TOKEN`, `OVERSHELL_TAB_ID`, and
`COPILOT_HOOK_ALLOW_LOCALHOST=1`. Copilot hooks (`type: http`), an OpenCode plugin
(`fetch`), a PowerShell one-liner and any future harness reach it with zero glue.

```
POST /v1/report   { tab, source, seq, state, message?, session?: { id, resumeCommand }, summary? }
POST /v1/release  { tab, source }
GET  /v1/tabs     (diagnostics)
```

Stale `seq` from the same `source` is ignored (herdr). `OverShell integrations install
<opencode|copilot>` writes the plugin/hook file and `status` shows what is installed.
Claude Code hooks and Codex `notify` follow later; they need a script shim.

### 12.5 Chrome

- **Commands & keys.** `CommandRegistry` (id, title, category, `when`, handler);
  `keybindings.jsonc` in Windows Terminal's shape; the palette searches it.
  `ShortcutRouter` stays the Win32 layer underneath.
- **Layouts & views.** `ChromeLayout` JSONC: a grid plus regions (`tabs terminal status
  sidebar dashboard`) → cell + options. Presets `top bottom left-rail left-list
  right-list zen herd`; user layouts in `%APPDATA%\OverShell\layouts\`. A **View** =
  layout + components + behaviours: **Terminal**, **Herd** (agent sidebar beside the
  live terminal), **Dashboard** (cards), **Zen**. `view.*` commands, one hotkey each, an
  attention badge on the view switch. **Skins**: optional XAML `ResourceDictionary`
  files overriding named templates, loaded with `XamlReader`, hot-reloaded, documented
  as trusted config.
- **Rich tab item.** Two lines (label / state · cwd tail · branch), state dot with motion
  (working pulse, blocked amber ring, done green until viewed, exited grey), progress
  ring, unread badge, harness glyph, pin, colour. Orientation-aware: strip, list, rail.
- **Tab switcher.** A `Popup` — its own HWND, so it renders over the terminal. Fuzzy over
  label/title/cwd/branch/harness/state; filters `@blocked @working #repo >command`; MRU;
  a screen-snapshot preview; **letter hints** for one-keystroke jumps; `Alt+1..9` stays
  as visible-order shortcuts. `tab.jumpToAttention`: next blocked, then done-unread.
- **Agent sidebar** (Herd): grouped by project, sorted attention → recency; token rows;
  rollups; hover peek; context actions. **Dashboard cards**: header (dot, label,
  harness, project/branch), body = last N screen rows in scheme colours **from UIA
  text**, footer (activity, progress, actions). Deliberately not live scaled-down HWNDs:
  shrinking a live terminal to a tile reflows the agent's TUI.
- **Prompt bar** (P2): send to focused/selected agents from a WPF input; broadcast;
  snippets — we own `WriteInput`.

### 12.6 Notifications — a sink pipeline

Events `blocked done exited error`; policy filters (`notActiveTab`, `windowUnfocused`,
per-harness, quiet hours); sinks run in parallel: `overlay` (toast layer, an
interactive sibling of `OverlayHost`), `taskbar` (`ITaskbarList3` overlay badge and
progress, `FlashWindowEx`), `sound`, and **`command`** — spawn an exe with templated
args and the event as JSON on stdin. The shipped recipe uses
[Palantir](https://github.com/MoaidHathot/Palantir) (flags verified against 2.0.1
`--help`, 2026-09-13):

```jsonc
{ "type": "command", "exe": "palantir",
  "args": ["-t", "{title}", "-m", "{message}", "-b", "{tab.cwd}",
           "--tag", "overshell-{tab.id}", "--replace", "-q"],
  "when": { "notActiveTab": true } }
```

`overshell://` protocol + single-instance handoff (toast click focuses the tab, via
`--launch overshell://focus/{tab.id}`): P2. A native WinRT toast sink: P3, optional.

### 12.7 Verify-first spikes

Run in-process with `OVERSHELL_SPIKES=1` (`Diagnostics/Spikes.cs`, log in
`%TEMP%\overshell-spikes.log`). Results 2026-09-13:

1. **UIA text from a hidden tab** ✅ — the first tab, hidden behind a second, answered
   `GetVisibleRanges` with 9585 chars in 27 ms (first UIA call) and `DocumentRange` in
   1.7 ms; `BoundingRectangle` still reports the last on-screen bounds. Dashboard cards
   and background detection can read hidden tabs.
2. **Re-parent a live surface** ✅ — moved within the window, to a **second window**, and
   back: same HWND throughout, `GetParent` follows, session alive, an `echo` marker sent
   after the move appears in the moved terminal. Layout switching and tear-off are safe.
3. **Popup over the terminal** ❌ then ✅ — a WPF `Popup` took *WPF* keyboard focus but
   **Win32 focus stayed on the terminal HWND**: typed keys would have reached the shell,
   not the palette. WPF popups assume the owner window holds Win32 focus; here the
   owner's native child holds it. An **owned, activated `Window`** works: Win32 focus =
   the palette HWND, WPF focus = its TextBox, and closing it plus `Surface.Focus()`
   returns focus to the terminal. Palette and switcher are owned windows, not popups.
4. **Integrations reach the listener** ✅ — the Copilot hook shim (`cmd.exe /d /c` +
   `curl.exe`, taken verbatim from the generated hook file, JSON on stdin) posted to
   `/v1/copilot/{tab}/agentStop` from inside a tab using only the injected environment;
   the **real OpenCode** with the installed plugin reported `session started → Working →
   turn finished` for an `opencode run`, with the session id and title (§12.9).
5. `TERM_PROGRAM` for Claude Code — deferred; lowest priority.
6. **Small-tile PTY reflow** ✅ confirmed — a 400×300 margin shrank the grid from 133×71
   to 1×39 and back. Cards use text, never live tiles.
7. **The text provider as a search engine** ✅ 2026-10-03 (`OVERSHELL_SPIKES=find`) —
   `FindText` over `DocumentRange` found 5 occurrences across viewport and scrollback in
   4.6 ms; hidden matches report no bounding rectangles, visible ones exact cell runs;
   `Select()` scrolled a hidden match into view and selected it, painted by the renderer;
   `ScrollIntoView(true)` put the match's row at the top; `SupportedTextSelection=Single`.
   Also found: a found range is one glyph too long, and with no hit `FindText` returns a
   range that fails with E_FAIL on use rather than null (§12.14).
8. **Transparency: can a DirectX surface in a WPF window blend with the system backdrop?**
   ✅ 2026-10-04, `spikes/dcomp-transparency` (a WPF window with the frame extended over
   the client and an acrylic backdrop; an 800x450 frame of 45 % black, opaque white bars,
   an opaque orange block and an alpha-0 hole, drawn four ways; the window captures itself
   with BitBlt and samples pixels). Results, six runs:
   - `child-hwnd` — a swapchain created for a child HWND, what `HwndTerminal` does today:
     alpha ignored, black background, black hole. Confirms §7.6.
   - `child-dcomp` — composition swapchain with premultiplied alpha, DirectComposition
     target bound to the **child** HWND: **non-deterministic**. One run in four blended with
     the acrylic (hole `#2576A7`, the backdrop); three blended with **white** (hole
     `#FFFFFF`, 45 % black → `#8C8C8C`) — the child''s share of the parent''s redirection
     surface is sometimes initialised opaque before the visual is committed, and the
     visual composes over it. `WS_EX_NOREDIRECTIONBITMAP` on the child changes nothing
     (children do not own a surface).
   - `parent-dcomp` — the same swapchain as a DirectComposition visual on the **top-level**
     window, offset to the terminal''s rectangle, no child HWND: **blends with the backdrop
     every time** (5/5): 45 % black over the acrylic, hole = pure backdrop, white and orange
     opaque and crisp — Windows Terminal''s look. Identical with `topmost` true or false.
   - WPF content under the visual''s rectangle is **covered** by it in both `topmost` modes
     (a WPF banner there is dimmed by the 45 % black and crossed by the bars; outside the
     rectangle it is bright). WPF''s own rendering is a DWM visual too, and the DComp visual
     lands above it. So the airspace rule stays: overlays keep their own window (§7.12).
   - Acrylic (`DWMSBT_TRANSIENTWINDOW`) is painted only for the **active** window; an
     inactive one gets the solid fallback colour. Mica tints and stays.
   **Consequence for a transparent terminal** (§7.6): a fork of the control must not render
   through its child HWND. The renderer already supports composition swapchains (the
   WinUI path: `AtlasEngine` creates one when no HWND is set and hands out the handle for
   `SwapChainPanel`); the patch is to take that path in `HwndTerminal`, export the handle
   and `EnableTransparentBackground`, and route input through the API (`TerminalSendKeyEvent`
   / `TerminalSendCharEvent` exist; mouse and wheel would be added) with the child HWND
   hidden or gone — OverShell then composes the swapchain on its top-level window as the
   spike does and extends the frame over the whole client. A child HWND kept for input over
   the terminal area would reintroduce the white race. Cost: a Windows Terminal build in CI
   and our own signed `Microsoft.Terminal.Control.dll` / `Microsoft.Terminal.Wpf.dll`.

### 12.8 Phases

- **P0 Foundation** ✅ 2026-09-13 — spikes 1–4; commands + keybindings + palette shell;
  Tab model; detector v1 + bundled rules; endpoint + OpenCode plugin + Copilot hooks +
  `integrations install/status`; rich tab item + `jumpToAttention` + status counts;
  notification pipeline with all four sinks + Palantir recipe; labels persisted.
- **P1 Views** ✅ 2026-09-13 — layouts + view switching; Herd sidebar **and** Dashboard
  cards; `settings.jsonc` + hot reload; tab switcher preview; git branch/dirty per tab.
- **P2 Depth** ✅ 2026-09-13 — prompt bar/broadcast; tab groups + drag reorder; session
  persistence/restore + harness resume; `overshell://`; XAML skins; Claude hooks;
  explain panel.
- **P3 Reach** ✅ 2026-09-14 — Codex `notify`; native toast sink; tear-off windows.
- **The control, built by us** ✅ 2026-10-07 — the fork of microsoft/terminal as a distributable (`OverShell.Terminal.Wpf`, Trusted Publishing, weekly sync); the composed rendering mode; `window.terminalOpacity` (§12.20).
- **P9 Oversight** ✅ 2026-10-06 — the herd log (one JSON line per event, a picker); changes while you were away (git status compared, counted on the item, listed) (§12.19).
- **P8 Orchestration** ✅ 2026-10-06 — spawn agents on worktrees with a first prompt; the control API; `OverShell mcp` (§12.18).
- **P7 Triage** ✅ 2026-10-06 — reply channels (integration / keys / typed); the inbox; toast actions; mute, watch, auto-advance (§12.17).
- **P6 An engine with an API** ✅ 2026-10-05 — IShell / IExtension; key sequences; herd mode; MRU Ctrl+Tab; global summon; prompt addressing; keyboard cursor; waiting tabs by age (§12.16).
- **Terminal parity** ✅ 2026-10-03 — shell integration injected into the command line (no profile edit); the environment rebuilt from the registry per tab (§12.15).
- **P5 Polish, honesty, depth** ✅ 2026-10-03 — WT chords; honest working directory + shell hook; restore hold + close question; previous screen; workspaces; strip overflow; jump list; themes; tear-off chrome; find.
- **P4 Resilience & history** ✅ 2026-10-03 — two-second session saves with a close reason; crash/sign-out aware restore; window placement; resume by id or by "most recent"; recently closed tabs + archived sessions + picker; restart with Windows (opt-in); vector harness icons.

Each phase ends as §11 did: zero warnings, in-process verification where possible, a
test-card section for what needs a hand, and this document updated.

### 12.9 P0 — what was built, what was verified, what was learned

**Where things live.** `OverShell.Core` (no WPF): `Commands/`, `Input/` (`KeyChord`
mirrors WPF's `Key` names so a typo is caught at load), `Agents/` (rule sets, loader,
state machine), `Search/`, `Notifications/`, `Settings/` (`AppSettings`, `PersistedState`),
`Integrations/` (report protocol, endpoint, Copilot translator, installer), bundled
`Resources/` (`keybindings.jsonc`, `settings.jsonc`, `agents/*.jsonc`, the OpenCode
plugin). App: `TerminalTab.Agent.cs` gathers evidence; `Agents/` (`ProcessTree`,
`ScreenReader`, `AgentServices`); `Chrome/PaletteWindow`; `Notifications/`
(`NotificationPipeline`, `ToastHost`, `TaskbarBadge`); `MainWindow.Herd.cs` wires it.

**Evidence flow per tab.** I/O thread: `TerminalStreamState.Observe` → signals queued.
UI thread: one drain per burst → `OnOutput` / `OnBell` / `OnNotification` / `OnProgress`
/ `OnPromptMark`; title changes → `OnTitle` + harness re-detection; the 500 ms heartbeat →
`Tick`, a UIA screen snapshot when output has settled for 300 ms (0.4–30 ms, hidden tabs
included), and a toolhelp process probe every 2.5 s when output changed or an
integration is authoritative. Integration reports arrive from the listener thread and
are marshalled to the tab by id.

**Harness identity** is the most trustworthy clue available, in order: an integration's
own `harness`, the launch command line, a known process image below the shell, the
title. The process tree below a `pwsh` running OpenCode, as measured:
`[opencode, cmd, cmd, cmd, conhost, dotnet, conhost, dotnet, conhost, node, dotnet…]` —
OpenCode is `opencode.exe` on Windows (the npm shim is a `.ps1`), so the probe sees it
directly; its MCP servers and LSPs are the rest.

**Release rule.** Integrations have no exit event we can trust (`opencode run` simply
ends; a TUI is quit), so an integration's authority lasts only while a process of its
harness runs below the shell; two probes (5 s) without one hand the tab back to the
detector — measured at +14.2 s for a run that finished at +9.5 s. An unseen `Done`
survives the hand-over and clears to `Unknown` (a shell has no "idle") when viewed.

**Done semantics, refined by a real run.** OpenCode emits `session.status idle` *and*
`session.idle` for one turn; the second report used to clear the fresh `Done`. An Idle
report while `Done` is unseen now only updates the explain trail.

**Self-test** (`OVERSHELL_SELFTEST=1`, `Diagnostics/HerdSelfTest.cs`) — 30 checks, all
passing, no input injected: commands run through the tab's own `SendText`, so OSC
sequences, environment variables and loopback requests take the real path. It found
three bugs before a human could: closing a tab raised an "Exited" notification (the exit
lands on the dispatcher after `Dispose`); an external `Close()` on the palette re-entered
from `Deactivated` while closing (`InvalidOperationException`); the summary fallback
showed the shell prompt as the agent's "summary". `OVERSHELL_SELFTEST=opencode` runs the
real harness (one small model call) — 8 checks, all passing.

**Notifications.** The `overlay` sink is a non-activating owned window (`WS_EX_NOACTIVATE`)
pinned to the terminal's top-right, so a toast never steals a keystroke; the taskbar sink
uses WPF's `TaskbarItemInfo` (ITaskbarList3) for the badge and progress colour plus
`FlashWindowEx`; `command` spawns without a shell, arguments templated. Palantir 2.0.1's
flags were verified against `--help`; `--launch overshell://…` waits for the P2 protocol
handler so a click does not open the "choose an app" dialog.

### 12.10 P1 — views, layouts, sidebar, dashboard, reload

**Layouts are regions, not a grid.** `ChromeLayout` (`Core/Layout`) says where the tabs
go (`top | bottom | left | right | hidden`) and how they look (`strip | list | rail`),
whether the Herd sidebar shows and on which side, and whether the status bar shows. A
free-form grid was considered and rejected: every arrangement anyone asked for is one of
these, a JSONC file stays five lines, and the window can hold the invariant that matters —
**the terminal keeps the middle cell whatever the layout**, so switching never re-parents
the native window (spike 2 says it would survive; not doing it is still cheaper). Eight
presets ship as embedded `layouts/*.jsonc`; a same-name file under
`%APPDATA%\OverShell\layouts\` replaces one wholesale, like the agent rule files.

**One strip, one sidebar, moved between hosts.** `TabStrip` is a single control with three
item templates; `ApplyLayout` detaches it and the `HerdSidebar` from wherever they are and
places them in the caption host, the bottom bar, or the left/right side panels (a list and
a sidebar on the same side sit side by side). The caption bar is 54 DIPs when it carries
the strip and 38 otherwise, and the backdrop bands follow (§7.5) — including no bottom
band in Zen, where the status bar is gone.

**A view is a layout plus content.** `settings.jsonc` → `views.{terminal,herd,dashboard,zen}`
each name a layout and `terminal` or `dashboard` content. `view.*` commands and the
caption switch (with the attention badge on the Herd button) change views; `view.toggle`
returns to the previous one; `layout.<name>` overrides the current view's layout for the
session. Switching to the dashboard collapses the terminal host — the native windows are
hidden, UIA still reads them (spike 1) — and gives the window keyboard focus so chords
keep routing; switching back refocuses the terminal (verified: Win32 focus = terminal HWND).

**Herd sidebar.** `HerdOrdering` (Core, tested) groups by project and sorts attention →
recency → tab order; groups by worst state → recency → name. The sidebar recomputes on
the heartbeat but compares a signature string first, so the visual tree is touched only
when something actually moved; rows bind to the live tabs for everything else. Activity
age ("12 s ago") is a per-tab property refreshed on the heartbeat.

**Dashboard.** One card per tab: header (state dot, glyph, label, project · branch), body
= the last 14 viewport rows read through UIA in the tab's scheme colours, footer (state,
summary, activity, progress). While the dashboard shows — or the switcher is open — every
tab is `ScreenWatched`: snapshots run about once a second whenever output changed,
regardless of settling; agents additionally keep their settle-based snapshot for detection.
A tab opened while the dashboard shows is watched from birth. Measured: 0.4–30 ms per
snapshot, on the worker thread.

**Hot reload.** One `FileSystemWatcher` on the configuration root (`*.jsonc`, subfolders),
400 ms debounce; the changed file decides what reloads: keybindings → chord map;
`agents\` → rule sets swapped in `AgentServices` and every tab re-detects
(`RulesReloaded`); `layouts\` → catalog, current view re-applied; `settings.jsonc` →
detection tuning, notification pipeline rebuilt, view re-applied. Verified live: a user
`layouts\top.jsonc` moved the strip to a left list with the sidebar on the right within
1.5 s of the write, a `keybindings.jsonc` bound `Ctrl+Alt+9` and unbound `Ctrl+T`, and
deleting both restored the presets.

**Git.** `GitRepository` (Core, tested) finds the root above a directory and reads the
branch from `HEAD`, following a `.git` *file* for worktrees; the tab re-reads only when
the directory or `HEAD`'s write time changed, every 2 s. The dirty marker spawns `git
status --porcelain` per repository through `GitStatusService`, at most once per
`git.statusIntervalMs`, and is **off by default**. The reference machine's pwsh profile
emits no OSC 9;9/7, so `cd` does not move the tab's directory (§9); the self-test
therefore opens a tab whose profile *starts* in the repository and sees `main`.

**Switcher preview.** In tabs mode the palette grows a 340 DIP pane showing the selected
tab's screen text, refreshed every 500 ms while open. Tabs are `ScreenWatched` for the
palette's lifetime and released on close unless the dashboard needs them.

**Self-test additions** (`OVERSHELL_SELFTEST=1`, 60+ checks): each view applied and
asserted (layout name, hosts, caption height, terminal/dashboard visibility, sidebar rows
= tabs, one card per tab with non-empty screen text, focus back in the terminal); every
preset applied and rendered to `%TEMP%\overshell-selftest-layout-*.png`; hot reload
through a temporary `OVERSHELL_CONFIG_DIR` (the test refuses to touch a real
configuration root); git branch on a repository tab. Palette focus checks **skip** when
OverShell is not the foreground window: an owned window cannot take focus then and closes
itself on `Deactivated`, by design — the machine was in use during several runs, which
also produced view switches and a `^C` in the transcript that were the user's, not ours.
### 12.11 P2 — depth: restore, protocol, prompt bar, groups, explain, skins, Claude

**Session restore.** `SessionSnapshot` (`Core/Settings`) is written to
`%LOCALAPPDATA%\OverShell\session.json` on close and every 30 s while running (only when
something changed; atomic temp-file rename). It holds the view, the active index, the
per-view layout overrides, and per tab: profile, working directory, label, group,
harness, whether the agent was still running, session id and resume command. At start the
tabs are reopened in order (a missing profile falls back to the default; a vanished
directory to the profile's own), and a tab whose agent **was running** gets its resume
command typed once the shell has printed something and been quiet for a second — never
before, or the text lands inside the banner; never after 20 s, or it lands in the void.
Verified across a real restart (`OVERSHELL_SELFTEST=session1` then `session2`): two tabs,
the label, the group, the active index and the layout came back, and `Write-Host
selftest-resumed` was typed and ran 2.0 s after the shell appeared. `OVERSHELL_STATE_DIR`
lets the tests use a throwaway state root, the same way `OVERSHELL_CONFIG_DIR` does.

**Where the resume command comes from.** An integration may send `session.resumeCommand`
with its report (the OpenCode plugin does: `opencode --session <id>`); otherwise the rule
file's `resumeCommand` pattern is filled with the reported id (`copilot --resume=<id>`,
`claude --resume <id>`). It is kept on the tab (`tab.resume` retypes it any time) and in
the session file.

**`overshell://` and one instance.** `ProtocolRegistration` writes
`HKCU\Software\Classes\overshell` at start (no elevation; only when absent or pointing at
another build) — `settings.protocol.register` turns it off; `OverShell protocol
status|register|unregister` does it by hand. `SingleInstance` listens on a per-user,
per-session named pipe; a second `OverShell.exe` connects, reads the server's pid, calls
`AllowSetForegroundWindow(pid)` — Windows lets the *clicked* process pass its foreground
right along, and nothing else can take it — sends its arguments as a JSON array and
exits 0. The running window parses `overshell://focus/<tab>` (`view/<id>`,
`new?profile=&cwd=`) with `ProtocolRequest` (Core, tested) and brings itself up on the
tab. The Palantir recipe now carries `--launch overshell://focus/{tab.id}`. Verified: a
second process launched from the self-test exited 0 and the running window switched tabs
in under a second.

**Prompt bar.** A WPF `TextBox` in the window (`Ctrl+Shift+Enter`), so it takes the
keyboard the ordinary way; Enter sends, Shift+Enter breaks the line, Up/Down walk the
history when the caret cannot move within the text. Targets: the active tab, every
running agent, the agents needing you, every tab. The text goes through
`TerminalTab.Paste` (bracketed where DECSET 2004 is on, one block otherwise) followed by
CR. `Ctrl+C`/`Ctrl+V` while the box has focus stay with the box: `OnChord` declines
clipboard commands when a `TextBoxBase` holds keyboard focus. **Snippets** —
`snippets.jsonc`, an array of `{ name, text, description? }` — become `snippet.<name>`
commands and a menu on the bar; the file hot-reloads and its commands are re-registered.
Verified: a prompt to the active tab ran; a broadcast to every tab ran in both.

**Groups and reorder.** `TerminalTab.Group` groups the strip and the list through a
live-grouping `ListCollectionView` (`IsLiveGrouping`), with a header per named group and
none for the ungrouped. The window keeps the *source* order equal to the grouped order —
`SetGroup` moves the tab next to its group's last member — so `Alt+N` still means what the
eye sees. Dragging a tab moves it live when the pointer crosses a neighbour (no ghost, no
drop indicator; the item simply changes place), and crossing into another group joins it;
`tab.moveLeft/Right` do the same by key. Groups persist in the session file.

**Explain panel.** `Ctrl+Shift+E`: an owned window (same reasons as the palette) showing
label, state, the explain trail, authority and source, harness and rule set, session id
and resume command, summary, project · branch · directory, the process images below the
shell from the last probe, the group, and the last 20 transitions with their reasons —
refreshed twice a second while open. Tabs keep the last 40 transitions.

**Skins.** `settings.skin` names `skins\<name>.xaml`, a `ResourceDictionary` loaded with
`XamlReader` and applied **before the first window** so every `StaticResource` — fonts,
metrics, brushes — resolves to it. Live recolour needed one fact the first attempt
missed: an application resource dictionary freezes every freezable it holds, and a frozen
brush cannot change colour. So at start each theme brush is replaced by one whose `Color`
is **data-bound** to a small model (a bound freezable cannot be frozen); a skin writes the
colour into the model and everything painted with that brush repaints. Fonts and metrics
already copied into controls wait for a restart. Verified live: `Accent.Base` went
`#4C8DFF → #00FF00` 1.5 s after the settings save, and back when the skin was removed.

**Claude Code hooks.** Claude has no hooks directory: entries live in
`~/.claude/settings.json` (or `$CLAUDE_CONFIG_DIR`), which `integrations install claude`
edits in place — our matcher groups (recognised by the `OVERSHELL_ENDPOINT` marker in their
command) appended per event, everything else round-tripped; `uninstall` removes only
ours. A settings file with comments or trailing commas is **refused, not rewritten**
(`integrations show claude` prints the entries for adding by hand). The shim is the same
`cmd.exe /d /c … curl.exe … --data-binary @-` line the Copilot hook uses;
`ClaudeHookTranslator` maps `SessionStart/End`, `UserPromptSubmit`, `Stop`, `StopFailure`
and `Notification(permission_prompt|elicitation_dialog|idle_prompt)` to states. Verified
without Claude itself (not installed here): the shim posted from inside a tab reached
`/v1/claude/{tab}/Notification`, blocked the tab, recorded the session id, and released
after two probes found no `claude` process — the merge/refuse/uninstall paths are unit
tested against a temporary `CLAUDE_CONFIG_DIR`.

**Found by the tests this phase.** The first skin implementation reported success while
nothing changed colour — the frozen-brush fact above; the check on the actual brush colour
caught it. A restore would have typed the resume command into every restored agent tab,
finished ones included; the snapshot records `agentRunning` and only those resume. The
palette and explain focus checks now **skip** rather than fail when another process has
taken the foreground mid-test: an owned window closing on `Deactivated` is the designed
behaviour, not a defect — the machine was in use during several runs, which also shrank
the window to 844 px once and made one prompt-bar screen read inconclusive (it passed on
the two quiet runs that followed).
### 12.12 P3 — reach: Codex, native toasts, tear-off windows

**Codex `notify`.** Codex CLI has one external hook: a top-level `notify = [program,
args…]` in `config.toml`, spawned directly (no shell, stdio null) with the event JSON
appended as the **last argument** — and it fires for `agent-turn-complete` only. Two
consequences shaped the integration. First, the shim must take a JSON argument
literally: a `cmd.exe /c` line or a PowerShell `-Command` string would both re-parse the
quotes, so it is a script run with `powershell.exe -NoProfile -NonInteractive
-ExecutionPolicy Bypass -File`, whose `$args` arrive verbatim; the body is posted as
UTF-8 *bytes* because Windows PowerShell would otherwise re-encode a string body as
Latin-1. Second, a hook that never says "working" cannot be an authority (§12.1): after
its first `idle` the detector would be ignored for the rest of the run. So Codex's
report is **advisory** — `IntegrationReport.Advisory`, also `"advisory": true` on the wire
for one-shot scripts — and `AgentStateMachine.OnAdvisory` marks the moment (Done when
unseen, Idle when viewed, attention raised, quiet timer reset so it cannot undo a fresh
Done), records the thread id and the last assistant message, and leaves the detector in
charge. `integrations install codex` writes the script into `$CODEX_HOME` and inserts a
marker comment plus the `notify` line among the top-level keys — before the first
`[table]`, where TOML requires them — touching nothing else; a `notify` the user wrote
is never replaced (`integrations show codex` prints ours to combine by hand). Uninstall
removes exactly the two lines and the script. Verified: the installed script, run from a
tab with the documented payload, reached `/v1/codex/{tab}/notify`; the tab became a
Codex agent with `codex resume thr-…` and the summary, authority still `Detector`.

**Native toasts.** `type: "toast"` shows Windows toasts without an external program.
The projection route (CsWinRT via a `net10.0-windows10.0.x` TFM) would put a ~30 MB
`Microsoft.Windows.SDK.NET.dll` back into an output trimmed to 2 MB (§4), so the five
interfaces needed — `IXmlDocumentIO`, `IToastNotificationFactory`,
`IToastNotification2`, `IToastNotificationManagerStatics`, `IToastNotifier` — are
declared by hand. Two facts the first attempt got wrong, both caught by the self-test:
.NET 5+ has **no built-in WinRT marshalling** — `UnmanagedType.HString`,
`UnmanagedType.IInspectable` and `ComInterfaceType.InterfaceIsIInspectable` throw
`MarshalDirectiveException` — so HSTRINGs are created and freed through
`WindowsCreateString`/`WindowsDeleteString` and every interface is IUnknown-based with
IInspectable's three slots spelled out first; and property accessors must be declared as
methods in IDL order (`put_Tag` comes before `get_Tag`; a C# property would emit the
getter first and shift every later slot). An unpackaged app needs an AppUserModelID:
`HKCU\Software\Classes\AppUserModelId\OverShell.Terminal` with a `DisplayName` is
enough (Windows 10 1709+). The toast carries `activationType="protocol"` and
`launch="overshell://focus/<tab>"`, so a click goes through the §12.11 handoff and no COM
activator is needed. Silent on purpose — the sound sink owns sound. Verified
independently: after the self-test, Windows PowerShell 5.1 (which can call WinRT) read
`ToastNotificationManager.History.GetHistory("OverShell.Terminal")` and found the toast
with its tag, group and `launch` URL. Off by default in favour of Palantir.

**Tear-off windows.** `tab.detach` (`Ctrl+Shift+D`, the tab menu) moves a tab's surface
into a `TearOffWindow` — standard chrome, the tab's background, titled by its label —
and `tab.attach` (`Ctrl+Shift+A`, or closing that window) brings it back. Spike 2
(§12.7) had shown the re-parent is safe; what P3 adds is everything around it: the tab
**stays in `Tabs`** (`Detached = true`), so detection, the sidebar, the dashboard,
notifications and the session file keep working; the main window shows the nearest
neighbour; `ActiveTab = detachedTab` raises the tear-off instead of changing the main
host; "viewed" is true when the tear-off is the foreground window; `ShortcutRouter`
accepts more windows (`AddWindow`) so Tab/arrows are still hand-delivered there and
chords fire — with `TargetTab` = the tear-off's tab while the chord comes from it, so
`Ctrl+Shift+W` closes *that* tab; right-click copy/paste and link hover resolve the tab
**from the HWND under the pointer** (`TabForTerminalHwnd`) rather than assuming the main
window's active tab; closing a detached tab attaches first so one path tears the view
down; a tear-off closed with a dead shell inside does not steal the active slot.
Verified: same terminal HWND before, during and after; `Write-Host` in the detached tab
appeared on its UIA screen; attach restored the host parent and the active tab, tear-off
count 0. Known gap: the link underline (owned by the main window) may sit behind a
tear-off in front of it.

### 12.13 P4 — resilience and history: what comes back after a crash, and from before

**The question.** Browsers survive a power cut: at the next start the tabs are there, and
the ones you closed on purpose are a menu away. P2 had a session file written on close and
every thirty seconds; a crash lost up to half a minute, the next start could not tell a
crash from a clean close, nothing came back where it had been, and a tab closed by
mistake was gone. P4 makes the session file the thing a browser's is.

**The file says how the run ended.** `SessionSnapshot` v2 carries `closeReason` —
`closed` from `OnClosed`, `sessionEnding` from `Application.SessionEnding` — and nothing
while running, so a file read at start *without* one was left by a run that was killed.
Version-1 files never said, so an upgrade does not report a crash. The save cadence is
**two seconds**: the comparable content (everything but the timestamp and the close
reason) is serialised and compared, and the write happens only on change — a `cd`, a new
tab, a label, a learned session id — so the file is cheap to keep current and a crash
loses two seconds at most. After the closing save nothing is written again, or a late
heartbeat would turn "closed" back into "running". Sign-out matters separately: WPF
answers `WM_QUERYENDSESSION` by raising `SessionEnding` and *scheduling* `Shutdown()`,
and Windows may end the process before that reaches `OnClosed` — so the handler saves
synchronously. Verified by sending the message to the hidden window WPF listens on (the
Application's parking HWND — the system broadcasts to every top-level window; sending it
to `MainWindow` raises nothing, which the first version of the test learned the hard way).

**What the next start does.** Restores, as always, and says in the status bar how the
last run ended when that is worth a word: *Restored 3 tabs from an interrupted session
(saved 10:21)* or *…after Windows signed out or restarted*. `--fresh` skips the restore
once; the session is not lost (below). The main window goes back to its placement, and so
does every tear-off — the P3 limitation "tear-offs come back attached" is gone. Placement
is DIPs plus a maximized flag (a minimized window remembers its last visible state), and
`WindowPlacement.Clamp` (Core, pure) keeps at least a caption's worth on the desktop
that exists *now*: a window saved on a monitor that is gone moves to the desktop corner,
one larger than a shrunken desktop shrinks. Tear-offs are re-created after the first
layout (a surface moves between windows only once its HWND exists, §12.12), at
`ApplicationIdle` rather than `Background` priority — the view's own focus request is
posted at `Input`, and it would otherwise pull activation back to the main window after a
restored tear-off had been brought forward.

**Which tab was in use.** Detaching the active tab moves `ActiveTab` to its neighbour (the
main window must show something), so a save that recorded `ActiveTab` made a restart come
back looking at the wrong tab when the user had been working in the tear-off. The save now
records the tab the user was last *looking at* — the tear-off's when a tear-off was the
OverShell window activated last — and a restore whose active tab is detached brings that
tear-off forward. Twice: a native child HWND revealing itself in the main window takes
activation back about 60 ms later, so once more after the reveal. Found by the extended
`session2` self-test; the three-way chase (`Activate()` returning true, the main window
re-activating, the trace of both) is in the commit.

**Getting the agent back.** `SessionRestore.Plan` (Core, pure, unit-tested) decides per
tab. The command is the saved one, else the rule's `resumeCommand` pattern with the saved
id, else — new — the rule's `resumeLastCommand`: the harness's own "most recent session"
form (`opencode --continue`, `copilot --continue`, `claude --continue`, `codex resume
--last`), used only when `session.resumeWithoutId` allows it (on by default). Then the
*profile* decides how: a **shell** profile gets the command typed once the shell has
printed and gone quiet (the abandon limit is now 45 s, for the shell that starts right
after a sign-in behind a dozen other start-ups); a profile whose **program is the agent**
(`…\opencode.exe`) is relaunched with the resume arguments appended — typing
`opencode --session …` into a running OpenCode lands in its prompt box as text, which is
what the P2 code would have done; a profile that **wraps** the agent in a shell command
(`pwsh -NoExit -Command opencode`) gets neither, only its directory, because neither way
is safe. `PlanAll` adds one rule a single-tab plan cannot: the "most recent" form is used
**once per harness per restore**, the active tab first — OpenCode's `--continue`, tried
from an empty directory, reopened the latest session of another project (the tool's notion
is machine-wide), so two id-less OpenCode tabs would both have landed in the same session.
A restored tab is seeded with the saved session id and resume command, so `tab.resume`
and the next session file work before the integration has spoken again.

**The plugin had a hole.** A resumed OpenCode session (`--continue`, `--session <id>` —
exactly what OverShell types after a restart) never fires `session.created`, the one
event the P0 plugin learned its id from, so a restored tab could never be resumed *by id*
again. The plugin now adopts the id from the first `session.updated` / `session.status`
/ `session.idle` that names a session it did not see born as a child. The
`opencode-resume` self-test proves it: a first `opencode run` creates a session; a second
OpenCode *process*, `opencode run --session <that id>`, reports the same id (7/7).

**History, the way a browser has it.** A tab the user closes is remembered — profile,
directory, label, group, harness, session id, resume command, when — in `recentlyClosed`
(the newest twenty, carried across restarts in the session file); `tab.reopenClosed`
(`Ctrl+Shift+T`) brings the newest back, activated, agent resumed through the planner.
Every clean close archives the session under `state\sessions\<stamp>.json`, and a start
that finds the live file interrupted archives it *first*, as `<stamp>-interrupted.json`,
so a session lost to a crash is never overwritten by the run that follows before the user
could bring it back; the newest ten are kept, an archive identical to the newest is
skipped. `session.history` opens a picker — recently closed tabs first (*closed 00:06 ·
agent was running · C:\…*), then earlier sessions (*Session interrupted Fri 22:06 — 2 tabs
· OpenCode ×2*); a tab reopens, a session adds all of its tabs next to the open ones.
`PaletteWindow.Picker` is the same owned window (§12.7 spike 3) with its own glyph and
empty text.

**Restart with Windows — opt-in.** `session.restartWithWindows` (default **off**)
registers through `RegisterApplicationRestart` with `RESTART_NO_CRASH | NO_HANG |
NO_PATCH`: Windows starts OverShell again after a restart or sign-out when "Automatically
save my restartable apps and restart them when I sign back in" is on — never after a
crash, so a crashing build cannot loop (Windows also refuses to restart a process that
ran under a minute). Toggled live on settings reload; read back with
`GetApplicationRestartSettings` for the trace (`'--restarted-by-windows' flags=0x7`).
Off because starting by itself is a choice the user makes, not the program.

**Icons.** The harness mark on tab items, sidebar rows and dashboard cards was a text
glyph (U+25C8 and friends) in a `TextBlock` with no font of its own: WPF's fallback drew
it from Segoe UI Symbol with Display-mode hinting at 11 px — a grey smudge, which is what
the user reported. It is now a filled vector per harness (`Theme/Icons.xaml`, 16×16 grid:
diamond-in-diamond, four-point star, eight-spoke asterisk, ring and dot, diamond), drawn
by one `HarnessIcon` control in all five places with the brush of the text beside it, so
skins recolour it by the same key; a user rule file can bring its own with `"icon":
"<path data>"`, a rule without one falls back to the text glyph in Segoe UI Symbol. On the
way, `settings.tabs.showHarnessGlyph` and `tabs.twoLine` turned out to be defined,
documented and never read; both now apply to every tab item.

**Verified** (all in-process, no synthetic input): 169 unit tests (snapshot v2 round trip
and v1 compatibility, interrupted detection, comparable JSON, planner — eight cases
including the once-per-harness rule, command-line tokenising, placement clamp, archive
order and retention). `session1`/`session2` 16/16 across a real restart: label, group,
the detached active tab recorded and brought forward, placement saved and re-applied
(read back from the trace — a tiling window manager on the reference machine moves every
window, so the test checks what was asked for, not where the window ended up), resume
typed and run, session seeded, archive written. `sessionend` 5/5. `history` 16/16.
`icons` 17/17 with PNGs of the icons alone and of the live strip. `opencode-resume` 7/7
and the standard OpenCode e2e 7/7 with the updated plugin. Live: OpenCode and Copilot
each resumed with `--continue` after a restore (typed 2.0 s / 1.5 s after the prompt,
both detected by the process probe); an OpenCode profile relaunched as
`"…\opencode.exe" --continue`; a kill followed by a start showed the interrupted note;
`--fresh` opened one tab; `restartWithWindows` registered and unregistered on hot reload.
The 85-check self-test still 85/85. Not exercised: a real sign-out or reboot (the
`WM_QUERYENDSESSION` path is), Claude Code and Codex (`--continue` / `resume --last` are
from their documentation).

### 12.14 P5 - polish, honesty, depth

Eleven items the user picked after living with P4, done in ten batches, one commit each,
every batch verified in-process before the next. The theme is honesty: the window should
not claim a directory it has not checked, should not restore into a crash loop, should
say when it is about to kill an agent, and should look like one program whether a tab is
in the main window or torn off.

**1 - Polish** (`979a80b`). `state.json` written atomically (temp + replace) - a power cut
mid-write had been able to lose every label. The detached mark is a Fluent glyph
(`\uE8A7`) in the strip, list, rail and sidebar instead of a text arrow. `TitleText.ForDisplay`
strips the state icons a harness puts at the front of its title (OpenCode's `●`, Copilot's
`◐`) so the tab label and status detail do not show the dot twice. A status message with
the status bar hidden (Zen) goes out as a toast through `NotificationPipeline.Announce`
rather than nowhere. The app got an icon (`build/Make-Icon.ps1` → `Assets/OverShell.ico`,
nine sizes, `>_`) and an explicit AUMID (`OverShell.Terminal`) so the taskbar groups the
window, the toasts and the jump list under one entry. Archive de-duplication compares
tabs only, not timestamps. **Chords follow Windows Terminal** where it has one:
`Ctrl+Shift+T` new, `Ctrl+Shift+W` close, `Ctrl+Shift+D` duplicate (new: `tab.duplicate`,
same profile, directory and group, placed next to its source), `Ctrl+Shift+Z` reopen,
`Ctrl+Shift+X` detach, `Ctrl+Shift+A` attach; **`Ctrl+T` is the shell's again** (PSReadLine
swaps characters on it). `.gitattributes` pins LF and marks binaries.

**2 - Working directory, honestly** (`a976eb6`). The status bar used to keep the starting
directory until the shell emitted OSC 9;9 / 7, which the reference machine's pwsh never
did. Two sources were added, each named in the explain panel (`CwdSource.Process` /
`Prompt`): the **process probe** reads the deepest shell descendant's current directory
from its PEB (`Agents/ProcessCwd.cs`, x64 and WOW64, `ProcessTree.DescendantEntries`,
`ShellCwdTarget.Pick`) - right for cmd and bash, useless for PowerShell, whose
`Set-Location` does **not** change the process directory; for pwsh the **prompt line** on
screen is read (`PromptPath.FromScreen`: `PS C:\path> `). Probes run 2 s after output
settles and every 10 s; a probed directory is accepted only when it is about the root shell
and the tab does not announce its own (`TerminalTab.AnnouncesDirectory`), so a shell that
emits OSC 9;9 is never second-guessed. `integrations install shell` appends a marked block
to **both** per-host PowerShell profiles (`shell-overshell-prompt.ps1`: wraps the existing
`prompt`, emits OSC 9;9 only inside OverShell / Windows Terminal and only for FileSystem
locations); `uninstall shell` removes it; `OVERSHELL_PROFILE_ROOT` stands in for Documents
in tests. `detection.cwdFromProcess` turns the probe off.

**3 - Resilience** (`e0c67db`). `RestorePolicy.ShouldHold`: two interrupted runs less than
60 s apart hold the restore and say so in the status bar, so a session that crashes the
window cannot loop (`SessionSnapshot.StartedAt` makes the gap measurable). A restored tab
carries a `RestoreNote` explaining its `restored` line. Closing with agents working asks
first - `PaletteWindow.Ask("Close OverShell?", …)` with "Close anyway - N agents working"
/ "Keep OverShell open"; `session.confirmCloseWithAgents` turns it off, a Windows
sign-out (`NoteSessionEnding`) and the self-tests (`AutoConfirmClose`) bypass it. On the
way, `PaletteItem.GlyphFont` lets picker rows use the icon font for PUA glyphs.

**4 - The previous screen** (`95f24d0`). After a crash the restored tab is a blank prompt;
what was on it is gone. `SessionScreens` saves the last 30 rows of every tab to
`session-screens.json` every 5 s when they changed; a restore after an interruption
injects them as a dim, italic **preamble** (`SessionDescriptor.Preamble`, written by
`WindowsTerminalSurface.Connect` before the session starts) with a rule under them - the
ghost of the last screen, clearly not live. `session.showPreviousScreen`:
`interrupted` (default), `always`, `never`.

**5 - Workspaces** (`696f7d0`). `workspaces\<name>.jsonc` (`Workspace`, `WorkspaceTab`,
`WorkspaceCatalog`): a named set of tabs - profile, directory, label, group - opened next
to the current tab by `workspace.open.<slug>` (one command per file, re-registered on hot
reload, as `layout.*` now is too - the "palette command only at next start" item is
closed) and saved from the live tabs by `workspace.save`. New `overshell://` actions:
`workspace/<name>`, `reopen`, `history[/<archive>]`, `new?profile=&cwd=`. **Bug found**:
`IntegrationEndpoint.Start` retried a port range on a listener that `HttpListener.Start()`
had already disposed on failure, so the second attempt threw `ObjectDisposedException` -
the Hyper-V excluded range on the reference machine (50000-50059) is exactly what made it
visible; a fresh listener per attempt now.

**6 - Strip overflow** (`667d976`). Forty tabs used to shrink to unreadable slivers. Tabs
have a width band (150-240 relaxed, equal widths down to a 104 px floor), the strip
scrolls with the wheel, `EnsureVisible` brings the active tab into view, edge fades
(`EdgeFadeBrush`, matched to the caption surface) show there is more, and an overflow
button opens `palette.tabs`. The label row is a star-column grid so ellipsis works.

**7 - Jump list** (`7b3db56`). The taskbar entry's jump list (`MainWindow.JumpList.cs`):
tasks New tab / Reopen closed tab / Session history, categories Recent sessions (5) and
Workspaces - all `overshell://` URLs to the registered exe, built at `Loaded` and when
workspaces reload; written to `CustomDestinations` under the AUMID.

**8 - Themes** (`6de0c5f`). `theme: "system" | "dark" | "light"` (default `system`,
read from `AppsUseLightTheme`) and `accent: "system" | "palette" | "#RRGGBB"` (the
Windows accent in the variant for the theme: light2 on dark, dark1 on light; DWM
colourisation as fallback). `Theme/Palette.Light.xaml` beside the dark one; a skin still
wins over the theme (`SkinLoader.SetBase` / `LoadPalette`); new keys `Accent.OnAccent`,
`Scroll.Thumb/Hover/Active`. Live on reload and on `WM_SETTINGCHANGE "ImmersiveColorSet"`
/ `WM_DWMCOLORIZATIONCOLORCHANGED`. **Bug found**: the active strip tab's text used a
hard-coded light colour, invisible on a light theme; it follows the scheme foreground now.

**9 - Tear-off chrome** (`b553727`). A tear-off had the stock frame beside a main window
with its own caption. It now has the same `WindowChrome`, the same backdrop
(`WindowChromeInterop.Apply`, whose dark-mode flag and frame border now follow the theme
for both windows and are re-applied on theme change), and in the caption the tab's state
dot, harness icon, label and state line; drag, double-click, restore-under-cursor and the
maximized margin correction match the main window. The close button re-attaches, as
closing a tear-off always has, and its tooltip says so.

**10 - Find** (`9d2bc8a`). Windows Terminal's search box is UWP-only; the one channel to
the buffer is the UI Automation text provider (§7.10). **Spike 7** (`OVERSHELL_SPIKES=find`)
measured what it offers: `FindText` over `DocumentRange` reaches scrollback (5 matches in
4.6 ms), an off-screen match reports **no bounding rectangles** - which is how a visible
match is told from a hidden one - `Select()` on a found range does what the terminal's own
search does (`Terminal::SelectNewRegion`: scrolls a hidden match into view, selects it,
and the renderer paints it at once, checked with a screen capture), `ScrollIntoView` is
exact. `Chrome/FindBar` sits under the terminal like the prompt bar: `Ctrl+Shift+F`
(`terminal.find`), a box, arrows, a match-case toggle, "3 of 5" / "No matches" / "500+",
Esc back to the terminal. **Enter and F3 walk upwards** through older text, as Windows
Terminal's box does - what you look for just scrolled past - Shift+Enter / Shift+F3
downwards; a fresh needle starts at the lowest match on screen. The current match is the
terminal's own selection with an accent outline; the other visible matches are tinted by
a second `OverlayHost` (`ShowCells`). `FindSession` **re-derives** matches rather than
tracking them: output (polled at 200 ms), the viewport scrolling (new
`ITerminalSurface.ViewportChanged`, read off the control's scroll bar - the package keeps
the terminal's scroll events internal) and the window moving simply run the search again
and keep the current match by its buffer position; only a step or a new needle moves the
viewport. The open bar follows the active tab, a closing tab takes its session with it,
and each tear-off has a bar of its own (`FindBarController` is the shared glue), so a
chord pressed there searches the tab in front of the user. **Two provider behaviours
found**, both handled in `TerminalTextProbe.TryFindText`: with **no hit, `FindText` hands
back a range object anyway**, one that fails every call with E_FAIL - so "No matches" was
an exception, and the link probe's `ApproximateCellRects` fallback for text the provider
"cannot find" had been unreachable since §11.8, the exception having skipped it; and the
known one-glyph-long range (`TrimToNeedle` now returns the trimmed text, which also keeps
a match-case search exact against what the provider matched).

**Verified** (all in-process, no synthetic input; 207 unit tests): one self-test mode per
batch - `polish` 15-17/17 (the two focus checks skip when another process holds the
foreground), `cwd` 13/13, `resilience` 12/12 (an archived interrupted run plus a live file
→ the hold note; the close question answered through its command), `ghost` 8/8 with a
PrintWindow capture of the preamble, `workspaces` 12/12, `overflow` 14/14 with strip
renders at 3, 12 and 40 tabs, `jumplist` 9/9 (the `.customDestinations-ms` file read
back), `theme` 12/12 with a full-window light capture, `tearoff` 10/10 with the caption
rendered, `find` 27/27 with a true screen capture (BitBlt - PrintWindow cannot see the
overlay window). After every batch the 85-check self-test still 85/85 and `session1`/
`session2` across a real restart. Side effects on the reference machine: the shell hook is
installed in both PowerShell profiles; the OpenCode plugin was updated to adopt the id of
a resumed session. Not exercised: a real sign-out or reboot, Claude Code and Codex, a
signed release run and the first winget review (§13.5).

### 12.15 Two things Windows Terminal gets right that OverShell had not copied

Both surfaced by the user in the same afternoon, both about what a tab *inherits*.

**Shell integration without a profile edit** (`2f064a3`). §12.14's `integrations install
shell` worked, but asking people to put a block into their PowerShell profile is a chore,
and the block only wraps the prompt defined *above* it. VS Code solved this years ago: it
launches `pwsh -NoExit -Command ". '<script>'"`. Profiles run first (they run unless
`-NoProfile`), then the script - so it wraps whatever prompt the user ends up with, in
the right order, which a profile edit cannot promise. `ShellLaunch.Inject` (Core, pure)
does that for a plain `pwsh` / `powershell` launch and leaves everything else alone: a
command line that already runs a command, a file or an encoded command - PowerShell
accepts unique prefixes (`-c`, `-com`) and short aliases (`-wd`, `-ep`), both matched - a
positional script, `--`, `-`, and any other program. An unquoted path with spaces
resolves as `CreateProcess` resolves it (the shortest run of tokens ending in `.exe`).
The script is written once per start under the state root (`shell\overshell-prompt.ps1`);
the detector keeps looking at the profile's own command line; the explain panel says
"injected" or why not, and whether the directory is announced or probed.
`detection.injectShellIntegration` (default true). `integrations install shell` stays
for profiles that wrap a command and for Windows PowerShell under `Restricted` policy (a
dot-sourced file is refused there; the `catch` keeps the shell usable, the probe stands in).
**Measured:** `-Command` suppresses PowerShell's version banner - a tab starts at its
prompt, as VS Code's terminal does; PSReadLine, the profile and the title are unchanged.
**Bug found:** `_cwdFromShell` was set only when the announced directory *differed* from
the current one, so a shell announcing the directory it started in was not counted as an
announcing shell - the probe kept guessing beside an exact source. It counts from the
first announcement now.

**The environment, rebuilt per tab** (`c2f2be4`). A tab inherited OverShell's process
environment, so its PATH was whatever *launched OverShell* had. Explorer refreshes its
block on `WM_SETTINGCHANGE`; a browser or a long-running app does not; a start through
`overshell://` from such an app gets its stale block. On the reference machine Copilot's
folder joined the user PATH on 2 Oct 22:43; Explorer had it, Edge (running since 1 Oct)
did not - and a URL opened from Edge started OverShell without it, while every Windows
Terminal tab had it. Terminal rebuilds the environment for every tab
(`compatibility.reloadEnvironmentVariables`, default true) from the registry - system and
user variables, the volatile per-session ones, the ProgramFiles family, PATH as
system;user, `%X%` expanded against the block being built, **process-only variables
dropped** (`til/env.h`, `regenerate()` on an empty map; only fifteen seeds such as
`USERPROFILE` and `APPDATA` come from the process). `CreateEnvironmentBlock` is the userenv
API the logon path uses for the same thing; `UserEnvironment.FromRegistry` calls it on our
own token and `ConPtySession` starts from that block, then adds `WT_SESSION`,
`WT_PROFILE_ID`, the integration variables, and - as Terminal does - names them in
`WSLENV` so a shell inside WSL sees them (never PATH). The setting carries Terminal's
name and default; `false` inherits as before, for a start from a shell with variables of
its own. The explain panel shows the source (`env  registry (86 variables)`).

**The harness icon, a second time.** §12.13 replaced the text glyphs with vectors and the
user still saw a smudge on the OpenCode tab - rightly: the icon was a *thin diamond ring*
with diagonal edges, drawn through a `Path` at 11 DIP, which at 150 % is 16.5 device
pixels - every edge on a half pixel, anti-aliased to grey. Two fixes. `HarnessIcon` now
draws in `OnRender` through one transform into a box that is a whole number of **device**
pixels with its origin on a pixel (12 DIP → 18 px at 150 %, not 16.5); a geometry on a
grid of at most 8 units is treated as **pixel art** and gets a whole number of pixels per
unit, so its edges are exact. And the OpenCode icon is OpenCode's own mark (its favicon,
MIT): a 4×5 frame one unit thick with a 2×2 block in the lower part of the hole - all
axis-aligned, so at 150 % it is a 12×15 px shape with three colours and no in-betweens;
the block is drawn as a second tone (`Harness.Icon.opencode.Muted`, same brush at 40 %).
The other marks were made solid: a fatter sparkle, the asterisk as four 2.6-unit bars
(a `GeometryGroup` of rotated rectangles), a thicker ring. The `icons` self-test now
renders the OpenCode mark at 150 % and checks the box is 12×15 px and the bitmap holds
three colours, and the vector box 18×18.

**Verified** (in-process, 250 unit tests): `inject` 14/14 - the command line, the script
on disk, the announcement from the first prompt, `cd` followed in 0.2 s, PSReadLine and
the guard variable read back from inside the shell, the explain lines, a `-Command`
profile and `cmd` left alone with the probe at work, the setting off and on live; `env`
13/13 - the harness starts OverShell with a PATH stripped of all 44 user-registry entries
and a variable of its own, the tab's shell prints its environment back: all 44 present,
the variable gone, the Terminal and integration variables set and in `WSLENV`, the
logon-time variables there; the setting off live inherits the stripped PATH and the
variable, back on returns to the registry. `icons` 17/17 with the live strip at 150 %
captured and zoomed: the mark pixel-exact. `cwd` 12/12 (its "never announces" shell now
carries `-Command`), 85-check 85/85, `session1`/`session2`, OpenCode e2e 7/7 (its plugin
lives on the carried variables). Side effect undone on the reference machine: the profile
block from §12.14 was uninstalled; the empty `WindowsPowerShell` profile that install had
created was removed.

### 12.16 P6 - an engine with an API, and the keyboard on top of it

**The shape.** The user asked for OverShell to be extensible: an engine kept generic, with
features added on top through an API - eventually from C#, PowerShell or Lua, for now at
least modular. So P6 began by drawing that line. `OverShell.Core/Extensibility` is the
contract: `IExtension` (an id, `Initialize(IShell)`, `Dispose`); `IShell` - the tabs, the
active and target tab, events for open / close / activation / state / attention, the
window's own 500 ms heartbeat (so extensions need no timers), `Ready` (the HWND exists),
`SettingsChanged`, the command registry, key bindings, per-extension settings bound from
`settings.extensions.<id>`, the host UI, open / activate / close, trace, post; `ITab` -
what a tab is to a feature, plus a `Properties` bag saved with the session and restored
with the tab; `IKeyBindings` - hints, defaults layered **beneath** the user's file (so
`keybindings.jsonc` overrides or unbinds anything an extension brings), the pending
sequence, Ctrl-release, and `Intercept` for an extension that owns the keyboard for a
moment; `IHostUi` - status line, notifications, prompt / pick / ask as tasks, a slot for
a bar under the terminal, global hotkeys, front / minimize / foreground. `ShellHost`
adapts `MainWindow` to it; `ExtensionHost` loads the built-ins, each one off with
`extensions.<id>.enabled: false`, a failing one skipped and traced rather than fatal. The
window keeps what only it can do (tabs, surfaces, sessions, the key dispatcher, the
panes' cursor); every feature below is an extension using the API and nothing else. A
script host later is a second implementation of the same interfaces, not a new design.

**Key sequences** (engine). `KeySequence` - chords separated by spaces, `ctrl+shift+k j`,
the VS Code / Zed notation - and a `KeybindingMap` keyed by sequence: `Resolve` says
command / prefix / nothing, `Continuations` lists what can follow a prefix, `"stay": true`
keeps the prefix after a command so the last chord repeats, and a chord that is both
bound and the start of a longer sequence is reported (the longer one can never fire).
`KeySequenceDispatcher` is the pure state machine: while a sequence is pending every
chord is taken - one that completes a binding runs it, a longer prefix keeps waiting,
anything else ends the sequence **without reaching the terminal** (a leader key that
leaks half a sequence into the shell is worse than none); Esc cancels; the timeout
(`keys.sequenceTimeoutMs`, 3 s) is the heartbeat's. `MainWindow.OnChord` feeds it instead
of a flat lookup; a chord that ran inside a sequence is always consumed, even by a command
that declined.

**Herd mode** (`herd.mode`). `Ctrl+Shift+K`, then: `j`/`k` next / previous (stay), `J`/`K`
move, `b`/`B` next / previous waiting (stay), `d` next finished unseen, `1-9`, `l` last tab,
`n` new, `x` close, `r` rename, `g` group, `e` explain, `p` prompt bar, `f` find, `/`
switcher, `t`/`a` detach / attach, `s`/`c` cursor into the sidebar / cards, `?` the keys. A
which-key bar built from the key map shows while the leader is pending - navigation
first, digits as one chip, a repeat mark on stay keys - so a user's own sequences appear
in it. `tab.last` bounces between the two most recent tabs.

**Waiting tabs by age.** `HerdOrdering.NextWaiting`: the tab that has waited longest
(`attention.order: age`, the default; `strip` keeps the old next-in-order), blocked before
unseen-done. `tab.jumpToAttention` uses it; `tab.nextBlocked` / `previousBlocked` /
`nextDone` are new.

**MRU switching** (`tabs.mru`). Windows Terminal's default (`tabSwitcherMode: mru`): hold
Ctrl, tap Tab through the tabs in the order you last used them, release to land - an
Alt+Tab for tabs. The overlay is a non-activating owned window over the terminal (the
terminal keeps the keyboard, which is what lets Ctrl's release be seen) listing the tabs
with the selected one's screen rows; Tab / Shift+Tab step through `Intercept`, release
commits through `ControlReleased`, Esc cancels, a quick tap bounces to the previous tab.
`Ctrl+PgUp/PgDn` still walk the strip in order; `switcherMode: "inOrder"` or a user
`ctrl+tab` binding restores cycling. The embedded `ctrl+tab` default is gone.

**Global summon** (`summon`). `extensions.summon.keys` (default Win+`) registered with
`RegisterHotKey` once the window has its handle: from any application the window comes to
the front on the tab that has waited longest (`to: attention`) or stays on its tab
(`current`); pressed in front, it minimizes (`toggle`). A key another program holds is
reported in the trace and the status line - on the reference machine the tiling window
manager has Win+`, so that is what the note says there. **Bug found:** "in front" has to
be the foreground window as Windows sees it, visible and not minimized; `Window.IsActive`
stays true for a minimized window that was active, so a summon of a minimized window
minimized it "again".

**Prompt addressing** (engine, `PromptAddress`). Address words at the front of a prompt
choose its tabs from the keyboard, in the grammar the switcher already uses: `@3` by
number, `@label` (exact, else prefix), `#group` (prefix), `@blocked` / `@working` / `@done`
/ `@idle` by state, `@agents` / `@all` / `@active`; several combine; the rest is sent. A
word that names nothing is reported and **nothing is sent**. The bar echoes the route
under the box while typing (→ api, web). The combobox gains "This tab's group".

**Keyboard cursor** (`keynav`). A cursor separate from the active tab in the sidebar and
the dashboard (`TerminalTab.IsCursor`, an accent outline): the view's own chord pressed
again - or herd mode `s` / `c` - puts it on the active tab; arrows and `j`/`k` move it in
the pane's own order, Home/End jump, Enter activates (a card also opens the terminal
view), Space activates and keeps the cursor, Esc returns to the terminal; a chord leaves
the cursor and runs.

**Verified** (in-process, no synthetic input; 281 unit tests): 32 new unit tests
(sequences, the map with shadowing and extension defaults beneath the user file, the
dispatcher's stay / stray / Esc / timeout, every address kind and the echo); self-test
modes `herdmode` 24/24 (the extension loaded, 28 sequences, the leader swallowed, the bar's
content with a render, stay and leave, a stray key swallowed then passed outside the
mode, Esc, the timeout, `tab.last`, oldest-first for the three waiting commands), `mru`
15/15 (order after activations, the overlay with a render, stepping both ways through the
interceptor, the commit landing and reordering, the quick bounce, Esc, a tab closed while
open, Ctrl+PgDn in order, Tab alone reaching the terminal - the Win32 Ctrl key-up is not
synthesised, the test calls the method the release handler calls), `summon` 8/8 with one
skip (the harness cannot give the window the foreground; the WM_HOTKEY handler is
invoked, not the keypress), `address` 12/12 (the echo, @api reaching one tab with the
word not sent, @3, #group reaching two tabs and not the third, a miss sending nothing,
the Group target), `keynav` 17/17 (renders of the outlined sidebar row and the cards).
85-check 85/85 and `session1`/`session2` after every batch.

### 12.17 P7 - triage: answer the herd without visiting each tab

**The reply channel.** Answering an agent meant switching to its tab. Three channels now,
chosen per tab (`ITab.ReplyChannel`): the **integration**, when the harness's plugin is
listening - exact, through the harness's own API; the rule file's **keys**
(`"answers": { "approve", "deny" }` - copilot `y`/`n` from its own `[y/N]` prompts, claude
`1`/Esc and codex `y`/`n` from their documentation, not measured here); **typed text**
otherwise. OpenCode has no `answers` on purpose: its permission dialog is arrows + Enter
with no letter keys, so typing into it would be a guess - the integration is the channel.
The plumbing: the endpoint keeps a per-tab command queue (`TabCommandQueue`) that the
plugin long-polls on `GET /v1/tabs/{id}/commands?after=N` (held up to 25 s; a tab counts
as "listening" while something has polled within 40 s); a blocked report may carry
`requestId` / `requestKind`, kept on the tab as its `OpenRequest`; `ITab.Answer(approve)`
and `Reply(text)` enqueue `{permission|question|prompt, requestId, response}` or fall
back to keys or typing. The OpenCode plugin (v2) reports `permission.asked` /
`question.asked` with the id, polls, and executes: `client.permission.reply` (the
per-session `postSessionIdPermissionsPermissionId` route as fallback), `question.reply`,
`tui.appendPrompt` + `submitPrompt` (`session.promptAsync` headless). **Measured:**
headless `opencode run` asks *and answers* its own permission within the same instant
(`permission.asked` → `permission.replied` 100 ms apart - the binary auto-replies "once"
outside the TUI), so an open permission exists only in the TUI, which the self-test does
not drive (§7.8); what is proven live is the ask reported with the state and the plugin
long-polling the queue; the reply itself is proven against a poller on the real endpoint.

**The inbox** (`inbox`, `Ctrl+Shift+I`, herd mode `i`): every waiting tab oldest first,
blocked before finished, each with the line that asked - the request's title, else the
screen row the blocked pattern matched (`StateRules.MatchingLine`; until now only the
pattern was kept), else the summary - its channel, and the tab's screen beside it;
`j`/`k`, Enter jumps, `y`/`n` answer and the item leaves when the tab moves on (the
selection moves to the next), a reply box (`i` or Tab; Enter sends), `Shift+A` approves
everything approvable after a question through `IHostUi.AskAsync`, Esc.

**Toast actions.** A blocked tab with a one-key channel (`NotificationEvent.CanAnswer`)
gets Allow / Deny on its in-window toast and `<actions>` on its native toast: buttons that
open `overshell://reply/<tab>?answer=approve|deny&nonce=...`. The nonce is this run's; a
reply URL without it only focuses the tab.

**Mute, watch, auto-advance** (`triage`). `tab.mute` drops a tab's notifications through
`IShell.NotificationFilter`, consulted before any sink - the dot and badge still show, a
glyph marks the item, and the mute lives in `ITab.Properties`, so it survives a restart
with the tab (`SavedTab.Extra`). `tab.watch` takes a regular expression over the screen
and fires one notification when a row starts matching, again only after the row has gone.
`extensions.triage.autoAdvance` (off) jumps to the next waiting tab when the blocked tab
in front moves on.

**Bugs found.** A tab opened in the background kept its state machine counted as
"viewed" until something toggled it (`TerminalTab._viewed` started false against the
machine's true, so the first `SetViewed(false)` never reached it): its first Done showed
as plain idle with no unread mark. Both start true now and `AddTab` calls `UpdateViewed`.
`TabCommandQueue.Forget` woke a long poll that then waited out its hold anyway.

**Verified** (291 unit tests): 9 new unit tests (the queue's poll after a sequence, wake
on enqueue, empty after the hold, expiry, forget, the report's request id, `MatchingLine`,
the bundled answers and launch commands, the reply URL); `inbox` 22/22 - a poller on the
real endpoint receiving `{permission, per_42, approve}` and a prompt with nothing typed
into the terminal, a reply naming an open question, a copilot tab typing `y`+Enter (read
back from the screen), the inbox's order and lines, the window (render), an item leaving
with the selection moving on, approve-all asking first and approving nothing when
dismissed, the toast with Allow / Deny (render), a Deny URL with this run's nonce typing
`n`, a URL without the nonce only focusing; `triage` 13/13; `opencode` e2e 7/7 with the v2
plugin; `opencode-reply` live as described. 85-check 85/85, `session1`/`session2`, every
P6 mode. Side effect on the reference machine: the OpenCode plugin was updated to v2.

### 12.18 P8 - orchestration: spawn agents, and a door for other agents

**Spawning.** A tab can be opened *with a job*: `TabRequest.Prompt` (and `WorkspaceTab.Prompt`
in a workspace file) is the agent's first prompt, delivered by `ITab.ScheduleFirstPrompt`
once the agent in the tab is **detected and ready** - through the integration's queue when
its plugin listens, else pasted with Enter - and given up on after 90 s with a status note
(`FirstPromptAbandoned`). `agent.new` (herd mode `N`) asks for the harness (the rule files
with a `launch` command), the directory and the prompt; `agent.newWorktree` (`W`) first
runs `git worktree add` for a branch beside the repository (`WorktreePlan`: `<repo>-<slug>`,
numbered when taken, `-b` only when the branch is new) and opens the tab in it, so three
agents can work on one repository without stepping on each other's files. Both are an
extension (`SpawnExtension`) over the API, like everything since P6.

**What "ready" turned out to mean.** Measured against the real OpenCode TUI, three rules in
a row were wrong before one was right. "The plugin is listening" is not readiness: the
plugin polls from the moment the server loads (+3.5 s), seconds before the TUI has drawn
its composer (+8 s), and `tui.appendPrompt` is a bus event the TUI picks up over its event
stream - appended before the composer exists, it is dropped on the floor, silently.
"Output quiet for two seconds" is not readiness either: the TUI's boot has gaps of two
seconds and more between its splash and its home screen. What is readiness is the thing
the rule files already describe for the other harnesses: **the composer on screen**
(`screen.idle` - opencode now has `Ask anything` / `ctrl+p commands`; copilot, claude and
codex had `? for shortcuts`). So a first prompt waits for an **explicit idle**
(`AgentStateMachine.ExplicitIdle`: a title, screen or progress rule that names idle, or the
integration's report - not the quiet timer) whenever the rules know what idle looks like
(`AgentRuleSet.KnowsIdle`), and only falls back to quiet output for a harness that has no
such rule. One more thing: the marker is looked for on the **whole screen**, not the
rules' bottom 12 rows - OpenCode's home screen centres its composer, 35 rows up on a tall
terminal. Delivered at +5.6 s on the composer, the turn finished at +13.6 s.

**The control API** (`endpoint.control`, on). `ShellControl` (Core) is the control API's
meaning on `IShell`/`ITab` alone - describe, screen, send, reply, open, wait, close - so a
caller over the wire gets exactly what an extension gets in-process, no more, and every
action is marshalled through `IShell.Post`. The endpoint serves it under `/v1/tabs` with the
per-run bearer token: `GET /v1/tabs` (the fuller description), `GET /v1/tabs/{tab}`,
`GET .../screen`, `GET .../wait?states=idle,done&timeout=60` (held up to 120 s; answers
`timedOut`, or `closed`), `POST /v1/tabs` (`profile`, `cwd`, `label`, `group`, `command`,
`prompt`, `activate`, or `harness` for a rule file's launch command), `POST .../input`
(`text`, `enter`), `POST .../reply` (`answer: approve|deny` through `ITab.Answer`, or
`text` through `Reply` - 409 when the tab has no such channel), `DELETE /v1/tabs/{tab}`
(never the last tab: that would close the window). A tab is its id or a unique label. With
the API on, the window writes `state\endpoint.json` (url, token, pid) for its lifetime, so
a process started later can find it; the token is per run, so a stale file names a dead
pid.

**`OverShell mcp`.** A Model Context Protocol server over stdio (`McpServer`, Core):
`initialize`, `ping`, `tools/list`, `tools/call`; seven tools - `overshell_list_tabs`,
`overshell_read_screen`, `overshell_send`, `overshell_reply`, `overshell_spawn`,
`overshell_wait`, `overshell_close` - each one control call against the running window
through `endpoint.json`, re-read at every call so a restarted window (new port, new token)
is picked up without restarting the host. A window that is not there is a *tool* error, not
a protocol error, so a host still starts. Nothing but JSON-RPC lines goes to stdout; run by
hand with no pipes the verb explains itself and leaves. The point: an agent in one tab (or
outside OverShell altogether) can now oversee the herd - spawn three workers on three
worktrees, wait for them, read what they ask, answer - with the same seven verbs a human
has.

**Bugs found.** (1) The heartbeat only re-evaluated a `Working` agent, so a TUI that drew
its screen and said nothing stayed `Unknown` until the next output - 68 s in one run;
`Tick` covers `Unknown` now. (2) An integration's *first* report ending a `Working` the
detector had only guessed at (0.3 s of render activity, then "idle: session started") was
counted as a finished turn - a `Done` flash with an attention event at session start; the
report path now applies the heuristic path's `MinimumWorkForDone` when the Working it ends
was the detector's. (3) The triage watch self-test typed its own pattern, so a snapshot
landing between the echo and the output counted two hits (a test artefact, not a watch
defect - the command is built from pieces now). (4) The OpenCode plugin swallowed a failed
call; it now tries both SDK call shapes (flat and nested), waits briefly between append and
submit, and reports a miss through OpenCode's log.

**Verified** (309 unit tests): `WorktreePlan`; `ExplicitIdle` / `KnowsIdle`; the heartbeat
turning a quiet Unknown idle; the first report after a guessed Working; `McpServer` against
a fake wire (initialize with version negotiation, notifications, -32601, the seven tools
and their schemas, each tool's call and its error shape, a window that is not there), the
stdio loop, `endpoint.json`, the 403 off switch against a real listener. In-process:
`spawn` 12/12 (a tab with a command and a first prompt - typed once the shell is at its
prompt, waiting while the agent works, pasted once idle, read back; a worktree beside a
temporary repository on the planned branch, numbered on repeat; a prompt with no agent
waits); **`spawn-opencode` 5/5 live** (delivered on the composer, no Done flash, OpenCode
went to work and finished the turn); `mcp` 21/21 (every route over real HTTP, then
`OverShell mcp` as a child process on pipes reaching this window through `endpoint.json`);
`inbox`, `herdmode`, `triage`, `opencode` e2e, 85-check 85/85.

### 12.19 P9 - oversight: the herd log, and what changed while you were away

**The herd log.** Everything the window saw happen, kept: a tab opened or closed, a state
change with its reason, a call for attention, the run itself - one JSON line each in
`state\logs\<stamp>.jsonl`, one file per run, flushed per line so a crash loses nothing,
the newest 30 kept (`extensions.herd.log.keep`). `HerdLog` (Core) writes and reads it;
`HerdLogExtension` feeds it from the API's events alone (`TabOpened`, `TabClosed`,
`TabStateChanged`, `TabAttention`, `Ready`), so what it records is exactly what an extension
can see - the point of P6, paying off again. `herd.log` (herd mode `L`) is this run's
entries newest first in a picker, each with its time, tab, transition and reason; Enter goes
to the tab. The answer to "what happened while I was at lunch" is a file a text editor can
read and a list a key can open.

**Changes while you were away.** The other question after lunch is "what did it *do*". When
an agent's turn ends in a tab you are not looking at (`Working` to anything else, the tab
not active), `ChangesExtension` runs `git status --porcelain` in the tab's repository and
compares it with the status taken when you last looked (the baseline, taken at open and
whenever the tab becomes active): the files whose status appeared, changed or went away
(committed, reverted - a change too, shown as *clean*) are counted on the tab's item -
`2 changed`, in the accent, next to the label in the strip, the list and the sidebar - and
listed by `tab.changes` (herd mode `D`) with their status in words; Enter opens the file.
The list accumulates across turns while you are away; looking at the tab clears the mark
but keeps the list readable (that is when you want it), and the first turn after that
starts a fresh one. Count and list ride in `ITab.Properties` (`git.changes`,
`git.changes.files`), so a restart keeps both. `GitStatus` (Core) does the parsing
(renames, quoted paths) and the comparison, both pure and tested without git; the one
process spawn is per finished turn, never while agents work. Needs git on PATH; a tab
outside a repository is never marked.

**A slip from P7 fixed.** The triage mute glyph had been dropped into the strip's and the
list's two-line template without a `Grid.Column`, so it sat in column 0 over the harness
icon (the self-test had checked the property, not the geometry). The detached mark, the
mute glyph and the new count now share a horizontal panel in column 2; in the rail the
glyph takes the free top-left corner.

**Verified** (313 unit tests): the log's round trip through the file with a line cut
short, the run files' order and pruning, porcelain parsing (renames, quotes, every status
word) and the comparison (appeared, changed, left). In-process: `herdlog` 12/12 (the file
under logs\, the run entry, open / state / attention / Done / close entries for a tab
driven by reports, the file reading back the same entries in order while the writer holds
it, the picker rendered); `changes` 15/15 (a temporary repository; a turn out of view
counting an edited and an added file, named with their status, on the item, in the
tooltip and in the properties; a turn that changed nothing adding nothing; a look clearing
the mark and keeping the list; the picker from the tab itself, rendered; the next turn
starting afresh; a tab outside any repository unmarked); `triage` 13/13 with the moved
glyph, `herdmode` 24/24, 85-check 85/85.

### 12.20 The terminal control, built by us: a fork as a distributable, and the body goes translucent

**Why a fork, and why not a dependency on its source.** Everything OverShell draws in the terminal
body comes from `Microsoft.Terminal.Control.dll`, and until now that binary arrived as
`CI.Microsoft.Terminal.Wpf` - a third party's repack of Microsoft's CI output, unsigned, with no
release behind it (§9). §7.6 had established that the one thing the herd wanted and could not
have, a translucent terminal body, needed a change *inside* that binary. The decision: fork
microsoft/terminal, but treat the result as a **distributable**, not as source. The fork
([MoaidHathot/terminal](https://github.com/MoaidHathot/terminal), branch `overshell`) builds the
control on GitHub-hosted runners and publishes it as the NuGet package **`OverShell.Terminal.Wpf`**;
OverShell references the package by version and upgrades by bumping it. No submodule, no vendored
binaries, no C++ toolchain on the consumer's machine. Upstream moves, the fork rebases, a new
version comes out, the consumer bumps - the same relationship OverShell has with any package, with
the fork's own README (`build/overshell/README.md`) as the contract: the upstream base commit,
the patch series, the versioning scheme `A.B.YYMMDD.N` (upstream line, upstream base commit date,
fork revision).

**The pipeline.** `Build.ps1` is a port of the `buildWPF` path of upstream's own Azure Pipelines
job (vcpkg from Visual Studio, `VCToolsVersion` pinned, the three restores, `msbuild
OpenConsole.slnx /t:Terminal\Control\TerminalControl;Terminal\wpf\WpfTerminalControl`), and it
built the unpatched tree on `windows-2022` (VS 2022, SDK 22621) in six minutes at the first
attempt. `Pack.ps1` + a nuspec produce the same shape as upstream's pack target minus the 80 MB
native PDBs, which go to the GitHub release. Three workflows: `overshell-ci` (PRs and pushes; x64
build + dry-run pack), `overshell-release` (a `wpf-v*` tag: x64 + ARM64, pack, GitHub release with
`SHA256SUMS.txt`, then nuget.org through **Trusted Publishing** - `NuGet/login` exchanges the job's
OIDC token for a one-hour key against a policy that names the repository and the workflow file;
the only secret is the nuget.org user name), `overshell-sync` (weekly: rebase the patch series onto
upstream's newest release tag on a `sync/<tag>` branch, open a PR, start CI; conflicts become an
issue). The first two releases: `1.25.260302.1`, unchanged upstream bits from commit `9ae724a`
(the one `CI.Microsoft.Terminal.Wpf 1.25.260303002` was built from), consumed by OverShell with
every self-test green - the mechanism proven before any C++ changed; and `1.25.260302.2` with the
patches below.

**The patch** (fork commits 2 and 3; upstream files touched are listed in the fork's README).
`CreateTerminalEx(parent, TERMINAL_CREATE_COMPOSED)` keeps the child HWND for everything a HWND
gives - focus, keyboard, mouse, text services, UI Automation - but with `WS_EX_NOREDIRECTIONBITMAP`,
so it has no pixels; AtlasEngine renders into a **composition surface** instead (its XAML path),
and the library wraps the surface handle in a DirectComposition visual on the child's **top-level
window** (one device per process, one target per window, `topmost`), kept at the child's position
and size, attached while the child is shown, moved when the child moves or is re-parented
(`TerminalUpdateComposition` for a host that `SetParent`s). `TerminalSetBackgroundOpacity` gives
cells with the default background an alpha - text and coloured cells stay opaque, as in Windows
Terminal - and survives a theme change. The engine gained one switch (`TargetSettings.undoXamlScale`,
default on: without a HWND it assumed a XAML SwapChainPanel and applied an inverse-DPI matrix that
would have shrunk our visual) and one fix (`_createSwapChain` loads `dcomp.dll` itself; under
XAML it was always loaded, under WPF `GetModuleHandle` returned null and the first frame threw, in
silence - every frame, for ever: the trace in the composition path found it in one run). The
managed control exposes `TerminalControl.UseComposition` (read once, at HWND creation) and
`BackgroundOpacity`. Existing callers see no change: `CreateTerminal` is `CreateTerminalEx` with
no flags, and the opaque path is byte-for-byte the old one.

**Spike 8b** (`spikes/dcomp-transparency`, modes `child-noredir-inputonly*`) measured the shape
before the C++ was written: a non-redirected child under a `topmost` target on the top-level window
still hit-tests (`WindowFromPoint` returns it) and the visual blends with the backdrop; with
`topmost = FALSE` the child's region covers the visual. Regions of the child not covered by the
visual show an opaque fill, so the visual must always cover the child rectangle - it does, the
swap chain is the child's size.

**OverShell's side.** `window.terminalOpacity` (1 = opaque, today's look) and `window.backdrop`
(`acrylic` | `mica` | `micaalt` | `none`; `OVERSHELL_BACKDROP` still overrides for a run). Below 1,
with a backdrop in play and transparency effects on, `TerminalFactory.Composition` is decided once,
before the first tab: every surface is created composed; the frame is extended over the **whole**
client area (the bands-only extension of §7.6 existed to hide the HWND terminal's frame flash); the
host paints nothing under the terminal and the views' margin ring carries the tab's background at
the body's opacity; the opacity follows the settings live, the mode needs a restart (a HWND is
created once). Two things the first run taught: (1) **WPF cannot repaint under a child HWND** -
with `WS_CLIPCHILDREN` on, whatever WPF painted under that rectangle before the child covered it
(a background, the margin ring at an earlier size) stays in the window's surface as a ghost under
the translucent terminal; the composed mode switches `WS_CLIPCHILDREN` off on its windows, WPF
repaints the whole client every frame, and the child has nothing to lose to that; (2) **DWM paints
acrylic only for the active window** - an inactive window gets an opaque fallback (Windows
Terminal shows the same), which the self-test has to respect.

**Verified.** `transparency` 12/12 against real screen pixels: a lime window placed behind
OverShell gives the acrylic something known to blur; the terminal's empty area at opacity 0.5 reads
`#0C6106` (half of `#0C0C0C` over acrylic-blurred lime), at 1.0 exactly `#0C0C0C`, at 0.5 again
`#0C6106`; a torn-off tab's terminal is drawn in its own window after a resize (the visual moved
with it) and is translucent again when brought back; a second tab is composed and translucent
while the first is hidden. Every earlier mode with the fork's package in the default, opaque mode:
85/85, `find` 27, `tearoff` 10, `session1`/`session2` 15, `polish` 14, `theme` 12, `inject` 14,
`cwd` 12; 313 unit tests. The fork's CI compiled the C++ and C# at the first attempt.

**Not done.** The sliver inside the control where the character grid does not fill it is painted
by the engine at the body's opacity (right); OverShell's own padding ring is WPF, which is why
`WS_CLIPCHILDREN` had to go. A real Mica body (wallpaper-tinted, no windows behind) works the same
way with `window.backdrop: mica`. The upstream submissions (phase 2) are written — an issue and
a pull request each for the dcomp.dll load and for the composed mode, texts in the fork's
`build/overshell/UPSTREAMING.md`, branches `upstream/atlas-dcomp-load` and
`upstream/wpf-composed-rendering` on upstream `main` — and are opened in the browser by hand, the
fork owner's token being SAML-bound for the microsoft organisation. Signing is configured: one Azure
Artifact Signing account (Basic tier: one Public Trust profile, shared with the owner's other
projects; the certificate's subject is the owner's validated identity either way) and an Entra
app *OverShell release signing* with a federated credential per repository (OverShell and the
fork), assigned *Artifact Signing Certificate Profile Signer* on the profile and nothing else;
the six `AZURE_*` secrets live on each repository's `release` environment. The fork's `pack`
job signs the two control binaries, OverShell's release signs its own and leaves signed files
alone (`build/Release.ps1` catalogues only what is unsigned).

### 12.21 The self-tests on a private desktop

**Problem.** Every self-test run took the desktop away from whoever was using the machine: the
window is shown (and activates), tear-offs are shown, the session restore calls
`SetForegroundWindow`, summon calls `Activate()`, the `transparency` mode brings the window to the
front three times — and a tiling window manager re-arranged each new window. The checks that
depended on the foreground were written as "X, or the foreground left us", so they passed either
way, which is what the random failures in 2 runs out of 5 were: the user's keystrokes landing in
the test instance, or the test's windows in the user's layout.

**Decision.** The runner (`tools/Invoke-SelfTest.ps1`) starts the instance on a private Win32
desktop (`CreateDesktop` + `CreateProcess` with `STARTUPINFO.lpDesktop`), never switched to.
Measured there: `GetForegroundWindow` is `NULL` and `SetForegroundWindow` / `Window.Activate()`
return false (the foreground belongs to the input desktop), yet `Show()` activates the window and
`SetFocus` moves the keyboard focus *within the thread* exactly as on screen — `IsActive`,
`GetFocus`, `GetActiveWindow` tell the truth, the palette takes and returns the focus, the restored
tear-off is the active window. `EnumWindows` and WinEvents are per desktop, so the window manager
never sees the instance; hotkeys cannot clash with the user's; the ConPTY children follow the
desktop. What does not exist there is the screen: nothing is composed, `GetPixel` fails and a
`BitBlt` from the screen DC would show the user's desktop. So `HerdSelfTest` tells the two apart
(`OnInputDesktop`: the thread's desktop name against `OpenInputDesktop`'s) and (1) the foreground
checks became `CanJudgeActivation()` — judged on a private desktop always, on the input desktop
only while the foreground is ours, `SKIP` otherwise, never "pass either way"; (2) the acrylic reads
and screenshots `SKIP` off the input desktop (`tools\Invoke-SelfTest.ps1 -Mode transparency
-Visible` runs them on screen, when the desktop can be spared); (3) a tally line
(`=== selftest tally: pass= fail= skip= desktop=private|input ===`) closes every run, so a skip is
counted, not hidden. The runner waits for the end line, prints the FAIL and SKIP lines, closes the
window with `WM_CLOSE` from a thread attached to that desktop, and exits non-zero on a failure.

**What else moved.** The state some modes start from — an archive for `history` and `jumplist`
(plus the `Jump WS` workspace), two early deaths for `resilience`, an interrupted run with a saved
screen for `ghost`, the 0.5 opacity for `transparency` — used to be seeded by hand before the
launch; it is written by `HerdSelfTest.SeedState()` before the window reads its files, and undone
by the mode (the workspace deleted, the settings put back). Only `env` keeps a launcher-side
precondition by nature (the launcher's PATH stripped of the user's registry entries, a variable of
its own), which the runner provides. The `env` mode's one long line (a PATH of seventy entries)
goes through a file instead of the screen: with no window manager enlarging the window, the line
scrolled off the top. The palette focus round trip starts from an explicit `Surface.Focus()`
rather than the tab switch's dispatched one, and the focus lines name the window class that holds
it.

**Measured.** All 33 modes green on the private desktop with the user working throughout: the
86-check run in 75 s, `transparency` 6 pass / 6 skipped (the pixel reads), `summon` 8 / 1 skipped
(minimize-when-in-front: the feature itself asks Windows for the foreground). Nothing appeared
on the user's desktop (0 windows of the process there) and no keystroke was lost.

---

## 13. Distribution

### 13.1 Channels - and why each artefact is what it is

One tag `vX.Y.Z` produces three ways in, all from the same `build/Release.ps1` run:

| Channel | Artefact | Runtime | Size |
|---|---|---|---|
| **winget** `MoaidHathot.OverShell` (moniker `overshell`) | `OverShell-<v>-win-x64.zip`, framework-dependent, installed as a *portable* with the command alias `overshell` | `Microsoft.DotNet.DesktopRuntime.10`, declared as a package dependency so winget installs it when missing | 1.9 MB zipped, 3.7 MB on disk |
| **.NET tool** `OverShell` | `OverShell.<v>.nupkg`; `dotnet tool install -g OverShell` then `overshell`, or `dnx OverShell` | whatever `dotnet` the user already has (10.x) | 1.9 MB |
| **GitHub Release** | the two above plus `OverShell-<v>-win-x64-selfcontained.zip` and `SHA256SUMS.txt` | the self-contained zip needs nothing | 65 MB zipped, 143 MB on disk |

The framework-dependent build is the default for the two package managers because
they can resolve the runtime themselves and the difference is thirty-five-fold; the
self-contained zip is for machines where a runtime cannot be installed, and it is on
the release page only. Every layout ships the same natives next to `OverShell.exe`:
`Microsoft.Terminal.Control.dll`, `Microsoft.Terminal.Wpf.dll`, `conpty.dll`,
`x64\OpenConsole.exe` - all four already carry Microsoft's Authenticode signature
(`Get-AuthenticodeSignature`: `Valid`, CN=Microsoft Corporation), so the pipeline signs
only ours. Release builds embed their PDBs (`DebugType=embedded`) so the crash log keeps
line numbers without loose `.pdb` files in any artefact.

### 13.2 The tool package - three things the SDK does not do for a WPF app

**Packing.** `Microsoft.NET.PackTool.targets` (SDK 10.0.401) fails with NETSDK1146
(`PackAsToolCannotSupportTargetPlatformIdentifier`) for any `-windows` TFM, and WPF
needs `net10.0-windows`. The tool is Windows-only by nature, so a target running before
`_PackToolValidation` blanks `TargetPlatformIdentifier`/`TargetPlatformMoniker`; the
package then comes out as `tools/net10.0/win-x64/` - exactly where `dotnet tool install`
looks - with `DotnetToolSettings.xml` saying `Runner="executable"
EntryPoint="OverShell.exe"`, because a RID-specific tool with an apphost is launched
through the apphost, not `dotnet OverShell.dll`. Same idea as Palantir's
`SuppressPackAsToolPlatformCheck`, minus its post-pack rename (not needed here).

**Returning.** For an executable runner `dotnet tool install` writes a **batch shim**
(`overshell.cmd` → `%~dp0.store\overshell\<v>\...\OverShell.exe %*`), and a batch file
waits for its child; `dnx` waits too. Right for `overshell version`, wrong for the
window: the user's terminal would be held until OverShell closed. So `ToolLaunch`
recognises a tool-store image path (`\.store\overshell\` or, under `dnx`,
`\.nuget\packages\overshell\`), starts a twin of itself with `--detached` appended and
exits; the twin strips the marker. `--no-detach` opts out. The twin is started through
**ShellExecute**, deliberately: a plain `CreateProcess` twin inherits the wrapper's
stdout pipe, and anything capturing that output (`overshell | Out-Null`, a script) waits
until the window closes - measured at 190 s for `dnx OverShell` before the cause was
understood; 1 s after.

**Testing locally.** The reference machine's global NuGet configuration uses package
source mapping, which makes `dotnet tool install --add-source` fail; a throwaway
`NuGet.config` with `<clear />` and the local folder as the only source, passed with
`--configfile`, is the way to install a freshly packed tool. nuget.org itself is
TLS-blocked from that machine, so publishing happens only from GitHub Actions.

### 13.3 winget portable - the alias is a symlink, and that is fine

winget extracts the zip under `%LOCALAPPDATA%\Microsoft\WinGet\Packages\<id>_<source>\`
and creates `%LOCALAPPDATA%\Microsoft\WinGet\Links\overshell.exe` as a **symbolic link**
to the real `OverShell.exe` (that `Links` directory is on the user's PATH). Two questions
had to be answered before choosing a framework-dependent portable:

- *Does the .NET apphost find `OverShell.dll` when it is started through a symlink?*
  Yes on .NET 10: `pal::realpath` in `src/native/corehost/hostmisc/pal.windows.cpp`
  opens the path with `CreateFileW` and asks `GetFinalPathNameByHandleW` - the source
  comment reads "Use CreateFileW + GetFinalPathNameByHandleW to resolve symlinks" - so
  the host looks for the assembly next to the *target*, not next to the link. (It could
  not be exercised on the reference machine: creating a symlink needs Developer Mode or
  elevation there, and neither was on.)
- *Can the alias be both `overshell` and `OverShell`?* One alias covers both: the link is
  a file on a case-insensitive file system, so `OverShell` and `overshell` typed at a
  prompt resolve to the same `overshell.exe`. The manifest declares the lower-case one,
  matching the tool command and the moniker.

`Environment.ProcessPath` is the *link* path in that case, which is what the
`overshell://` registration and the toast AUMID record - harmless, it resolves through
the link again. Both registrations are rewritten at every start (§12.11, §12.12), which
matters more for the tool store, where the path changes with every version
(`.store\overshell\<v>\...`); the winget path is stable across upgrades.

### 13.4 The pipeline

`build/Release.ps1 -Version <v> -Phase build|package|all` is the whole build, usable
locally without any account:

1. **build** - `dotnet publish` framework-dependent and self-contained (`-r win-x64`,
   `-p:Version=<v>`), `dotnet pack` the tool, unpack the `.nupkg`, and write
   `artifacts/release/stage/signing-catalog.txt`: the relative paths of every
   `OverShell*.exe`/`OverShell*.dll` in the three layouts - twelve files, nothing else.
2. *(CI only)* `azure/login@v3` with OIDC, then `azure/artifact-signing-action@v2` with
   `files-catalog` pointing at that file - so exactly our binaries are signed and the
   Microsoft ones keep their signature - `SHA256`, RFC 3161 timestamp from
   `timestamp.acs.microsoft.com`.
3. **package** - re-zip the tool package from the staged (signed) tree, zip both publish
   folders, `SHA256SUMS.txt`, and render the winget manifests from `winget/templates`
   (version, release date, the zip's URL on the release and its SHA-256). With
   `-RequireSigned` it refuses to package if any catalogued file is not validly signed,
   so a silent signing failure cannot ship unsigned bits under a signed-looking release.

`.github/workflows/release.yml` runs on a `v*` tag in the `release` environment - which
makes the OIDC subject `repo:MoaidHathot@<owner id>/OverShell@<repo id>:environment:release`
(GitHub's token carries the numeric ids), one federated credential in Entra instead of one per
tag - and then: GitHub Release (both zips, the
`.nupkg`, the sums; prerelease when the version has a `-`), `dotnet nuget push` with
`NUGET_API_KEY`, and `wingetcreate submit` of the rendered manifest folder with
`WINGET_CREATE_GITHUB_TOKEN` (the documented CI form; `submit` also works for the first
version, where `update` has nothing to update). Every channel is optional: a missing
secret skips its step, so the workflow is usable before the accounts exist, and a manual
run builds the same artefacts without publishing. Prereleases never go to winget. The
tag is the version; a `Directory.Build.props` `<Version>` that disagrees produces a
warning, not a failure, since `-p:Version` wins anyway. `ci.yml` builds with
`-warnaserror`, runs the tests and dry-runs the same script on every push, so the
packaging cannot rot between tags.

Secrets the workflow reads: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID` (Entra app registration with a federated credential for the
`release` environment, plus the *Artifact Signing Certificate Profile Signer* role on the
account), `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE`,
`NUGET_API_KEY`, `WINGET_TOKEN`.

### 13.5 Verified

- `dotnet pack` → `OverShell.0.1.0.nupkg`, 1.86 MB, package type `DotnetTool`, content
  under `tools/net10.0/win-x64/`; the **repacked** package (the unzip/re-zip cycle
  signing needs) has no backslash entry names and no PDBs, installs with
  `dotnet tool install --configfile`, and `overshell.cmd version` prints
  `OverShell 0.1.0+<sha>`.
- Through the shim, the window returns control in 0.3-0.7 s even with stdout captured;
  `dnx OverShell@0.1.0` returns in 1 s; the window stays; `overshell.cmd
  overshell://view/herd` from a second prompt is handed to the running window in 273 ms
  and the view changes; still one process; no crash log; no leftovers.
- Self-contained zip: 405 files, runs with `DOTNET_ROOT` pointed at an empty directory
  (no shared runtime reachable): `version` exits 0, the window comes up, the endpoint
  listens, `overshell://` re-registers to the new path, closes cleanly.
- `winget validate` on the rendered manifests: "Manifest validation succeeded" (the
  runtime dependency is reported as not validated locally, by design).
- `Release.ps1 -Phase all` end to end on the reference machine.

Not yet exercised: a signed run (needs the secrets), the first winget-pkgs review (a new
package is checked by a person), and `winget install` from the published manifest -
its URL exists only after the first tag.