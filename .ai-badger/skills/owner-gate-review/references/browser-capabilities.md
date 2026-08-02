# What a `file://` review form can actually do — measured

Everything the save chain in `form-template.html` is built on. Read this when the save behaves
unexpectedly, when porting the form to another browser, or when someone proposes simplifying the
chain. The skill body carries the consequences; this file carries the evidence they rest on.

## The first pass — is a `file://` page capable at all

Measured on this machine: real Google **Chrome 150.0.7871.181** (macOS, `--headless=new`, fresh
profile), plus Playwright **Firefox 153** and **WebKit 605** builds, all on a `file://` page.

| Fact | Result |
|---|---|
| `window.isSecureContext` on `file://` | `true` in all three engines |
| `showSaveFilePicker` / `showDirectoryPicker` | **Chromium: `function`. Firefox: `undefined`. WebKit: `undefined`.** |
| Calling it without a user gesture | `SecurityError: Must be handling a user gesture to show a file picker.` — **must run inside the click handler** |
| `startIn` with a filesystem path | `TypeError: … not a valid enum value of type WellKnownDirectory` — **you cannot preset the picker to the HTML file's own folder** |
| `localStorage`, `indexedDB` on `file://` | both work |
| `a[download]`, `blob:` URLs | supported in all three |
| `navigator.clipboard.writeText` | rejects `NotAllowedError` without a gesture; resolves with one |
| **All `file://` pages share one origin (`file://`)** | a value set by `pageA.html` was read back by `subdir/pageB.html` |

**Unverified — say so rather than assert:**

| Claim | Check that would settle it |
|---|---|
| The picker actually opens and the bytes land on disk. Headless returns `AbortError` because there is no picker UI, so only the *gesture requirement* was proven, not the write. | Open the form in headful Chrome, click Save, `ls` the chosen path. |
| Safari.app behaves like Playwright's WebKit build. | Open the form in Safari, run `typeof window.showSaveFilePicker` in the Web Inspector console. |
| `a[download]` on `file://` saves silently vs. opening a Save-as dialog. | Depends on the browser's "ask where to save" setting; click it once and watch. |
| macOS save dialogs accept a pasted path via ⌘⇧G. | Standard macOS behaviour, not re-tested here; the form offers it as a hint, not a promise. |

## The second pass — remembering the directory

Prompted by reviewer feedback after the first real run: *"save feedback should use the same dir as
html file — selection is a noise."*

A `file://` page cannot discover its own folder, and `startIn` takes only the
`WellKnownDirectory` enum, so the folder cannot be preset. What it *can* do is take the directory
grant **once**, persist the `FileSystemDirectoryHandle` in IndexedDB, and write silently
thereafter. Handles survive reload; the *permission* does not, so it is re-requested inside the
click handler, where a gesture exists.

Measured on Chrome 150, `--headless=new`, served over `http://127.0.0.1` — Playwright blocks
`file:`:

| Fact | Result |
|---|---|
| `showDirectoryPicker` exposed | `function` |
| `indexedDB` open / `put` / `get` round-trip | works |
| Form still renders with the new chain — 13 verdict groups, 4 buttons, no JS errors | ✅ |

**Unverified — say so rather than assert:**

| Claim | Check that would settle it |
|---|---|
| A real `FileSystemDirectoryHandle` survives the IndexedDB round-trip. Only a plain object was round-tripped; handles are structured-cloneable *per spec*, not tested here. | Grant a folder in headful Chrome, reload, save again, confirm no dialog. |
| `queryPermission` returns `granted` on a later page load rather than re-prompting. | Same run: the second save should be silent. |
| The picker opens and bytes land on disk at all. Headless returns `AbortError` because there is no picker UI. | Open headful, click Save, `ls` the folder. |

## Why the template looks the way it does

Each of these is a consequence of a row above, not an independent design choice.

- **The save chain is remembered directory → one-time directory grant → `a[download]` → clipboard
  → always-visible textarea**, and the UI **names which link it used and where the file went**.
  Firefox and WebKit expose no picker at all, so the chain cannot assume one; and a save that
  silently lands somewhere unexpected is worse than no save.
- **An `AbortError` stops the chain.** It means the reviewer cancelled — falling through to a
  download they did not ask for is not a fallback, it is ignoring them.
- **The folder is asked for once, not once per save.** Point the one dialog at the folder holding
  the HTML and it never returns.
- **The IndexedDB key is `<storageKey>:dir` and the storage key must be unique per review.** Every
  `file://` page shares one origin, so a generic key loads another document's answers into this
  form. This is the whole reason the protocol has a "press Clear first" step.
- **No external hosts.** The artifact CSP blocks every one, and a `file://` page has no server. No
  CDN, no fonts, no fetch — everything inline.
