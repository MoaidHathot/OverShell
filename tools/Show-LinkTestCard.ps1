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
Say ""
Say "Report: one line per number (ok / what you saw), plus %TEMP%\overshell-links.log and overshell-crash.log if present."
