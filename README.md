# OverShell

*Overseer Shell* - a Windows terminal shell that wraps the real Windows Terminal
rendering engine in your own chrome, tabs and layout - and watches the AI coding agents
running in its tabs so you know which one needs you.

The terminal emulation is Microsoft's: VT/ANSI parsing, the text buffer, the GPU-accelerated
DirectWrite renderer, selection and scrollback all come from `Microsoft.Terminal.Control.dll`,
built from Windows Terminal's source by [our fork](https://github.com/MoaidHathot/terminal) and
shipped as the `OverShell.Terminal.Wpf` package - upstream plus a short, documented patch series
(a rendering mode that lets the terminal body be translucent). Everything around it - window
chrome, tab strip, layout, keybindings, agent detection, notifications - is ours.

It reads your existing **Windows Terminal `settings.json`**: profiles, colour schemes and
fonts are shared, and there is no second configuration to maintain. OverShell never
writes to it.

## Install

Windows 10 19041+ (Windows 11 22621+ for the system backdrop), x64 only.

| Channel | Command |
|---|---|
| winget | `winget install MoaidHathot.OverShell` - installs the .NET 10 Desktop Runtime if needed, puts `overshell` on your PATH. Pending: the first winget-pkgs submission has not been made yet |
| .NET tool | `dotnet tool install -g OverShell` then `overshell` - or run it without installing: `dnx OverShell` |
| Zip | From the [releases](https://github.com/MoaidHathot/OverShell/releases): `OverShell-<version>-win-x64.zip` (needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)) or `OverShell-<version>-win-x64-selfcontained.zip` (needs nothing). Unzip anywhere, run `OverShell.exe` |

Every release ships `SHA256SUMS.txt`. Release assets are built by the
[release workflow](.github/workflows/release.yml), which Authenticode-signs the OverShell
binaries with Azure Artifact Signing; the Windows Terminal control binaries arrive signed the
same way from [our fork](https://github.com/MoaidHathot/terminal), and the ConPTY binaries carry
Microsoft's signature. `overshell version` prints the version and where it runs from.

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
| `OVERSHELL_CONFIG_DIR` | a directory | Configuration root; see *Configuration* below |
| `OVERSHELL_STATE_DIR` | a directory | State root (labels, the session, `sessions\` history) |
| `OVERSHELL_TRACE_KEYS` | `1` | Trace keyboard chords to `%TEMP%\overshell-keys.log` |
| `OVERSHELL_TRACE_LINKS` | `1` | Trace link hover/click resolution to `%TEMP%\overshell-links.log` |
| `OVERSHELL_TRACE_AGENTS` | `1` | Trace agent detection, state changes and their evidence, endpoint traffic and notifications to `%TEMP%\overshell-agents.log` |
| `OVERSHELL_SELFTEST` | `1`, `opencode`, `opencode-resume`, `session1`/`session2`, `sessionend`, `history`, `icons`, `polish`, `cwd`, `resilience`, `ghost`, `workspaces`, `overflow`, `jumplist`, `theme`, `tearoff`, `find`, `inject`, `env`, `herdmode`, `mru`, `summon`, `address`, `keynav`, `inbox`, `triage`, `opencode-reply`, `spawn`, `spawn-opencode`, `mcp`, `herdlog`, `changes`, `transparency` | Run the in-process end-to-end self-test (`%TEMP%\overshell-selftest.log`); `opencode` runs the real `opencode run` with the plugin installed; `session1` then `session2` check restore across a restart; the rest one feature each. `tools\Invoke-SelfTest.ps1 -Mode <mode>` runs one on a private desktop, so it never takes your focus (`-Visible` for the pixel checks) |
| `OVERSHELL_WT_SETTINGS` | a file | Read this Windows Terminal `settings.json` instead of the installed one |

Crashes are always logged to `%TEMP%\overshell-crash.log`.

To exercise the link handling and shortcuts by hand, run `tools\Show-LinkTestCard.ps1`
inside a tab: it prints every case with what should happen.

## Configuration

Configuration lives in the first of `OVERSHELL_CONFIG_DIR`, `$XDG_CONFIG_HOME\overshell`,
`%APPDATA%\OverShell` - so a dotfiles directory that other tools already share through
`XDG_CONFIG_HOME` picks up OverShell too. Machine-local state (labels, the last
session) goes to the first of `OVERSHELL_STATE_DIR`, `$XDG_STATE_HOME\overshell`,
`%LOCALAPPDATA%\OverShell`.

```powershell
OverShell settings path    # which directories, chosen by which variable, and every file
OverShell settings init    # a fully commented settings.jsonc + keybindings.jsonc to start from
```

All files are JSONC and reload live when saved: `settings.jsonc`, `keybindings.jsonc`,
`snippets.jsonc`, `agents\*.jsonc`, `layouts\*.jsonc`, `skins\*.xaml`. Below,
*the configuration directory* means that root.

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
OverShell integrations install claude     # merges hook entries into ~/.claude/settings.json
OverShell integrations install codex      # a notify script + one line in ~/.codex/config.toml
OverShell integrations install shell      # only for profiles that run their own command: a prompt hook in the PowerShell profiles
OverShell integrations status
```

The OpenCode and Copilot files are separate from your own configuration; Claude Code
and Codex keep hooks in their own config files, so OverShell's entries are merged in under
a marker and taken out again by `uninstall` — never replacing anything of yours (a Claude
`settings.json` with comments, or a Codex `notify` you wrote, is left alone and
`integrations show <claude|codex>` prints the entries to add by hand). All of them do
nothing outside OverShell. Inside a tab, `OVERSHELL_ENDPOINT`,
`OVERSHELL_TOKEN` and `OVERSHELL_TAB_ID` let any script report state with one `curl`:

```powershell
curl.exe -X POST "$env:OVERSHELL_ENDPOINT/v1/report?token=$env:OVERSHELL_TOKEN" `
  -H "Content-Type: application/json" `
  -d "{`"tab`":`"$env:OVERSHELL_TAB_ID`",`"source`":`"me`",`"state`":`"blocked`",`"message`":`"needs a decision`"}"
```

Rules per harness live in `agents\*.jsonc` in the configuration directory (bundled defaults inside
the app; a file with the same name replaces one wholesale).

**Spawning and orchestration.** `agent.new` (herd mode `N`) opens a tab that starts a harness
in a directory with a **first prompt**, delivered once the agent shows its composer;
`agent.newWorktree` (`W`) does it in a fresh `git worktree` beside the repository, so several
agents share one repository without touching each other's files. The same is available to
programs: a **control API** on the loopback endpoint (`GET /v1/tabs`, `.../screen`,
`.../wait`, `POST /v1/tabs` to spawn, `.../input`, `.../reply`, `DELETE`) and **`OverShell
mcp`**, an MCP server over stdio with seven tools (`overshell_list_tabs`, `read_screen`,
`send`, `reply`, `spawn`, `wait`, `close`) - so an agent can oversee the herd itself.
`"endpoint": { "control": false }` turns both off.

**Coming back.** The **herd log** (herd mode `L`) lists everything that happened this run - every
state change with its reason, every call for attention - newest first, and keeps it as JSON lines
under the state folder's `logs\`. When an agent's turn ends in a tab you are not looking at, the tab
says *2 changed*: `tab.changes` (`D`) lists the files it changed in its repository since you last
looked, with their status; Enter opens one.

## Prompt bar, sessions, groups

`Ctrl+Shift+Enter` opens a prompt bar under the terminal: type once and send to the
active tab, to every agent, to the agents that need you, or to every tab (`Ctrl+Shift+B`
opens it aimed at all agents). Snippets in `snippets.jsonc` —
`[ { "name": "Explain", "text": "Explain what you just did." } ]` — appear on the bar and
as `Snippet: …` commands.

The session - tabs (profile, directory, label, group), view, layout, where every window
sits - is saved every two seconds, so a crash or a power cut loses seconds, not the
session: the next start brings everything back, says so when the last run was
interrupted, and resumes each agent that was running - by session id when the
integration reported one (`opencode --session <id>`, `copilot --resume=<id>`), else
with the tool's "most recent session" form (`opencode --continue`); a profile whose
program is the agent is relaunched with those arguments, a shell gets them typed.
`Ctrl+Shift+Z` reopens the tab you just closed, agent included; `session.history` lists
recently closed tabs and the last ten sessions to bring back - so does the taskbar jump
list. After a crash each restored tab shows the last screen it had, dimmed, above its new
prompt; two crashes in a minute hold the restore instead of looping; closing with agents
working asks first. Workspaces (`workspaces\<name>.jsonc`, `workspace.save`) are named
sets of tabs to open together. `OverShell --fresh` starts without restoring. `Ctrl+Shift+G` puts a tab in a named group (headers in the strip and the list);
drag a tab along the strip to reorder it, or use `Alt+Shift+←/→`. `Ctrl+Shift+E` opens
the explain panel — why a tab is in its state, with the transition history.
`Ctrl+Shift+X` detaches a tab into a window of its own with the same chrome (the terminal
moves; nothing restarts) and `Ctrl+Shift+A`, or that window's close button, brings it back.
`Ctrl+Shift+F` finds in the tab, scrollback included: Enter walks up through older text,
Shift+Enter down; the match is the terminal's selection, the others on screen are tinted.

`overshell://focus/<tab>` is registered for your user at start; a clicked toast (the
Palantir recipe sets `--launch`) opens the running window on that tab. A second
`OverShell.exe` hands its arguments to the first and exits — one window per user.
`settings.theme` is `system` (follows Windows, live), `dark` or `light`, `settings.accent`
the Windows accent, the palette's own or any colour; `settings.skin` names a
`skins\<name>.xaml` ResourceDictionary that overrides theme brushes, fonts and metrics;
colours update live on save.

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
into `layouts\` to change one, or pick any preset for the current view
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
`Ctrl+Shift+1..4` views · `Ctrl+Shift+Enter` prompt bar · `Ctrl+Shift+G` group · `Ctrl+Shift+E` explain ·
`Ctrl+Shift+F` find · `Ctrl+Shift+T` / `Ctrl+Shift+W` / `Ctrl+Shift+D` / `Alt+1..9` tabs (Windows
Terminal's chords; `Ctrl+T` stays with the shell) · `Ctrl+Tab` most-recent switcher (hold, tap,
release) · `Ctrl+Shift+K` then a key: **herd mode** (`j`/`k` tabs, `b` the tab waiting longest,
`1-9`, `l` last, `s`/`c` a cursor into the sidebar / cards, `i` the inbox, `m`/`w` mute / watch, `N`/`W` spawn an agent / on a worktree, `L` the herd log, `D` what changed while away, `?` the keys) · `` Win+` `` summon from
anywhere · `@3 ...` / `#group ...` / `@blocked ...` in the prompt bar address it · `Alt+Shift+←/→` reorder ·
`Ctrl+Shift+C` / `Ctrl+Shift+V` clipboard (`Ctrl+C` copies only with a selection).
Right-click a tab, a sidebar row or a card for rename / treat as agent or shell / explain /
close.

Everything is a command bound in `keybindings.jsonc` (Windows
Terminal's shape; `"command": "unbound"` removes a default).

## Notifications

`settings.jsonc` names the sinks: an in-window toast for background
tabs, a taskbar badge with the number of tabs needing you (amber while one is blocked),
a system sound while the window is unfocused, a native Windows toast (`toast`, no
external program; a click opens the tab), and any command - the shipped recipe sends
richer toasts through [Palantir](https://github.com/MoaidHathot/Palantir):

```jsonc
{ "notifications": { "sinks": { "palantir": { "enabled": true } } } }
```

## Status

Working: single terminal on launch, tabs with live titles, profile menu, theming from
your colour schemes, custom chrome with a Windows 11 backdrop, links (hover underlines
one in its own colour where the renderer would, Ctrl+click opens it), bracketed paste,
the agent layer (detection, two-line tabs with state, palette, endpoint, OpenCode plugin,
Copilot, Claude Code and Codex hooks, notifications incl. native toasts), the views
(layouts, Herd sidebar, Dashboard cards, Zen, live configuration reload, git branch per
tab), the depth features (prompt bar and broadcast, session restore with agent
resume, `overshell://` toast clicks, tab groups and drag reorder, explain panel, skins,
tear-off windows), and resilience (two-second session saves, restore after a crash with
window placement and tear-offs, agent resume with or without a session id, recently
closed tabs and session history, opt-in restart with Windows), and the polish pass
(Windows Terminal chords, an honest working directory with a shell hook, restore hold and
close question, the previous screen after a crash, workspaces, tab-strip overflow, the jump
list, light and dark themes with the Windows accent, tear-offs with the window's chrome,
find in the buffer), Terminal parity (the shell integration injected into a plain
PowerShell launch, no profile edit; every tab's environment rebuilt from the registry, so
a freshly installed tool is on PATH in the next tab), and an **extension API** (`IShell`,
`ITab`, `IExtension`: the shell is the engine, features sit on top) with the keyboard
features built on it (herd mode with a which-key bar, MRU `Ctrl+Tab`, global summon,
prompt addressing, a cursor in the sidebar and dashboard, waiting tabs by age), and triage
(an **inbox** of everything waiting for you with `y`/`n` and replies that reach the agent
through its own API when the integration is installed, Allow / Deny on toasts, mute,
watch, auto-advance), and orchestration (spawn an agent in a tab or a fresh worktree with a
first prompt; a control API over the endpoint; `OverShell mcp` so an agent can oversee the
herd itself), and oversight (a **herd log** of every state change as JSON lines with a picker;
**changes while you were away** - the files an agent changed in its repository while you were
not looking, counted on the tab and listed).

Not yet confirmed by a human: see the test card (`tools\Show-LinkTestCard.ps1`) and
[DESIGN.md 8](DESIGN.md#8-status).

A translucent terminal body: `"window": { "terminalOpacity": 0.85 }` - the one thing the control
as Microsoft ships it could not do ([DESIGN.md 7.6](DESIGN.md#76-transparency-was-structurally-impossible-here-until-the-fork-1220)),
and the reason the control is now built by our fork ([DESIGN.md 12.20](DESIGN.md#1220-the-terminal-control-built-by-us-a-fork-as-a-distributable-and-the-body-goes-translucent)).

## Documentation

**[docs/GUIDE.md](docs/GUIDE.md)** - the user guide: configuration and where it lives,
every setting, keys and commands, views and layouts, agents and integrations,
notifications, prompt bar, sessions, `overshell://`, skins, the command line,
troubleshooting.

**[DESIGN.md](DESIGN.md)** - what it is, why it is built this way, the architecture
decision behind embedding Windows Terminal, the configuration pipeline, field notes on
every non-obvious trap encountered, the herd-overseer plan (12), distribution and the
release pipeline (13), and the open/closed item list.

## Licence

MIT - see [LICENSE](LICENSE).
