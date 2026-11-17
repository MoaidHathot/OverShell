# User extensions

OverShell can host a resident command or load a .NET DLL implementing `IExtension`.
Both are opt-in entries under `extensions` in the existing configuration file,
`$XDG_CONFIG_HOME/overshell/settings.jsonc` (or the configured OverShell root).
The implementation can live alongside your dotfiles.

## Command extensions

```jsonc
{
  "extensions": {
    "my.status": {
      "enabled": true,
      "process": {
        "command": "pwsh",
        "args": ["-NoLogo", "-NoProfile", "-File", "{configDir}/extensions/status.ps1"]
      },
      "showIdle": false // Your extension's own settings.
    }
  }
}
```

A .NET file-based app uses the same host:

```jsonc
"process": {
  "command": "dotnet",
  "args": [
    "run", "--file", "{configDir}/extensions/status.cs",
    "--no-launch-profile", "--verbosity", "quiet", "--disable-build-servers"
  ]
}
```

Any executable with stdin/stdout works: PowerShell, Python, Node, a compiled utility,
or a C# file-based app. File-based C# apps require a .NET SDK (10 or later); their
runtime can differ from OverShell's. A DLL runs inside OverShell's runtime instead.

### Launch and lifecycle

`process` accepts:

| Setting | Default | Meaning |
|---|---|---|
| `command` | required | Executable name on PATH, or path to an executable |
| `args` | `[]` | Exact argument array; no shell quoting or evaluation |
| `workingDirectory` | configuration root | Working directory, relative to the configuration root when not absolute |
| `environment` | `{}` | Environment overrides; `null` removes an inherited variable |
| `restart` | `true` | Restart a command that exits, including exit code zero |
| `restartDelayMs` | `1000` | Initial retry delay, clamped to 250–60,000 ms; doubles to 60 seconds after short runs |
| `shutdownTimeoutMs` | `1500` | Grace period for shutdown, clamped to 100–5,000 ms |

Paths, arguments and environment values expand `{configDir}`, `{stateDir}`, and
`%ENVIRONMENT_VARIABLES%`. Relative executable paths containing a slash are rooted
in the configuration directory; bare executable names use PATH. Shell expressions
such as `$env:NAME`, pipes, and command substitution are not evaluated. To use a
shell, name its executable and pass its arguments explicitly.

The child also receives `OVERSHELL_EXTENSION_ID`, `OVERSHELL_CONFIG_DIR`,
`OVERSHELL_STATE_DIR`, and `OVERSHELL_HOST_PID`.

OverShell starts each enabled command once, asynchronously. Changes to ordinary
extension settings arrive in the feed without restarting the command. Changing
`process` stops its previous process before launching its replacement. Setting
`enabled: false` or removing the entry stops it; enabling it starts it again.
Editing a script's source alone requires restarting its extension or OverShell.

On a graceful stop the host sends `{"type":"shutdown","protocol":1}` and closes
stdin. The command should release external resources and exit on that message or
EOF. After the grace period its process tree is terminated. A Windows job object
also owns the process tree so an OverShell crash does not leave script runners
behind. Window shutdown gives all commands a shared three-second grace period.

### Input: complete snapshots

stdin is UTF-8, one JSON object per line. The first message is a complete snapshot;
subsequent snapshots replace it:

```json
{"type":"snapshot","protocol":1,"extensionId":"my.status","hostPid":1234,"settings":{"showIdle":false},"tabs":[{"id":"abc123","label":"Build API","harness":"opencode","isAgent":true,"state":"Blocked","unread":true,"needsAttention":true,"isRunning":true,"sessionId":"ses_example","request":{"id":"q1","kind":"question","title":"Which approach?"}}]}
```

`settings` is that extension's complete configuration section, including `process`.
`tabs` follows display order and includes plain shells, floating and detached tabs.
Each item has:

- Identity: `id`, `label`, `title`, `alias`, `hint`, `harness`, `isAgent`,
  `sessionId`, `sessionTitle`.
- State: `state` (`Unknown`, `Idle`, `Working`, `Blocked`, `Done`, `Error`,
  `Exited`), `unread`, `needsAttention`, `attentionSince`, `isRunning`,
  and `request` (`id`, `kind`, `title`, or null).
- Location/presentation: `cwd`, `project`, `group`, `isActive`, `isFocused`,
  `detached`, `floating`.

The feed describes OverShell's observations, including uncertainty. It does not
discover nested subagents. Consumers can group known session IDs, keep unknown
identities separate, and exclude `isAgent: false` themselves. An idle-looking
session with a non-null `request` still needs input; an unread completion should
not be hidden merely because idle sessions are hidden.

Changes are coalesced on OverShell's existing 500 ms heartbeat. Identical snapshots
are suppressed. There are no terminal screen reads, output/activity timestamps,
HTTP polls, or extra refresh timers. At most one unsent snapshot is retained, so a
slow command receives current state rather than a growing backlog. This is a
current-state feed, not a lossless transition log. Use the herd log for history.

Readers should ignore unknown fields. Incompatible changes require a new protocol
version. A restarted command always receives a fresh full snapshot.

### Output: commands and diagnostics

stdout also uses one JSON object per line. Optional messages are:

```json
{"type":"command","command":"tab.activate","arguments":{"target":{"id":"abc123"}}}
{"type":"command","command":"view.dashboard"}
{"type":"resync"}
{"type":"log","message":"Connected"}
```

Commands go through the public command registry on the UI thread, with the same
arguments as keybindings. Capture a target ID when the user clicks rather than
relying on whichever tab later becomes active. `resync` requests another snapshot.
There is no command-response RPC on this channel; use the existing control API
for operations requiring a response.

stderr is diagnostic text. Both stderr and `log` go to the agents trace
(`OVERSHELL_TRACE_AGENTS=1`, `%TEMP%/overshell-agents.log`). Non-protocol stdout is
ignored/reported there, so compiler progress is not interpreted as a command.
Output is bounded to 32 queued lines and 65,536 characters per line, drained on the heartbeat.

### Minimal PowerShell consumer

```powershell
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
while ($null -ne ($line = [Console]::ReadLine())) {
    $message = $line | ConvertFrom-Json
    if ($message.type -eq 'shutdown') { break }
    if ($message.type -ne 'snapshot' -or $message.protocol -ne 1) { continue }
    $agents = @($message.tabs | Where-Object isAgent)
    [Console]::Error.WriteLine("{0} agent views" -f $agents.Count)
    # Publish to your service here. Keep stdout for host protocol messages.
}
```

## DLL extensions

```jsonc
{
  "extensions": {
    "my.extension": {
      "enabled": true,
      "assembly": {
        "path": "{configDir}/extensions/my.extension/MyExtension.dll",
        "type": "MyExtensions.MyExtension"
      },
      "mySetting": 42
    }
  }
}
```

The named public type needs a parameterless constructor, must implement
`OverShell.Core.Extensibility.IExtension`, and its `Id` must match the configuration
key. Reference `OverShell.Core.dll` and, where needed, `OverShell.Config.dll` from
the host build; target `net10.0-windows`, matching OverShell. No OverShell.App
reference is required. Rebuild against the host if its public contracts change.

The existing `IShell` API supplies events, tabs, settings, commands, keybindings,
and host UI. `Initialize` and ordinary host events run on the UI thread. Keep I/O
asynchronous and use `IShell.Post` to return to the host thread. `Dispose` must
unsubscribe events and remove owned commands, key layers, and other contributions.

Each DLL has its own load context and dependency resolver; the two public API
assemblies are shared with the host. Adjacent dependencies and a `.deps.json` are
supported. Disabling/removing an entry disposes it; changing its assembly definition
loads a replacement. Replacing a DLL at the same path requires an OverShell restart;
unloading is cooperative and cannot reclaim references an extension left behind.

Use exactly one of `process` or `assembly` in an entry. Configured extensions cannot
replace built-in IDs. A load failure is traced and other extensions still load.

## Verification

`ConfiguredExtensionTests` exercises exact launch arguments, real child-process
snapshot/command exchange, idle suppression, settings-only updates, serialized
restart, disable/EOF cleanup, restart after an unexpected exit, blocked-stdin
shutdown, and DLL contract sharing and disposal.

A private-desktop probe also verified the real window's configured launch,
agent-report-to-snapshot delivery, stdout command activation, unread acknowledgement,
live settings, graceful cleanup, and publisher termination after killing the host.
The probe used a private named-pipe peer and did not interact with the live desktop.

```powershell
dotnet test tests/OverShell.Tests/OverShell.Tests.csproj -c Debug -p:Platform=x64 `
  --filter FullyQualifiedName~ConfiguredExtensionTests
```
