# OverShell

*Overseer Shell* — a Windows terminal shell that wraps the real Windows Terminal
rendering engine in your own chrome, tabs and layout.

The terminal emulation is Microsoft's, unmodified: VT/ANSI parsing, the text buffer, the
GPU-accelerated DirectWrite renderer, selection and scrollback all come from
`Microsoft.Terminal.Control.dll`. Everything around it — window chrome, tab strip,
layout, keybindings — is ours.

It reads your existing **Windows Terminal `settings.json`**: profiles, colour schemes and
fonts are shared, and there is no second configuration to maintain. OverShell never
writes to it.

## Build & run

```powershell
dotnet build OverShell.slnx -c Debug -p:Platform=x64
.\artifacts\bin\OverShell.App\debug_win-x64\OverShell.exe
```

Requires the .NET 10 SDK and Windows 10 19041+ (Windows 11 22621+ for the system
backdrop). x64 only.

| Environment variable | Values | Purpose |
|---|---|---|
| `OVERSHELL_BACKDROP` | `acrylic` (default), `mica`, `micaalt`, `none` | Backdrop behind the chrome |
| `OVERSHELL_TRACE_KEYS` | `1` | Trace keyboard chords to `%TEMP%\overshell-keys.log` |
| `OVERSHELL_TRACE_LINKS` | `1` | Trace link hover/click resolution to `%TEMP%\overshell-links.log` |

Crashes are always logged to `%TEMP%\overshell-crash.log`.

To exercise the link handling and shortcuts by hand, run `tools\Show-LinkTestCard.ps1`
inside a tab: it prints every case with what should happen.

## Status

Working: single terminal on launch, tabs with live titles, profile menu, theming from
your colour schemes, custom chrome with a Windows 11 backdrop, links — hover underlines
one (in its own colour, where the renderer would) and previews it in the status bar,
Ctrl+click opens it — and bracketed paste.

In progress: configurable tab strip placement, multiple tab groups, drag-and-drop.

Not possible on the default terminal surface: transparency of the terminal body — see
[DESIGN.md §7.6](DESIGN.md#76-transparency-is-structurally-impossible-here) for why, and
§11 for the surface abstraction that would allow a second, translucent one.

## Documentation

**[DESIGN.md](DESIGN.md)** — what it is, why it is built this way, the architecture
decision behind embedding Windows Terminal, the configuration pipeline, field notes on
every non-obvious trap encountered, and the open/closed item list.

## Licence

MIT — see [LICENSE](LICENSE).
