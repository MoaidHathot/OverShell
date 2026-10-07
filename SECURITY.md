# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub's private vulnerability
reporting for this repository (Security > Report a vulnerability), which reaches the maintainer
privately; you will get an acknowledgement within a week and a fix or a decision within 90 days,
sooner for anything exploitable.

In scope: OverShell itself (`src/`), its release and build scripts (`build/`), its workflows
(`.github/`), and the fork of Windows Terminal that builds the terminal control it ships
([MoaidHathot/terminal](https://github.com/MoaidHathot/terminal), branch `overshell` - its own
patches, scripts and workflows). A problem in Windows Terminal itself belongs to Microsoft:
see [microsoft/terminal's SECURITY.md](https://github.com/microsoft/terminal/blob/main/SECURITY.md).

## What OverShell exposes, so you know where to look

- A loopback HTTP endpoint (`127.0.0.1`, random port, per-run bearer token) that the agent
  integrations report to and that serves the control API (`endpoint.control`). The token is
  only ever in the environment of OverShell's own child processes and in
  `%LOCALAPPDATA%\OverShell\endpoint.json` for the lifetime of the window.
- `OverShell mcp`, an MCP server over stdio that acts on the running window through that endpoint.
- The `overshell://` protocol handler (HKCU), which focuses tabs, opens workspaces and - with a
  per-run nonce - answers a permission prompt.
- Shell integration it installs on request (`integrations install ...`): plugin and hook files in
  the agents' own configuration folders, each removable with `uninstall`.

## Supply chain

Releases are built by GitHub Actions from a tag, with actions pinned to commits. The NuGet
packages (`OverShell`, and `OverShell.Terminal.Wpf` from the fork) are published through nuget.org
Trusted Publishing: a short-lived key exchanged for the workflow's OIDC token against a policy that
names the repository and workflow - there is no long-lived publishing credential anywhere. Every
GitHub release carries `SHA256SUMS.txt`.