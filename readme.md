# fux

A cross-platform terminal based XML editor. Browse, edit and validate XML from a TUI —
schema-aware, and undoable. Runs on linux, windows, and macos  — as a single self-contained
binary with no runtime to install.

fux reuses the battle-tested document engine from
[Microsoft XML Notepad](https://github.com/microsoft/XmlNotepad) (XSD validation,
schema cache, DOM loader, undo manager) behind a
[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui/) v2 front end. See
[Origins](#origins) for the full story.

## Install

On macOS or Linux, with [Homebrew](https://brew.sh):

```sh
brew install marcelinto/tap/fux
```

Otherwise download the archive for your platform from
[Releases](https://github.com/MarcelInTO/fux/releases), unpack it, and put `fux` on
your `PATH`. Nothing else is needed — the binary bundles its own runtime.

```sh
tar xzf fux-*-osx-arm64.tar.gz
sudo install -m 0755 fux-*/fux /usr/local/bin/fux
```

Builds are published for macOS (arm64/x64), Linux (x64/arm64) and Windows (x64).

The binaries are unsigned, so macOS quarantines anything downloaded through a
browser: if Gatekeeper refuses to launch it, download with `curl` instead or clear
the flag with `xattr -d com.apple.quarantine /usr/local/bin/fux`. Windows SmartScreen
warns for the same reason. Homebrew sidesteps this — it does not set the quarantine
flag — which is why it is listed first.

Each release carries a `SHA256SUMS` file, and the archives have build provenance you
can check with `gh attestation verify <archive> --repo MarcelInTO/fux`.

## Build

To build from source instead, you need the .NET 10 SDK. Everything else comes from
NuGet.

```sh
make                 # self-contained single-file binary for this host -> bin/fux
make install         # install to $(PREFIX)/bin (default /usr/local; may need sudo)
```

Targets macOS (arm64/x64) and Linux (arm64/x64); the host RID is auto-detected, or
pass `RID=...`. `make help` lists everything.

## Use

```sh
fux document.xml           # open the editor
fux --no-backup doc.xml    # edit without keeping backups
fux --dump document.xml    # headless structure dump
fux --validate doc.xml     # headless XSD validation (1 = errors, 3 = no schema)
fux --help                 # usage summary
fux --version              # version only
```

A release reports its tag (`fux 0.3.0`). A build from source reports the commit it
came from instead — `fux 0.3.0-8-g3738819`, with `-dirty` appended when the working
tree did not match any commit — so `--version` identifies the binary you are actually
running. Built outside a git checkout, it falls back to `fux 0.0.0-dev`.

Before overwriting a file, fux copies its previous contents next to it as
`<name>.<YYYYMMDD-HHMMSS>.bak` — `doc.xml` becomes `doc.xml.20260815-142530.bak`.
A save that changes nothing writes no backup, and old ones are left for you to
delete; `--no-backup` turns the whole thing off.

`.htm`, `.html`, `.json` and `.csv` files are converted to XML on open, following
XML Notepad's conversion conventions. An import never overwrites its own source:
saving writes XML, so `fux data.csv` will not silently turn `data.csv` into an XML
file.

## Schemas

fux validates against whatever the document's `xsi:schemaLocation` and
`xsi:noNamespaceSchemaLocation` hints point at — a URL as readily as a file sitting
next to the document. A schema published once on the web and referenced by every
document beats a copy beside each file, so remote hints are meant to be used.

Being briefly unable to reach one is then routine rather than exotic, and fux is built
for that. Remote schemas are fetched on a background thread, never on the one drawing
the screen, so a host that swallows packets — VPN down, captive portal, firewall — does
not freeze the editor; a fetch that fails is remembered for the session instead of
being retried after every keystroke; and one fetch is capped at five seconds, or
whatever `--schema-timeout=N` says.

When a schema cannot be fetched, or turns out not to be a schema, fux says so instead
of reporting a document nothing has checked as clean. On open you get a dialog —
**Retry**, **Continue**, or **Quit**, with Quit last so that a reflexive Enter cannot
leave you editing unvalidated — and for the rest of the session the validation pane
reads `Not validated: 1 schema unavailable` rather than `0 errors`. Headless,
`fux --validate` exits **3** for the same condition, so a CI job cannot mistake "the
schema host is down" for "the document is fine".

## Keys

| Key | Action |
| --- | --- |
| `F9` | menu |
| `F6` | cycle panes |
| `F2` / `Enter` | edit value in place (`F2` commit, `Esc` cancel) |
| `^C` / `^X` / `^V` | copy / cut / paste the selected node's value |
| `^R` | rename element / attribute / PI |
| `^N` | insert element, attribute, comment or PI |
| `^B` | insert a named block (see [Named blocks](#named-blocks)) |
| `Del` | delete node (asks first if anything goes with it) |
| `^Shift+←↑↓→` | nudge: reorder siblings, promote / demote |
| `^Z` / `^Y` | undo / redo |
| `^F`, `F3`, `Shift+F3` | find (text, regex or XPath), next, previous |
| `^O` | open |
| `^S` | save |
| `F5` | toggle light / dark theme |
| `^Q` | quit (asks first if the document has unsaved changes) |

`Esc` only ever backs out of something — a live edit, a dialog, an open menu. It
never quits, so the second `Esc` on the way out of an edit lands on nothing rather
than on the door. Quitting is `^Q`, and with unsaved changes it asks before it goes.

Save As lives in the File menu rather than on a chord: `Ctrl+Shift+S` is
indistinguishable from `Ctrl+S` in legacy terminal encoding, and an advertised
shortcut that quietly saves the wrong file is worse than no shortcut.

Copy is `^C`, not your terminal's copy. fux reads the mouse, so dragging in the
value pane makes a selection fux knows about and the terminal does not — `Cmd+C` /
`Ctrl+Shift+C` would copy the terminal's own selection, which is empty. `^C` copies
the highlight if there is one and the whole node value otherwise, to the real system
clipboard. To make a terminal selection instead (to grab part of the tree, say), hold
`Alt` / `Option` while dragging: that bypasses mouse reporting in most terminals.

## What it does

- **XSD validation** as you type, with an error pane and jump-to-node.
- **Schema-aware editing** over the reused XML Notepad engine.
- **Byte-preserving saves** — untouched parts of a document come back out exactly as
  they went in, rather than being re-indented wholesale.
- **Find** by text, regular expression or XPath.
- **Import** from HTML, JSON and CSV.
- **Named blocks** you define once and insert with `^B` — see
  [Named blocks](#named-blocks).
- **Full undo/redo** across every edit, including position-exact delete undo.
- **Solarized light/dark** theming, switchable at runtime.

Not included, by design: the HTML and XSLT preview panes, which need a browser
control that has no place in a terminal.

## Named blocks

Structures you type often can be named once in a config file and inserted with `^B`
(Edit ▸ Snippet…). The panel puts the position on one row and the whole list under
it, in the order the file lists them, so you can order the file to suit the work.
Once there are enough of them to be worth grouping — several schemas' worth, say —
put them in `<section>`s and the panel heads each group and indents what is under it:

```
┌──────────────┤Snippet at <block>├──────────────┐
│ ◉ Below  ○ Above  ○ Child                      │
│                                                │
│ Blocks                                         │
│   Paragraph                                    │
│   Argument                                     │
│   Footnote                                     │
│ Illustrations                                  │
│   Plate - own page                             │
│   Headpiece                                    │
│ …                                              │
│                          ⟦ Cancel ⟧  ⟦► OK ◄⟧  │
└────────────────────────────────────────────────┘
```

The list has focus when the panel opens and `Enter` on it commits, so inserting is
`^B`, arrow, `Enter` — no `Tab`, no button, no mouse. Typing a letter jumps to the
next entry starting with it. A heading is a label rather than a choice, so the
selection steps over it and every one of those gestures still lands on something
insertable. The list scrolls when the config is longer than the screen, and the panel
reopens on the snippet and position you used last, which is what makes a run of
twelve verse-lines bearable. Picking one inserts the element, its attributes and
everything under it as a single undoable edit.

The file is `~/.config/fux/snippets.xml` (`$XDG_CONFIG_HOME/fux/snippets.xml` if that
is set; `%APPDATA%\fux\snippets.xml` on Windows):

```xml
<snippets>
  <snippet name="Footnote">
    <block kind="footnote"/>
  </snippet>

  <section name="Illustrations">
    <snippet name="Plate - own page">
      <illustration role="plate" print-placement="own-page" src="" alt=""/>
    </snippet>
    <snippet name="Headpiece">
      <illustration role="headpiece" src="" alt=""/>
    </snippet>
  </section>
</snippets>
```

- Each `<snippet>` needs a `name` — that is the label in the dialog — and holds
  **exactly one element**, which may nest as deeply as you like and carry attributes
  and text. The indentation around it in the config is ignored; a block is re-indented
  to wherever it lands.
- Blocks appear in the order the file lists them. A `<section>` groups them and
  nothing more — it does not sort, so ordering the file still orders the panel.
- A `<section>` needs a `name`, and holds `<snippet>`s. Sections do not nest, and a
  snippet may still be written outside every section: it then appears where the file
  puts it, unindented and under no heading. A config with no sections at all draws
  exactly the list it always did.
- Getting a section wrong never costs the snippets inside it. An unnamed one leaves
  them ungrouped, a nested one folds them into its parent, and either way it is
  reported alongside the other skips.
- The file is re-read every time the dialog opens, so you can edit it in fux and see
  the change on the next `^B`.
- A prefix a block uses must be declared in the config, on `<snippets>` or on the
  block itself; the declaration travels with the block.
- A snippet that cannot load is skipped and reported under the group, and the rest of
  the file still loads. A config that will not parse at all never stops fux opening a
  document.
- With no config file, the dialog is exactly what it has always been — no extra group.

## Development

```sh
make run FILE=x      # build + run on a file
make drill FILE=x    # headless interactive self-test: key injection + render assertions
make smoke           # headless engine build + XSD-validation check
```

`--drill` is the main safety net: it drives the real TUI under a PTY, injecting keys
and asserting on rendered output. It is fixture-agnostic and runs in CI on Linux and
macOS. Every behavioural fix should come with a drill check that goes red when the
fix is removed.

The fixtures in `sandbox/testdata` are chosen to disagree with each other: `emp.xml`
and `emp-invalid.xml` for valid and invalid XML, `import.json` and `import.csv` for
the import paths (no whitespace of their own, retargeted on save), and
`robin-hood.xml` — a trimmed excerpt of a real book export — for a document shaped
like the ones fux is actually used on. That last one earns its place by being long:
~190 blocks, deep enough that the tree scrolls many times over. A clipped list and a
lost scroll position have both shipped past fixtures that fit on one screen. Drill
time is steeply superlinear in document size (68 KB ≈ 7 s, 122 KB ≈ 12 s, 170 KB ≈
28 s), so a fixture is a budget as well as a sample.

## Origins

fux is a derivative work of **Microsoft XML Notepad**, Copyright (c) Microsoft
Corporation, used under the MIT License. XML Notepad is a Windows GUI application;
fux keeps its document engine — the part that knows XML, XSD and schema
IntelliSense — and replaces the Windows Forms UI with a terminal one, so the same
capability works over SSH and on machines that will never run Windows.

Substantial portions of this repository are unmodified upstream source, and those
files retain their original copyright headers. Full attribution for the incorporated
code and for every third-party dependency is in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Microsoft does not endorse, sponsor or support fux. Please report fux issues here,
not to Microsoft or to the XML Notepad project.

## License

MIT — see [LICENSE](LICENSE). fux's own code is Copyright (c) 2026 Marcel Samek;
incorporated portions remain Copyright (c) Microsoft Corporation.
