# Clipboard and file input parity

## Goal

Make copy/paste, file/media transfer, drag-and-drop and the prompt bar work for terminal
applications generally. File delivery accepts every extension, including documents,
audio, video, archives and images, plus directories and multiple selections. File-backed
content is delivered as paths; clipboard-only media can be materialized into files.

Work is sequential, with a tested commit for each item. Tests exercise data and input
routing without injecting system-wide keyboard or mouse events (DESIGN.md 7.8).

## Evidence before changes

- `MainWindow.Paste` only understands text, bitmap and registered PNG clipboard data.
  Explorer's `FileDrop` list is not read.
- The pinned `OverShell.Terminal.Wpf` 1.26.260930.3 package identifies source commit
  `f855d6979af4a8701ffdbf7d4fc85f33383cf680`. Its `HwndTerminal.cpp:288` handles native
  right-button down by copying or pasting. `ShortcutRouter.cs:270` independently handles
  right-button up. The native text paste writes directly to the connection.
- OverShell's right-click handler does not consult the tab's mouse-tracking mode.
- Neither the application nor the pinned WPF/native surface registers a file drop target.
- `PromptBar` is a text box; `SendPrompt` accepts and broadcasts only text.
- The embedded keybindings contain Ctrl+V and Ctrl+Shift+V, but no Shift+Insert.
- Baseline: solution builds with no warnings; 333 unit tests pass. The preceding image
  fix and its guide text are present in the working tree and have not been committed.

## Sequence and acceptance

- [x] **1. Image baseline.** Regression coverage for image/text precedence and native
  image paste; commit the preceding fix.
- [x] **2. Right-click ownership.** One copy or paste per gesture, matching release
  consumed, correct native HWND/tab, and one bracketed multiline paste.
- [x] **3. Mouse reporting.** Application-owned presses/releases pass through;
  Shift+right-click provides terminal copy/paste; teardown cannot leave stale ownership.
- [ ] **4. Clipboard files/media.** All extensions, folders and multiple files; paths
  quoted for their target; explicit file delivery for clipboard-only media; clipboard
  failures handled; immutable transfer data shared by later input surfaces.
- [ ] **5. Native drops.** Real OLE registration on the terminal HWND; drop data uses the
  same transfer path; copy semantics; correct target after detach/reattach; disposal.
- [ ] **6. Prompt attachments.** Paste, drop and an all-files picker; visible removable
  attachments; attachment-only prompts; correct broadcast/address routing; failed sends
  retain the draft; history preserves attachments.
- [ ] **7. Shift+Insert.** Default terminal paste; user overrides and WPF editing work.
- [ ] **8. Final audit.** Revisit every requirement; full tests, focused private-desktop
  in-process checks, build/diff checks, and fixes for defects uncovered along the way.

## Verification log

Each implementation commit records its focused checks here. The final audit records
what was exercised and any environment-dependent behavior requiring manual confirmation.

### 1. Image baseline

`dotnet test OverShell.slnx -c Debug -p:Platform=x64 --no-restore`: 338 passed.
Five new STA cases exercise real WPF data objects: bitmap with/without text, PNG-only,
Unicode multiline text and empty data. Image delivery emits exactly one unbracketed
paste key; text takes the text-paste path. The tests never replace the user's clipboard.

### 2. Right-click ownership

`dotnet test OverShell.slnx -c Debug -p:Platform=x64 --no-restore`: 344 passed.
Press/release regression tests cover exactly one paste, copy without a following paste,
foreign releases and cancellation. Native messages are intercepted on press and routed
by terminal HWND, so window chrome and another tab cannot receive the action. Multiline
and Unicode/control-character checks cover the text delivered to the terminal.

Additional defect fixed: `TerminalTab.Paste` previously read the asynchronously changing
bracketed-paste flag twice. Encoding now takes one snapshot, preventing an unmatched
opening/closing bracket if the application changes mode during a paste.

### 3. Mouse reporting

349 tests passed. New cases feed fragmented DECSET 1000/1002/1003 output, verify both
halves of application-owned gestures pass through, verify Shift overrides ownership,
and change modes between press and release. Ownership is remembered from the press.

Additional defect fixed: RIS (`ESC c`) did not clear the observed mouse, paste or focus
modes. The observer now resets them in stream order, including split output chunks.
The existing mutually exclusive mouse-mode behavior was checked against the pinned
native `TerminalInput::SetInputMode` rather than assuming modes were independent.
