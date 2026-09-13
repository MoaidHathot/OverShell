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
Say "Ctrl+T new tab | Ctrl+Shift+W close | Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+PgUp/PgDn cycle | Alt+1..9 jump"
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
Case 54 "Ctrl+Shift+D: this tab moves into a window of its own; the shell keeps typing there (Tab completes, arrows recall history); Ctrl+Shift+W in that window closes THAT tab; closing the window with X brings the tab back instead."
Case 55 "In the tear-off, right-click pastes / copies a selection; Ctrl+click on a URL opens it; Ctrl+Shift+A brings the tab back to the main window with the same scrollback."
Case 56 "The sidebar and the dashboard still show the detached tab; clicking it there raises its window; its state keeps updating; it is saved in the session (and comes back attached)."
Case 57 "Set `"toast`": { `"enabled`": true } in settings.jsonc, let an agent finish in a background tab: a Windows toast appears (Action Center too); clicking it brings OverShell up on that tab."
Case 58 "OverShell integrations install codex, then run codex here and finish a turn: the tab shows Codex, 'done' when unseen, the explain panel lists the thread and 'codex resume <thread>'; the tab still shows 'working' on the next turn (detector)."
Case 59 "OverShell settings path: with XDG_CONFIG_HOME set, configuration resolves under it (…\overshell); settings init writes the two starter files there; editing settings.jsonc there changes the running window within two seconds."

Head "Distribution (13 - packaging verified locally, the published channels never)"
Case 60 "dotnet tool install -g OverShell; overshell: the prompt comes back within a second while the window stays; overshell version prints 'OverShell <version>+<sha>' and a .store path; overshell overshell://view/herd switches the running window's view instead of opening a second one."
Case 61 "winget install MoaidHathot.OverShell on a machine without .NET 10: the Desktop Runtime is installed first; overshell (and OverShell) start it from any prompt; Get-AuthenticodeSignature on the linked OverShell.exe is Valid; winget upgrade later keeps labels, session and the overshell:// registration working."
Case 62 "Unzip OverShell-<version>-win-x64-selfcontained.zip on a machine without any .NET runtime and run OverShell.exe: it starts; SHA256SUMS.txt matches Get-FileHash of the zip you downloaded."
Say ""
Say "Report: one line per number (ok / what you saw), plus %TEMP%\overshell-links.log, overshell-agents.log (OVERSHELL_TRACE_AGENTS=1) and overshell-crash.log if present."
