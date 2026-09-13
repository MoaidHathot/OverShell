# OverShell

*Overseer Shell* - a Windows terminal shell that wraps the real Windows Terminal
rendering engine in your own chrome, tabs and layout - and watches the AI coding agents
running in its tabs so you know which one needs you.

The terminal emulation is Microsoft's, unmodified: VT/ANSI parsing, the text buffer, the
GPU-accelerated DirectWrite renderer, selection and scrollback all come from
`Microsoft.Terminal.Control.dll`. Everything around it - window chrome, tab strip,
layout, keybindings, agent detection, notifications - is ours.

It reads your existing **Windows Terminal `settings.json`**: profiles, colour schemes and
fonts are shared, and there is no second configuration to maintain. OverShell never
writes to it.

## Build & run

```powershell
dotnet build OverShell.slnx -c Debug -p:Platform=x64
.\artifacts\bin\OverShell.App\debug_win-x64\OverShell.exe
dotnet test OverShell.slnx -c Debug -p:Platform=x64
```

Requires the .NET 10 SDK and Windows 10 19041+ (Windows 11 22621+ for the system
backdrop). x64 only.

| Environment variable | Values | Purpose |
|---|---|---|
| `OVERSHELL_BACKDROP` | `acrylic` (default), `mica`, `micaalt`, `none` | Backdrop behind the chrome |
| `OVERSHELL_CONFIG_DIR` | a directory | Configuration root instead of `%APPDATA%\OverShell` |
| `OVERSHELL_TRACE_KEYS` | `1` | Trace keyboard chords to `%TEMP%\overshell-keys.log` |
| `OVERSHELL_TRACE_LINKS` | `1` | Trace link hover/click resolution to `%TEMP%\overshell-links.log` |
| `OVERSHELL_TRACE_AGENTS` | `1` | Trace agent detection, state changes and their evidence, endpoint traffic and notifications to `%TEMP%\overshell-agents.log` |
| `OVERSHELL_SELFTEST` | `1`, `opencode` | Run the in-process end-to-end self-test (`%TEMP%\overshell-selftest.log`); `opencode` runs the real `opencode run` with the plugin installed |

Crashes are always logged to `%TEMP%\overshell-crash.log`.

To exercise the link handling and shortcuts by hand, run `tools\Show-LinkTestCard.ps1`
inside a tab: it prints every case with what should happen.

## Agents

Every tab is watched for the harness running in it - OpenCode, GitHub Copilot CLI,
Claude Code, Codex, or any other - and shows what it is doing: **working**, **needs you**
(a permission or question prompt - never inferred from silence), **done** (finished
while you were not looking; stays until you look), **error**, **exited**. Detection uses
what the program already emits (title, OSC 9;4 progress, notifications, bell, screen
text, the process tree) plus, when installed, the harness's own hooks:

```powershell
OverShell integrations install opencode   # writes a plugin into OpenCode's plugins folder
OverShell integrations install copilot    # writes hooks into ~/.copilot/hooks
OverShell integrations status
```

Both files are separate from your own configuration, do nothing outside OverShell, and
are safe to delete. Inside a tab, `OVERSHELL_ENDPOINT`, `OVERSHELL_TOKEN` and
`OVERSHELL_TAB_ID` let any script report state with one `curl`:

```powershell
curl.exe -X POST "$env:OVERSHELL_ENDPOINT/v1/report?token=$env:OVERSHELL_TOKEN" `
  -H "Content-Type: application/json" `
  -d "{`"tab`":`"$env:OVERSHELL_TAB_ID`",`"source`":`"me`",`"state`":`"blocked`",`"message`":`"needs a decision`"}"
```

Rules per harness live in `%APPDATA%\OverShell\agents\*.jsonc` (bundled defaults inside
the app; a file with the same name replaces one wholesale).

## Views & layouts

Four views, one key each (`Ctrl+Shift+1..4`, `` Ctrl+Shift+` `` toggles the last two), and a
switch in the caption bar with a badge for tabs that need you:

| View | What it is |
|---|---|
| **Terminal** | The live tab; tab strip in the caption bar |
| **Herd** | The live tab plus a sidebar: every tab grouped by project, the ones needing you first |
| **Dashboard** | One card per tab — header, the last screen rows in the tab's colours, state and activity — no live terminal is shrunk into a tile |
| **Zen** | Just the terminal |

A view is a **layout** plus what the middle shows. Layouts are small JSONC files: where
the tabs go (`top`, `bottom`, `left`, `right`, `hidden`), how they look (`strip`, `list`,
`rail`), whether the sidebar and status bar show. Eight presets ship (`top`, `bottom`,
`left-rail`, `left-list`, `right-list`, `zen`, `herd`, `dashboard`); drop a same-named file
into `%APPDATA%\OverShell\layouts\` to change one, or pick any preset for the current view
from the palette (`Layout: …`). Retune the views in `settings.jsonc`:

```jsonc
{ "views": { "terminal": { "layout": "left-list" } } }
```

Every configuration file — `settings.jsonc`, `keybindings.jsonc`, `agents\*.jsonc`,
`layouts\*.jsonc` — reloads live when saved. Tabs in a git repository show their branch
(from `.git/HEAD`, no process spawned); `"git": { "dirty": true }` adds a `*` from
`git status`.

## Keys & palette

`Ctrl+Shift+P` commands · `Ctrl+Shift+Space` switch tab (fuzzy; `@blocked`, `#repo`; with a
screen preview) · `Ctrl+Shift+J` jump to the tab that needs you · `Ctrl+Shift+R` rename ·
`Ctrl+Shift+1..4` views · `Ctrl+T` / `Ctrl+Shift+W` / `Ctrl+Tab` / `Alt+1..9` tabs ·
`Ctrl+Shift+C` / `Ctrl+Shift+V` clipboard (`Ctrl+C` copies only with a selection).
Right-click a tab, a sidebar row or a card for rename / treat as agent or shell / explain /
close.

Everything is a command bound in `%APPDATA%\OverShell\keybindings.jsonc` (Windows
Terminal's shape; `"command": "unbound"` removes a default).

## Notifications

`%APPDATA%\OverShell\settings.jsonc` names the sinks: an in-window toast for background
tabs, a taskbar badge with the number of tabs needing you (amber while one is blocked),
a system sound while the window is unfocused, and any command - the shipped recipe
sends Windows toasts through [Palantir](https://github.com/MoaidHathot/Palantir):

```jsonc
{ "notifications": { "sinks": { "palantir": { "enabled": true } } } }
```

## Status

Working: single terminal on launch, tabs with live titles, profile menu, theming from
your colour schemes, custom chrome with a Windows 11 backdrop, links (hover underlines
one in its own colour where the renderer would, Ctrl+click opens it), bracketed paste,
the agent layer (detection, two-line tabs with state, palette, endpoint, OpenCode plugin
and Copilot hooks, notifications), and the views above (layouts, Herd sidebar, Dashboard
cards, Zen, live configuration reload, git branch per tab).

In progress: prompt bar and broadcast to agents, tab groups, drag-and-drop, session
persistence and harness resume, `overshell://`, XAML skins.

Not possible on the default terminal surface: transparency of the terminal body - see
[DESIGN.md 7.6](DESIGN.md#76-transparency-is-structurally-impossible-here) for why, and
11 for the surface abstraction that would allow a second, translucent one.

## Documentation

**[DESIGN.md](DESIGN.md)** - what it is, why it is built this way, the architecture
decision behind embedding Windows Terminal, the configuration pipeline, field notes on
every non-obvious trap encountered, the herd-overseer plan (12) and the open/closed
item list.

## Licence

MIT - see [LICENSE](LICENSE).
