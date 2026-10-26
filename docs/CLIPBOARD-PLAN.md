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
- [x] **4. Clipboard files/media.** All extensions, folders and multiple files; paths
  quoted for their target; explicit file delivery for clipboard-only media; clipboard
  failures handled; immutable transfer data shared by later input surfaces.
- [x] **5. Native drops.** Real OLE registration on the terminal HWND; drop data uses the
  same transfer path; copy semantics; correct target after detach/reattach; disposal.
- [x] **6. Prompt attachments.** Paste, drop and an all-files picker; visible removable
  attachments; attachment-only prompts; correct broadcast/address routing; failed sends
  retain the draft; history preserves attachments.
- [x] **7. Shift+Insert.** Default terminal paste; user overrides and WPF editing work.
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

### 4. Clipboard files and media

371 tests passed, including a real Windows PowerShell process receiving paths containing
spaces, apostrophes, dollar signs, backticks, shell punctuation, Unicode and a drive root.
File-list tests preserve order and snapshot the provider's array. Binary tests preserve
audio, video, PDF, AVIF and private-format bytes exactly; bitmap export is decoded back
as PNG. RTF/plain-text precedence, failed-write cleanup, device-name/path containment,
clipboard retry exhaustion and recovery after a failed provider are covered.

Normal image paste keeps the application-owned paste key. `clipboard.pasteFiles` provides
file delivery for every application, including clipboard-only images, audio, rich text,
text and raw binary formats. Exported paths remain under the state folder's `clipboard`.
Copy/paste requests are serialized on the UI context and failures produce a status message.

### Additional defect: private-desktop test isolation

Before running live checks, source inspection found that `SingleInstance` used the same
per-user/session named pipe on every desktop. A test could hand over to the user's live
instance. The launcher now gives each run a distinct inherited test identity; handoff
tests within that run still share a pipe. Normal application identity is unchanged.
The two focused identity/isolation tests pass.

### 5. Native drops

381 automated tests pass. Tests register/revoke real native HWND drop targets, negotiate
COPY/LINK without MOVE, exercise failures/disposal, and invoke the COM vtable with an
actual OLE data object to check the unmanaged callback signature and data marshaling.

`tools/Invoke-SelfTest.ps1 -Mode transfers` on a private desktop: **15 passed, 0 failed,
0 skipped**. The built app's real terminal and ConPTY received mixed files/folders with
literal special-character paths; source bytes survived. Drops reached the detached tab,
survived reattachment, exported bitmap/audio data and pasted plain text. Closing the tab
revoked its registration. This run used scratch configuration/state and did not replace
the system clipboard. WPF window fixtures initially reported ALREADYREGISTERED; changing
the fixture to the same native-window kind as the terminal verified actual registration.

### 6. Prompt attachments

388 automated tests pass. WPF control tests load the shipped styles and exercise routed
paste, selection replacement, attachment-only drafts, removal/history, rejection, and
Enter racing a pending paste or failed provider. No product startup or system clipboard
is used by these control tests.

`-Mode promptattachments` on a private desktop: **11 passed, 0 failed, 0 skipped**.
Checks include command routing into the focused prompt, visible attachment controls,
literal file delivery through ConPTY to two group members, exclusion of other tabs,
addressed attachment-only input, missing-file and partial-address rejection, and routed
bitmap paste into the real prompt bar.

Old defects fixed in this path: the bar cleared drafts before delivery was acknowledged;
history discarded the unsent draft; partly unmatched addresses could cause a partial
send. File formatting is prepared for all targets before delivering a broadcast.

### 7. Shift+Insert

388 automated tests pass, including default binding resolution and explicit user
unbinding. Live checks: `transfers` **17/17**, `promptattachments` **13/13**. Shift+Insert
reaches a real shell through the shared paste command and reaches the focused prompt
bar. Switching tabs while clipboard work is queued still delivers to the original tab.

### Completion audit: virtual files

The audit found that `FileGroupDescriptor`/`FileContents` was being treated as metadata
and silently skipped. Following the Windows Shell clipboard contract, the shared reader
now requests each virtual file by its native `FORMATETC.lindex`. Unicode/ANSI descriptor
lists, HGLOBAL, IStream and IStorage contents are supported. Compound documents use
`IStorage.CopyTo`; nested directories retain their hierarchy. Paths are published only
after a complete batch; failed batches are removed.

Tests use a real COM IDataObject provider, native streams/global memory/structured
storage, byte comparisons, Unicode names, empty files, nested directories, invalid and
truncated descriptors, and a provider failure midway through a batch.
The full suite passes **404 tests**. Export tests also cover Windows device-like names
with multiple extensions and superscript digits, without altering valid `COM0` names.

### Completion audit: HTML export fidelity

Five regression cases first reproduced clipboard transport headers leaking into exported
HTML files. The reader now follows the CF_HTML contract: UTF-8 byte offsets, CR/LF/CRLF
headers, context or fragment extraction, and fragment-marker fallback for stale offsets.
An invalid envelope is reported before creating a file. Standalone HTML is marked UTF-8;
normal paste still uses a supplied plain-text representation. All eight focused HTML
tests pass, including strings/streams, Unicode, case-insensitive format names and errors.
