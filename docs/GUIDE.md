# OverShell — user guide

OverShell is a Windows terminal shell built around Microsoft's Windows Terminal rendering
engine, made for running several AI coding agents at once: it tells you which tab is
working, which one is waiting for you, and which one finished while you were looking
elsewhere. This guide is about using it; [DESIGN.md](../DESIGN.md) is about why it is
built the way it is.

Contents

1. [Install, or build and run](#1-install-or-build-and-run)
2. [Where configuration lives](#2-where-configuration-lives)
3. [`settings.jsonc`](#3-settingsjsonc)
4. [Keys and the command palette](#4-keys-and-the-command-palette)
5. [Tabs, groups, reorder, tear-off windows](#5-tabs-groups-reorder-tear-off-windows)
6. [Views and layouts](#6-views-and-layouts)
7. [Agents: what OverShell sees and how](#7-agents-what-overshell-sees-and-how)
8. [Harness integrations](#8-harness-integrations)
9. [Notifications](#9-notifications)
10. [The prompt bar and snippets](#10-the-prompt-bar-and-snippets)
11. [Sessions: restore and resume](#11-sessions-restore-and-resume)
12. [`overshell://` and toast clicks](#12-overshell-and-toast-clicks)
13. [Skins](#13-skins)
14. [Command line](#14-command-line)
15. [Diagnostics and troubleshooting](#15-diagnostics-and-troubleshooting)

---

## 1. Install, or build and run

Windows 10 19041 or later (Windows 11 22621+ for the system backdrop), **x64 only**.
Pick one:

| | |
|---|---|
| **winget** | `winget install MoaidHathot.OverShell` — installs the .NET 10 Desktop Runtime when it is missing and puts `overshell` on your PATH. `winget upgrade` keeps it current. |
| **.NET tool** | `dotnet tool install -g OverShell`, then `overshell`. Or without installing: `dnx OverShell`. Update with `dotnet tool update -g OverShell`. |
| **Zip** | From the [releases page](https://github.com/MoaidHathot/OverShell/releases): `OverShell-<version>-win-x64.zip` needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); `OverShell-<version>-win-x64-selfcontained.zip` needs nothing. Unzip anywhere, run `OverShell.exe`. `SHA256SUMS.txt` lists every asset. |

Started from a tool wrapper (`overshell`, `dnx`) the window detaches so your prompt
comes back at once; `overshell --no-detach` keeps it attached. `overshell version`
prints the version and the path it runs from — useful when more than one copy is around.

Your existing Windows Terminal `settings.json` is read for profiles, colour schemes and
fonts; OverShell never writes to it.

To build from source instead (.NET 10 SDK):

```powershell
dotnet build OverShell.slnx -c Debug -p:Platform=x64
.\artifacts\bin\OverShell.App\debug_win-x64\OverShell.exe
dotnet test OverShell.slnx -c Debug -p:Platform=x64     # the unit tests
```

`build\Release.ps1 -Version 0.1.0` builds every release artefact locally
(`artifacts\release\dist`): both zips, the tool package and the winget manifests.

## 2. Where configuration lives

OverShell keeps **configuration** (things you edit, worth syncing) and **state** (labels
you gave tabs, the last session — machine-local) in two roots. Each is the first that
applies:

| Order | Configuration | State |
|---|---|---|
| 1 | `OVERSHELL_CONFIG_DIR` | `OVERSHELL_STATE_DIR` |
| 2 | `$XDG_CONFIG_HOME\overshell` | `$XDG_STATE_HOME\overshell` |
| 3 | `%APPDATA%\OverShell` | `%LOCALAPPDATA%\OverShell` |

So if you keep your dotfiles in one place and set `XDG_CONFIG_HOME` (as OpenCode, Neovim
and git honour it on Windows), OverShell's files go to `…\overshell` under it; nothing
else is required. A relative `XDG_*` value is ignored, as the specification asks.

```powershell
OverShell settings path    # prints both roots, which variable chose them, and every file
OverShell settings init    # writes settings.jsonc + keybindings.jsonc from the defaults (never over existing files)
OverShell settings open    # Explorer on the configuration root
```

The same two actions are in the command palette (`Settings: …`). Files under the
configuration root:

| File | What |
|---|---|
| `settings.jsonc` | Everything in [§3](#3-settingsjsonc) |
| `keybindings.jsonc` | Key → command, Windows Terminal's shape ([§4](#4-keys-and-the-command-palette)) |
| `snippets.jsonc` | Reusable prompts ([§10](#10-the-prompt-bar-and-snippets)) |
| `agents\*.jsonc` | Per-harness detection rules ([§7](#7-agents-what-overshell-sees-and-how)) |
| `layouts\*.jsonc` | Chrome arrangements ([§6](#6-views-and-layouts)) |
| `skins\*.xaml` | Theme overrides ([§13](#13-skins)) |

**Every one of them reloads live** when saved (a 400 ms debounce lets editors finish
writing). A broken file is reported in the status bar and the trace log and the
previous or default values stay in force — a typo never leaves you without a working
terminal. All files are JSONC: comments and trailing commas are fine.

## 3. `settings.jsonc`

You only write what you change. Objects merge key by key with the built-in defaults,
arrays replace as a whole, and setting a key to `null` removes it. `OverShell settings
init` gives you the full default file, commented, as a starting point.

```jsonc
{
  // The view shown at start: terminal | herd | dashboard | zen.
  "view": "terminal",

  // A view is a layout (a preset name or a file under layouts\) plus what the middle
  // shows: "terminal" (the live tab) or "dashboard" (one card per tab).
  "views": {
    "terminal":  { "layout": "top",       "content": "terminal",  "title": "Terminal" },
    "herd":      { "layout": "herd",      "content": "terminal",  "title": "Herd" },
    "dashboard": { "layout": "dashboard", "content": "dashboard", "title": "Dashboard" },
    "zen":       { "layout": "zen",       "content": "terminal",  "title": "Zen" }
  },

  // A ResourceDictionary under skins\<name>.xaml overriding theme keys; null for none.
  "skin": null,

  "detection": {
    "snapshotDebounceMs": 300,       // wait for output to settle before reading the screen
    "snapshotMinIntervalMs": 300,    // never read one tab's screen more often than this
    "processProbeIntervalMs": 2500,  // how often the process tree below a shell is checked
    "treatUnknownAsAgent": false     // true: every tab is an agent even when nothing is recognised
  },

  "git": {
    "branch": true,                  // from .git/HEAD, no process spawned
    "dirty": false,                  // `git status --porcelain` per repository for a "*" marker
    "statusIntervalMs": 10000
  },

  "session": {
    "restore": true,                 // reopen last run's tabs and view
    "resumeAgents": true             // type the resume command into a tab whose agent was mid-session
  },

  "protocol": { "register": true },  // overshell:// for this user (HKCU), so toast clicks find their tab

  "notifications": { "sinks": { /* see §9 */ } },

  "tabs": { "twoLine": true, "showHarnessGlyph": true }
}
```

## 4. Keys and the command palette

Every chord resolves through `keybindings.jsonc` to a **command id**; nothing is
hard-wired. `Ctrl+Shift+P` opens the command palette (fuzzy search over titles,
categories and ids; the bound chord is shown on the right). The defaults:

| Chord | Command | What |
|---|---|---|
| `Ctrl+T` | `tab.new` | New tab, default profile |
| `Ctrl+Shift+W` | `tab.close` | Close tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | `tab.next` / `tab.previous` | Cycle |
| `Ctrl+PgDn` / `Ctrl+PgUp` | same | Cycle |
| `Alt+1` … `Alt+9` | `tab.switchTo.N` | Jump to the Nth visible tab |
| `Ctrl+Shift+J` | `tab.jumpToAttention` | Next tab that needs you: blocked first, then finished-unseen |
| `Ctrl+Shift+R` | `tab.rename` | Label the tab (persisted per profile + directory) |
| `Ctrl+Shift+G` | `tab.moveToGroup` | Put the tab under a named header; empty removes |
| `Alt+Shift+←` / `→` | `tab.moveLeft` / `tab.moveRight` | Reorder |
| `Ctrl+Shift+E` | `tab.explain` | Why is this tab in this state |
| `Ctrl+Shift+D` / `Ctrl+Shift+A` | `tab.detach` / `tab.attach` | Tear the tab off into its own window / bring it back |
| `Ctrl+Shift+Space` | `palette.tabs` | Tab switcher with screen preview |
| `Ctrl+Shift+P` | `palette.commands` | Command palette |
| `Ctrl+Shift+1` … `4` | `view.terminal` / `view.herd` / `view.dashboard` / `view.zen` | Views |
| `` Ctrl+Shift+` `` | `view.toggle` | Back to the previous view |
| `Ctrl+Shift+Enter` | `prompt.toggle` | Prompt bar |
| `Ctrl+Shift+B` | `prompt.broadcast` | Prompt bar aimed at every agent |
| `Ctrl+C` | `clipboard.copyIfSelection` | Copies when something is selected, else reaches the shell as an interrupt |
| `Ctrl+V`, `Ctrl+Shift+V` | `clipboard.paste` | Paste (bracketed when the application asked for it) |
| `Ctrl+Shift+C` | `clipboard.copy` | Copy |

Right-click in the terminal copies a selection, else pastes. Right-click on a tab (in
the strip, the list, the rail, the sidebar) opens a menu with rename / agent or shell /
explain / group / detach / close.

Commands with no default chord, for the palette or your own bindings: `tab.resume`,
`tab.markAgent`, `tab.markShell`, `prompt.blocked`, `layout.<name>`, `settings.reload`,
`settings.open`, `settings.init`, `session.save`, `protocol.register`,
`integrations.status`, `integrations.install.<id>`, `integrations.uninstall.<id>`,
`snippet.<name>`.

`keybindings.jsonc` is an array; later entries win, `"unbound"` removes a default:

```jsonc
[
  { "keys": "ctrl+shift+n", "command": "tab.new" },
  { "keys": "ctrl+t",       "command": "unbound" },
  { "keys": "ctrl+alt+h",   "command": "view.herd" }
]
```

Key names follow Windows Terminal: `ctrl`, `shift`, `alt`, `win`; letters, digits,
`f1`–`f24`, `tab`, `esc`, `enter`, `space`, `backspace`, `del`, `ins`, `home`, `end`,
`pgup`, `pgdn`, arrows, `plus`, `minus`, `comma`, `period`, `slash`, `backslash`,
`semicolon`, `quote`, `backtick`, `[`, `]`. A name that is not a key is reported, not
guessed. The Windows Terminal object shape (`{ "keybindings": [ { "command": { "action":
"…" }, "keys": "…" } ] }`) is accepted too.

**The tab switcher** (`Ctrl+Shift+Space`) searches label, title, project, directory,
harness and state. `@blocked`, `@working`, `@done` filter by state; `#repo` filters by
project; `>` switches to commands. The pane on the right previews the selected tab's
screen, refreshed while open. Tabs needing you come first when the box is empty.

## 5. Tabs, groups, reorder, tear-off windows

A tab item has two lines: the **label** (your name for it, else the harness, else the
shell's title) and the **detail** (state · summary or project). The dot is the profile's
colour for a plain shell and the state's colour for an agent: pulsing blue working,
amber with a ring **needs you**, green **done** until you look, red error, grey exited.
An unread badge marks a state you have not seen; a thin bar shows OSC 9;4 progress.

- **Rename** (`Ctrl+Shift+R`): the label is remembered for that profile in that
  directory and comes back next time.
- **Groups** (`Ctrl+Shift+G`): tabs under a named header in the strip and the list.
  Dragging a tab onto another group's tab joins that group; so does `Alt+Shift+←/→`.
- **Reorder**: drag a tab along the strip — it changes place as the pointer crosses its
  neighbours. `Alt+N` always means what you see.
- **Tear-off** (`Ctrl+Shift+D`): the live terminal moves into a window of its own; nothing
  restarts, the scrollback stays, keys and clicks work there, and the tab keeps its place
  in the sidebar, the dashboard and notifications (`⧉` in its detail). `Ctrl+Shift+A` in
  either window, or closing the tear-off, brings it back. Chords pressed in a tear-off act
  on its tab. Tear-offs come back attached after a restart.
- **Explain** (`Ctrl+Shift+E`): state, why, who decided (detector or which integration),
  harness, session id, resume command, processes below the shell, the last transitions.

## 6. Views and layouts

Four views, one key each, and a switch at the right of the tab strip whose badge counts
the tabs needing you:

| View | Middle | Chrome |
|---|---|---|
| **Terminal** `Ctrl+Shift+1` | the live tab | tab strip in the caption bar |
| **Herd** `Ctrl+Shift+2` | the live tab | plus a sidebar: every tab grouped by project, the ones needing you first, with activity age |
| **Dashboard** `Ctrl+Shift+3` | one **card per tab** | header (state, label, project · branch), body = the last screen rows in the tab's colours, footer (state, summary, activity). Click selects, double-click or the button opens it in Terminal |
| **Zen** `Ctrl+Shift+4` | the live tab | no tabs, no status bar, slim caption |

Cards are text read from the terminal, refreshed about once a second — never a live
terminal shrunk into a tile, which would reflow the agent's screen.

A view = a **layout** + what the middle shows. Layouts are five-line JSONC files that say
where the tabs go and how they look, whether the sidebar shows, whether the status bar
shows:

```jsonc
{
  "description": "Tab list on the left with the herd beside the terminal",
  "tabs":    { "placement": "left", "style": "list", "width": 240 },   // top|bottom|left|right|hidden; strip|list|rail
  "sidebar": { "placement": "right", "width": 320 },                   // hidden|left|right
  "status":  { "visible": true }
}
```

Presets: `top`, `bottom`, `left-rail`, `left-list`, `right-list`, `zen`, `herd`,
`dashboard`. Save a file with a preset's name under `layouts\` to replace it; save any
other name and point a view at it in `settings.jsonc` (`"views": { "terminal": { "layout":
"mine" } }`), or pick one for the current view from the palette (`Layout: …`, lasts the
session and is remembered in the session file). The terminal never re-parents when the
layout changes — only its neighbours move.

Tabs in a git repository show the branch (from `.git/HEAD`, worktrees included) in the
status bar, the card header and the sidebar; `"git": { "dirty": true }` adds `*` from
`git status`.

## 7. Agents: what OverShell sees and how

Every tab is watched for the program inside it. The **harness** (OpenCode, Copilot CLI,
Claude Code, Codex, or `generic`) is recognised from the launch command line, from a
known process below the shell (checked every 2.5 s while output changes), from the
title, or from an integration's own report. The **state** is one of:

| State | Meaning | Comes from |
|---|---|---|
| working | producing output or reporting a turn | output activity, title spinner, OSC 9;4 progress, integration |
| **needs you** (blocked) | a permission or question prompt | an explicit match on the screen or a notification, or an integration — **never from silence** |
| **done** | the turn ended while the tab was not being looked at; stays until you look | quiet after work, an explicit idle signal, a bell, an integration |
| idle | waiting, and you saw it | |
| error | the agent reported a failure | |
| exited | the process ended | |

Two authorities, never both: while an integration reports for a tab its word is final;
otherwise the detector weighs the evidence. A tab "viewed" is the active tab in a
focused window (or a focused tear-off) — that is what clears **done**.

Evidence the detector reads without any integration: the OSC 0/2 title, OSC 9;4
progress, OSC 9/99/777 notifications, the bell, OSC 133 shell-integration marks, and
the last rows of the screen (read through UI Automation, hidden tabs included). Rules
per harness live in `agents\<id>.jsonc`; a bundled file with the same id is replaced
wholesale by yours. Shape:

```jsonc
{
  "id": "myagent", "displayName": "My Agent", "glyph": "◆",
  "detect": { "commandline": ["\\bmyagent\\b"], "title": ["My Agent"], "process": ["myagent"] },
  "title":  { "working": ["^⠋|^⠙"], "idle": ["^✳"] },
  "screen": { "blocked": ["Do you want to proceed", "\\(y/n\\)"], "working": ["Esc to interrupt"], "idle": ["\\? for shortcuts"] },
  "notification": { "blocked": ["permission", "approval"], "done": ["finished"] },
  "progress": { "1": "Working", "2": "Error", "3": "Working" },
  "summary": "^✻\\s+(.+)$",          // one capture group: the status line shown as the tab's summary
  "idleAfterMs": 1500, "screenRows": 12,
  "resumeCommand": "myagent --resume {sessionId}"
}
```

`tab.markAgent` / `tab.markShell` override detection for one tab; `"detection":
{ "treatUnknownAsAgent": true }` makes every tab an agent.

## 8. Harness integrations

An integration makes the harness itself tell OverShell what it is doing — exact, and
with the session id for resume. Inside every tab, `OVERSHELL_ENDPOINT`,
`OVERSHELL_TOKEN` and `OVERSHELL_TAB_ID` are set; anything that can POST JSON can report:

```powershell
curl.exe -X POST "$env:OVERSHELL_ENDPOINT/v1/report?token=$env:OVERSHELL_TOKEN" `
  -H "Content-Type: application/json" `
  -d "{`"tab`":`"$env:OVERSHELL_TAB_ID`",`"source`":`"me`",`"state`":`"blocked`",`"message`":`"needs a decision`"}"
```

States: `idle`, `working`, `blocked` (also `waiting`, `permission`, `question`), `done`,
`error`, `exited`. Optional: `seq` (stale numbers from the same source are dropped),
`summary`, `harness`, `session: { id, resumeCommand }`, and `"advisory": true` for a
one-shot report that should mark the moment without becoming the tab's authority.
`GET /v1/tabs?token=…` lists every tab's state and explanation.

The shipped integrations are installed with one command each and do nothing outside
OverShell:

| Harness | `OverShell integrations install …` | What is written |
|---|---|---|
| OpenCode | `opencode` | `$XDG_CONFIG_HOME/opencode/plugins/overshell.ts` (or `~/.config/opencode/plugins/`) — a plugin reporting `session.status`, `permission.asked`, `question.asked`, errors, with the session id |
| Copilot CLI | `copilot` | `~/.copilot/hooks/overshell.json` (`COPILOT_HOME` honoured) — hooks for session start/end, prompt, stop, permission and elicitation notifications |
| Claude Code | `claude` | hook entries **merged into** `~/.claude/settings.json` (`CLAUDE_CONFIG_DIR` honoured) under a marker; `uninstall` removes only those. A file with comments is refused, not rewritten — `integrations show claude` prints the entries to add by hand |
| Codex CLI | `codex` | a PowerShell script next to `~/.codex/config.toml` (`CODEX_HOME` honoured) and one marked `notify = […]` line among the top-level keys. A `notify` you wrote is never replaced — `integrations show codex` prints ours to combine |

`OverShell integrations status` shows what is installed and whether the harness is on
PATH. Restart the harness inside an OverShell tab after installing. Codex's hook only
fires when a turn completes, so Codex tabs report **advisory**: the turn's end, the
thread id (`codex resume <id>`) and the last message, while OverShell's own detection
keeps saying when it is working or blocked.

## 9. Notifications

Events: **blocked**, **done**, **error**, **exited**. Each enabled **sink** in
`settings.jsonc` runs for every event its `when` allows: `notActiveTab`,
`windowUnfocused`, `kinds` (list), `harnesses` (list; `shell` for tabs without one),
`quietHours` (`"22:00-07:00"`).

| Sink | Type | What |
|---|---|---|
| `overlay` | `overlay` | an in-window toast over the terminal for background tabs; click focuses the tab |
| `taskbar` | `taskbar` | badge with the count, amber while one is blocked, red on error; flash when unfocused |
| `sound` | `sound` | `asterisk`, `exclamation`, `hand`, `question`, `beep` or a `.wav`, per kind under `sounds` |
| `toast` | `toast` | a Windows toast, no external program; a click opens the tab. Off by default |
| `palantir` | `command` | any executable with templated arguments — the shipped recipe uses [Palantir](https://github.com/MoaidHathot/Palantir). Off by default |

Placeholders for `command` arguments: `{kind}`, `{title}`, `{message}`, `{detail}`,
`{time}`, `{tab.id}`, `{tab.label}`, `{tab.harness}`, `{tab.harnessName}`, `{tab.project}`,
`{tab.cwd}`; `"stdinJson": true` also writes the event as JSON to stdin.

```jsonc
{ "notifications": { "sinks": {
    "palantir": { "enabled": true },                         // Windows toasts through Palantir, click focuses the tab
    "sound":    { "when": { "windowUnfocused": true, "quietHours": "22:00-07:00" } },
    "overlay":  null                                          // remove a sink entirely
} } }
```

The status bar counts `● working`, `▲ needs you`, `✓ done`; clicking it jumps to the tab
that needs you.

## 10. The prompt bar and snippets

`Ctrl+Shift+Enter` opens a text box under the terminal. Enter sends, Shift+Enter breaks
the line, Up/Down walk the history, Esc hides it (the shell gets the keyboard back). The
target box chooses **Active tab**, **All agents**, **Agents needing you**, or **Every
tab**; `Ctrl+Shift+B` opens it aimed at all agents. The text is typed into each target
as one block (bracketed paste where the program asked for it) followed by Enter.

`snippets.jsonc` holds reusable prompts; each appears on the bar's snippet menu and as a
`Snippet: …` command:

```jsonc
[
  { "name": "Explain", "text": "Explain what you just changed and why.", "description": "Recap" },
  { "name": "Tests",   "text": "Run the tests and fix any failure before reporting back." }
]
```

## 11. Sessions: restore and resume

Closing OverShell saves the open tabs (profile, directory, label, group), the active
tab, the view and any layout overrides to `session.json` in the state root (also every
30 s while running). The next start reopens them. A tab whose agent was **still running**
gets its resume command typed once the shell is ready — `opencode --session <id>`,
`copilot --resume=<id>`, `claude --resume <id>`, `codex resume <id>` — from the
integration's report or the rule file's `resumeCommand`. `tab.resume` types it again any
time; `session.save` writes the file now. `"session": { "restore": false }` or
`"resumeAgents": false` turn either off.

## 12. `overshell://` and toast clicks

At start OverShell registers `overshell://` for your user (HKCU, no elevation; `"protocol":
{ "register": false }` to opt out, `OverShell protocol status|register|unregister` by
hand). Only one OverShell runs per user: a second `OverShell.exe` hands its arguments
to the first and exits. So:

- `overshell://focus/<tabId>` brings the running window up on that tab — this is what the
  Palantir recipe's `--launch` and the native `toast` sink put on their toasts;
- `overshell://view/<terminal|herd|dashboard|zen>` switches views;
- `overshell://new?profile=<id or name>&cwd=<path>` opens a tab.

## 13. Skins

`"skin": "mine"` loads `skins\mine.xaml`, a WPF `ResourceDictionary` whose keys override
the theme's — brushes (`Surface.Chrome`, `Surface.Base`, `Accent.Base`, `Text.Primary`,
`State.Blocked`, `State.Done`, …), fonts (`Font.Ui`, `Font.Mono`), metrics
(`Metrics.TabHeight`, `Metrics.StatusBarHeight`). It is applied before the first window,
so everything picks it up; when you save it later, **colours** update live, fonts and
metrics at the next start. Skins are your own files and are trusted like configuration.

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="Accent.Base" Color="#FF3DD68C" />
  <SolidColorBrush x:Key="Surface.Chrome" Color="#FF14171C" />
</ResourceDictionary>
```

## 14. Command line

```
OverShell                                   # the window (a second start hands over and exits)
OverShell overshell://focus/<tabId>         # same, with a request for the running window
OverShell settings path|init|open
OverShell integrations status
OverShell integrations install   <opencode|copilot|claude|codex|all>
OverShell integrations uninstall <opencode|copilot|claude|codex|all>
OverShell integrations show      <claude|codex>
OverShell protocol status|register|unregister
OverShell version                           # also --version, -v: version, commit, path
OverShell help                              # also --help, -h, -?
```

Output goes to the console you ran it from, or to a file when redirected. Installed as
a .NET tool the command is `overshell`; the wrapper returns at once for the window
([1](#1-install-or-build-and-run)) and `--no-detach` keeps it attached instead.

## 15. Diagnostics and troubleshooting

| Variable | Effect |
|---|---|
| `OVERSHELL_CONFIG_DIR`, `OVERSHELL_STATE_DIR` | override the roots ([§2](#2-where-configuration-lives)) |
| `OVERSHELL_BACKDROP` | `acrylic` (default), `mica`, `micaalt`, `none` |
| `OVERSHELL_TRACE_AGENTS=1` | `%TEMP%\overshell-agents.log`: detection, every state change with its reason, endpoint traffic, notifications, reloads |
| `OVERSHELL_TRACE_KEYS=1` | `%TEMP%\overshell-keys.log`: every chord seen |
| `OVERSHELL_TRACE_LINKS=1` | `%TEMP%\overshell-links.log`: link hover and click resolution |
| `OVERSHELL_SELFTEST=1` | runs the in-process end-to-end self-test; `opencode` runs it against the real OpenCode; `session1`/`session2` check restore across a restart. `%TEMP%\overshell-selftest.log` |

Crashes always go to `%TEMP%\overshell-crash.log`. `tools\Show-LinkTestCard.ps1` prints
the manual test card for everything a human should confirm.

**A tab says "working" but the agent is waiting.** Nothing on screen matched a `blocked`
pattern — by design OverShell never infers "needs you" from silence. Install the harness's
integration, or add the prompt's text to `screen.blocked` in that harness's rule file
(`Ctrl+Shift+E` shows what the detector last saw and matched).

**The working directory does not follow `cd`.** OverShell learns it from the shell's
OSC 9;9 / OSC 7 prompt sequences, the same way Windows Terminal does; add the one-liner
Windows Terminal documents to your profile and the branch, project and cards follow.

**A toast click opens a "choose an app" dialog.** `overshell://` is not registered for
this executable (a moved build, or `protocol.register` off): `OverShell protocol
register`.

**Nothing reloads when I save a file.** `OverShell settings path` shows which root is
watched; a file saved elsewhere is not it.
