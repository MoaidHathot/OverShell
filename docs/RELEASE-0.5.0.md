# OverShell 0.5.0

## Quick-access tabs

Give an open tab a temporary alias with **Set tab alias…**, then address it directly
from the tab picker, prompt bar, shortcuts, or control API. Assign local key sequences
or global hotkeys on the fly. Aliases and runtime shortcuts belong to the live tab:
hiding, moving and renaming preserve them; closing or restarting releases them.

Custom display names retain their existing restoration behavior. `tabs.aliasDisplay`
controls how aliases appear beside those names.

## Interactive floating terminals

Any tab can float, including the only tab and a detached terminal. Its existing terminal
surface moves, retaining the process and scrollback, while the tab stays listed.
Floating terminals have their own find and prompt bars; unsent prompt drafts survive
hide/show independently of the underlying work.

| Shortcut | Action |
|---|---|
| **Ctrl+Shift+Y** | Toggle the current terminal floating |
| **Ctrl+Alt+Shift+J** | Float a tab needing attention |
| **Shift+Enter** in the tab picker | Float the selected tab |
| **Ctrl+Shift+H** | Return to interrupted work |
| **Ctrl+Alt+Shift+T** | New scratch float |

**X hides the float; Ctrl+Shift+W terminates its tab.** Escape continues to reach the
terminal application. Notifications offer **Open floating**. Size, placement,
focus-loss dismissal and notification activation are configurable.

## Return to interrupted work

Attention jumps retain their original context: after `A → attention B → attention C`,
**Ctrl+Shift+H** returns to A, including its view and input focus. The same command
dismisses a current floating detour. **Ctrl+Shift+K, L** remains the last-used-tab switch.

## Scratch terminals and one-shot commands

Scratch tabs use explicit retention and exit policies. By default they inherit the
invoking directory, retain output on exit, and stay out of restoration and closed-tab
history. **Keep this tab** promotes useful work to a normal restorable tab.

**Run a command in a scratch float…** launches a real one-shot process. Exit policies
are `keep`, `closeOnSuccess`, and `closeAlways`. Exact executable/argument lists are
available through parameterized commands and the control API. Duplication and
restoration open the original profile rather than replaying a one-shot command.

## APIs and fixes

UI-free Core contracts now cover addressing, presentation, navigation and retention.
First-party commands, HTTP endpoints and twelve MCP tools consume these same services.
Parameterized commands capture their targets; asynchronous reads and waits retain the
initially resolved runtime tab ID.

The verification pass also fixed:

- Welcome/restored-screen text being overwritten by commands, through startup cursor
  synchronization with ConPTY.
- Stale screen text overriding an explicit completion signal.
- Focus-loss handling through owned pickers and delayed callbacks.
- Saved active indices when temporary tabs are excluded.
- The last remaining detached tab becoming inaccessible after its main-host neighbor closes.
- Control subresource DELETE requests accidentally closing a tab, and the control-API
  setting not applying live.

## Verification

- **556 C# tests** and **11 bridge tests** passed.
- **557 UI checks** passed across feature, restart, rendering-mode and regression suites.
- UI tests used private Windows desktops; one existing foreground dashboard pixel check
  was skipped.
- Release publication builds with warnings treated as errors.

See the [guide](GUIDE.md#51-aliases-floating-terminals-and-returning) and
[architecture/verification record](TAB-QUICK-ACCESS.md).

## Upgrade

Close the running OverShell instance, then launch the new release:

```powershell
dnx OverShell@0.5.0
```

For a globally installed tool:

```powershell
dotnet tool update -g OverShell --version 0.5.0
```

Release assets, the tool package and checksums are attached to the
[GitHub release](https://github.com/MoaidHathot/OverShell/releases/tag/v0.5.0).
