# OverShell 0.7.0

## Extensions from your configuration

User extensions can now live in your dotfiles and be hosted by OverShell. Configure
each one under `extensions` in `settings.jsonc`, using either:

- **`process`**: a resident command, including a PowerShell script, a C# file-based
  app, or a compiled executable.
- **`assembly`**: a .NET DLL implementing the existing `IExtension` contract.

For example:

```jsonc
{
  "extensions": {
    "my.status": {
      "enabled": true,
      "process": {
        "command": "pwsh",
        "args": ["-NoLogo", "-NoProfile", "-File", "{configDir}/extensions/status.ps1"]
      },
      "showIdle": false
    }
  }
}
```

Paths support `{configDir}`, `{stateDir}` and environment-variable expansion.
Arguments are passed as an exact array. Display preferences and integration logic
remain the extension's own settings and code.

## Change-driven command protocol

Commands receive complete, versioned UTF-8 JSON-line snapshots on stdin, covering
agent/session identity, state, unread results, pending questions and tab location.
Updates use the existing 500 ms heartbeat, suppress identical snapshots and retain
only the latest unsent snapshot. The feed reads no terminal screens and requires
no HTTP polling.

An extension can send command-registry requests on stdout, such as focusing a
specific tab after a status-bar click. stderr and structured log messages go to
the existing agents trace.

OverShell manages the command's lifecycle: live enable/disable, settings updates,
serialized restart after launch-setting changes, backoff after an unexpected exit,
and graceful shutdown followed by bounded process-tree cleanup. A Windows job
object also terminates hosted processes when OverShell crashes.

DLL extensions share the host's public API assemblies while loading their own
dependencies in a private load context. A failing extension is reported while the
other extensions continue loading.

See [User extensions](EXTENSIONS.md) for the full protocol, lifecycle, PowerShell
and file-based C# examples, and DLL configuration.

## Verification

- **601 C# tests** passed, including real child-process protocol exchange,
  settings/restart/disable behavior, blocked-stdin cleanup, and DLL loading.
- **16 OpenCode bridge tests** passed.
- A private-desktop integration probe passed **12 checks** covering a real
  OverShell window, agent reports, publisher updates, click activation, unread
  acknowledgement, live settings, graceful exit and host-crash cleanup.

## Build and local-feed upgrade

```powershell
.\build\Release.ps1 -Version 0.7.0
dotnet nuget push .\artifacts\release\dist\OverShell.0.7.0.nupkg --source <local-feed>
```

With that feed registered, close the running OverShell instance and launch:

```powershell
dnx OverShell@0.7.0
```

For a globally installed tool:

```powershell
dotnet tool update -g OverShell --version 0.7.0
```

The release script also produces framework-dependent and self-contained Windows
x64 ZIPs, winget manifests and SHA-256 checksums. Local packaging uses the binaries'
existing signatures; the hosted release workflow signs the OverShell binaries.
