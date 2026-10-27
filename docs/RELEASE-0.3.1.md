# OverShell 0.3.1

## Highlights

- Select Windows Terminal **Stable**, **Preview**, an **unpackaged** installation, or a
  custom settings file through OverShell's `profiles` configuration.
- Choose OverShell's default profile independently, by name or GUID. Profile preferences
  and imported-file edits reload live; a failed import retains the last loaded profiles.
- **Ctrl+Shift+N** opens the searchable profile picker. **Ctrl+Shift+T** opens the default
  profile. Individual profiles also have commands that can be bound to custom shortcuts.
- The **+** and **profile arrow** stay accessible in crowded or small windows. The **…**
  menu offers **Switch tab…**, **New tab**, and **New tab with profile…**. Side-list and
  rail layouts keep creation controls below the scrolling list.
- Long tab labels and group headers share available space before the strip scrolls.

## Fixes

- Windows Terminal defaults specified by name now resolve correctly.
- Explicit missing or ambiguous profiles report an error consistently across picker,
  workspace, extension, protocol and control API launches.
- Saved profile names help restore tabs after switching channels or machines; duplicating
  a live tab preserves its original appearance after the profile source changes.
- Profile and command pickers preserve the originating detached window and keyboard focus.
  Newly created and immediately detached terminals receive native input focus correctly.
- Extension pickers, prompts and questions return accepted answers rather than cancellation.

## Upgrade and configure

Close the running OverShell instance, update your installation, then launch OverShell:

```powershell
dotnet tool update -g OverShell --version 0.3.1
```

For example, to use Preview with a named default profile, add this to OverShell's
`settings.jsonc` under `$XDG_CONFIG_HOME\overshell` or your configured root:

```jsonc
{
  "profiles": {
    "source": "preview",
    "defaultProfile": "PowerShell"
  }
}
```

The palette offers **Setting: Profile source**, **Setting: Profile settings file**, and
**Setting: Default profile**. `profiles.settingsPath` overrides `source`, and
`OVERSHELL_WT_SETTINGS` has highest priority. `source: "auto"` discovers Stable, Preview,
then unpackaged; `defaultProfile: null` follows the imported default.

See the [profile guide](GUIDE.md#31-profile-source-and-default) and
[tutorial](TUTORIAL.md#2-tabs-that-remember-who-they-are) for keyboard and mouse controls.
Upgrading from 0.2.x also requires the [0.3.0 OpenCode integration migration](RELEASE-0.3.0.md#upgrading-from-02x).

## Verification

The changes passed **487 C# tests**, a Release build with warnings treated as errors,
and focused live checks for profiles, overflow, settings, history, workspaces, keyboard
navigation and MRU switching. The final overflow/profile/polish runs passed all **117**
checks, including minimum-window-size hit testing and exact-profile launches through
both menus. Details are recorded in [DESIGN.md](../DESIGN.md#1228-profile-source-independent-defaults-and-keyboard-launches).
