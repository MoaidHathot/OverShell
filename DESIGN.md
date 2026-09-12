# OverShell — Design & Development Notes

> **OverShell** = *Overseer Shell*. A Windows terminal **shell** (chrome, tabs, layout)
> wrapped around the real Windows Terminal rendering engine.

Last updated: 2026-08-26

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

### Non-goals

- Replacing Windows Terminal. OverShell reads WT's config; it never writes to it.
- Being a profile editor. Profiles are edited in Windows Terminal's own settings UI.
- Writing a terminal emulator. We host one.

---

## 2. Why this shape — the core decision

Windows Terminal **cannot be embedded**. There is no supported API.
[microsoft/terminal#6999](https://github.com/microsoft/terminal/issues/6999)
("Productize the WPF, UWP Terminal Controls") has been open since 2020 and sits in the
icebox: *"we don't expect the core team to ever have the resources to get around to
this."*

Three viable approaches were evaluated in depth.

### Option A — embed the real Windows Terminal process

Devolutions ships this commercially in Remote Desktop Manager via
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

### Option B — host the WPF control in-process ← **chosen**

[`EasyWindowsTerminalControl`](https://github.com/mitchcapper/EasyWindowsTerminalControl)
(MIT, actively maintained) wraps `CI.Microsoft.Terminal.Wpf`, which P/Invokes the genuine
`Microsoft.Terminal.Control.dll`.

- ✅ In-process: keyboard focus, drag-and-drop and layout are ordinary WPF
- ✅ Full ANSI stream interception, both directions
- ✅ Live sessions can be detached from one control and re-attached to another
- ❌ **No transparency of any kind** in the terminal body (see §7.6)
- ❌ No built-in search, hyperlinks, shell-integration marks, command palette
- ❌ Dynamic profiles must be re-implemented (see §6)

### Option C — write our own emulator

Rejected. The whole point is Windows Terminal fidelity.

### The decision

**Option B.** Layout control and configuration reuse were the primary goals, and B keeps
those in one process where they are simple. Transparency in the terminal body is the
price, and it is a real one — see §7.6 for exactly why it cannot be recovered without
switching to A.

Option A remains a documented escape hatch. `TerminalTab` is deliberately the only type
that touches the session, so a second implementation could slot in without disturbing the
layout engine.

---

## 3. Architecture

```
OverShell.slnx
├── Directory.Build.props          net10.0-windows · x64 · win-x64 RID · artifacts output
├── Directory.Packages.props       central package management
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
        ├── MainWindow              chrome, tab strip, status bar, tab lifecycle
        ├── TerminalTab             one profile + one PTY + one control
        ├── ShortcutRouter          pre-dispatch chord interception (see §7.2)
        ├── WindowChromeInterop     DWM: dark mode, rounded corners, system backdrop
        ├── TerminalThemeMapper     ColorScheme → TerminalTheme (COLORREF)
        ├── TabAccent               deterministic per-profile accent colour (FNV-1a)
        └── Theme/                  Palette.xaml, Controls.xaml
```

### Dependency graph

```
EasyWindowsTerminalControl 1.0.38
├── CI.Microsoft.Terminal.Wpf 1.25.260303002
│   ├── Microsoft.Terminal.Wpf.dll          (managed wrapper)
│   └── Microsoft.Terminal.Control.dll      (native — the real WT control, 1.6 MB)
└── Microsoft.Windows.Console.ConPTY 1.24.260710001
    └── conpty.dll
```

### Process model

Identical to Windows Terminal's:

```
OverShell.exe      ~156 MB    1 UI process
├── conhost.exe      ~9 MB    ┐ one pair per tab
├── pwsh.exe       ~114 MB    ┘
└── …
```

Measured Windows Terminal for comparison: `WindowsTerminal.exe` 185 MB + 6 ×
`OpenConsole.exe` + 7 × `pwsh.exe`. **Opening a tab does not spawn a new UI process** in
either.

### One control per tab — and why

Each tab owns its own `EasyTerminalControl`. Inactive tabs are `Visibility.Hidden`
(not `Collapsed`, which would resize the grid to zero and reflow the shell's output).

This matters because **the scrollback buffer lives in the control, not in the PTY**.
Sharing one control and swapping connections would discard history on every tab switch.

---

## 4. Build & run

```powershell
dotnet build OverShell.slnx -c Debug -p:Platform=x64
.\artifacts\bin\OverShell.App\debug_win-x64\OverShell.exe
```

Requires .NET 10 SDK, Windows 10 19041+ (Windows 11 22621+ for the system backdrop).
**x64 only** — `Microsoft.Terminal.Control.dll` is not published for AnyCPU and the
wrapper pins x64.

### Environment variables

| Variable | Values | Purpose |
|---|---|---|
| `OVERSHELL_BACKDROP` | `acrylic` (default), `mica`, `micaalt`, `none` | System backdrop behind the chrome |
| `OVERSHELL_TRACE_KEYS` | `1` | Log every chord the router sees to `%TEMP%\overshell-keys.log` |

### Diagnostics

- `%TEMP%\overshell-crash.log` — every unhandled exception, always written
- `%TEMP%\overshell-keys.log` — key trace, only when `OVERSHELL_TRACE_KEYS=1`

### Build layout

`UseArtifactsOutput` puts everything under `artifacts/` (already git-ignored).
`Directory.Build.props` strips the native PDBs, which `CI.Microsoft.Terminal.Wpf` ships
at ~82 MB **per architecture** — build output went from **246 MB → 2 MB**. Set
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
- `OverShell.Config` must stay free of any WPF reference.
- Anything touching a PTY assumes the session may already be dead (see §7.3).

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

### 7.3 `TermPTY` throws on writes after teardown

```csharp
void ITerminalConnection.WriteInput(string data) {
    if (span.Length > 0 && !_ReadOnly)     // ← the only guard
        WriteToTerm(span);
}
public void WriteToTerm(ReadOnlySpan<char> input) {
    if (_consoleInputWriter == null && _consoleInputWriterB == null)
        throw new InvalidOperationException("There is no writer attached to a pseudoconsole…");
```

`CloseStdinToApp()` nulls those writers, but the control keeps its `Connection` reference
and keeps delivering focus and key messages. The next one throws.

Rules that follow:

- Call `SetReadOnly(true, updateCursor: false)` **before** closing pipes.
  `updateCursor: false` matters — hiding the cursor emits VT back through the very path
  being shut down.
- Dispose a tab **before** removing it from the visual tree: unloading the `HwndHost`
  generates focus traffic.
- On shutdown, suppress input on *all* tabs first, then dispose them.
- `SendText` (paste) uses raw `WriteToTerm`, which **does not** consult `_ReadOnly` —
  it needs its own guard.

### 7.4 `IsRunning` has a startup race

The PTY starts on a background thread, so `Process` is null for the first moments of every
tab. A naive `Process?.HasExited == false` is therefore **false on every healthy new tab**.
Split into `HasStarted` (`TermProcIsStarted`) and `IsRunning`; exit detection requires
`HasStarted && !IsRunning`.

### 7.5 Window chrome: two traps

**`GlassFrameThickness="-1"` is a trap.** It hands the non-client frame to DWM, which then
(a) draws **its own caption buttons** over the custom ones, and (b) makes the whole client
area glass, so every repaint gap flashes through to the desktop. Keep it at `0` and call
`DwmExtendFrameIntoClientArea` manually with margins covering **only the chrome bands**.
Those margins are physical pixels — recompute them on `DpiChanged`.

**An unpresented swapchain composites as transparent.** A freshly created terminal owns a
child HWND whose swapchain has not presented a frame yet, so revealing it immediately
flashes the desktop through. Reveal only after `TermReady` plus one turn at
`DispatcherPriority.Render`, with a timeout fallback so a bad commandline cannot leave a
permanently blank pane.

### 7.6 Transparency is structurally impossible here

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

**What we do instead:** a Windows 11 system backdrop behind the title bar and status bar
only, with the terminal body opaque. This matches `useAcrylicInTabRow: true` but *not*
per-profile `useAcrylic` / `opacity`, which stay unreproducible.

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

### Fixed but not yet confirmed by a human

| Item | Fix |
|---|---|
| Tab stolen from the shell | Claim + `SendMessageW` forward (§7.2c) |
| Arrow keys | Same mechanism, fixed pre-emptively |
| Transparent flash on new tab | Reveal after `TermReady` + render turn (§7.5) |
| Doubled caption icons | `GlassFrameThickness` back to `0` (§7.5) |
| `InvalidOperationException` on tab/app close | Read-only before teardown (§7.3) |

### Open — near term

- [ ] **Verify shortcuts on real hardware.** `Ctrl+T`, `Ctrl+Shift+W`, `Ctrl+Tab`,
      `Alt+1..9`, `Ctrl+C`/`Ctrl+V`, `Ctrl+Shift+C`/`V`, right-click copy-or-paste.
      None have been confirmed by a human yet.
- [ ] **Watch for double-Tab.** If one press yields two tabs, the terminal is receiving
      both the forwarded `WM_KEYDOWN` and a `WM_CHAR`; narrow the forward.
- [ ] Live settings reload — `FileSystemWatcher` on `settings.json` (designed, not built).
- [ ] `closeOnExit` semantics. Today a dead tab stays open, dimmed and italic.
- [ ] Commit. Nothing is committed yet beyond the initial commit.

### Open — the actual feature work

- [ ] **Configurable tab strip placement** — top / left / bottom / right at runtime.
- [ ] **Multiple independent tab groups** with splitters.
- [ ] **Drag-and-drop tab reorder and move between groups.** Needs a separate layered
      top-level window for the drag preview — WPF cannot render over the terminal (§9).
      Note the scrollback caveat in §7.1.
- [ ] Layout persistence to `%APPDATA%\OverShell\layout.json`.
- [ ] Split panes (our own splitter tree, independent of WT's panes).

### Open — later

- [ ] Search. WT's search box is UWP-only; would have to be built over the buffer.
- [ ] Keybindings driven from `settings.json` `actions` / `keybindings`.
- [ ] Profile icons in the tab strip and new-tab menu (paths are already parsed).
- [ ] Azure Cloud Shell generator.
- [ ] Unit tests for `OverShell.Config` — the one genuinely testable layer.

---

## 9. Known limitations

| Limitation | Cause | Recoverable? |
|---|---|---|
| No terminal-body transparency | HWND swapchain cannot alpha-blend (§7.6) | Only via Option A |
| No acrylic / background image / retro effect | Same | Only via Option A |
| Scrollback lost when moving a session | ConPTY repaints viewport only (§7.1) | No |
| WPF cannot draw over the terminal | Airspace — same as WebView2 | Use separate layered windows |
| Chrome text is grayscale-antialiased | Transparent composition target disables ClearType | `OVERSHELL_BACKDROP=none` |
| No search / hyperlinks / shell-integration marks | Not in the Hwnd C API | Build our own |
| x64 only | Native control not published AnyCPU | No |
| `CI.Microsoft.Terminal.Wpf` is an unsigned CI-feed package | [microsoft/terminal#15404](https://github.com/microsoft/terminal/issues/15404) | Vendor it if it disappears |

---

## 10. Useful references

- [microsoft/terminal#6999](https://github.com/microsoft/terminal/issues/6999) — embedding, iceboxed
- [microsoft/terminal#20488](https://github.com/microsoft/terminal/pull/20488) — community PR for a UWP control NuGet
- [microsoft/terminal#15061](https://github.com/microsoft/terminal/issues/15061) — WPF control crashes on a null `Connection`
- [Devolutions/wt-distro](https://github.com/Devolutions/wt-distro) — the Option A patches
- [EasyWindowsTerminalControl](https://github.com/mitchcapper/EasyWindowsTerminalControl) — what we build on
- `src/cascadia/TerminalControl/HwndTerminal.cpp` — the native side we talk to
- `src/cascadia/WpfTerminalControl/` — the managed wrapper
- `src/cascadia/TerminalSettingsModel/defaults.json` — source of the embedded schemes

### Handy

Set a tab title from inside PowerShell (emits OSC 2 through ConPTY, which OverShell
parses into the tab label):

```powershell
$Host.UI.RawUI.WindowTitle = 'OverShell'
```
