# OverShell 0.3.0

## Highlights

- Agent tabs show session/task names prominently, with project and state underneath.
  `tabs.labelMode` selects session-first, project-first or harness-name labels live.
  Duplicate automatic names have stable visible identifiers. Custom names belong to
  individual tabs and survive restore/reopen without leaking into fresh tabs.
- The OpenCode V2 bridge tracks the selected session in each client, including renames
  and navigation. It supports permission, form and prompt replies. Form replies accept
  option labels, comma-separated or JSON multi-select values, and short yes/no answers.
- Clipboard and drag-and-drop support files, folders, multiple selections and arbitrary
  file types. Clipboard-only media and virtual files can be saved and delivered as paths.
- The prompt bar supports attachments, file selection, removal, history and broadcasts.
  Rejected drafts and pending clipboard data are preserved.
- Right-click copy/paste runs once, mouse-aware applications retain their events,
  Shift+right-click overrides mouse reporting, and Shift+Insert pastes.

## Upgrading from 0.2.x

1. Close the running OverShell instance and install or launch the 0.3.0 build from your
   chosen distribution source. Existing sessions and per-tab names are retained.
2. Run `OverShell integrations install opencode` with the new executable, then restart
   OpenCode clients. Installation replaces OverShell's owned V1 entrypoint with
   `plugins/overshell-v2/index.ts` and `tui.ts` in OpenCode's configuration directory.
3. If you previously configured a separate custom OverShell bridge in OpenCode's
   `cli.json`, select one bridge. To use the bundled bridge explicitly, its plugin
   directory is `./plugins/overshell-v2`. Keep other plugins and settings as they are.

Session-first labels are the default. To make the preference explicit in OverShell's
`settings.jsonc`:

```jsonc
{ "tabs": { "labelMode": "session" } }
```

The OpenCode bridge works with terminal-title output enabled or disabled. The older
folder-wide label cache is retained but no longer supplies names for newly opened tabs.

## Building and publishing to a local feed

```powershell
.\build\Release.ps1 -Version 0.3.0
dotnet nuget push .\artifacts\release\dist\OverShell.0.3.0.nupkg --source <local-feed>
```

The release script also produces framework-dependent and self-contained ZIPs, winget
manifest files and SHA-256 checksums. Local packaging uses the binaries' existing
signatures; the hosted release workflow signs the OverShell binaries when publishing.

See the [user guide](GUIDE.md) for all settings and input behavior, and
[DESIGN.md](../DESIGN.md#1227-session-aware-names-and-independent-tab-identity) for
implementation and verification details.
