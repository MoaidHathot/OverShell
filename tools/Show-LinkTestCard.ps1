<#
.SYNOPSIS
Prints the manual test card for OverShell's link handling (DESIGN.md §11.8).

.DESCRIPTION
Run this inside an OverShell tab, then work through the numbered lines with the mouse.
It only prints text — nothing here injects input (DESIGN.md §7.8).

For a log of every hover and click decision, start OverShell with
OVERSHELL_TRACE_LINKS=1 and read %TEMP%\overshell-links.log alongside.

Works in PowerShell 7 and Windows PowerShell 5.1.
#>
[CmdletBinding()]
param()

$e = [char]27
$width = 0
try { $width = $Host.UI.RawUI.WindowSize.Width } catch { }
if (-not $width -or $width -lt 40) { try { $width = [Console]::WindowWidth } catch { } }
if (-not $width -or $width -lt 40) { $width = 80 }

# Non-ASCII built from code points so this file reads the same in every host and encoding.
$cjk = -join ([char]0x65E5, [char]0x672C, [char]0x8A9E, [char]0x30C6, [char]0x30AD, [char]0x30B9, [char]0x30C8)   # 日本語テキスト
$rocket = [char]::ConvertFromUtf32(0x1F680)                                                                           # 🚀
$dash = [char]0x2014

function Say([string]$text) { [Console]::Out.Write($text + "`r`n") }
function Head([string]$text) { Say ""; Say ("${e}[1m" + $text + "${e}[0m") }
function Case([int]$n, [string]$text) { Say (("{0,2}. " -f $n) + $text) }
function Expect([string]$text) { Say ("    ${e}[90m" + $dash + " " + $text + "${e}[0m") }

Say ("${e}[1mOverShell link test card${e}[0m   grid width " + $width + " columns")
Say "Hover each link (no modifier): expect an underline and the URL in the status bar."
Say "Hold Ctrl: the pointer becomes a hand. Ctrl+click opens it in the default browser."
Say "A plain click on a link starts a selection, as it always has."

Head "Text URLs"
Case 1 "Plain:        https://example.com/plain/path?q=1&x=2"
Expect "underline exactly under the URL; status bar shows the same URL"
Case 2 "Punctuation:  (see https://example.com/paren)."
Expect "the trailing ')' and '.' are NOT part of the link"
Case 3 "Wikipedia:    https://en.wikipedia.org/wiki/Foo_(bar)"
Expect "the balanced ')' IS part of the link"
Case 4 "file URL:     file:///C:/Windows/System32/drivers/etc/hosts"
Expect "Ctrl+click opens the file with its default handler"
Case 5 "Two on one:   https://example.com/twin   and   https://example.com/twin"
Expect "hovering the second underlines the second, not the first"

Head "Colour and metrics"
Case 6 ("Red URL:      ${e}[31mhttps://example.com/red${e}[0m")
Expect "underline is red (the text's own colour), not the scheme foreground"
Case 7 ("Mixed colour: ${e}[31mhttps://exam${e}[32mple.com/mixed${e}[0m")
Expect "underline in the scheme foreground (documented fallback for mixed colour)"
Case 8 ("Rendered UL:  ${e}[4mhttps://example.com/reference${e}[0m")
Expect "the terminal already underlines this one; hovering should look unchanged (our line lands on its line)"

Head "OSC 8 hyperlinks (visible text differs from target)"
Case 9 ("OSC 8:        ${e}]8;;https://github.com/microsoft/terminal${e}\Windows Terminal on GitHub${e}]8;;${e}\")
Expect "hovering 'Windows Terminal on GitHub' shows github.com/microsoft/terminal"
Case 10 ("OSC 8 + SGR:  ${e}]8;;https://learn.microsoft.com/windows/terminal${e}\${e}[1;36mdocs${e}[0m${e}]8;;${e}\ (bold cyan text)")
Expect "hovering 'docs' shows learn.microsoft.com/windows/terminal"

Head "Wide glyphs before the link"
Case 11 ($cjk + " https://example.com/after-cjk")
Expect "underline starts under 'h', not shifted left by the number of CJK glyphs"
Case 12 ($rocket + $rocket + " https://example.com/after-emoji")
Expect "same: underline under the URL only"

Head "Wrapped lines"
$wrapped = "https://example.com/wrapped/" + (("segment/") * [Math]::Ceiling(($width + 30) / 8.0))
Case 13 "Wraps once (hover on either row):"
Say $wrapped
Expect "one URL across two rows; underline on both rows; same URL whichever row you hover"
$wrappedWide = $cjk + $cjk + " https://example.com/wide-wrapped/" + (("part/") * [Math]::Ceiling(($width + 20) / 5.0))
Case 14 "Wraps once, after wide glyphs:"
Say $wrappedWide
Expect "underline on both rows still aligned with the glyphs"

Head "Row ends"
$prefix = "Last column: "
$body = "https://example.com/ends-at-last-column/"
$pad = $width - $prefix.Length - $body.Length
if ($pad -lt 0) { $pad = 0 }
Case 15 "URL ending exactly in the last column:"
Say ($prefix + $body + ("z" * $pad))
Expect "link opens; underline may be computed rather than measured (documented)"
Case 16 "Very long (more than nine rows):"
Say ("https://example.com/too-long/" + ("x" * ($width * 9 + 20)))
Expect "no link: longer than the row walk (documented); nothing should break"

Head "Not links"
Case 17 "Plain text: Ctrl+click and drag HERE should start a selection a hair late, and open nothing."
Case 18 "Ctrl pressed and click within the same instant on plain text: same, selection, nothing opens."
Case 19 "Ctrl+double-click on any link above: opens ONCE, no word selection appears."
Case 20 "Hover a link, then press and release Ctrl: the hand appears and goes; underline and URL stay while you hover."
Case 21 "Hover a link, then move or resize the window: everything clears; hover again works."
Case 22 "In nvim with :set mouse=a, Ctrl+click a URL: nothing opens (the app owns the mouse)."
Case 23 "Hover a link, then move the pointer up onto the tab strip: underline and URL clear."
Case 24 "Hover a link on the LAST line, keep the mouse still, press Enter twice: the underline follows the text up (or clears) within half a second."
Case 25 "Sweep the pointer quickly across the whole card: no lag in the shell, underlines keep up."

Head "Shortcuts (never confirmed by a human either)"
Say "Ctrl+Shift+T new tab (Ctrl+T reaches the shell) | Ctrl+Shift+W close | Ctrl+Shift+D duplicate | Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+PgUp/PgDn cycle | Alt+1..9 jump"
Say "Ctrl+C: interrupt with no selection, copy with one | Ctrl+V / Ctrl+Shift+V paste | Ctrl+Shift+C copy"
Say "Right-click: copies a selection, else pastes | Tab completes in the shell ONCE (not twice) | arrows recall history"

Head "Herd overseer (P0 - verified in-process, never by a human)"
Case 26 "Ctrl+Shift+P: the command palette opens over the terminal and TAKES the keyboard; type 'new' and Enter opens a tab; Esc closes and the shell has the keyboard again."
Case 27 "Ctrl+Shift+Space: the tab switcher lists tabs (state dot, 'state · project · cwd', Alt+N hint); '@blocked' / '#repo' filter; Enter switches."
Case 28 "Ctrl+Shift+R: rename prompt; a name you type shows as the tab's first line; empty restores the automatic label; it survives a restart in the same directory."
Case 29 "Open a second tab, come back here, then run this and switch away for 10 s:"
Say "  `$e=[char]27; `$a=[char]7; Write-Host `"`${e}]0;OC | demo | card`${a}`"; Start-Sleep 2; Write-Host `"`${e}]9;4;1;50`${a}`"; Start-Sleep 4; Write-Host `"`${e}]9;4;4;100`${a}`""
Expect "this tab turns into 'OpenCode', its dot goes amber with a ring (needs you) then green (done) with a badge; toasts appear top-right of the other tab; the status bar counts them; the taskbar icon shows a badge"
Case 30 "Click a toast: this tab becomes active; 'done' clears; the badge and the count go."
Case 31 "Ctrl+Shift+J from another tab while this one is amber or green: jumps here."
Case 32 "Run 'opencode' (or 'copilot') here after 'OverShell integrations install opencode': the tab shows OpenCode · working while it thinks and 'needs you' on a permission prompt; quitting returns it to a shell within ~5 s."
Case 33 "Hover a tab: the tooltip explains the state ('progress state 1', 'screen matched /.../', 'opencode reported Working')."

Head "Views and layouts (P1 - verified in-process, never by a human)"
Case 34 "Ctrl+Shift+2 (Herd): a sidebar appears on the right listing every tab grouped by project, the ones needing you first; click a row to switch; the terminal keeps typing (no reflow, no lost focus after the click)."
Case 35 "Ctrl+Shift+3 (Dashboard): one card per tab with the last screen rows in the tab's colours; the card updates within ~1 s as output arrives; double-click or the open button goes to that tab in the terminal view; the shell has the keyboard again."
Case 36 "Ctrl+Shift+4 (Zen): tabs and status bar disappear, the caption bar gets slim and shows the title; Ctrl+Tab still cycles; Ctrl+Shift+1 comes back."
Case 37 "Ctrl+Shift+backtick toggles between the last two views."
Case 38 "Palette > 'Layout: left-rail': dots and glyphs on the left, tooltips on hover; 'Layout: bottom': strip under the terminal; 'Layout: top' restores."
Case 39 "Save %APPDATA%\OverShell\layouts\top.jsonc with { `"tabs`": { `"placement`": `"left`" } } while running: the strip moves within two seconds; delete the file: it moves back."
Case 40 "Save a keybindings.jsonc with { `"keys`": `"ctrl+alt+9`", `"command`": `"tab.new`" }: Ctrl+Alt+9 opens a tab without a restart."
Case 41 "The view switch (four icons right of the tabs) shows an amber/green badge with the number of tabs needing you; clicking Herd shows them first."
Case 42 "Right-click a tab, a sidebar row: rename / treat as agent / explain / close work."
Case 43 "A tab started in a git repository shows the branch in the status bar, the card header and the sidebar row."

Head "Depth (P2 - verified in-process, never by a human)"
Case 44 "Ctrl+Shift+Enter: the prompt bar appears under the terminal and takes the keyboard; type 'echo hi', Enter: it runs in this tab; Esc hides the bar and the shell has the keyboard again."
Case 45 "With two agent tabs open, Ctrl+Shift+B then a prompt: both agents receive it (each shows the text at its input)."
Case 46 "In the prompt bar, Up recalls the last prompt; Shift+Enter makes a new line; Ctrl+C/V edit the box, not the terminal."
Case 47 "Ctrl+Shift+G, type 'work', Enter: a 'work' header appears before this tab in the strip; another tab moved onto it (drag, or Alt+Shift+Left/Right) joins the group."
Case 48 "Drag a tab along the strip: it changes place as the pointer crosses its neighbours; no ghost, nothing else moves; release leaves it there."
Case 49 "Ctrl+Shift+E: the explain panel lists state, why, authority, harness, session, processes and the last transitions; Esc closes; the shell has the keyboard."
Case 50 "Close OverShell with two tabs, a label and a group; start it again: same tabs, label, group, active tab and view come back. If an agent was mid-session, its resume command is typed a moment after the prompt appears."
Case 51 "Enable the Palantir sink, let an agent finish in a background tab, click the Windows toast: OverShell comes to the front on that tab (no second window opens)."
Case 52 "Save %APPDATA%\OverShell\skins\mine.xaml with a ResourceDictionary setting Accent.Base to a green brush and put `"skin`": `"mine`" in settings.jsonc: accents turn green without a restart; remove it: they turn back."
Case 53 "OverShell integrations install claude: your ~/.claude/settings.json gains OverShell hook entries and keeps everything else; uninstall removes only those."

Head "Reach (P3 - verified in-process, never by a human)"
Case 54 "Ctrl+Shift+X: this tab moves into a window of its own; the shell keeps typing there (Tab completes, arrows recall history); Ctrl+Shift+W in that window closes THAT tab; closing the window with X brings the tab back instead."
Case 55 "In the tear-off, right-click pastes / copies a selection; Ctrl+click on a URL opens it; Ctrl+Shift+A brings the tab back to the main window with the same scrollback."
Case 56 "The sidebar and the dashboard still show the detached tab; clicking it there raises its window; its state keeps updating; it is saved in the session and comes back as a tear-off."
Case 57 "Set `"toast`": { `"enabled`": true } in settings.jsonc, let an agent finish in a background tab: a Windows toast appears (Action Center too); clicking it brings OverShell up on that tab."
Case 58 "OverShell integrations install codex, then run codex here and finish a turn: the tab shows Codex, 'done' when unseen, the explain panel lists the thread and 'codex resume <thread>'; the tab still shows 'working' on the next turn (detector)."
Case 59 "OverShell settings path: with XDG_CONFIG_HOME set, configuration resolves under it (…\overshell); settings init writes the two starter files there; editing settings.jsonc there changes the running window within two seconds."

Head "Distribution (13 - packaging verified locally, the published channels never)"
Case 60 "dotnet tool install -g OverShell; overshell: the prompt comes back within a second while the window stays; overshell version prints 'OverShell <version>+<sha>' and a .store path; overshell overshell://view/herd switches the running window's view instead of opening a second one."
Case 61 "winget install MoaidHathot.OverShell on a machine without .NET 10: the Desktop Runtime is installed first; overshell (and OverShell) start it from any prompt; Get-AuthenticodeSignature on the linked OverShell.exe is Valid; winget upgrade later keeps labels, session and the overshell:// registration working."
Case 62 "Unzip OverShell-<version>-win-x64-selfcontained.zip on a machine without any .NET runtime and run OverShell.exe: it starts; SHA256SUMS.txt matches Get-FileHash of the zip you downloaded."

Head "Resilience and history (P4 - 12.13; verified in-process, never by a human)"
Case 63 "Open three tabs in three directories, start opencode in one, move and resize the window, tear a tab off and move it; then kill OverShell from Task Manager (or pull the plug). Start it again: all three tabs in their directories, the windows where they were, the tear-off as a tear-off, the status bar says 'Restored 3 tabs from an interrupted session (saved HH:mm)', and OpenCode resumes - by id if the plugin is installed, else with opencode --continue."
Case 64 "Sign out of Windows with OverShell open and sign back in; start it: the tabs come back and the status bar says '...after Windows signed out or restarted'. With `"session`": { `"restartWithWindows`": true } and the Windows setting 'Automatically save my restartable apps...' on, it starts by itself after a restart."
Case 65 "Close a tab with Ctrl+Shift+W, then Ctrl+Shift+Z: it comes back in the same directory with its label, and its agent resumes if one was running. Ctrl+Shift+P > 'Session history': the closed tab is first, earlier sessions below; pick a session: its tabs are added next to the open ones."
Case 66 "OverShell --fresh: one default tab, nothing restored; Session history still lists the session that was not restored."
Case 67 "Agent tabs show a crisp icon (OpenCode's own blocky mark with its grey block, Copilot sparkle, Claude asterisk, Codex ring) next to the label in the strip, the list, the rail, the sidebar and the dashboard - no smudge; `"tabs`": { `"showHarnessGlyph`": false } hides it and `"twoLine`": false drops the state line, both without a restart."
Case 68 "A Windows Terminal profile whose command line is opencode.exe itself (not a shell): after a restart that tab is relaunched as `opencode.exe --continue` (or --session <id>) rather than having the command typed into the running TUI."

Head "Polish, honesty, depth (P5 - 12.14; verified in-process, never by a human)"
Case 69 "Ctrl+T in PowerShell swaps the two characters before the cursor (PSReadLine) - OverShell does not take it; Ctrl+Shift+T opens a tab; Ctrl+Shift+D opens a duplicate of this tab (same directory, same group, right next to it)."
Case 70 "cd into another directory in this PowerShell tab: within a few seconds the status bar, the sidebar row and the dashboard card show the new path (read from the prompt line) and the branch follows. OverShell integrations install shell, open a new tab, cd again: it follows at once; Ctrl+Shift+E names the source (prompt / process / shell)."
Case 71 "Start an agent, then close the window: a question 'Close OverShell?' with 'Close anyway - 1 agent working' and 'Keep OverShell open'; Keep leaves everything as it was. `"session`": { `"confirmCloseWithAgents`": false } closes without asking."
Case 72 "Kill OverShell from Task Manager and start it: each restored tab shows the last rows of its previous screen, dim and italic, above a rule, then the fresh prompt below. Kill it twice within a minute: the restore is held, the status bar says so, Session history still has the tabs."
Case 73 "Ctrl+Shift+P > 'Save tabs as a workspace', name it: a workspaces\<name>.jsonc appears in the configuration root and 'Workspace: <name>' is in the palette at once; run it from another tab: its tabs open next to the current one; edit the file (add a `"command`") and save: the next open runs it."
Case 74 "Open 25 tabs: every tab stays readable (never thinner than a label), the strip scrolls with the wheel and fades at the edges, the active tab is always scrolled into view, and the ... button at the end opens the tab switcher."
Case 75 "Right-click the OverShell taskbar button: New tab / Reopen closed tab / Session history, the recent sessions, your workspaces; each item does what it says in the running window (no second window)."
Case 76 "Windows Settings > Colours > Choose your mode: Light - the chrome, palette, sidebar, cards, prompt bar and every tear-off turn light within a second, text stays readable everywhere, the terminal body keeps its scheme; `"accent`": `"#C0392B`" recolours the accent live; back to `"system`" follows the Windows accent."
Case 77 "Ctrl+Shift+X: the tear-off has the same chrome as the main window - the tab's dot, icon, label and state line in its caption, minimize/maximize/close, drag and double-click to maximize work; the close button brings the tab home; it follows the theme."
Case 78 "Run 'foreach (`$i in 1..200) { `"row `$i`" }', then Ctrl+Shift+F, type 'row 7': the bar shows 'n of 21' (7, 70-79, 170-179), the lowest match on screen is selected and the others on screen are tinted; Enter walks up through older rows and scrolls when needed; Shift+Enter walks down; both wrap; Aa with 'ROW' finds nothing ('No matches'); Esc returns the keyboard to the shell with the selection kept; Ctrl+Shift+C copies it; Ctrl+Shift+F in a tear-off searches that tab."Say ""

Head "Terminal parity (12.15; verified in-process, never by a human)"
Case 79 "A plain PowerShell profile: the tab opens at the prompt (no version banner); cd anywhere - the status bar, sidebar and card follow at once; Ctrl+Shift+E says 'shell integration injected into the command line; directory announced by the shell'; your own prompt (starship, oh-my-posh) is intact; Tab completion and history work. A profile whose command line has -Command or -File is left alone and says 'not injected - the profile runs a command'."
Case 80 "Start OverShell from an overshell:// link in a browser that has been open since before you installed some tool (or add a folder to your user PATH while OverShell runs), open a new tab: the tool is found; Ctrl+Shift+E says 'env registry (N variables)'. With `"compatibility`": { `"reloadEnvironmentVariables`": false } a new tab inherits OverShell's own environment instead and says so."Say "Report: one line per number (ok / what you saw), plus %TEMP%\overshell-links.log, overshell-agents.log (OVERSHELL_TRACE_AGENTS=1) and overshell-crash.log if present."

Head "Keyboard control (P6 - 12.16; verified in-process, never by a human)"
Case 81 "Ctrl+Shift+K: a hint bar appears under the terminal listing the keys; j j j walks three tabs with the bar staying; n opens a tab and the bar goes; Ctrl+Shift+K then q: nothing reaches the shell (no 'q' typed) and the mode ends; Ctrl+Shift+K then Esc cancels; Ctrl+Shift+K then wait 3 s: it lapses."
Case 82 "Hold Ctrl, tap Tab: an overlay lists the tabs most recent first with the previous one selected and its screen beside it; tap again to move, Shift+Tab back; release Ctrl: you land there; a quick Ctrl+Tab bounces between the two last tabs; Esc while held cancels; Ctrl+PgDn still cycles in order."
Case 83 "With OverShell behind another window, press Win+`` (or the key extensions.summon.keys names): it comes to the front, on the tab that has waited longest if one is blocked; press it again while in front: it minimizes. If the status bar said the key is taken by another program at start, set another key in settings.jsonc and it registers live."
Case 84 "Ctrl+Shift+Enter, type '@2 Write-Host hi': the echo under the box reads '-> <tab 2's label>'; Enter: only tab 2 runs it. '#<group> ...' reaches every tab of the group; '@nobody ...' sends nothing and says so."
Case 85 "Ctrl+Shift+2 (Herd) then Ctrl+Shift+2 again: a cursor outline appears on the active tab's row in the sidebar; Down/Up or j/k move it without changing the tab; Enter switches to the row; Esc returns the keyboard to the terminal. Ctrl+Shift+3 twice does the same on the dashboard cards; Enter there opens the terminal view on that tab."
Case 86 "Two agents blocked, the older one first: Ctrl+Shift+J goes to the older; again to the newer; `"attention`": { `"order`": `"strip`" } makes it the next in tab order instead. `"extensions`": { `"herd.mode`": { `"enabled`": false } } removes the leader key and its bar after a reload."

Head "Triage (P7 - 12.17; verified in-process, never by a human)"
Case 87 "With the opencode integration installed, start opencode in a tab and make it ask a permission (a bash tool call with permissions set to ask); from another tab press Ctrl+Shift+I: the inbox lists it oldest first with the permission text and 'answers go through the integration'; press y: OpenCode's dialog is answered without you visiting the tab, the item leaves; type a reply + Enter on a question: it lands in OpenCode."
Case 88 "A Copilot CLI permission prompt ([y/N]) in a background tab: its in-window toast shows Allow / Deny; Deny types n into that tab. With `"toast`": { `"enabled`": true } the Windows toast has Allow / Deny / Open too; Allow answers; a stale toast from an earlier run only opens the tab."
Case 89 "Herd mode m on a noisy tab: a mute glyph appears on its item, its state keeps updating, no toast or sound fires for it; restart OverShell: still muted; m again unmutes."
Case 90 "Herd mode w, type 'BUILD SUCCEEDED': run a build in that tab; one notification when the line appears, none while it stays; clear the screen and build again: one more."
Case 91 "`"extensions`": { `"triage`": { `"autoAdvance`": true } }, two agents blocked, answer the one in front: you land on the other without a key."
Case 92 "Herd mode N: pick opencode, a repository folder, type a prompt; the new tab starts opencode, and once its composer is on screen the prompt appears in it and a turn runs (no typing from you). Try it while that tab is in the background too."
Case 93 "Herd mode W on a tab inside a git repository, branch 'agent/try': a folder <repo>-agent-try appears beside the repository on that branch, the agent starts there; W again with the same name: <repo>-agent-try-2."
Case 94 "A first prompt for a tab that never starts an agent (pick a shell, type a prompt): after 90 s the status bar says it was not delivered; nothing was typed into the shell."
Case 95 "With OverShell running: curl -H 'Authorization: Bearer <token>' <url>/v1/tabs using %LOCALAPPDATA%\OverShell\endpoint.json lists your tabs; POST /v1/tabs/<label>/input {`"text`":`"dir`"} runs dir in that tab; GET .../screen shows it; DELETE on the last tab is refused (409)."
Case 96 "Add { `"overshell`": { `"type`": `"local`", `"command`": [`"<path>\OverShell.exe`", `"mcp`"] } } to an MCP host (opencode.json mcp section); in that agent ask it to list the OverShell tabs, read one's screen, and spawn an opencode tab with a prompt: each tool call lands in the window; close OverShell and ask again: the tool reports it is not running, the host stays up."
Case 97 "`"endpoint`": { `"control`": false }: endpoint.json is not written, GET /v1/tabs/<id>/screen answers 403, the opencode integration still reports state."
Case 98 "After a few agent turns and a permission, herd mode L: the picker lists what happened newest first with times, tabs and reasons; Enter on an entry lands on that tab; %LOCALAPPDATA%\OverShell\logs\<stamp>.jsonl holds the same lines (one JSON object per line); restart twice: a new file per run, the old ones kept."
Case 99 "An agent working in a git repository in a background tab edits two files and finishes: its tab item reads '2 changed' (strip, list and sidebar); herd mode D on it lists the two with modified / untracked, Enter opens one in your editor; switch to the tab: the mark goes, D still lists them; let it run another turn that changes nothing: still two; a turn that edits one more after you looked: '1 changed'."
Case 100 "Close OverShell with a '2 changed' mark showing and start it again: the mark and the list are back on the restored tab; a tab whose directory is not in a repository never shows the mark."
Case 101 "settings: `"window`": { `"terminalOpacity`": 0.85 }, restart: the terminal body is translucent over the acrylic (move a bright window behind OverShell: it shows through the text area, text stays crisp and opaque); the margin around the terminal matches the body; the chrome bands look as before."
Case 102 "With the body translucent: resize the window several times, switch tabs, open a second tab and close it, tear a tab off and bring it back, maximize and restore: no ghost rectangles or stale strips anywhere in the terminal area, the translucency follows the tab into the tear-off window."
Case 103 "Edit terminalOpacity to 1.0 while running: the body goes opaque at once (status bar: 'terminal opacity 1.00'); back to 0.6: translucent again; set it from 1.0 to 0.85 in a run that started opaque: the status bar asks for a restart and nothing changes until then."
Case 104 "Click OverShell away so it is inactive: the body behind the text turns to the opaque fallback (as Windows Terminal does); click back: translucent. Switch 'Transparency effects' off in Windows Settings and restart: opaque, no error."
Case 105 "Select text with the mouse, Ctrl+Shift+C, paste into the shell; type with an IME or dead keys; use a screen reader or Accessibility Insights on the terminal: all unchanged with the translucent body (the input window is still there, only the pixels moved)."
Case 106 "tools\Invoke-SelfTest.ps1 -Mode 1 while you keep typing in another program: nothing appears, the focus never moves, no window of yours is rearranged; the summary ends with 'tally: ... desktop=private' and exit code 0. -Mode transparency -Visible: the window appears, the pixel checks run instead of being skipped."
Case 107 "Fresh machine (or OVERSHELL_STATE_DIR pointing at an empty folder): the first tab shows a dim welcome note above the prompt - the keys as bound, 'Help: Tutorial' - and the terminal body is translucent over the acrylic without any settings file; close and start again: no note the second time."
Case 108 "Ctrl+Shift+P, type 'opacity', Setting: Terminal body opacity: the picker lists 1.0 .. 0.5 with 0.85 marked current; pick 0.6: the body changes within a second and the status bar reads 'Reloaded Terminal body opacity = 0.6 ...'; type 0.73 into the box: 'Use 0.73' is offered first; type 2: nothing is offered."
Case 109 "Change a setting...: every row shows its current value and '(default)' until you change it; Theme = light applies live and the row then reads 'light' without '(default)'; Open settings.jsonc opens the file in your editor: the header says it was written by OverShell, the comments are intact, and only the lines you changed differ from the defaults."
Case 110 "Help: Tutorial and Help: User guide open the pages in the browser; Help: About puts the version, the executable and the two roots in the status bar; OverShell help in a shell prints both links."
Say ""
Say "Report: one line per number (ok / what you saw), plus %TEMP%\overshell-links.log, overshell-agents.log (OVERSHELL_TRACE_AGENTS=1) and overshell-crash.log if present."
