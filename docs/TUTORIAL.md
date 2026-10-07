# OverShell in ten minutes

A hands-on walk through what OverShell does, in the order you will meet it: one shell, then
a few, then an agent in one of them, then a herd of agents and the ways to keep up with it.
Every step says what to do and what you should see. The [guide](GUIDE.md) has the whole story
per topic; this page is the short road through it.

You need Windows 10 19041+ (Windows 11 22621+ for the translucent window), x64, and
PowerShell 7. An AI coding agent (OpenCode, Copilot CLI, Claude Code or Codex) makes steps 4
onwards real, but the steps work with any program that prints and waits.

## 0. Install and start

Pick one:

```powershell
dotnet tool install -g OverShell ; overshell        # or run once without installing: dnx OverShell
# or: unzip OverShell-<version>-win-x64.zip from https://github.com/MoaidHathot/OverShell/releases and run OverShell.exe
```

The first start opens one tab with your default Windows Terminal profile. Above its prompt,
in dim text, a short welcome note names the three keys you need to begin and this page. It
shows once: OverShell knows a first start because it has no state yet.

> OverShell reads your Windows Terminal `settings.json` for the profiles, colour schemes and
> font. It changes nothing there.

## 1. The window, and the key that finds everything

**Do:** press `Ctrl+Shift+P`.

**You see:** the command palette - a search box with `>` in it and the list of every command,
its category, and the key bound to it on the right. Type a word (`tab`, `view`, `setting`) and
the list narrows; `Enter` runs the selected one, `Esc` closes. Everything in this tutorial is in
that list, so when you forget a key, this is the one to remember.

**Do:** press `Ctrl+Shift+Space`.

**You see:** the tab switcher: the same box without the `>`, listing the tabs with a preview of
the selected tab's screen on the right. With one tab it is a short list; it earns its key in
step 5.

The strip of tabs sits in the caption bar; the status bar at the bottom shows the active
tab's directory and, once there are agents, how many are working, waiting for you, or done.

## 2. Tabs that remember who they are

**Do:** `Ctrl+Shift+T` for a second tab. In it, `cd` somewhere. Press `Ctrl+Shift+R` and type
a name, say `notes`. Press `Ctrl+Shift+G` and type a group name, say `writing`.

**You see:** the tab's label is `notes`, the strip shows a `writing` header over it, and the
status bar follows the directory as you `cd` - a plain PowerShell profile announces it from
the first prompt without any setup on your part. Close the tab (`Ctrl+Shift+W`) and open the
same profile in the same directory again: the label comes back. Labels are remembered per
profile and directory, which is what you mean by them.

**Do:** `Ctrl+Shift+Z`.

**You see:** the tab you just closed is back, where it was - directory, label, group. Closed
tabs are kept in a history (step 10).

**Do:** `Ctrl+Shift+X`, then `Ctrl+Shift+A`.

**You see:** the tab's terminal moves into a window of its own and back. Nothing restarts and
the scrollback stays: the same terminal is re-parented. Tear-offs are for the agent you want
on the other monitor; they come back as tear-offs after a restart.

`Alt+1` ... `Alt+9` jump to a tab by position; dragging reorders; `Ctrl+PgUp` / `Ctrl+PgDn`
cycle; `Ctrl+Tab` walks the tabs most-recently-used first, like Windows Terminal.

## 3. Make it look like yours

**Do:** `Ctrl+Shift+P`, type `opacity`, choose **Setting: Terminal body opacity**.

**You see:** a picker with the values the setting takes, the current one marked. Pick `0.7`.
The status bar says *Reloaded Terminal body opacity = 0.7 ...* and the terminal's background
lets more of what is behind the window through - acrylic, blurred. Text stays opaque. The
default is 0.85; `1.0` is a solid body. (Windows paints acrylic for the active window only,
so an inactive OverShell shows a solid fallback behind the text, as Windows Terminal does.)

**Do:** `Ctrl+Shift+P`, **Change a setting...**

**You see:** every setting the palette knows - theme, accent, backdrop, the view at start,
two-line tabs, what comes back after a restart, notification sinks, the summon key - each
with its current value and a line on what it does. Choosing one opens its values. Try
**Theme** = `light` and back.

Every change is one line written into `settings.jsonc`; the file is written from the
commented defaults the first time, so the line lands among its explanation and the rest of
the file is untouched. **Open settings.jsonc** (same menu) opens it in your editor, and
anything you type there applies live when you save - a typo keeps the previous values and
is reported in the status bar, it never leaves you without a terminal. `Open keybindings.jsonc`
is the same for keys: Windows Terminal's shape, later entries win, `"unbound"` removes a default.

## 4. Run an agent and watch it

**Do:** in a tab, start your agent - `opencode` is the example here - and give it something
to do that takes a minute. Switch to another tab (`Alt+1`).

**You see:** the agent tab's dot pulses blue while it works. When it needs you - a permission,
a question - the dot turns amber with a ring, the status bar counts *needs you*, and a toast
appears over the terminal in the tab you are looking at (with **Allow** / **Deny** when the
request can be answered). When the agent finishes while you are not looking, the dot turns
green and stays green until you look: *done* is unread until seen.

**Do:** `Ctrl+Shift+E` on the agent tab.

**You see:** the evidence: which harness was recognised and how (the command line, a
process below the shell, the title), the state and why, which rule decided, the session id
when one is known, the processes below the shell. Nothing is a guess you cannot inspect.

**Do:** `Ctrl+Shift+P`, **Integrations: install opencode** (or `OverShell integrations install
opencode` in any shell), then restart the agent inside the tab.

**You see:** the states now come from the harness itself - exact, with the session id - and
the explain panel says *authority: integration*. The integration is one file inside OpenCode's
plugins folder that reports to OverShell and does nothing outside it; `uninstall` removes it.
The guide's [section 8](GUIDE.md#8-harness-integrations) has Copilot, Claude and Codex.

> *Needs you* never comes from silence. A quiet agent is working or done, never "probably
> waiting"; blocked is an explicit prompt on the screen, a notification, or the integration's
> word. That is why you can trust the amber dot.

## 5. Several agents: herd mode

**Do:** start a second agent in another tab (step 4), and a third if you like. Then press
`Ctrl+Shift+K` and let go.

**You see:** a hint bar under the terminal listing one key per action. Herd mode is one leader
chord, then single keys:

| Key | What |
|---|---|
| `b` / `B` | the next / previous tab **waiting for you** - the one that has waited longest first |
| `d` | the next tab that finished unseen |
| `j` / `k` | next / previous tab (these repeat: `Ctrl+Shift+K j j j` walks three tabs) |
| `1`-`9`, `l` | a tab by number; the last tab you were in |
| `n` `x` `r` `g` `e` | new, close, rename, group, explain |
| `i`, `p`, `/` | the inbox, the prompt bar, the tab switcher |
| `N` / `W` | spawn an agent in a new tab / in a fresh git worktree (step 9) |
| `L` / `D` | the herd log; what an agent changed while you were away (step 10) |
| `?` | the keys |

`Ctrl+Shift+J` outside herd mode is `b`: the tab that needs you, blocked first, the one that
has waited longest. The status bar's counters are clickable and do the same.

**Do:** switch to another application. When a tab needs you, press `` Win+` ``.

**You see:** OverShell comes to the front on that tab. Pressed while it is in front, it goes
away again. The key is a setting (**Setting: Summon key**).

## 6. Answer without visiting

**Do:** with an agent waiting on a permission, press `Ctrl+Shift+I`.

**You see:** the inbox: every tab waiting for you, oldest first, each with the line that asked
and the tab's screen beside it. `y` approves, `n` denies, without leaving the inbox; the item
leaves when the tab moves on and the selection moves to the next. `Enter` jumps to the tab,
`i` or `Tab` focuses a reply box for a free-text answer, `Shift+A` approves everything that
can be approved after a question.

The inbox says *how* each reply travels: through the integration when the plugin is connected
(OpenCode's own permission API, never typed into its dialog), or as the harness's own keys
typed into the tab when the rule file knows them, or typed as text otherwise. The toast's
**Allow** / **Deny** buttons go the same way.

Two more keys in herd mode: `m` mutes a tab (its dot still shows, nothing fires for it,
survives a restart) and `w` watches a tab for a pattern - a regular expression over its screen,
one notification when a row starts matching, again only after the row has gone. `BUILD
SUCCEEDED` in a plain shell is the classic.

## 7. Four views of the same tabs

**Do:** `Ctrl+Shift+2`, `Ctrl+Shift+3`, `Ctrl+Shift+4`, `Ctrl+Shift+1`.

**You see:**

- **Herd** - the terminal with a sidebar listing every tab grouped by project, the ones
  needing you first, with how long since they did anything. Press `Ctrl+Shift+2` again and
  a cursor appears in the list: `j`/`k` move it, `Enter` switches, `Esc` returns to the
  terminal.
- **Dashboard** - one card per tab: state, label, project and branch, the last rows of its
  screen in the tab's colours. Text read from the terminal about once a second, never a live
  terminal shrunk into a tile (that would reflow the agent's screen). Double-click a card to
  open it.
- **Zen** - the terminal alone.
- **Terminal** - back to the strip in the caption bar. `` Ctrl+Shift+` `` bounces between the
  last two views.

A view is a layout plus what the middle shows; the five-line layout files are yours to edit
([guide, section 6](GUIDE.md#6-views-and-layouts)).

## 8. Say it once, to several

**Do:** `Ctrl+Shift+Enter`.

**You see:** a prompt bar under the terminal with a target box: *Active tab*, *All agents*,
*Agents needing you*, *Every tab*. Type a line, `Enter` sends it to each target as one block
followed by Enter; `Shift+Enter` breaks a line; `Esc` hides the bar. `Ctrl+Shift+B` opens it
aimed at all agents.

**Do:** type `@2 run the tests` or `@api what is left?` or `#writing status`.

**You see:** the bar echoes where it will go (`-> api, web`) before you press Enter. `@3` is a
tab by number, `@name` by label (a prefix is enough), `#group` a group, `@blocked` /
`@working` / `@done` by state, `@agents` / `@all`. Reusable prompts go in `snippets.jsonc` and
appear on the bar's menu and as `Snippet: ...` commands.

## 9. Spawn agents with their first task

**Do:** `Ctrl+Shift+K` then `N`.

**You see:** three questions - which harness (the ones with a known launch command), which
directory, and the first prompt. A tab opens, the agent starts, and the prompt is delivered
once the agent is up and waiting for input (through its integration when the plugin listens,
else pasted). Not before: a starting TUI is silent in places, so OverShell waits for the
harness's own idle marker, not for silence.

**Do:** `Ctrl+Shift+K` then `W`, in a tab inside a git repository.

**You see:** the same, in a fresh **git worktree**: the branch name you give, checked out beside
the repository as `<repo>-<branch>`, so several agents work on one repository without touching
each other's files. A workspace file (step 10) can carry a `"prompt"` per tab for the same
effect at open.

## 10. Leave, and come back

**Do:** with agents working, close the window.

**You see:** *Close anyway - N agents working* or *Keep OverShell open*. Choose close. Start
OverShell again.

**You see:** the tabs in their directories, the windows where they were, the tear-offs as
tear-offs, the tab you were looking at in front - and each agent **resumed**: by session id
when the integration reported one (`opencode --session <id>`), else by the tool's own
"most recent session" form, for one tab per harness. After a crash or a power cut the same
happens, the status bar says *Restored N tabs from an interrupted session*, and each restored
tab shows the last thirty rows of what was on its screen, dimmed above the new prompt, so you
see where the agent was before you decide what to do.

**Do:** `Ctrl+Shift+K` then `L`; then `D` on a tab whose agent finished while you were away.

**You see:** the **herd log** - everything that happened this run, newest first: tabs opened
and closed, every state change with its reason, every call for attention; `Enter` goes to the
tab. The same is on disk as one JSON line per event under `%LOCALAPPDATA%\OverShell\logs\`.
And the files the agent **changed while you were away** - `git status` in its repository
compared with the last time you looked, the tab's item saying *2 changed*, the list naming
them; `Enter` opens one.

**Do:** `Ctrl+Shift+P`, **Save tabs as a workspace** (`workspace.save`), give it a name.

**You see:** a `Workspace: <name>` command that opens that set of tabs - profile, directory,
label, group, an optional command to type and prompt to deliver - next to the open ones, and
a `workspaces\<name>.jsonc` file in your configuration root that syncs with your dotfiles.
**Session history** (palette, or `overshell://history`) lists closed tabs and earlier
sessions to bring back; the taskbar jump list has the five most recent sessions and your
workspaces.

## 11. Let an agent oversee the herd

Everything above is also available to a program, on the loopback endpoint every tab carries
in its environment (`OVERSHELL_ENDPOINT`, `OVERSHELL_TOKEN`):

```powershell
curl.exe "$env:OVERSHELL_ENDPOINT/v1/tabs" -H "Authorization: Bearer $env:OVERSHELL_TOKEN"
curl.exe -X POST "$env:OVERSHELL_ENDPOINT/v1/tabs" -H "Authorization: Bearer $env:OVERSHELL_TOKEN" `
  -H "Content-Type: application/json" -d '{ "harness": "opencode", "cwd": "D:\\src\\api", "label": "api", "prompt": "Add tests for the auth module" }'
```

And as a Model Context Protocol server, for an agent that should run the herd itself:

```jsonc
// opencode.json (or any MCP host)
"mcp": { "overshell": { "type": "local", "command": ["overshell", "mcp"] } }
```

The tools are `overshell_list_tabs`, `overshell_read_screen`, `overshell_send`,
`overshell_reply`, `overshell_spawn`, `overshell_wait`, `overshell_close`. An agent in one tab
can then open three more, wait for them, read their screens and answer their prompts.
[Guide, section 7](GUIDE.md#the-control-api-and-overshell-mcp).

## 12. Where next

- The [guide](GUIDE.md): [settings](GUIDE.md#3-settingsjsonc), [keys](GUIDE.md#4-keys-and-the-command-palette),
  [agents and detection](GUIDE.md#7-agents-what-overshell-sees-and-how),
  [integrations](GUIDE.md#8-harness-integrations), [notifications](GUIDE.md#9-notifications),
  [sessions](GUIDE.md#11-sessions-restore-resume-and-history), [themes and skins](GUIDE.md#13-themes-and-skins),
  [the command line](GUIDE.md#14-command-line), [troubleshooting](GUIDE.md#15-diagnostics-and-troubleshooting).
- `OverShell settings path` prints where your configuration and state live and which
  variable chose them; `OverShell integrations status` what is installed; `Ctrl+Shift+E` why
  a tab is in the state it is in. `OVERSHELL_TRACE_AGENTS=1` writes a trace of every decision
  to `%TEMP%\overshell-agents.log`.
- [DESIGN.md](../DESIGN.md) is the why behind every one of these decisions, and what was
  measured before each was made.

The palette has this page under **Help: Tutorial**, and the guide under **Help: User guide**.
