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
11. [Sessions: restore, resume and history](#11-sessions-restore-resume-and-history)
12. [`overshell://` and toast clicks](#12-overshell-and-toast-clicks)
13. [Themes and skins](#13-themes-and-skins)
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
| `skins\*.xaml` | Colour overrides on top of the theme ([§13](#13-themes-and-skins)) |
| `workspaces\*.jsonc` | Named sets of tabs to open together ([§11](#11-sessions-restore-resume-and-history)) |

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

  // Chrome palette and accent (§13): system follows Windows, live.
  "theme": "system",                 // system | dark | light
  "accent": "system",                // system | palette | "#RRGGBB"

  // A ResourceDictionary under skins\<name>.xaml overriding theme keys; null for none.
  "skin": null,

  "detection": {
    "snapshotDebounceMs": 300,       // wait for output to settle before reading the screen
    "snapshotMinIntervalMs": 300,    // never read one tab's screen more often than this
    "processProbeIntervalMs": 2500,  // how often the process tree below a shell is checked
    "treatUnknownAsAgent": false,    // true: every tab is an agent even when nothing is recognised
    "injectShellIntegration": true,  // a plain PowerShell profile announces its directory, no profile edit (§7)
    "cwdFromProcess": true           // learn the directory from the shell process / prompt line when it emits no OSC 9;9 (§15)
  },

  "compatibility": {
    "reloadEnvironmentVariables": true   // every tab's environment from the registry, as Windows Terminal does (§7)
  },

  "git": {
    "branch": true,                  // from .git/HEAD, no process spawned
    "dirty": false,                  // `git status --porcelain` per repository for a "*" marker
    "statusIntervalMs": 10000
  },

  "session": {
    "restore": true,                 // reopen last run's tabs, view and windows - also after a crash
    "resumeAgents": true,            // bring a tab's agent back: by session id when one is known
    "resumeWithoutId": true,         // ...else the tool's "most recent session" form (opencode --continue)
    "restoreWindows": true,          // main window and tear-offs back where they were
    "showPreviousScreen": "interrupted", // after a crash, the last screen as a dim preamble: interrupted | always | never
    "confirmCloseWithAgents": true,  // ask before closing while agents are working
    "restartWithWindows": false      // ask Windows to start OverShell again after a restart or sign-out
  },

  "protocol": { "register": true },  // overshell:// for this user (HKCU), so toast clicks find their tab

  "notifications": { "sinks": { /* see §9 */ } },

  "tabs": { "twoLine": true, "showHarnessGlyph": true }   // the state · project line; the harness icon on agent tabs
}
```

## 4. Keys and the command palette

Every chord resolves through `keybindings.jsonc` to a **command id**; nothing is
hard-wired. `Ctrl+Shift+P` opens the command palette (fuzzy search over titles,
categories and ids; the bound chord is shown on the right). The defaults:

| Chord | Command | What |
|---|---|---|
| `Ctrl+Shift+T` | `tab.new` | New tab, default profile (`Ctrl+T` stays with the shell — PSReadLine uses it) |
| `Ctrl+Shift+W` | `tab.close` | Close tab |
| `Ctrl+Shift+D` | `tab.duplicate` | Another tab like this one: same profile, directory and group, next to it |
| `Ctrl+Shift+Z` | `tab.reopenClosed` | Reopen the most recently closed tab, agent session included ([11](#11-sessions-restore-resume-and-history)) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | `tab.next` / `tab.previous` | Cycle |
| `Ctrl+PgDn` / `Ctrl+PgUp` | same | Cycle |
| `Alt+1` … `Alt+9` | `tab.switchTo.N` | Jump to the Nth visible tab |
| `Ctrl+Shift+J` | `tab.jumpToAttention` | Next tab that needs you: blocked first, then finished-unseen |
| `Ctrl+Shift+R` | `tab.rename` | Label the tab (persisted per profile + directory) |
| `Ctrl+Shift+G` | `tab.moveToGroup` | Put the tab under a named header; empty removes |
| `Alt+Shift+←` / `→` | `tab.moveLeft` / `tab.moveRight` | Reorder |
| `Ctrl+Shift+E` | `tab.explain` | Why is this tab in this state |
| `Ctrl+Shift+X` / `Ctrl+Shift+A` | `tab.detach` / `tab.attach` | Tear the tab off into its own window / bring it back |
| `Ctrl+Shift+Space` | `palette.tabs` | Tab switcher with screen preview |
| `Ctrl+Shift+P` | `palette.commands` | Command palette |
| `Ctrl+Shift+F` | `terminal.find` | Find in this tab's buffer, scrollback included (below) |
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
`settings.open`, `settings.init`, `session.save`, `session.history`, `workspace.save`,
`workspace.open.<name>`, `terminal.findUp`, `terminal.findDown`, `protocol.register`,
`integrations.status`, `integrations.install.<id>`, `integrations.uninstall.<id>`,
`snippet.<name>`.

`keybindings.jsonc` is an array; later entries win, `"unbound"` removes a default:

```jsonc
[
  { "keys": "ctrl+shift+n", "command": "tab.new" },
  { "keys": "ctrl+shift+t", "command": "unbound" },
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

**Find in the tab** (`Ctrl+Shift+F`) opens a search box under the terminal. It searches
the whole buffer, scrollback included, as you type: the match nearest the bottom of what
you see is selected — it is the terminal's own selection, so `Ctrl+Shift+C` copies it —
and the other matches on screen are tinted. **Enter** or **F3** goes to the previous match
upwards, through older text, scrolling to it when needed; **Shift+Enter** / **Shift+F3**
downwards; both wrap. `Aa` matches case; the counter reads *3 of 5*, *No matches*, or
*500+*. **Esc** gives the keyboard back to the terminal and leaves the selection where it
is. The box follows the active tab, and a tear-off window has one of its own. Plain text
only — no regular expressions.

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
- **Duplicate** (`Ctrl+Shift+D`): another tab of the same profile in the same directory,
  in the same group, right next to this one.
- **Tear-off** (`Ctrl+Shift+X`): the live terminal moves into a window of its own — with
  the same chrome as the main window and the tab's state in its caption; nothing restarts,
  the scrollback stays, keys and clicks work there, and the tab keeps its place in the
  sidebar, the dashboard and notifications (a small window glyph marks it). `Ctrl+Shift+A`
  in either window, or the tear-off's close button, brings it back. Chords pressed in a
  tear-off act on its tab. After a restart a tear-off comes back as a tear-off, where it
  was.
- **Many tabs**: the strip keeps every tab at least readable, scrolls with the wheel,
  fades at the edges where more are hiding, and the `…` button at its end opens the
  switcher.
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

**The working directory** shown in the status bar, the cards and the sidebar is what the
shell announces (OSC 9;9 / OSC 7, as Windows Terminal reads them). A plain PowerShell
profile announces it from its first prompt without any setup: OverShell starts it with
`-NoExit -Command ". '<script>'"` — the same way VS Code's terminal does — so a small
script runs after your profile and wraps your prompt (starship, oh-my-posh, the default,
whichever you ended up with). One visible difference: the tab starts at the prompt, without
the "PowerShell 7.x" banner. A profile that runs its own command or file is left exactly as
it is, and so is every other shell; `"detection": { "injectShellIntegration": false }` turns
it off. A shell that announces nothing is still not left at its starting directory: for cmd
and bash the directory is read from the shell process itself; for PowerShell, whose
`Set-Location` does not move the process, from the prompt line on screen (`PS C:\path>`).
`OverShell integrations install shell` (§8) is the fallback for those wrapped profiles.
`Ctrl+Shift+E` shows whether the integration is injected and where the directory comes from.

**The environment** of every tab is built from the registry — system and user variables,
PATH as system;user — the way Windows Terminal builds it, not inherited from whatever
started OverShell. So a tool installed after OverShell (or after the browser you clicked an
`overshell://` link in) is on PATH in the next tab, and a variable that existed only in the
shell you started OverShell from is not. `"compatibility": { "reloadEnvironmentVariables":
false }` inherits OverShell's own environment instead, like Terminal's setting of the same
name. `Ctrl+Shift+E` shows which. Inside WSL, `WSLENV` carries `WT_SESSION`, `WT_PROFILE_ID`
and the `OVERSHELL_*` variables across.

Evidence the detector reads without any integration: the OSC 0/2 title, OSC 9;4
progress, OSC 9/99/777 notifications, the bell, OSC 133 shell-integration marks, and
the last rows of the screen (read through UI Automation, hidden tabs included). Rules
per harness live in `agents\<id>.jsonc`; a bundled file with the same id is replaced
wholesale by yours. Shape:

```jsonc
{
  "id": "myagent", "displayName": "My Agent", "icon": "M8,0 L16,8 L8,16 L0,8 Z", "glyph": "◆",
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
| PowerShell (the shell itself) | `shell` | **usually unnecessary** — a plain PowerShell profile gets the integration on its command line (§7). For a profile that runs its own command or file, or under a `Restricted` execution policy: a marked block at the end of both PowerShell profiles (`PowerShell\` and `WindowsPowerShell\` under Documents) that wraps your `prompt` and emits OSC 9;9 — only inside OverShell or Windows Terminal, only for file-system locations. `uninstall shell` removes the block |

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

## 11. Sessions: restore, resume and history

OverShell keeps `session.json` in the state root current while it runs — every two
seconds, whenever something changed: the open tabs (profile, directory, label, group),
the active tab, the view and layout overrides, where the main window and every tear-off
sit, and for each tab whose agent is running its harness, session id and resume command.
Closing the window writes it one last time with how the run ended.

**After a restart** everything comes back: the tabs in their directories, the windows
where they were (on the monitors you still have — a window saved on a screen that is gone
moves to the desktop corner), the tear-offs as tear-offs, the tab you were looking at in
front.

**After a crash or a power cut** the same happens, and the status bar says *Restored N
tabs from an interrupted session (saved HH:mm)* — the file never got its closing note, so
OverShell knows. After a Windows sign-out or restart it says that instead. Start with
`OverShell --fresh` to skip the restore once; the session is not lost, it is in the
history below. Each restored tab shows **what was on its screen** when the run was cut
off — the last thirty rows, dimmed and in italics above a rule, so you can see where the
agent was before you decide what to do (`"session": { "showPreviousScreen": "always" |
"never" }` changes when). And if two runs in a row end interrupted within a minute, the
restore is **held** — the tabs stay in the history, the status bar says so — rather than
reopening whatever keeps bringing the window down.

**Closing with agents working** asks first: *Close anyway — N agents working* or *Keep
OverShell open*. A Windows sign-out does not ask. `"session": { "confirmCloseWithAgents":
false }` turns the question off.

**Agents come back too.** A tab whose agent was running gets it resumed:

- by **session id** when one is known — `opencode --session <id>`, `copilot --resume=<id>`,
  `claude --resume <id>`, `codex resume <id>` — from the integration's report;
- else, when `resumeWithoutId` is on (default), by the tool's own **most recent session**
  form — `opencode --continue`, `copilot --continue`, `claude --continue`,
  `codex resume --last`. "Most recent" is the tool's notion, usually machine-wide, so
  OverShell uses it for at most one tab per harness per restore (the one you were
  looking at); the others of that harness come back with their directory only.

*How* depends on the profile: a shell profile gets the command typed once the shell has
shown its prompt and gone quiet; a profile whose program *is* the agent (`opencode.exe`
as the command line) is relaunched with the resume arguments appended; a profile that
wraps the agent in a shell command (`pwsh -NoExit -Command opencode`) gets neither — only
its directory, because neither way is safe. `tab.resume` types the known command again
any time; `session.save` writes the file now. Install the harness's integration
([8](#8-harness-integrations)) and the id is always known: OpenCode's plugin learns it
even for a session that was itself resumed.

**History.** A tab you close is remembered — directory, label, group, agent session —
and `Ctrl+Shift+Z` (`tab.reopenClosed`) brings the most recent one back, agent resumed.
Every clean close archives the whole session under `sessions\` in the state root (and a
start after a crash archives the interrupted one first, so nothing overwrites it); the
newest ten are kept. `session.history` (palette) lists recently closed tabs, then earlier
sessions — *Session interrupted Fri 22:06 — 2 tabs · OpenCode ×2* — choose a tab to
reopen it, a session to add all of its tabs next to the open ones.

**Workspaces.** A workspace is a named set of tabs — profile, directory, label, group —
kept as `workspaces\<name>.jsonc` in the configuration root, so it syncs with your
dotfiles. `workspace.save` (palette) writes the open tabs under a name you give; each file
is a `Workspace: <name>` command that opens its tabs next to the current one (`command`
is typed once the shell is at its prompt; `view` switches the view; `detached` opens the
tab in a tear-off); the files reload live. `overshell://workspace/<name>` opens one from outside (§12).

```jsonc
{
  "name": "Blog", "description": "The site and an agent on it", "view": "herd",
  "tabs": [
    { "profile": "PowerShell", "cwd": "D:\\src\\blog", "label": "site", "command": "npm run dev" },
    { "profile": "PowerShell", "cwd": "D:\\src\\blog", "label": "agent", "group": "blog", "command": "opencode" },
    { "profile": "PowerShell", "cwd": "%USERPROFILE%", "detached": true }
  ]
}
```

**The taskbar jump list** (right-click the OverShell button) has *New tab*, *Reopen closed
tab* and *Session history*, the five most recent sessions, and your workspaces.

**Restart with Windows.** With `"session": { "restartWithWindows": true }` OverShell asks
Windows to start it again after a restart or sign-out, when the Windows setting
*Automatically save my restartable apps and restart them when I sign back in* is on.
Never after a crash. Off by default.

`"session": { "restore": false }`, `"resumeAgents": false`, `"resumeWithoutId": false`
or `"restoreWindows": false` turn each part off.

## 12. `overshell://` and toast clicks

At start OverShell registers `overshell://` for your user (HKCU, no elevation; `"protocol":
{ "register": false }` to opt out, `OverShell protocol status|register|unregister` by
hand). Only one OverShell runs per user: a second `OverShell.exe` hands its arguments
to the first and exits. So:

- `overshell://focus/<tabId>` brings the running window up on that tab — this is what the
  Palantir recipe's `--launch` and the native `toast` sink put on their toasts;
- `overshell://view/<terminal|herd|dashboard|zen>` switches views;
- `overshell://new?profile=<id or name>&cwd=<path>` opens a tab;
- `overshell://workspace/<name>` opens a workspace, `overshell://reopen` the last closed tab,
  `overshell://history` the session picker and `overshell://history/<archive>` a whole
  archived session — the jump list uses these (§11).

## 13. Themes and skins

`"theme": "system" | "dark" | "light"` picks the chrome palette; `system` (default) follows the
Windows *Choose your mode* setting and changes live when it does. `"accent": "system" |
"palette" | "#RRGGBB"` picks the accent — `system` (default) is the Windows accent colour in
the variant that suits the theme, `palette` the theme's own blue, or any colour. The
terminal body keeps its colour scheme either way; the theme is the chrome around it, the
tear-off windows included.

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
OverShell --fresh                           # the window without the last session (kept in the history)
OverShell overshell://focus/<tabId>         # same, with a request for the running window
OverShell settings path|init|open
OverShell integrations status
OverShell integrations install   <opencode|copilot|claude|codex|shell|all>
OverShell integrations uninstall <opencode|copilot|claude|codex|shell|all>
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
| `OVERSHELL_SELFTEST=1` | runs the in-process end-to-end self-test; `opencode` / `opencode-resume` run it against the real OpenCode; `session1`/`session2` check restore across a restart, `sessionend` a sign-out, `history` the history, `icons` the icons; `polish`, `cwd`, `resilience`, `ghost`, `workspaces`, `overflow`, `jumplist`, `theme`, `tearoff`, `find`, `inject`, `env` one feature each. `%TEMP%\overshell-selftest.log` |
| `OVERSHELL_WT_SETTINGS` | read this Windows Terminal `settings.json` instead of the installed one |
| `OVERSHELL_PROFILE_ROOT` | where `integrations install shell` looks for the PowerShell profile folders instead of Documents |

Crashes always go to `%TEMP%\overshell-crash.log`. `tools\Show-LinkTestCard.ps1` prints
the manual test card for everything a human should confirm.

**A tab says "working" but the agent is waiting.** Nothing on screen matched a `blocked`
pattern — by design OverShell never infers "needs you" from silence. Install the harness's
integration, or add the prompt's text to `screen.blocked` in that harness's rule file
(`Ctrl+Shift+E` shows what the detector last saw and matched).

**The working directory does not follow `cd`.** A plain PowerShell profile announces it
(the integration rides on its command line, §7); OverShell otherwise reads the shell process
(cmd, bash) or the prompt line (PowerShell). A PowerShell profile that runs its own command,
with a prompt that does not print the path, hides it: `OverShell integrations install
shell` makes the prompt announce it. `Ctrl+Shift+E` shows which source the tab is using.

**A tool I just installed is not found in a new tab.** It should be — every tab's
environment comes from the registry (§7). If `Ctrl+Shift+E` says `env  inherited`, the
setting `compatibility.reloadEnvironmentVariables` is off, or Windows refused to build the
block; otherwise the installer did not put the tool on the user or system PATH (check
*Edit environment variables for your account*).

**The PowerShell banner is gone.** `-Command`, which carries the shell integration,
suppresses it — as in VS Code's terminal. `"detection": { "injectShellIntegration": false }`
brings it back (the probe follows `cd` instead).

**A toast click opens a "choose an app" dialog.** `overshell://` is not registered for
this executable (a moved build, or `protocol.register` off): `OverShell protocol
register`.

**Nothing reloads when I save a file.** `OverShell settings path` shows which root is
watched; a file saved elsewhere is not it.
