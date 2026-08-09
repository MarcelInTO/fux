# fux

A cross-platform terminal XML editor. Browse, edit and validate XML from a TUI —
schema-aware, undoable, and byte-preserving on save — as a single self-contained
binary with no runtime to install.

fux reuses the battle-tested document engine from
[Microsoft XML Notepad](https://github.com/microsoft/XmlNotepad) (XSD validation,
schema cache, DOM loader, undo manager) behind a
[Terminal.Gui](https://github.com/gui-cs/Terminal.Gui/) v2 front end. See
[Origins](#origins) for the full story.

## Build

Requires the .NET 10 SDK. Everything else comes from NuGet.

```sh
make                 # self-contained single-file binary for this host -> bin/fux
make install         # install to $(PREFIX)/bin (default /usr/local; may need sudo)
```

Targets macOS (arm64/x64) and Linux (arm64/x64); the host RID is auto-detected, or
pass `RID=...`. `make help` lists everything.

## Use

```sh
fux document.xml           # open the editor
fux --dump document.xml    # headless structure dump
fux --validate doc.xml     # headless XSD validation (exit 1 if there are errors)
fux --help                 # usage summary
fux --version              # version only
```

`.htm`, `.html`, `.json` and `.csv` files are converted to XML on open, following
XML Notepad's conversion conventions. An import never overwrites its own source:
saving writes XML, so `fux data.csv` will not silently turn `data.csv` into an XML
file.

## Keys

| Key | Action |
| --- | --- |
| `F9` | menu |
| `F6` | cycle panes |
| `F2` / `Enter` | edit value in place (`F2` commit, `Esc` cancel) |
| `^C` / `^X` / `^V` | copy / cut / paste the selected node's value |
| `^R` | rename element / attribute / PI |
| `^N` | insert element, attribute, comment or PI |
| `Del` | delete node |
| `^Shift+←↑↓→` | nudge: reorder siblings, promote / demote |
| `^Z` / `^Y` | undo / redo |
| `^F`, `F3`, `Shift+F3` | find (text, regex or XPath), next, previous |
| `^O` | open |
| `^S` | save |
| `F5` | toggle light / dark theme |
| `^Q` | quit |

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
- **Full undo/redo** across every edit, including position-exact delete undo.
- **Solarized light/dark** theming, switchable at runtime.

Not included, by design: the HTML and XSLT preview panes, which need a browser
control that has no place in a terminal.

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
