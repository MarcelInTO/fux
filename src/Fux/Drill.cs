using System;
using System.Collections.Generic;
using System.Xml;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using XmlNotepad;

namespace Fux
{
    // Headless self-test for the interactive TUI — the v2 counterpart of the old PTY/ANSI
    // capture harness, minus the ANSI parsing: it builds the real UI, drives it with injected
    // key events (IKeyboard.RaiseKeyDownEvent), and asserts against the driver's in-process
    // output buffer (text + TrueColor attribute per cell). The driver still wants a real TTY,
    // so run it as `script -q /dev/null fux --drill file.xml`; the report goes to stderr and
    // ends with "DRILL: PASS" or "DRILL: FAIL".
    internal static class Drill
    {
        private static int _failures;

        public static int Run(string file)
        {
            // The document exactly as it arrived, captured before anything can save over it.
            // Section 13 needs this: several earlier sections ^S-save the scratch copy, so by
            // the time it runs, reading `file` back would compare fux's output against fux's
            // own output and pass no matter how badly the writer mangles a document.
            byte[] sourceBytes = file == null ? null : System.IO.File.ReadAllBytes(file);

            // Point the named-block config at a scratch directory before anything can read it,
            // so the drill neither depends on nor disturbs the config of whoever is running it.
            // Set for the whole run, not just §14b: the Insert dialog reads it on every open.
            var cfgDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fux-drill-config");
            System.IO.Directory.CreateDirectory(cfgDir);
            Environment.SetEnvironmentVariable("FUX_CONFIG_DIR", cfgDir);

            var ui = Program.BuildUi(file);
            var app = ui.App;
            app.Driver.SetScreenSize(100, 30);
            var token = app.Begin(ui.Top);
            app.LayoutAndDraw(true);

            // --- 1. Chrome: bordered panes render with their titles; summary on the error pane.
            // The tree pane title doubles as the document title (file basename).
            // The model's own path, not the source path: an import is retargeted to a sibling
            // .xml so ^S cannot overwrite the .csv/.json/.html it came from, and the pane title
            // follows the model. The scratch copy's underscore survives either way, which is
            // what keeps this a standing check on the hotkey-marker bug (see Main).
            var docTitle = file == null ? "Tree"
                : System.IO.Path.GetFileName(Program.Model.FileName ?? file);
            var screen = ScreenText(app);
            // Row 1 is the tree pane's top border, where its title is drawn (row 0 is the menu).
            // Searching the whole screen would be a weak oracle: the validation pane quotes the
            // file name in its diagnostics, so a mangled title still "appears" somewhere. The
            // scratch copy's name carries an underscore on purpose — see the note in Main.
            Check(ScreenRow(app, 1).Contains(docTitle), $"tree pane titled '{docTitle}'");
            Check(screen.Contains("Value"), "value pane title renders");
            Check(screen.Contains("Validation:") || screen.Contains("(no file loaded)"), "validation summary renders");
            Check(screen.Contains("┌") && screen.Contains("│") && screen.Contains("┘"), "pane borders render");

            // The terminal window's own name. Only the composition is asserted: the write is an
            // escape sequence straight to stdout, which the driver's output buffer never sees,
            // and the drill deliberately does not claim the title (see TerminalTitle). The file
            // name leads, the dirty marker matches the pane's, and a control character in a name
            // never reaches the terminal — that last one is the security-shaped case, since an
            // ESC would end the OSC string and run the rest of the name as escape sequences.
            Check(TerminalTitle.Compose("/tmp/book.frxml", false) == "book.frxml — fux",
                "window title names the document, then fux");
            Check(TerminalTitle.Compose("/tmp/book.frxml", true) == "book.frxml * — fux",
                "window title carries the dirty marker");
            Check(TerminalTitle.Compose(null, false) == "fux", "window title with no document is just fux");
            Check(TerminalTitle.Compose("/tmp/o\u001b[2Jd.xml", false) == "o[2Jd.xml — fux",
                "control characters are stripped out of the window title");

            // --- 1b. A save the user did not ask to reformat must not reformat anything. This
            // has to happen HERE, before any other section saves over the scratch copy: compared
            // against a file fux has already written, the check would pass no matter how badly
            // the writer mangles a document. SaveCopy writes through the same seam as ^S without
            // retargeting the model or clearing dirty, so it costs the rest of the run nothing.
            // Everything the old writer got wrong — a BOM appearing, LF becoming CRLF, the
            // declaration regenerated, indentation rebuilt, <b/> growing a space — is invisible
            // to a re-parse and loud in a diff, so bytes are the oracle.
            if (file != null && sourceBytes != null)
            {
                var ext1 = System.IO.Path.GetExtension(file).ToLowerInvariant();
                var probe1 = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file), "fux_drill_pristine.xml");
                Program.Model.SaveCopy(probe1);
                if (!Import.IsImport(ext1)) // an import deliberately saves as XML, not as itself
                    Check(SameBytes(sourceBytes, System.IO.File.ReadAllBytes(probe1)),
                        "an unedited save reproduces the source file byte for byte");
                Check(!Program.Model.Dirty, "SaveCopy leaves the model untouched");

                if (Import.IsImport(ext1))
                {
                    // Everything fux imports is written back as XML, so the model must point at
                    // a sibling .xml. Aimed at the source, ^S would replace the user's CSV or
                    // HTML with XML — losing the original outright. Stated in terms of what the
                    // path has to look like, NOT by calling Import.XmlPathFor: comparing the
                    // function against itself passes however the retarget is broken.
                    string target = Program.Model.FileName ?? "";
                    Check(target != file && target.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                          && System.IO.Path.GetDirectoryName(target) == System.IO.Path.GetDirectoryName(file),
                        $"an import retargets the model to a sibling .xml (got '{target}')");
                }

                // A reader that yields no whitespace leaves the document with no layout at all,
                // so the writer has to supply it — otherwise a CSV of a thousand rows saves as
                // one enormous line.
                if (Program.Model.PrettyPrint)
                    Check(System.IO.File.ReadAllText(probe1).Contains("\n  <"),
                        "a document with no layout of its own is written indented");
            }

            // --- 2. TrueColor + zero-leak: every cell is painted, and painted with a theme
            // background — nothing shows the terminal's own colors through. Node-kind and
            // severity accents (vim xml.vim mapping) are actually on screen.
            CheckCells(app);
            CheckAccents(app, ui.Errors.Count > 0);

            // --- 3. F6 cycles focus tree -> value -> errors -> tree (app-wide binding).
            Check(ui.Tree.HasFocus, "tree takes initial focus");
            app.Keyboard.RaiseKeyDownEvent(Key.F6);
            Check(ui.ValueView.HasFocus, "F6 moves focus to value pane");
            app.Keyboard.RaiseKeyDownEvent(Key.F6);
            Check(ui.ErrorList.HasFocus, "F6 moves focus to error pane");
            app.Keyboard.RaiseKeyDownEvent(Key.F6);
            Check(ui.Tree.HasFocus, "F6 cycles back to tree");

            // --- 4. Tree/value sync: moving the tree selection updates the value pane.
            var root = ui.Tree.SelectedObject;
            app.Keyboard.RaiseKeyDownEvent(Key.CursorDown);
            var selected = ui.Tree.SelectedObject;
            if (Check(selected != null && !ReferenceEquals(selected, root), "cursor-down moves tree selection"))
                Check(ui.ValueView.Text == (Program.GetValue(selected) ?? ""), "value pane reflects the new selection");

            // --- 5. Enter on an error row jumps to the offending node.
            var positioned = ui.Errors.FindIndex(it => it.Line > 0);
            if (positioned >= 0)
            {
                ui.ErrorList.SetFocus();
                ui.ErrorList.SelectedItem = positioned;
                app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                Check(ui.Tree.HasFocus, "Enter on error row focuses the tree");
                Check(ui.Tree.SelectedObject != null, "Enter on error row selects the offending node");
            }
            else
            {
                Console.Error.WriteLine("  (no positioned diagnostics; Enter-to-jump not exercised)");
            }

            // --- 6. F5 flips to Solarized light at runtime — same scheme definitions, flipped
            // base palette — and every painted cell follows; then back to dark.
            Check(Theme.IsDark, "starts in dark mode");
            app.Keyboard.RaiseKeyDownEvent(Key.F5);
            app.LayoutAndDraw(true);
            Check(!Theme.IsDark, "F5 switches to light mode");
            CheckCells(app);
            CheckAccents(app, ui.Errors.Count > 0);
            app.Keyboard.RaiseKeyDownEvent(Key.F5);
            app.LayoutAndDraw(true);
            Check(Theme.IsDark, "F5 returns to dark mode");

            // --- 7. Editing: F2 starts an in-place edit in the value pane, F2 commits it
            // through the UndoManager (DOM write + dirty + revalidate), ^Z/^Y walk history,
            // Esc abandons a live edit, and ^S persists to disk (Main redirected --drill to
            // a scratch copy, so saving here can't touch the caller's file).
            var editable = FindEditable(Program.Model.Document?.DocumentElement);
            if (editable != null)
            {
#pragma warning disable CS0618 // TextView: see the BuildUi note on the obsolete flag
                var tv = (Terminal.Gui.Views.TextView)ui.ValueView;
#pragma warning restore CS0618
                var before = EditNodeValue.GetNodeValue(editable);
                ui.Tree.EnsureVisible(editable);
                ui.Tree.SelectedObject = editable;
                ui.Tree.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                Check(ui.Editing, "F2 enters value-edit mode");
                Check(tv.HasFocus && !tv.ReadOnly, "value pane becomes the editable focus target");
                tv.Text = before + "-edited";
                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                Check(!ui.Editing && ui.Tree.HasFocus, "F2 commits, leaves edit mode, refocuses tree");
                Check(EditNodeValue.GetNodeValue(editable) == before + "-edited", "commit writes the DOM");
                Check(Program.Model.Dirty, "edit marks the model dirty");
                Check(ui.Tree.Title.EndsWith(" *"), "tree title shows the dirty marker");

                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(EditNodeValue.GetNodeValue(editable) == before, "^Z undoes the edit");
                Check(tv.Text == before, "value pane follows the undo");
                app.Keyboard.RaiseKeyDownEvent(Key.Y.WithCtrl);
                Check(EditNodeValue.GetNodeValue(editable) == before + "-edited", "^Y redoes the edit");

                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                tv.Text = "abandoned";
                app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                Check(!ui.Editing && ui.Tree.HasFocus, "Esc cancels the edit and refocuses the tree");
                Check(EditNodeValue.GetNodeValue(editable) == before + "-edited", "cancelled edit leaves the DOM alone");

                // #20: an edit is confined to the value pane, at the field's edges too. Up on
                // the first line was declined by the TextView, became focus navigation, and
                // walked the *tree* selection — whose sync handler then replaced the edit
                // buffer with the newly selected node's value, so the next commit wrote one
                // node's value into another. Three things have to hold at once, and the bug
                // broke all three: the caret still moves, it stops at the edge instead of
                // escaping, and what was typed is still there.
                //
                // A multi-line value on purpose. A single-line one is at both edges at once,
                // so it cannot tell "confined" from "swallowed" — the caret would never have
                // anywhere to go and a fix that ate every arrow would pass.
                var beforeArrows = ui.Tree.SelectedObject;
                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                tv.Text = "fuxrow0\nfuxrow1\nfuxrow2";
                var typed = tv.Text;
                for (int i = 0; i < 5; i++) app.Keyboard.RaiseKeyDownEvent(Key.CursorUp);
                Check(tv.CurrentRow == 0, $"Up parks the caret on the first line (row {tv.CurrentRow})");
                app.Keyboard.RaiseKeyDownEvent(Key.CursorDown);
                app.Keyboard.RaiseKeyDownEvent(Key.CursorDown);
                Check(tv.CurrentRow == 2, $"Down still moves the caret inside the field (row {tv.CurrentRow})");
                // Past the last line: more presses than the value has lines, which is the
                // reported gesture (holding an arrow to reach the end of a paragraph).
                for (int i = 0; i < 5; i++) app.Keyboard.RaiseKeyDownEvent(Key.CursorDown);
                Check(tv.CurrentRow == 2, $"...and stops at the last line rather than escaping (row {tv.CurrentRow})");
                Check(ReferenceEquals(ui.Tree.SelectedObject, beforeArrows),
                    "the tree selection cannot move while an edit is live");
                Check(tv.Text == typed, "the edit buffer still holds what was typed");
                Check(ui.Editing && tv.HasFocus, "the edit is still live, and still has the keyboard");
                // The net under the net. Containment above is meant to make a mid-edit
                // selection change unreachable through the UI, which leaves the guard on the
                // sync handler asserted by nothing — so drive one directly. It is the last
                // thing standing between "focus escaped by a route nobody thought of" and the
                // commit below writing the wrong node's value. The first Check validates the
                // detector: if the selection will not move, the second proves nothing.
                var otherNode = (XmlNode)Program.Model.Document?.DocumentElement;
                if (otherNode != null && !ReferenceEquals(otherNode, editable))
                {
                    ui.Tree.SelectedObject = otherNode;
                    if (Check(ReferenceEquals(ui.Tree.SelectedObject, otherNode),
                            "arrow drill: a selection change was forced mid-edit"))
                        Check(tv.Text == typed,
                            "a forced selection change still cannot touch the edit buffer");
                    ui.Tree.SelectedObject = editable;
                }
                // The corruption itself, pinned by name: commit after the arrows and the value
                // has to land on the node the edit started on, not on whatever the selection
                // wandered to. This is what "46613 became http://Employees" was.
                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                Check(EditNodeValue.GetNodeValue(editable) == typed,
                    "a commit after the arrows writes the typed text to the edited node");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(EditNodeValue.GetNodeValue(editable) == before + "-edited", "arrow drill: undone");

                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl);
                Check(!Program.Model.Dirty, "^S saves and clears dirty");
                Check(!ui.Tree.Title.EndsWith(" *"), "dirty marker clears after save");
                // Read back the path the model actually writes to, which for an import is the
                // retargeted .xml rather than the source it was coerced from.
                Check(System.IO.File.ReadAllText(Program.Model.FileName).Contains(before + "-edited"),
                    "saved file contains the edited value");

                // Enter on the tree is the discoverable alias for F2's start-edit.
                app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                Check(ui.Editing, "Enter on the tree starts an edit");
                app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                Check(!ui.Editing, "Esc closes it again");

                // --- 7a. #22: the menu stops offering what it will not do, and Save means
                // "commit this and write it" instead of nothing at all.
                //
                // Named rows, not counts alone: the report was thirteen live-looking rows that
                // did nothing, and a check that only counted disabled rows would pass with the
                // wrong ones disabled. Snippet… is named because it is the row the report came
                // in about; Cut is named because disabling it would be the same bug pointing
                // the other way — it is one of the few that still means something mid-edit.
                var inertSnippet = Array.Find(ui.EditInertMenu, m => m.Title == "_Snippet…");
                var liveCut = Array.Find(ui.EditLiveMenu, m => m.Title == "Cu_t");
                Check(inertSnippet != null && liveCut != null,
                    $"menu drill: found the named rows in {ui.EditInertMenu.Length} inert / {ui.EditLiveMenu.Length} live");
                Check(Array.TrueForAll(ui.EditInertMenu, m => m.Enabled),
                    "outside an edit, every row that an edit would disable is enabled");

                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                Check(Array.TrueForAll(ui.EditInertMenu, m => !m.Enabled),
                    "an edit disables every row that cannot run during one");
                Check(inertSnippet != null && !inertSnippet.Enabled, "...Edit ▸ Snippet… among them");
                Check(Array.TrueForAll(ui.EditLiveMenu, m => m.Enabled),
                    "...and leaves enabled the rows that still mean something");
                Check(liveCut != null && liveCut.Enabled, "...Edit ▸ Cut among those");
                // Both ways out of an edit restore the menu, not just the one that commits.
                app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                Check(Array.TrueForAll(ui.EditInertMenu, m => m.Enabled), "Esc re-enables them");
                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                Check(Array.TrueForAll(ui.EditInertMenu, m => m.Enabled), "...and so does a commit");

                // Save mid-edit. The oracle is the file, not the dirty flag: the old behaviour
                // returned silently, which left no save, no error, no cleared marker, and every
                // reason to believe the paragraph was on disk.
                app.Keyboard.RaiseKeyDownEvent(Key.F2);
                tv.Text = "fuxsavedmidedit";
                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl);
                Check(!ui.Editing, "^S during an edit commits it");
                Check(EditNodeValue.GetNodeValue(editable) == "fuxsavedmidedit", "...and writes it to the DOM");
                Check(!Program.Model.Dirty, "...and clears dirty");
                Check(System.IO.File.ReadAllText(Program.Model.FileName).Contains("fuxsavedmidedit"),
                    "...and the typed value is in the file on disk");
                // Put the document back the way §7 left it, so later sections see what they expect.
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(EditNodeValue.GetNodeValue(editable) == before + "-edited", "menu drill: undone");
                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl);
                Check(!Program.Model.Dirty, "menu drill: saved back clean");
            }
            else
            {
                Console.Error.WriteLine("  (no editable node; editing drill not exercised)");
            }

            // --- 8. Rename: TryRename drives the same command path as the ^R dialog (the
            // modal itself can't run headless — a nested Run would block the injector).
            // Renames swap node instances, so these pin selection identity, attribute
            // order, namespace auto-generation and the root-rebind path; each case undoes
            // itself so the block ends where it started (then saves to come out clean).
            var doc = Program.Model.Document;
            var owner = doc?.SelectSingleNode("//*[@title]") as XmlElement;
            if (owner != null)
            {
                var attr = owner.GetAttributeNode("title");
                var attrValue = attr.Value; // fixture-agnostic: --drill may run on any document
                int pos = IndexOfAttr(owner, attr);
                Check(Program.TryRename(ui, attr, "fuxrenamed") == null, "attribute rename accepted");
                var renamed = owner.GetAttributeNode("fuxrenamed");
                Check(renamed != null && renamed.Value == attrValue, "attribute keeps its value");
                Check(IndexOfAttr(owner, renamed) == pos, "attribute keeps its position");
                Check(ReferenceEquals(ui.Tree.SelectedObject, renamed), "tree selects the new attribute instance");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                var back = owner.GetAttributeNode("title");
                Check(ReferenceEquals(back, attr) && back.Value == attrValue && IndexOfAttr(owner, back) == pos,
                    "undo restores the original attribute in place");
                app.Keyboard.RaiseKeyDownEvent(Key.Y.WithCtrl);
                Check(owner.GetAttributeNode("fuxrenamed") != null, "redo re-applies the rename");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
            }
            else
            {
                Console.Error.WriteLine("  (no @title attribute; attribute rename not exercised)");
            }

            var city = doc?.SelectSingleNode("//*[local-name()='City']") as XmlElement;
            if (city != null)
            {
                var parent = city.ParentNode;
                var text = city.InnerText; // MoveContent empties the detached original
                Check(Program.TryRename(ui, city, "Town") == null, "element rename accepted");
                var town = ui.Tree.SelectedObject as XmlElement;
                Check(town != null && town.LocalName == "Town" && !ReferenceEquals(town, city),
                    "tree selects the new element instance");
                Check(town != null && town.InnerText == text && town.NamespaceURI == city.NamespaceURI,
                    "element keeps content and namespace");
                app.LayoutAndDraw(true);
                Check(ScreenText(app).Contains("<Town>"), "tree repaints the renamed element");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(city.ParentNode != null && ReferenceEquals(parent, city.ParentNode) && city.InnerText == text,
                    "undo restores the original element with its content");

                var street = doc.SelectSingleNode("//*[local-name()='Street']") as XmlElement;
                Check(Program.TryRename(ui, street, "z:Street") == null, "prefixed rename accepted");
                var zs = ui.Tree.SelectedObject as XmlElement;
                Check(zs != null && zs.Prefix == "z" && zs.NamespaceURI == "uri:1",
                    "unknown prefix gets a generated namespace");
                Check(zs != null && zs.GetAttribute("xmlns:z") == "uri:1", "xmlns:z declaration rides the element");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(street.ParentNode != null && street.Prefix == "" && street.GetAttribute("xmlns:z") == "",
                    "undo removes the generated declaration with the element");
            }
            else
            {
                Console.Error.WriteLine("  (no City element; element rename not exercised)");
            }

            if (doc?.DocumentElement != null)
            {
                var top2 = ui.Undo.Peek();
                Check(Program.TryRename(ui, doc.DocumentElement, "not a name") != null,
                    "invalid name is rejected with a message");
                Check(ReferenceEquals(ui.Undo.Peek(), top2), "rejected rename pushes nothing");

                var oldRoot = doc.DocumentElement;
                Check(Program.TryRename(ui, oldRoot, "Staff") == null, "root rename accepted");
                Check(doc.DocumentElement.LocalName == "Staff" &&
                      ReferenceEquals(ui.Tree.SelectedObject, doc.DocumentElement), "tree rebinds to the new root");
                app.LayoutAndDraw(true);
                Check(ScreenText(app).Contains("<Staff>"), "tree repaints the renamed root");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(ReferenceEquals(doc.DocumentElement, oldRoot), "undo restores the original root");

                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl); // undo churn marks dirty; end clean
                Check(!Program.Model.Dirty, "save clears dirty after the rename drill");
            }

            // --- 9. Insert & delete: TryInsert/TryDelete drive the same command paths as
            // the ^N dialog and the Del key. Fixture-agnostic: anchors come from whatever
            // document is loaded. Each case undoes itself; a final save comes out clean.
            var root9 = doc?.DocumentElement;
            if (root9 != null)
            {
                int kids = root9.ChildNodes.Count;
                Check(Program.TryInsert(ui, root9, InsertKind.Element, InsertPos.Child, "fuxnew") == null,
                    "element child insert accepted");
                var fresh = LastShown(root9) as XmlElement;
                // compare against the LIVE default namespace: an earlier drill section may
                // have edited the xmlns attribute value (baked element URIs don't follow)
                Check(fresh != null && fresh.LocalName == "fuxnew" && fresh.NamespaceURI == root9.GetNamespaceOfPrefix(""),
                    "new element lands last, inheriting the in-scope default namespace");
                Check(fresh != null && ReferenceEquals(ui.Tree.SelectedObject, fresh), "tree selects the inserted element");
                app.LayoutAndDraw(true);
                Check(ScreenText(app).Contains("<fuxnew>"), "tree repaints the inserted element");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(root9.ChildNodes.Count == kids && fresh.ParentNode == null, "undo removes the inserted element");
                app.Keyboard.RaiseKeyDownEvent(Key.Y.WithCtrl);
                Check(ReferenceEquals(LastShown(root9), fresh), "redo restores it at the same spot");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);

                // sibling position: insert before the first element child
                XmlElement firstChild = null;
                foreach (XmlNode c in root9.ChildNodes)
                    if (c is XmlElement fe) { firstChild = fe; break; }
                if (firstChild != null)
                {
                    Check(Program.TryInsert(ui, firstChild, InsertKind.Element, InsertPos.Before, "fuxbefore") == null,
                        "sibling insert accepted");
                    var sib = ui.Tree.SelectedObject as XmlElement;
                    Check(sib != null && ReferenceEquals(NextShown(sib), firstChild), "Before lands directly before the anchor");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                }

                Check(Program.TryInsert(ui, root9, InsertKind.Attribute, InsertPos.Child, "fuxattr") == null,
                    "attribute insert accepted");
                Check(root9.HasAttribute("fuxattr"), "attribute exists on the element");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(!root9.HasAttribute("fuxattr"), "undo removes the attribute");

                Check(Program.TryInsert(ui, root9, InsertKind.Comment, InsertPos.Child, null) == null,
                    "comment insert accepted (no name needed)");
                Check(LastShown(root9) is XmlComment, "comment lands as last child");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);

                Check(Program.TryInsert(ui, root9, InsertKind.Pi, InsertPos.Child, "fuxpi") == null,
                    "processing instruction insert accepted");
                Check(LastShown(root9) is XmlProcessingInstruction lastPi && lastPi.Target == "fuxpi",
                    "PI lands as last child with its target");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);

                // rejections: each returns a message and pushes nothing
                var top9 = ui.Undo.Peek();
                Check(Program.TryInsert(ui, root9, InsertKind.Element, InsertPos.Before, "x") != null,
                    "sibling of the document root is rejected");
                Check(Program.TryInsert(ui, root9, InsertKind.Element, InsertPos.Child, "not a name") != null,
                    "invalid element name is rejected");
                Check(Program.TryInsert(ui, root9, InsertKind.Element, InsertPos.Child, "") != null,
                    "empty element name is rejected");
                if (root9.Attributes.Count > 0)
                    Check(Program.TryInsert(ui, root9, InsertKind.Attribute, InsertPos.Child, root9.Attributes[0].Name) != null,
                        "duplicate attribute name is rejected");
                Check(Program.TryDelete(ui, root9) != null, "deleting the document root is rejected");
                Check(ReferenceEquals(ui.Undo.Peek(), top9), "rejected operations push nothing");

                // delete an element via the Del key path (central handler), undo restores in place
                if (firstChild != null)
                {
                    var parent9 = firstChild.ParentNode;
                    var successor = NextShown(firstChild);
                    // Where the delete should land, derived from this fixture rather than fixed:
                    // the CI documents differ on whether the root's first element has siblings,
                    // and an assertion of "the container" only held for the one that does not.
                    // The rule itself is what is under test; §13d exercises all three arms of it
                    // on a document built for the purpose.
                    var landing9 = successor ?? PrevShown(firstChild) ?? parent9;
                    var landing9What = successor != null ? "the next sibling"
                        : ReferenceEquals(landing9, parent9) ? "the container, nothing left in the band"
                        : "the previous sibling";
                    ui.Tree.SelectedObject = firstChild;
                    ui.Tree.SetFocus();

                    // #21: Del asks first when the node takes others with it, so the keypress
                    // opens a modal whose nested Run would block the injector this harness
                    // depends on. That loop is a real main loop, so a timeout armed beforehand
                    // fires inside it and can answer — the §14a recipe. Whether it asks at all
                    // is derived from the node, not assumed: the CI fixtures differ on whether
                    // the root's first element has anything under it.
                    bool asks9 = System.Linq.Enumerable.Any(Program.GetChildren(firstChild));
                    var top9b = ui.Undo.Peek();

                    // First, cancel it. Nothing may move: not the document, not the selection,
                    // not the undo stack. This is the half that makes the prompt a guard rather
                    // than a speed bump, and no other check covers it.
                    if (asks9)
                    {
                        int cTick = 0, cDepth = -1;
                        var cTok = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
                        {
                            if (ui.ModalDepth == 0) return true; // no prompt up: stay out of the main loop
                            cTick++;
                            if (cDepth < 0) cDepth = ui.ModalDepth;
                            app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                            if (cTick > 20 && !ReferenceEquals(app.TopRunnable, ui.Top))
                                app.RequestStop(app.TopRunnable);
                            return cTick <= 30;
                        });
                        app.Keyboard.RaiseKeyDownEvent(Key.DeleteChar);
                        app.RemoveTimeout(cTok);
                        Check(cDepth == 1, $"Del on a node with children asks first (modal depth was {cDepth})");
                        Check(firstChild.ParentNode != null, "a cancelled delete leaves the node in the document");
                        Check(ReferenceEquals(ui.Undo.Peek(), top9b), "...pushes nothing onto the undo stack");
                        Check(ReferenceEquals(ui.Tree.SelectedObject, firstChild), "...and leaves the selection alone");
                    }

                    // The prompt's own shape, asserted from the constants rather than by
                    // pressing the button. This harness CANNOT press it: MessageBox's Dialog
                    // exposes no SubViews in 2.4.17 (checked — its adornments are lightweight
                    // settings objects, not views, so the buttons are not there either), and
                    // injected keys reach only app-scope bindings, which is why Esc appears to
                    // work while Tab and the arrows do nothing. So the button ORDER and the
                    // index that proceeds are pinned here; what is not covered is the press
                    // itself, and nothing in this file should be read as claiming otherwise.
                    Check(Program.DeleteButtons.Length == 2
                          && Program.DeleteButtons[Program.DeleteButtons.Length - 1] == "Cancel",
                        "Cancel is the delete prompt's LAST button, which is the one MessageBox defaults to");
                    Check(Program.DeleteButtons[Program.DeleteConfirmed] == "Delete",
                        "...and the index that proceeds is the Delete button");
                    Check(Program.NeedsDeleteConfirm(firstChild) == asks9,
                        $"the prompt rule matches what is under the node (asks={asks9})");
                    // ...and the other side of the rule, which is the whole reason it is a rule
                    // and not "always ask": a leaf costs nothing to restore, is the commonest
                    // delete there is, and a modal in front of it would train people to dismiss
                    // modals. Searched for rather than assumed — not every fixture has attributes.
                    var leaf9 = FindLeaf(Program.Model.Document?.DocumentElement);
                    if (Check(leaf9 != null, "delete drill: found a leaf node"))
                        Check(!Program.NeedsDeleteConfirm(leaf9),
                            $"deleting a leaf ({Program.GetLabel(leaf9)}) does not ask");
                    if (asks9)
                        Check(Program.DeletePrompt(firstChild).Contains(Program.GetLabel(firstChild))
                              && Program.DeletePrompt(firstChild).Contains(" row"),
                            $"the prompt names the node and the count: \"{Program.DeletePrompt(firstChild)}\"");

                    // The deletion itself goes through the commit path the prompt guards, which
                    // is what every other delete check in this file uses too.
                    //
                    // Guarded on the node still being attached, because if the cancel above
                    // failed to cancel, this node is already gone and DeleteNode would take a
                    // null container straight into a NullReferenceException — which does not
                    // fail the run, it ENDS it, truncating the report at 120 of 388 checks and
                    // hiding everything after. Measured while mutation-testing this section.
                    if (firstChild.ParentNode != null)
                        Check(Program.TryDelete(ui, firstChild) == null, "the guarded delete is accepted");
                    Check(firstChild.ParentNode == null, "Del removes the selected element");
                    Check(ReferenceEquals(ui.Tree.SelectedObject, landing9),
                        $"Del lands on the surviving neighbour — here, {landing9What}");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Check(ReferenceEquals(firstChild.ParentNode, parent9) && ReferenceEquals(NextShown(firstChild), successor),
                        "undo restores the element in its exact position");
                    Check(ReferenceEquals(ui.Tree.SelectedObject, firstChild), "undo reselects the restored element");
                }

                // delete an attribute, undo restores it in place
                if (root9.Attributes.Count > 0)
                {
                    var victim = root9.Attributes[0];
                    int vpos = IndexOfAttr(root9, victim);
                    Check(Program.TryDelete(ui, victim) == null, "attribute delete accepted");
                    Check(victim.OwnerElement == null, "attribute is detached");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Check(ReferenceEquals(root9.Attributes[vpos], victim), "undo restores the attribute in place");
                }

                // A delete has to leave the TREE, not just the DOM. Every check above is on the
                // document, and the document was never the half that was wrong: a deleted row
                // stayed on screen, still selectable and still editable through F2, while find —
                // which walks the DOM — could not see it. Edits typed into that row went to a
                // detached node and were dropped by the next save.
                //
                // The container has to be something other than the document element, which is
                // why this does not reuse root9 above. RefreshTreeFor rebuilds the *parent* of
                // the node it is handed, and a delete hands it the container; when that container
                // is the document element its parent is the XmlDocument, so the refresh falls
                // through to a full tree rebuild and papers over the bug. One level down, the
                // rebuild lands on the grandparent, Branch.Refresh never touches descendants, and
                // the row survives. Names are unique to the drill, so a count over the screen is
                // still a count of exactly this row.
                if (firstChild != null)
                {
                    Check(Program.TryInsert(ui, firstChild, InsertKind.Attribute, InsertPos.Child, "fuxghostattr") == null,
                        "nested attribute insert accepted");
                    app.LayoutAndDraw(true);
                    Check(CountOnScreen(app, "@fuxghostattr") == 1, "nested attribute is drawn");
                    Check(Program.TryDelete(ui, firstChild.GetAttributeNode("fuxghostattr")) == null,
                        "nested attribute delete accepted");
                    app.LayoutAndDraw(true);
                    Check(CountOnScreen(app, "@fuxghostattr") == 0, "a deleted attribute leaves the tree");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl); // undo the delete
                    app.LayoutAndDraw(true);
                    Check(CountOnScreen(app, "@fuxghostattr") == 1, "undo brings the attribute row back exactly once");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl); // undo the insert

                    Check(Program.TryInsert(ui, firstChild, InsertKind.Element, InsertPos.Child, "fuxghostelem") == null,
                        "nested element insert accepted");
                    app.LayoutAndDraw(true);
                    Check(CountOnScreen(app, "<fuxghostelem>") == 1, "nested element is drawn");
                    Check(Program.TryDelete(ui, LastShown(firstChild)) == null, "nested element delete accepted");
                    app.LayoutAndDraw(true);
                    Check(CountOnScreen(app, "<fuxghostelem>") == 0, "a deleted element leaves the tree");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl); // undo the delete
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl); // undo the insert
                }

                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl); // undo churn marks dirty; end clean
                Check(!Program.Model.Dirty, "save clears dirty after the insert/delete drill");
            }

            // --- 10. Nudge: Up/Down reorder within the display band, Left/Right change level.
            // The playground is two freshly inserted, uniquely named elements, so every check
            // holds on any document. The view checks around promote/demote are the load-bearing
            // ones — v2's Branch.Refresh rebuilds a single level, so a move that refreshed one
            // end would branch the node under both containers at once. TreeRows catches that;
            // CountOnScreen catches the mirror failure, a node branched somewhere invisible.
            var rootD = doc?.DocumentElement;
            if (rootD != null)
            {
                int kidsD = rootD.ChildNodes.Count;
                Check(Program.TryInsert(ui, rootD, InsertKind.Element, InsertPos.Child, "fuxa") == null,
                    "nudge playground: first element inserted");
                var fa = ui.Tree.SelectedObject as XmlElement;
                Check(Program.TryInsert(ui, rootD, InsertKind.Element, InsertPos.Child, "fuxb") == null,
                    "nudge playground: second element inserted");
                var fb = ui.Tree.SelectedObject as XmlElement;

                // Up/Down and the full history walk over one nudge.
                Check(Program.TryNudge(ui, fb, NudgeDir.Up) == null, "nudge up accepted");
                Check(ReferenceEquals(NextShown(fb), fa), "nudge up swaps with the previous sibling");
                Check(ReferenceEquals(ui.Tree.SelectedObject, fb), "the moved node stays selected");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(ReferenceEquals(NextShown(fa), fb) && ReferenceEquals(LastShown(rootD), fb),
                    "undo restores the original sibling order exactly");
                app.Keyboard.RaiseKeyDownEvent(Key.Y.WithCtrl);
                Check(ReferenceEquals(NextShown(fb), fa), "redo re-applies the move");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(Program.TryNudge(ui, fa, NudgeDir.Down) == null, "nudge down accepted");
                Check(ReferenceEquals(NextShown(fb), fa) && ReferenceEquals(LastShown(rootD), fa),
                    "nudge down moves it past its successor");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);

                // Band edges are quiet: nothing is pushed, nothing is reported.
                var topD = ui.Undo.Peek();
                XmlNode firstD = null;
                foreach (XmlNode c in rootD.ChildNodes)
                    if (Program.IsShown(c)) { firstD = c; break; }
                Check(Program.TryNudge(ui, firstD, NudgeDir.Up) == null, "nudging the first child up is silent");
                Check(Program.TryNudge(ui, fb, NudgeDir.Down) == null, "nudging the last child down is silent");
                Check(Program.TryNudge(ui, rootD, NudgeDir.Up) == null, "nudging the document root is silent");
                Check(Program.TryNudge(ui, firstD, NudgeDir.Left) == null,
                    "promoting out of the document root is silent");
                Check(ReferenceEquals(ui.Undo.Peek(), topD), "refused nudges push nothing");

                // The key path: Ctrl+Shift+Arrow, plus the Ctrl-only alias for terminals that
                // send no modifier for the Shift form.
                ui.Tree.SelectedObject = fb;
                ui.Tree.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(Key.CursorUp.WithCtrl.WithShift);
                Check(ReferenceEquals(NextShown(fb), fa), "^Shift+Up nudges the selection up");
                app.Keyboard.RaiseKeyDownEvent(Key.CursorDown.WithCtrl);
                Check(ReferenceEquals(NextShown(fa), fb), "^Down (alias) nudges it back down");

                // Demote: fb moves into fa, so two containers change at once. Both a row count
                // that survives the move and the node being on screen afterwards are load-
                // bearing — see TreeRows/CountOnScreen for what each one catches.
                int rowsD = TreeRows(ui);
                Check(Program.TryNudge(ui, fb, NudgeDir.Right) == null, "demote accepted");
                Check(ReferenceEquals(fb.ParentNode, fa), "demote makes it a child of the preceding sibling");
                app.LayoutAndDraw(true);
                Check(TreeRows(ui) == rowsD, $"demote adds no row (was {rowsD}, now {TreeRows(ui)})");
                Check(CountOnScreen(app, "<fuxb>") == 1,
                    $"the demoted node is on screen once (drawn: {CountOnScreen(app, "<fuxb>")})");

                // Attributes nudge within their own band, and promote onto the parent element.
                Check(Program.TryInsert(ui, fb, InsertKind.Attribute, InsertPos.Child, "fuxp") == null &&
                      Program.TryInsert(ui, fb, InsertKind.Attribute, InsertPos.Child, "fuxq") == null,
                    "two attributes inserted on the demoted element");
                var ap = fb.GetAttributeNode("fuxp");
                var aq = fb.GetAttributeNode("fuxq");
                Check(Program.TryNudge(ui, aq, NudgeDir.Up) == null, "attribute nudge up accepted");
                Check(IndexOfAttr(fb, aq) == 0, "the attribute moves ahead of its predecessor");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(IndexOfAttr(fb, ap) == 0 && IndexOfAttr(fb, aq) == 1, "undo restores attribute order");
                Check(Program.TryNudge(ui, aq, NudgeDir.Left) == null, "attribute promote accepted");
                Check(fa.GetAttributeNode("fuxq") != null && fb.GetAttributeNode("fuxq") == null,
                    "the attribute lands on the parent element");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(fb.GetAttributeNode("fuxq") != null && IndexOfAttr(fb, aq) == 1,
                    "undo puts the attribute back in place");

                // The one refusal worth a message: the parent already has that attribute.
                Check(Program.TryInsert(ui, fa, InsertKind.Attribute, InsertPos.Child, "fuxp") == null,
                    "colliding attribute added to the parent");
                topD = ui.Undo.Peek();
                Check(Program.TryNudge(ui, ap, NudgeDir.Left) != null,
                    "promoting onto a duplicate attribute name is reported");
                Check(Program.TryNudge(ui, ap, NudgeDir.Right) == null, "demoting an attribute is silent");
                Check(ReferenceEquals(ui.Undo.Peek(), topD), "attribute refusals leave the stack alone");

                // Promote: fb comes back out, again touching two containers. It was fa's only
                // child, so upstream's rule lands it after fa rather than before.
                int rowsP = TreeRows(ui);
                Check(Program.TryNudge(ui, fb, NudgeDir.Left) == null, "promote accepted");
                Check(ReferenceEquals(fb.ParentNode, rootD) && ReferenceEquals(NextShown(fa), fb),
                    "promote lifts it back out, after its old parent");
                app.LayoutAndDraw(true);
                Check(TreeRows(ui) == rowsP, $"promote adds no row (was {rowsP}, now {TreeRows(ui)})");
                Check(CountOnScreen(app, "<fuxb>") == 1,
                    $"the promoted node is on screen once (drawn: {CountOnScreen(app, "<fuxb>")})");

                // Tear the playground down and come out clean.
                Check(Program.TryDelete(ui, fa) == null && Program.TryDelete(ui, fb) == null,
                    "playground deleted");
                Check(rootD.ChildNodes.Count == kidsD,
                    $"insert/delete drill: the document is back to its original children (was {kidsD}, now {rootD.ChildNodes.Count})");
                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl);
                Check(!Program.Model.Dirty, "save clears dirty after the nudge drill");
            }

            // --- 11. Find. The ^F dialog can't run headless (a nested Run would block the
            // injector), so these drive TryFind directly — the same path the dialog commits
            // through — plus the F3 key bindings, which need no modal. Two uniquely named
            // elements make the ring deterministic on any document.
            var rootF = doc?.DocumentElement;
            if (rootF != null)
            {
                int kidsF = rootF.ChildNodes.Count;

                string DoFind(string expr, FindFlags flags, SearchFilter filter, bool back = false)
                {
                    ui.FindExpr = expr;
                    ui.FindOptions = flags;
                    ui.FindIn = filter;
                    return Program.TryFind(ui, back);
                }

                Check(Program.TryInsert(ui, rootF, InsertKind.Element, InsertPos.Child, "fuxfind") == null,
                    "find playground: first element inserted");
                var h1 = ui.Tree.SelectedObject as XmlElement;
                Check(Program.TryInsert(ui, rootF, InsertKind.Element, InsertPos.Child, "fuxfind") == null,
                    "find playground: second element inserted");
                var h2 = ui.Tree.SelectedObject as XmlElement;

                // Walking the ring forwards, wrapping at the end, then backwards.
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("fuxfind", FindFlags.Normal, SearchFilter.Everything) == "1/2",
                    "find reports the first of two hits");
                Check(ReferenceEquals(ui.Tree.SelectedObject, h1), "find selects the first hit");
                Check(Program.TryFind(ui, false) == "2/2", "find next steps to the second hit");
                Check(ReferenceEquals(ui.Tree.SelectedObject, h2), "find next selects the second hit");
                Check(Program.TryFind(ui, false) == "1/2", "find next wraps at the end");
                Check(Program.TryFind(ui, true) == "2/2", "find previous wraps at the start");
                Check(ReferenceEquals(ui.Tree.SelectedObject, h2), "find previous selects the last hit");

                // The status slot carries the ring position.
                app.LayoutAndDraw(true);
                Check(ScreenText(app).Contains("find 2/2"), "the status bar shows the ring position");

                // The F3 bindings go through the central key handler. FindExpr is already set,
                // so neither opens the modal.
                ui.Tree.SelectedObject = rootF;
                ui.Tree.SetFocus();
                app.Keyboard.RaiseKeyDownEvent(Key.F3);
                Check(ReferenceEquals(ui.Tree.SelectedObject, h1), "F3 finds the next match");
                app.Keyboard.RaiseKeyDownEvent(Key.F3.WithShift);
                Check(ReferenceEquals(ui.Tree.SelectedObject, rootF) == false &&
                      ReferenceEquals(ui.Tree.SelectedObject, h2), "Shift+F3 finds the previous match");

                Check(DoFind("fuxzzznotthere", FindFlags.Normal, SearchFilter.Everything)
                        .StartsWith("no match"), "a term with no hits reports no match");

                // A term carrying an underscore proves the title escape end to end: without
                // Program.EscapeTitle, Terminal.Gui eats the '_' as a hotkey marker and the
                // pane title reads "fuxnosuchterm". Nothing else on screen holds this string,
                // so a hit here can only have come from the title.
                Check(DoFind("fux_no_such_term", FindFlags.Normal, SearchFilter.Everything)
                        .StartsWith("no match"), "an underscored term reports no match");
                app.LayoutAndDraw(true);
                Check(ScreenText(app).Contains("fux_no_such_term"),
                    "an underscore survives into the pane title");

                // Case sensitivity, on the same two names.
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("FUXFIND", FindFlags.Normal, SearchFilter.Everything) == "1/2",
                    "matching is case-insensitive by default");
                Check(DoFind("FUXFIND", FindFlags.MatchCase, SearchFilter.Everything)
                        .StartsWith("no match"), "match case rejects the wrong casing");

                // Values vs names: give one hit a value and search for that instead.
                // "fux"-prefixed like every other magic string here, and for the same
                // reason: a plain word collides with real prose. "needle" cost three
                // failures the first time the drill was pointed at a real book, which
                // contains the word once — the checks were asserting on a hit count that
                // only held for documents that happened not to mention needles.
                ui.Undo.Push(new EditNodeValue(h1, "a fuxneedle here"));
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("fuxneedle", FindFlags.Normal, SearchFilter.Text) == "1/1",
                    "a value search finds the element by its text");
                Check(ReferenceEquals(ui.Tree.SelectedObject, h1), "the value hit is the element holding it");
                Check(DoFind("fuxneedle", FindFlags.Normal, SearchFilter.Names)
                        .StartsWith("no match"), "a name search ignores values");
                ui.Tree.SelectedObject = rootF; // a miss leaves the selection where it was
                Check(DoFind("fuxfind", FindFlags.Normal, SearchFilter.Names) == "1/2",
                    "a name search still finds names");

                // Whole word matches a run between delimiters, not a prefix of one.
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("fuxneedle", FindFlags.WholeWord, SearchFilter.Text) == "1/1",
                    "whole word matches a whole word");
                Check(DoFind("fuxneedl", FindFlags.WholeWord, SearchFilter.Text)
                        .StartsWith("no match"), "whole word rejects a partial word");
                Check(DoFind("fuxneedl", FindFlags.Normal, SearchFilter.Text) == "1/1",
                    "the same partial matches without whole word");

                // Regex and XPath modes, and what each says about a malformed expression.
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("fuxf.nd", FindFlags.Regex, SearchFilter.Everything) == "1/2",
                    "regex mode matches");
                Check(DoFind("fuxf[", FindFlags.Regex, SearchFilter.Everything)
                        .StartsWith("bad regex"), "a malformed regex is reported, not thrown");
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("//*[local-name()='fuxfind']", FindFlags.XPath, SearchFilter.Everything) == "1/2",
                    "xpath mode matches");
                Check(ReferenceEquals(ui.Tree.SelectedObject, h1), "xpath selects the first hit in tree order");
                Check(DoFind("///", FindFlags.XPath, SearchFilter.Everything)
                        .StartsWith("bad XPath"), "a malformed xpath is reported, not thrown");

                // A find changes no DOM: the undo stack is untouched by all of the above.
                var topF = ui.Undo.Peek();
                ui.Tree.SelectedObject = rootF;
                DoFind("fuxfind", FindFlags.Normal, SearchFilter.Everything);
                Check(ReferenceEquals(ui.Undo.Peek(), topF), "finding pushes nothing on the undo stack");

                ui.FindExpr = null;
                Check(Program.TryDelete(ui, h1) == null && Program.TryDelete(ui, h2) == null,
                    "find playground deleted");
                Check(rootF.ChildNodes.Count == kidsF,
                    $"find drill: the document is back to its original children (was {kidsF}, now {rootF.ChildNodes.Count})");
                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl);
                Check(!Program.Model.Dirty, "save clears dirty after the find drill");
            }

            // --- 12. Open and Save As. The pickers can't run headless, so these drive TryOpen /
            // TrySaveAs — the paths OpenDialog and SaveDialog commit through. Everything is
            // written next to the drill's own scratch copy, so no fixture is touched.
            {
                var scratch = System.IO.Path.GetDirectoryName(file);
                var other = System.IO.Path.Combine(scratch, "fux_drill_other.xml");
                System.IO.File.WriteAllText(other, "<?xml version=\"1.0\"?>\n<fuxother><kid/></fuxother>\n");

                // Leave history behind, so replacing the document has something to discard.
                Program.TryInsert(ui, Program.Model.Document.DocumentElement,
                    InsertKind.Element, InsertPos.Child, "fuxstale");
                Check(ui.Undo.Peek() != null, "there is undo history to discard");

                Check(Program.TryOpen(ui, other) == null, "open accepted");
                Check(Program.Model.Document?.DocumentElement?.Name == "fuxother",
                    "the model holds the newly opened document");
                Check(ReferenceEquals(ui.Tree.SelectedObject, Program.Model.Document.DocumentElement),
                    "the tree rebinds to the new root");
                // The old stack's commands close over nodes no longer in any document.
                Check(ui.Undo.Peek() == null, "opening clears the undo history");
                Check(!Program.Model.Dirty, "a freshly opened document is clean");
                app.LayoutAndDraw(true);
                Check(ScreenRow(app, 1).Contains("fux_drill_other.xml"), "the pane title follows the new file");
                Check(ScreenText(app).Contains("<fuxother>"), "the tree draws the new document");

                // Save As writes elsewhere and retargets the model at the new path.
                var saved = System.IO.Path.Combine(scratch, "fux_drill_saved.xml");
                Check(Program.TrySaveAs(ui, saved) == null, "save as accepted");
                Check(System.IO.File.Exists(saved), "save as writes the file");
                Check(Program.Model.FileName == saved, "the model retargets to the saved path");
                Check(!Program.Model.Dirty, "save as leaves the document clean");
                app.LayoutAndDraw(true);
                Check(ScreenRow(app, 1).Contains("fux_drill_saved.xml"), "the pane title follows the saved name");

                // A document that will not parse reports, and leaves the view consistent with
                // whatever the model ended up holding rather than with the document it replaced.
                var bad = System.IO.Path.Combine(scratch, "fux_drill_bad.xml");
                System.IO.File.WriteAllText(bad, "<unclosed>");
                Check(Program.TryOpen(ui, bad) != null, "a malformed document is reported");
                Check(ReferenceEquals(ui.Tree.SelectedObject, Program.Model.Document?.DocumentElement),
                    "the tree matches the model after a failed open");
                Check(ui.Undo.Peek() == null, "a failed open leaves no stale history");

                // Back to the drill's own document so the rest of the run is unsurprising.
                Check(Program.TryOpen(ui, file) == null, "the original document reopens");
                Check(!Program.Model.Dirty, "and comes back clean");
            }

            // --- 12a. Backups. A save destroys the version that was on disk, and the undo stack
            // reaches back no further than the last open — so unless a copy is taken first, what
            // the file held a moment ago is gone. Asserted against the directory rather than
            // against the model, because what a user can recover afterwards is the whole point.
            {
                var scratch = System.IO.Path.GetDirectoryName(file);
                var target = System.IO.Path.Combine(scratch, "fux_drill_backup.xml");
                var pattern = System.IO.Path.GetFileName(target) + ".*.bak";
                var v1 = System.Text.Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<fuxv1/>\n");
                System.IO.File.WriteAllBytes(target, v1);

                // Save As onto an existing file: what is in hand is not what is on disk, so
                // what is on disk has to end up somewhere.
                Check(Program.TrySaveAs(ui, target) == null, "save over an existing file accepted");
                var bak = Program.Model.LastBackup;
                if (Check(bak != null, "overwriting a file leaves a backup"))
                {
                    // The oracle is the *content*, not the existence of a .bak: a copy taken
                    // after the write, or of the wrong file, satisfies "a backup appeared" while
                    // preserving nothing at all.
                    Check(SameBytes(v1, System.IO.File.ReadAllBytes(bak)),
                        "the backup holds the displaced contents byte for byte");
                    Check(System.IO.Path.GetDirectoryName(bak) == scratch,
                        "the backup sits beside the file it came from");
                    // Spelled out rather than rebuilt from Backup's own format string, which
                    // would only compare the code against itself.
                    var name = System.IO.Path.GetFileName(bak);
                    Check(name.StartsWith("fux_drill_backup.xml.", StringComparison.Ordinal)
                          && name.EndsWith(".bak", StringComparison.Ordinal)
                          && IsStamp(name.Substring("fux_drill_backup.xml.".Length, name.Length - "fux_drill_backup.xml.".Length - ".bak".Length)),
                        $"the backup is named for the file and timestamped (got '{name}')");
                }

                // A save that rewrites the same bytes has displaced nothing. Without this, the
                // idle ^S of anyone who has ever lost work fills the directory with copies.
                int kept = System.IO.Directory.GetFiles(scratch, pattern).Length;
                Check(Program.TrySaveAs(ui, target) == null, "the unchanged document saves again");
                Check(Program.Model.LastBackup == null, "a save that changes nothing keeps nothing");
                Check(System.IO.Directory.GetFiles(scratch, pattern).Length == kept,
                    "...and leaves no second copy behind");

                // A second overwrite inside the same second must not reuse the first backup's
                // name: recording the newer version by destroying the older one is the one
                // outcome worse than not backing up at all.
                byte[] v2 = System.IO.File.ReadAllBytes(target);
                Check(Program.TryInsert(ui, Program.Model.Document.DocumentElement,
                        InsertKind.Element, InsertPos.Child, "fuxbak2") == null, "backup drill: document edited");
                Check(Program.TrySaveAs(ui, target) == null, "the edited document saves over itself");
                var bak2 = Program.Model.LastBackup;
                if (Check(bak2 != null && bak2 != bak, "a second overwrite makes a second backup"))
                    Check(SameBytes(v2, System.IO.File.ReadAllBytes(bak2)),
                        "the second backup holds the version it displaced");
                Check(bak != null && System.IO.File.Exists(bak) && SameBytes(v1, System.IO.File.ReadAllBytes(bak)),
                    "the first backup survives the second save");

                // A backup name is the document's name plus the second the save landed in, so in
                // a directory anyone can write to — /tmp, a shared build tree — it is guessable
                // ahead of time. Someone who plants a symlink there must not have the document
                // written through it. Windows only: CreateSymbolicLink needs a privilege there,
                // and the drill does not run on Windows anyway (no PTY on the runner).
                if (!OperatingSystem.IsWindows())
                {
                    // A file of its own, untouched by the saves above: their backups already hold
                    // this second's first few names, and the planted links have to be what the
                    // save reaches for first or this section quietly tests nothing.
                    var fresh = System.IO.Path.Combine(scratch, "fux_drill_planted.xml");
                    System.IO.File.WriteAllBytes(fresh, v1);

                    // The victim is a path that does NOT exist — the dangerous shape, since a
                    // link to a file that does exist is refused by any copy that declines to
                    // overwrite. Links for this second and the next two: the save picks the
                    // second, and a check must not depend on which one it gets.
                    var victim = System.IO.Path.Combine(scratch, "fux_drill_victim.txt");
                    var planted = new List<string>();
                    var stamps = new List<string>();
                    for (int s = 0; s < 3; s++)
                    {
                        var stamp = DateTime.Now.AddSeconds(s).ToString("yyyyMMdd-HHmmss",
                            System.Globalization.CultureInfo.InvariantCulture);
                        var link = $"{fresh}.{stamp}.bak";
                        if (System.IO.File.Exists(link)) continue;
                        System.IO.File.CreateSymbolicLink(link, victim);
                        planted.Add(link);
                        stamps.Add(stamp);
                    }

                    var err = Program.TrySaveAs(ui, fresh);
                    Check(!System.IO.File.Exists(victim),
                        "a symlink planted at the backup name is not written through");
                    // Stepping over the planted name is the point; refusing to save because
                    // someone littered the directory is not.
                    Check(err == null, $"the save works around the planted name (err was '{err}')");
                    var chosen = System.IO.Path.GetFileName(Program.Model.LastBackup ?? "");
                    Check(Program.Model.LastBackup != null && !planted.Contains(Program.Model.LastBackup),
                        "the backup went to a name of its own");
                    // Guards the check above against going vacuous: if the naming ever changes,
                    // the links would be planted at names no save would have used, and "not
                    // written through" would pass by never being tested at all.
                    Check(stamps.Exists(s => chosen.StartsWith($"fux_drill_planted.xml.{s}", StringComparison.Ordinal)),
                        $"the planted names were the ones the save reached for (backup was '{chosen}')");

                    foreach (var link in planted) System.IO.File.Delete(link);

                    // A backup of a document its owner keeps to themselves must not be readable
                    // by anyone the original was not. Writing the copy by hand is what puts this
                    // at risk: the permission bits have to be carried over deliberately, where
                    // File.Copy brought them along.
                    System.IO.File.SetUnixFileMode(fresh, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
                    Check(Program.TryInsert(ui, Program.Model.Document.DocumentElement,
                            InsertKind.Element, InsertPos.Child, "fuxbak4") == null, "backup drill: private document edited");
                    Check(Program.TrySaveAs(ui, fresh) == null, "the private document saves");
                    var mode = Program.Model.LastBackup == null ? (System.IO.UnixFileMode?)null
                        : System.IO.File.GetUnixFileMode(Program.Model.LastBackup);
                    Check(mode == (System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite),
                        $"the backup of a 0600 document is 0600 (was {mode?.ToString() ?? "no backup"})");
                }

                // --no-backup: the same edit and save, keeping nothing.
                Program.Model.Backups = false;
                Check(Program.TryInsert(ui, Program.Model.Document.DocumentElement,
                        InsertKind.Element, InsertPos.Child, "fuxbak3") == null, "backup drill: document edited again");
                kept = System.IO.Directory.GetFiles(scratch, pattern).Length;
                Check(Program.TrySaveAs(ui, target) == null, "the document saves with backups off");
                Check(Program.Model.LastBackup == null, "--no-backup keeps nothing");
                Check(System.IO.Directory.GetFiles(scratch, pattern).Length == kept,
                    "--no-backup writes no backup file");
                Program.Model.Backups = true;

                Check(Program.TryOpen(ui, file) == null, "the original document reopens after the backup drill");
            }

            // --- 12b. Mixed content. An element whose children are only text folds that text
            // into its own value and shows no child row; a *container* has no value to fold
            // into, so its text children get rows of their own. Before that second half existed,
            // an imported HTML document lost every word that shared a parent with an inline tag
            // — the text was in the DOM and saved back correctly, but appeared on no row and so
            // could not be found either, which is exactly how the bug was reported.
            //
            // Driven through the real HTML import (SgmlReader), because that is where mixed
            // content actually arrives from; the fixture is the shape a Gutenberg book uses for
            // its front matter.
            {
                var scratch = System.IO.Path.GetDirectoryName(file);
                var mixed = System.IO.Path.Combine(scratch, "fux_drill_mixed.html");
                System.IO.File.WriteAllText(mixed,
                    "<html><body>\n" +
                    "<p><strong>Release date</strong>: July 1, 2001<br>\n" +
                    "Most recently updated: February 10, 2026</p>\n" +
                    "<p>Call me Ishmael.</p>\n" +
                    "<div>\n  <span>inner</span>\n</div>\n" +
                    "</body></html>\n");

                Check(Program.TryOpen(ui, mixed) == null, "mixed-content HTML imported");
                var body = Program.Model.Document?.DocumentElement?["body"];
                var ps = body?.GetElementsByTagName("p");

                if (Check(ps != null && ps.Count == 2, "the import produced both paragraphs"))
                {
                    // The mixed paragraph: <strong>, text, <br>, text — in source order, and
                    // asserted as the whole row list rather than "the screen contains it
                    // somewhere", so a stray extra row (an indentation node leaking through)
                    // fails just as loudly as a missing one.
                    var mixedP = (XmlElement)ps[0];
                    Check(Rows(mixedP) == "<strong>|#text|<br>|#text",
                        $"mixed content shows its text on rows of its own (was \"{Rows(mixedP)}\")");
                    // The line break after <br> belongs to the text node that follows it, and is
                    // asserted rather than trimmed away: a text row's value is the source's
                    // characters exactly, which is what lets a save reproduce the file.
                    Check(Values(mixedP) == "Release date|: July 1, 2001||\nMost recently updated: February 10, 2026",
                        $"each text row carries its own text (was \"{Vis(Values(mixedP))}\")");
                    Check(Program.GetValue(mixedP) == "",
                        "the container itself has no scalar value, so nothing is shown twice");

                    // The text-only paragraph keeps the old fold: one row, no children.
                    var plainP = (XmlElement)ps[1];
                    Check(Rows(plainP) == "", $"a text-only element still shows no child rows (was \"{Rows(plainP)}\")");
                    Check(Program.GetValue(plainP) == "Call me Ishmael.",
                        "a text-only element still folds its text into its own value");

                    // Indentation between elements is layout, not content, and stays hidden.
                    var div = (XmlElement)body.GetElementsByTagName("div")[0];
                    Check(Rows(div) == "<span>", $"whitespace between elements gets no row (was \"{Rows(div)}\")");

                    // The rows reach the screen, and in the right place: the text between
                    // <strong> and <br> is drawn directly under <strong>. Asserting the
                    // neighbouring row rather than "#text appears somewhere" keeps this honest
                    // — the paragraph has two text rows, and only one of them belongs here.
                    var lost = mixedP.LastChild;
                    ui.Tree.SelectedObject = lost;
                    app.LayoutAndDraw(true);
                    Console.Error.WriteLine(); // the repaint leaves the cursor mid-line
                    int strongRow = RowIndexOf(app, "<strong>");
                    Check(strongRow >= 0 && ScreenRow(app, strongRow + 1).Contains("#text"),
                        "the tree draws a text row under <strong>");

                    // Find reaches it. This is the reported symptom: text plainly present in the
                    // source that a search could not turn up.
                    ui.FindExpr = "Most recently updated";
                    ui.FindOptions = FindFlags.Normal;
                    ui.FindIn = SearchFilter.Text;
                    ui.Tree.SelectedObject = Program.Model.Document.DocumentElement;
                    Check(Program.TryFind(ui, false) == "1/1", "find locates text inside mixed content");
                    Check(ReferenceEquals(ui.Tree.SelectedObject, lost),
                        "and selects the text node holding it, not its parent");
                    // Landing on the hit puts the text in the value pane, verbatim.
                    Check(ui.ValueView.Text == "\nMost recently updated: February 10, 2026",
                        $"the value pane shows the found text (was \"{Vis(ui.ValueView.Text)}\")");
                    ui.FindExpr = null;

                    // A text row is reachable, so it is also editable — and editing it must
                    // write the text node itself rather than flattening the paragraph the way
                    // setting InnerText on the container would.
                    Check(EditNodeValue.CanEditValue(lost), "a text row can be edited");
                    Check(!EditNodeValue.CanEditValue(mixedP), "its container cannot — that would drop the inline tags");
                    ui.Undo.Push(new EditNodeValue(lost, " rewritten"));
                    Check(Rows(mixedP) == "<strong>|#text|<br>|#text" && lost.Value == " rewritten",
                        "editing a text row changes that text and nothing else");
                    ui.Undo.Undo();
                    Check(lost.Value == "\nMost recently updated: February 10, 2026",
                        $"undo restores it (was \"{Vis(lost.Value)}\")");
                }

                // Back to the drill's own document for section 13.
                Check(Program.TryOpen(ui, file) == null, "the original document reopens after the mixed-content drill");
            }

            // --- 13. Byte-preserving saves. The oracle is the file itself: a save the user did
            // not ask to reformat must not reformat anything. Comparing bytes rather than
            // re-parsing is the point — every defect this guards against (a BOM appearing, LF
            // becoming CRLF, the declaration being regenerated, indentation being rebuilt,
            // <b/> growing a space) produces a document that is semantically identical and
            // wrong in a diff. Fixture-agnostic: everything is measured against this run's own
            // first save, so no assertion hard-codes a document's shape.
            {
                var scratch = System.IO.Path.GetDirectoryName(file);
                var probe = System.IO.Path.Combine(scratch, "fux_drill_bytes.xml");

                // SaveCopy rather than TrySaveAs: these checks compare the document against
                // itself either side of an edit-and-undo, and retargeting the model each time
                // would add churn they would then have to tolerate.
                Program.Model.SaveCopy(probe);
                byte[] pristine = System.IO.File.ReadAllBytes(probe);

                // An insert has to land on a line of its own, indented like the siblings it
                // joins — the visible half of keeping the document's whitespace in the DOM.
                var rootB = Program.Model.Document?.DocumentElement;
                if (rootB != null)
                {
                    // What "correct" looks like depends on how the container is already written:
                    // a block-formatted one gives the new node its own indented line, while a
                    // container written inline must be left inline. Adding a node is not a
                    // licence to reformat the document around it, in either direction.
                    // An import has no whitespace nodes, so ShouldIndent reads it as inline — but
                    // the writer synthesizes layout for it, so the output is block either way.
                    bool blockLaidOut = XmlLayout.ShouldIndent(rootB) || Program.Model.PrettyPrint;
                    // A sibling to measure against. "Correctly indented" means "indented like
                    // the neighbours", not "indented at all" — an HTML import's root children
                    // sit at column zero, and matching that is right, not a bug.
                    string siblingName = null;
                    foreach (XmlNode c in rootB.ChildNodes)
                        if (c is XmlElement se) { siblingName = se.Name; break; }

                    Check(Program.TryInsert(ui, rootB, InsertKind.Element, InsertPos.Child, "fuxbyte") == null,
                        "byte drill: element inserted");
                    Program.Model.SaveCopy(probe);
                    string inserted = System.IO.File.ReadAllText(probe);
                    string line = LineWith(inserted, "<fuxbyte");
                    if (blockLaidOut)
                    {
                        Check(line.Trim() == "<fuxbyte/>" || line.Trim() == "<fuxbyte />",
                            $"the inserted element gets a line to itself (line was \"{line}\")");
                        if (siblingName != null)
                        {
                            string sibLine = LineWithTag(inserted, siblingName);
                            string want = sibLine.Substring(0, sibLine.Length - sibLine.TrimStart().Length);
                            string got = line.Substring(0, line.Length - line.TrimStart().Length);
                            Check(got == want,
                                $"the inserted element is indented like its siblings (\"{got}\" vs \"{want}\")");
                        }
                    }
                    else
                    {
                        Check(line.Contains("<fuxbyte/>") || line.Contains("<fuxbyte />"),
                            "the inserted element is written");
                        Check(line.Trim() != "<fuxbyte/>" && line.Trim() != "<fuxbyte />",
                            $"an inline container is not reformatted around the insert (line was \"{line}\")");
                    }

                    // The strongest check here: an edit and its undo must cancel out in the
                    // bytes, not merely in the tree. It catches whitespace added on insert but
                    // not removed on undo, and an element that went in as <a/> coming back
                    // out as <a></a>.
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Program.Model.SaveCopy(probe);
                    Check(SameBytes(pristine, System.IO.File.ReadAllBytes(probe)),
                        "insert then undo restores the file byte for byte");

                    // A nudge is reversible by its opposite by design (it stays inside the
                    // node's display band), so the bytes have to come back too.
                    Check(Program.TryInsert(ui, rootB, InsertKind.Element, InsertPos.Child, "fuxb1") == null &&
                          Program.TryInsert(ui, rootB, InsertKind.Element, InsertPos.Child, "fuxb2") == null,
                        "byte drill: two elements for the nudge");
                    var n2 = ui.Tree.SelectedObject as XmlElement;
                    Program.Model.SaveCopy(probe);
                    byte[] beforeNudge = System.IO.File.ReadAllBytes(probe);
                    Check(Program.TryNudge(ui, n2, NudgeDir.Up) == null, "byte drill: nudge up");

                    // Assert on the state after ONE nudge, not just after the round trip: a
                    // nudge that tears a node out from between two indents and drops it against
                    // its new neighbour is still perfectly reversible, so the round-trip check
                    // alone passes while the document is visibly mangled in between.
                    Program.Model.SaveCopy(probe);
                    if (blockLaidOut)
                    {
                        string nudgedLine = LineWith(System.IO.File.ReadAllText(probe), "<fuxb2");
                        Check(nudgedLine.Trim() == "<fuxb2/>" || nudgedLine.Trim() == "<fuxb2 />",
                            $"a nudged element keeps a line to itself (line was \"{nudgedLine}\")");
                    }

                    Check(Program.TryNudge(ui, n2, NudgeDir.Down) == null, "byte drill: nudge back down");
                    Program.Model.SaveCopy(probe);
                    Check(SameBytes(beforeNudge, System.IO.File.ReadAllBytes(probe)),
                        "a nudge and its opposite cancel out byte for byte");

                    // Same contract for delete.
                    Check(Program.TryDelete(ui, n2) == null, "byte drill: element deleted");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Program.Model.SaveCopy(probe);
                    Check(SameBytes(beforeNudge, System.IO.File.ReadAllBytes(probe)),
                        "delete then undo restores the file byte for byte");
                }

                // Back to the drill's own document, clean, for the rest of the run.
                Check(Program.TryOpen(ui, file) == null, "byte drill: the original document reopens");
            }

            // --- 13c. Clipboard. ^X/^C/^V act on the selected node's *value*, which is what
            // makes them do something with the tree focused — a text copy has no caret to act
            // on there. What ^C decides to copy (CopyText) is asserted apart from the OS write,
            // so the section still says something on a box with no clipboard — a headless one,
            // or an X11 one with no xclip. No runner here is such a box: ubuntu-latest and
            // macos-latest both have a working clipboard (measured, on the first CI run this
            // section ever had), and the drill is skipped on Windows. So the else branch below
            // is reached by forcing Program.TrySetClipboard to fail, never by a runner.
            var clipNode = file == null ? null : FindValuedNode(Program.Model.Document?.DocumentElement);
            if (clipNode != null)
            {
                var clipValue = EditNodeValue.GetNodeValue(clipNode);
                ui.Tree.EnsureVisible(clipNode);
                ui.Tree.SelectedObject = clipNode;
                ui.Tree.SetFocus();
#pragma warning disable CS0618 // TextView: see the BuildUi note on the obsolete flag
                var tvc = (Terminal.Gui.Views.TextView)ui.ValueView;
#pragma warning restore CS0618

                Check(Program.CopyText(ui) == clipValue, "^C from the tree copies the whole node value");

                // A highlight in the value pane wins, and wins without the pane holding focus
                // — that is what makes the Edit▸Copy row, which runs with the menu focused,
                // agree with the key. A sentinel rather than a slice of the real value, so the
                // two candidate strings cannot accidentally be the same one.
                tvc.Text = "fux-highlight";
                tvc.InvokeCommand(Terminal.Gui.Input.Command.SelectAll);
                Check(!tvc.HasFocus && Program.CopyText(ui) == "fux-highlight",
                    "a highlight in the value pane beats the node value");

                // Reassigning the pane's Text is exactly what the tree's SelectionChanged
                // handler does on every move, and TextView drops its selection when Text is
                // set. Without that, ^C would keep copying a leftover range from a node the
                // user has already navigated away from.
                tvc.Text = Program.GetValue(clipNode) ?? "";
                Check(!tvc.IsSelecting, "reassigning the pane text drops a stale highlight");
                Check(Program.CopyText(ui) == clipValue, "…so ^C copies the node value again");

                // Does this box actually have a clipboard? Probe with a sentinel, so the
                // read-backs below prove fux wrote them rather than finding what we seeded.
                bool clipOk;
                try { clipOk = ui.App.Clipboard?.TrySetClipboardData("fux-drill-probe") ?? false; }
                catch { clipOk = false; }

                string onClipboard()
                {
                    string s = null;
                    try { ui.App.Clipboard?.TryGetClipboardData(out s); } catch { s = null; }
                    return s;
                }

                app.Keyboard.RaiseKeyDownEvent(Key.C.WithCtrl);
                if (clipOk)
                {
                    Check(onClipboard() == clipValue, "^C puts the node value on the OS clipboard");
                    Check(ui.ValueView.Title == "Value — copied", "the value pane reports the copy");
                }
                else
                {
                    Check(ui.ValueView.Title == "Value — no clipboard",
                        "^C says so on a box with no clipboard");
                }

                // ^X copies then clears, as one undoable edit. With no clipboard it must refuse
                // outright: cutting into a clipboard that didn't take the text would destroy
                // the only copy of it.
                app.Keyboard.RaiseKeyDownEvent(Key.X.WithCtrl);
                if (clipOk)
                {
                    Check(EditNodeValue.GetNodeValue(clipNode) == "", "^X clears the node value");
                    Check(onClipboard() == clipValue, "^X put the cut text on the OS clipboard");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Check(EditNodeValue.GetNodeValue(clipNode) == clipValue, "^Z restores a cut value");

                    ui.App.Clipboard.TrySetClipboardData("fux-pasted");
                    app.Keyboard.RaiseKeyDownEvent(Key.V.WithCtrl);
                    Check(EditNodeValue.GetNodeValue(clipNode) == "fux-pasted",
                        "^V replaces the node value with the clipboard text");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Check(EditNodeValue.GetNodeValue(clipNode) == clipValue, "^Z undoes a paste");
                }
                else
                {
                    Check(EditNodeValue.GetNodeValue(clipNode) == clipValue,
                        "^X leaves the value alone when there is no clipboard to cut to");
                }

                // Undo leaves the model dirty even though the content matches, and §15's ^Q
                // would then block on the unsaved-changes prompt. Reopen, as §13 does.
                Program.SetValueStatus(ui, "");
                Check(Program.TryOpen(ui, file) == null, "clipboard drill: the document reopens clean");
            }
            else
            {
                Console.Error.WriteLine("  (no node with a value; clipboard drill not exercised)");
            }

            // --- 13d. Reveal: WHICH node a delete lands on (#18), and WHERE a reveal comes to
            // rest (#10). Two issues, one section, because they meet in the same call and the
            // second is what makes the first observable: a delete that lands on a neighbour of
            // the row it removed only leaves the view alone because an already-visible node is
            // not scrolled to.
            //
            // On a document built here rather than on the fixture. Every check below is about
            // scrolling, so it needs more rows than the pane has — and the CI fixtures are not
            // all tall enough. Building it means these run on all four of them instead of
            // silently skipping on three, which is how a scroll bug ships.
            {
                var tallPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fux-drill-tall.xml");
                var sb = new System.Text.StringBuilder("<fuxtall>\n");
                for (int i = 0; i < 60; i++) sb.Append($"  <fuxrow>r{i:00}</fuxrow>\n");
                sb.Append("  <fuxsolo><fuxonly>x</fuxonly></fuxsolo>\n");
                sb.Append("  <fuxattrs p=\"1\" q=\"2\" r=\"3\">z</fuxattrs>\n</fuxtall>\n");
                System.IO.File.WriteAllText(tallPath, sb.ToString());

                if (Check(Program.TryOpen(ui, tallPath) == null, "reveal drill: the tall document opens"))
                {
                    app.LayoutAndDraw(true);
                    var tree = ui.Tree;
                    var tallRoot = Program.Model.Document.DocumentElement;
                    var kids = new List<XmlNode>(Program.GetChildren(tallRoot));
                    int h = tree.Viewport.Height;
                    int total = tree.GetSize().Height;
                    // Assert the shape of the fixture before asserting anything with it: a pane
                    // as tall as the document would make every "did it scroll?" check vacuous.
                    Check(kids.Count == 62 && h > 4 && total > h + 4,
                        $"reveal drill: {kids.Count} rows under the root, pane {h} of {total} — tall enough to scroll");

                    // --- #10: where a reveal comes to rest.
                    tree.ScrollOffsetVertical = 0;
                    Program.RevealNode(ui, kids[40]);
                    int inPane = tree.GetScrollOffsetOf(kids[40]) - tree.ScrollOffsetVertical;
                    Check(Math.Abs(inPane - h / 2) <= 1,
                        $"a reveal that must scroll comes to rest near the middle (row {inPane} of {h})");
                    Check(inPane >= 0 && inPane < h, "...and inside the pane at all");

                    // Clamped at the top: no blank space scrolled in above the root.
                    Program.RevealNode(ui, kids[1]);
                    Check(tree.ScrollOffsetVertical == 0,
                        $"a reveal near the top leaves the view at the top (offset {tree.ScrollOffsetVertical})");

                    // Clamped at the bottom: the last screenful, no blank space below the last row.
                    Program.RevealNode(ui, kids[kids.Count - 1]);
                    Check(tree.ScrollOffsetVertical == total - h,
                        $"a reveal near the bottom leaves the last screenful (offset {tree.ScrollOffsetVertical}, max {total - h})");

                    // Already on screen: not moved, even sitting on an edge. This is the decided
                    // behaviour, and the reason F3 through neighbouring matches does not jump.
                    int parked = tree.ScrollOffsetVertical;
                    var onEdge = tree.GetObjectOnRow(0);
                    if (Check(onEdge != null, "reveal drill: something is on the pane's first row"))
                    {
                        Program.RevealNode(ui, onEdge);
                        Check(tree.ScrollOffsetVertical == parked,
                            $"a reveal of a node already on screen does not move the view (offset {tree.ScrollOffsetVertical}, was {parked})");
                        Check(ReferenceEquals(tree.SelectedObject, onEdge), "...but still selects it");
                    }

                    // --- #18: which node a delete lands on, and that the view stays put.
                    Program.RevealNode(ui, kids[38]);
                    int before = tree.ScrollOffsetVertical;
                    var successor = kids[39];
                    Check(Program.TryDelete(ui, kids[38]) == null, "reveal drill: delete accepted");
                    Check(ReferenceEquals(tree.SelectedObject, successor),
                        "a delete selects the following sibling, not the container");
                    Check(tree.ScrollOffsetVertical == before,
                        $"...and leaves the viewport exactly where it was (offset {tree.ScrollOffsetVertical}, was {before})");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Check(ReferenceEquals(tree.SelectedObject, kids[38]), "^Z restores the node and reselects it");

                    // Last in its band: nothing follows it, so the cursor falls back one row.
                    // The genuinely last child, not the last <fuxrow> — the rows are followed by
                    // <fuxsolo> and <fuxattrs>, and landing on those is the *forward* rule doing
                    // its job. Getting this wrong first is why the index is spelled out.
                    var last = kids[kids.Count - 1];
                    var beforeLast = kids[kids.Count - 2];
                    Check(Program.TryDelete(ui, last) == null, "reveal drill: delete of the last row accepted");
                    Check(ReferenceEquals(tree.SelectedObject, beforeLast),
                        "deleting the last of a band selects the one before it");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);

                    // An only child still selects the container — it is on screen by construction.
                    var solo = tallRoot.SelectSingleNode("fuxsolo");
                    var only = solo?.FirstChild;
                    if (Check(only != null, "reveal drill: found the only child"))
                    {
                        Check(Program.TryDelete(ui, only) == null, "reveal drill: only-child delete accepted");
                        Check(ReferenceEquals(tree.SelectedObject, solo),
                            "deleting an only child falls back to the container");
                        app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    }

                    // Attributes follow the same rule inside their own band.
                    var attrOwner = (XmlElement)tallRoot.SelectSingleNode("fuxattrs");
                    var pAttr = attrOwner?.GetAttributeNode("p");
                    var qAttr = attrOwner?.GetAttributeNode("q");
                    if (Check(pAttr != null && qAttr != null, "reveal drill: found the attributes"))
                    {
                        Check(Program.TryDelete(ui, pAttr) == null, "reveal drill: attribute delete accepted");
                        Check(ReferenceEquals(tree.SelectedObject, qAttr),
                            "deleting an attribute selects the next attribute, not the owner");
                        app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                        var rAttr = attrOwner.GetAttributeNode("r");
                        Check(Program.TryDelete(ui, rAttr) == null, "reveal drill: last-attribute delete accepted");
                        Check(ReferenceEquals(tree.SelectedObject, attrOwner.GetAttributeNode("q")),
                            "deleting the last attribute selects the one before it");
                        app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    }
                }
                // Back to the document every later section expects, clean.
                Check(Program.TryOpen(ui, file) == null, "reveal drill: the real document reopens");
                Check(!Program.Model.Dirty, "reveal drill: and reopens clean");
            }

            // --- 13e. Whitespace ownership: a container holding text or CDATA (#1). Insert and
            // delete have to agree about who owns the whitespace in front of a node, or an
            // insert-then-delete cycle returns a document one node short — silently, and in a
            // region the user never edited.
            //
            // Two containers, because the two halves of the fix are independently sufficient for
            // the reported document and would otherwise go unpinned: reverting either one alone
            // still passed the original repro (measured). <fuxblock> is block-formatted despite
            // its CDATA, so it must INDENT; <fuxinline> has text sharing a line, so it must not.
            // The oracle is OuterXml, not a node count: it catches whitespace that changed value
            // as well as whitespace that went missing.
            {
                var layoutPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fux-drill-layout.xml");
                System.IO.File.WriteAllText(layoutPath,
                    "<fuxlayout>\n"
                    + "  <fuxblock>\n    <a>1</a>\n    <b>2</b>\n    <![CDATA[data]]>\n  </fuxblock>\n"
                    + "  <fuxinline>text <i>x</i>\n    <c/>\n  </fuxinline>\n"
                    + "</fuxlayout>\n");

                if (Check(Program.TryOpen(ui, layoutPath) == null, "layout drill: the document opens"))
                {
                    var layoutRoot = Program.Model.Document.DocumentElement;
                    var block = (XmlElement)layoutRoot.SelectSingleNode("fuxblock");
                    var inline = (XmlElement)layoutRoot.SelectSingleNode("fuxinline");
                    if (Check(block != null && inline != null, "layout drill: found both containers"))
                    {
                        // The classification itself, named. This is the line that was wrong: any
                        // text child at all made a container inline, CDATA on its own line
                        // included.
                        Check(XmlLayout.ShouldIndent(block),
                            "a container holding CDATA on a line of its own is still laid out as a block");
                        Check(!XmlLayout.ShouldIndent(inline),
                            "a container whose text shares a line with an element stays inline");

                        // Block: insert adds the node AND its indentation, delete takes both back.
                        var blockBefore = block.OuterXml;
                        int blockKids = block.ChildNodes.Count;
                        Check(Program.TryInsert(ui, block, InsertKind.Element, InsertPos.Child, "fuxnew") == null,
                            "layout drill: insert into the block container");
                        Check(block.ChildNodes.Count == blockKids + 2,
                            $"the insert brings its own indentation ({blockKids} -> {block.ChildNodes.Count}, want +2)");
                        var blockNew = block.SelectSingleNode("fuxnew");
                        Check(blockNew != null && XmlLayout.LeadingWhitespace(blockNew) != null,
                            "...and the new node sits on a line of its own");
                        Check(Program.TryDelete(ui, blockNew) == null, "layout drill: delete it again");
                        Check(block.OuterXml == blockBefore,
                            $"insert then delete leaves the block container byte for byte ({block.ChildNodes.Count} of {blockKids} children)");

                        // Inline: insert adds only the node, and delete must take only the node.
                        // Unconditional whitespace reclaiming took a sibling's line break here.
                        var inlineBefore = inline.OuterXml;
                        int inlineKids = inline.ChildNodes.Count;
                        Check(Program.TryInsert(ui, inline, InsertKind.Element, InsertPos.Child, "fuxnew") == null,
                            "layout drill: insert into the inline container");
                        Check(inline.ChildNodes.Count == inlineKids + 1,
                            $"the insert adds no indentation there ({inlineKids} -> {inline.ChildNodes.Count}, want +1)");
                        var inlineNew = inline.SelectSingleNode("fuxnew");
                        Check(Program.TryDelete(ui, inlineNew) == null, "layout drill: delete it again");
                        Check(inline.OuterXml == inlineBefore,
                            $"insert then delete leaves the inline container byte for byte ({inline.ChildNodes.Count} of {inlineKids} children)");
                    }
                }
                Check(Program.TryOpen(ui, file) == null, "layout drill: the real document reopens");
                Check(!Program.Model.Dirty, "layout drill: and reopens clean");
            }

            // --- 14. The About box carries the attribution the MIT notice requires.
            // Not a cosmetic check: fux bundles Microsoft's engine, and this is the only notice a
            // user who never opens the repo will read. Asserting on the specific strings, so
            // dropping the upstream copyright line goes red here rather than shipping.
            {
                var about = Program.AboutText();
                Check(about.Contains("Copyright (c) 2026 Marcel Samek"), "About names fux's copyright");
                Check(about.Contains("Copyright (c) Microsoft Corporation"), "About names the upstream copyright");
                Check(about.Contains("MIT"), "About names the license");
                Check(about.Contains("Not endorsed by"), "About disclaims endorsement");
                // Read from the same accessor the About box uses, not from AssemblyVersion:
                // the two say different things now. A build from source stamps `git describe`
                // into InformationalVersion, while AssemblyVersion stays at the numeric
                // placeholder, so comparing against AssemblyVersion here would assert that the
                // About box carries 0.0.0 — which is exactly the string #26 was about.
                var version = Program.VersionString;
                Check(about.Contains("fux " + version), $"About carries the build version ({version})");
                Check(Program.UsageText().StartsWith("fux " + version),
                    "...and the usage text says the same thing");
                // The SDK appends "+<sha>" to InformationalVersion unless told not to, and that
                // would break release.yml's exact version assertion on the next release rather
                // than here. Fux.csproj turns it off; this is what notices if that stops working.
                Check(!version.Contains("+"),
                    $"the build version carries no '+<sha>' suffix (was '{version}')");
                // MessageBox sizes itself to the longest line; keep it inside 80 columns.
                int widest = 0;
                foreach (var line in about.Split('\n')) if (line.Length > widest) widest = line.Length;
                Check(widest <= 60, $"About fits a narrow terminal (widest line {widest} <= 60)");
            }

            // --- 14a. Esc never quits, and no exit gets past the unsaved-changes gate.
            // One section because they are one bug: v2 binds Esc to Command.Quit at the
            // application scope, which stopped the top runnable directly — it neither asked
            // about unsaved work nor could be asked. Two Escs to back out of an edit (the
            // first cancels it, the second lands on the tree) threw the document away.
            // Ordered before §15 because that one requests a stop and never takes it back.
            {
                Check(!ui.Top.StopRequested, "esc drill: nothing has requested a stop yet");
                // MenuIsOpen's disjunction is what lets Esc through to the menu. If a
                // popover were always reported active, it would let Esc through always and
                // the no-quit rule would be dead code that still reads as if it worked.
                Check(app.Popovers.GetActivePopover() == null,
                    "no popover is active while the menu is closed");

                // Give the key something to destroy: an unsaved edit. Without this the
                // section would prove only that Esc does nothing to a document that had
                // nothing to lose.
                ui.Tree.SetFocus();
                Check(Program.TryInsert(ui, Program.Model.Document.DocumentElement,
                        InsertKind.Element, InsertPos.Child, "fuxesc") == null, "esc drill: document edited");
                Check(Program.Model.Dirty, "esc drill: there are unsaved changes to lose");

                app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                Check(!ui.Top.StopRequested, "Esc does not request a stop");
                Check(Program.Model.Dirty, "...and the unsaved edit is still there");
                // Not just "the app is still up": say which key does quit, where the user
                // is already looking. (A bare "did Esc get consumed?" proves nothing — the
                // app-scope quit binding consumes it too, and that is the bug.)
                Check(ui.ValueView.Title.Contains("Esc does not quit"),
                    $"Esc says what does quit instead (title was '{ui.ValueView.Title}')");

                // The state #24 quit from, and the one neither check above can reach: an edit
                // is live but the value pane does not hold the keyboard. Every keyboard route
                // into edit mode leaves the pane focused, which is why §7's two Esc checks
                // pass with that bug present. In real use a mouse click opens the gap;
                // SetFocus opens it here without needing mouse injection.
                var escEditable = FindEditable(Program.Model.Document?.DocumentElement);
                if (escEditable != null)
                {
                    ui.Tree.EnsureVisible(escEditable);
                    ui.Tree.SelectedObject = escEditable;
                    ui.Tree.SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(Key.F2);
                    if (Check(ui.Editing, "esc drill: an edit is live"))
                    {
                        // Containment first: the gap should not open at all. Asserted rather
                        // than assumed, because if focus ever escapes again the Esc rule below
                        // is the only thing left between the user and a silent exit.
                        ui.Tree.SetFocus();
                        Check(ui.ValueView.HasFocus && !ui.Tree.HasFocus,
                            "a live edit keeps the keyboard: focus will not move to the tree "
                            + $"(pane {ui.ValueView.HasFocus}, tree {ui.Tree.HasFocus})");
                        Check(Program.Model.Dirty, "esc drill: there is still unsaved work to lose");

                        // Now the backstop, on its own. Containment makes "edit live, pane
                        // unfocused" unreachable through the UI — which would leave the rule
                        // that decides Esc in *every* state asserted by nothing, and that rule
                        // is the one whose absence shipped as #24. So defeat containment
                        // deliberately and press the key: whichever of the two guards fails,
                        // the other still has to refuse to take the document down.
                        ui.Tree.CanFocus = true;
                        ui.Tree.SetFocus();
                        if (Check(ui.Tree.HasFocus && ui.Editing,
                                $"esc drill: forced the #24 state (tree {ui.Tree.HasFocus}, editing {ui.Editing})"))
                        {
                            app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                            Check(!ui.Top.StopRequested,
                                "Esc with an edit live and the pane unfocused does not request a stop");
                            Check(!ui.Editing, "...it cancels the edit instead");
                            Check(Program.Model.Dirty, "...and the unsaved document is still here");
                        }
                        // However that went, do not leave a live edit behind: everything after
                        // this section runs through the app-wide handler, which is inert on
                        // most keys while ui.Editing.
                        if (ui.Editing) app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                        Check(!ui.Editing, "esc drill: no live edit left behind");
                    }
                }

                // The one Esc fux must not eat: a menu opened by accident is dismissed with
                // it, and the MenuBar's own binding — not the app-scope one — is what does
                // that. This is the check that keeps the fix from being a blanket swallow.
                app.Keyboard.RaiseKeyDownEvent(Key.F9);
                app.RaiseIteration(); // popover show is processed by the main loop
                app.LayoutAndDraw(true);
                if (Check(Program.MenuIsOpen(ui), "esc drill: the menu is open"))
                {
                    app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                    app.RaiseIteration();
                    app.LayoutAndDraw(true);
                    Check(!Program.MenuIsOpen(ui), "Esc still closes the menu");
                    Check(!ui.Top.StopRequested, "...and closing it does not request a stop either");
                }

                // And the key that does quit has to ask first, or the Esc rule above would
                // only be moving the same loss one keystroke away. Driven with the real ^Q
                // through the real prompt, which is the whole point: a check on
                // ConfirmDiscard alone would pass while nothing called it.
                //
                // The prompt is a MessageBox, whose nested Run would normally block the key
                // injector this harness depends on — but that nested loop is a real main
                // loop, so a timeout armed beforehand fires inside it and answers. Esc on
                // the prompt is Cancel (ModalQuery returns null, ConfirmDiscard reads that
                // as choice 2), which is the answer under test.
                int escs = 0, depthWhilePrompting = -1;
                // Held so it can be removed below. A timeout that outlives its modal is not
                // idle: it fires inside the NEXT nested loop and Escs whatever is open then,
                // which is how §14b's dialog was being shut before its own probe could run.
                var escTok = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
                {
                    escs++;
                    // ModalDepth is raised by nothing but ModalQuery, so reading it from
                    // inside the nested loop is what proves a *prompt* stopped the quit,
                    // rather than ^Q having quietly done nothing at all.
                    if (depthWhilePrompting < 0) depthWhilePrompting = ui.ModalDepth;
                    app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                    // Escape hatch, so a prompt that will not take Esc fails the run instead
                    // of hanging it. Never ui.Top — that is the session the drill is in.
                    if (escs > 20 && !ReferenceEquals(app.TopRunnable, ui.Top))
                        app.RequestStop(app.TopRunnable);
                    return escs <= 30;
                });
                app.Keyboard.RaiseKeyDownEvent(Key.Q.WithCtrl);
                app.RemoveTimeout(escTok);
                Check(depthWhilePrompting == 1,
                    $"^Q on unsaved changes prompts (modal depth while quitting was {depthWhilePrompting})");
                Check(escs == 1, $"the prompt closed on the first Esc (took {escs})");
                Check(!ui.Top.StopRequested, "a cancelled prompt refuses the quit");
                Check(Program.Model.Dirty, "...and leaves the unsaved edit alone");

                // The other half: with nothing to lose there is nothing to ask about, so ^Q
                // on a saved document has to stay one keystroke. §15 quits for real.
                Check(Program.TryOpen(ui, file) == null, "esc drill: the document reopens clean");
                Check(!Program.Model.Dirty, "esc drill: nothing left to lose");

                // #22: quitting with an uncommitted edit. The model is clean here — the typed
                // text is in the pane and has not reached the DOM — which is exactly the case
                // the gate used to wave straight through, taking the paragraph with it. Run on
                // a clean document on purpose: a dirty one would prompt for the document and
                // the check would pass without the edit being noticed at all.
                var quitEditable = FindEditable(Program.Model.Document?.DocumentElement);
                if (quitEditable != null)
                {
                    ui.Tree.EnsureVisible(quitEditable);
                    ui.Tree.SelectedObject = quitEditable;
                    ui.Tree.SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(Key.F2);
#pragma warning disable CS0618 // TextView: see the BuildUi note on the obsolete flag
                    ((TextView)ui.ValueView).Text = "fuxuncommitted";
#pragma warning restore CS0618
                    if (Check(ui.Editing && !Program.Model.Dirty,
                            "esc drill: an uncommitted edit, on a document the model calls clean"))
                    {
                        int qEscs = 0, qDepth = -1;
                        // Same shape as the ^Q probe above, and removed just as carefully: a
                        // timeout left armed fires inside the NEXT nested loop and Escs
                        // whatever is open there.
                        var qTok = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
                        {
                            qEscs++;
                            if (qDepth < 0) qDepth = ui.ModalDepth;
                            app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                            if (qEscs > 20 && !ReferenceEquals(app.TopRunnable, ui.Top))
                                app.RequestStop(app.TopRunnable);
                            return qEscs <= 30;
                        });
                        app.Keyboard.RaiseKeyDownEvent(Key.Q.WithCtrl);
                        app.RemoveTimeout(qTok);
                        Check(qDepth == 1,
                            $"^Q on an uncommitted edit prompts rather than discarding it (depth was {qDepth})");
                        Check(!ui.Top.StopRequested, "...and cancelling the prompt keeps the document open");
                        // Cancel leaves the edit live, by design — that is what Cancel means.
                        Check(ui.Editing, "...with the edit still live, untouched by the question");
                    }
                    if (ui.Editing) app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                    Check(!ui.Editing && !Program.Model.Dirty, "esc drill: the uncommitted edit is cleaned up");
                }
            }

            // --- 14b. Named blocks. The config is re-read on every ^N, so the drill writes a
            // fixture and reopens the dialog rather than restarting fux. FUX_CONFIG_DIR was
            // pointed at a scratch directory at the top of Run, so nothing here can read — or
            // be perturbed by — the config of whoever is running the drill.
            // ConfigPath rather than Combine(ConfigDir(), FileName): it is the same value,
            // null-guarded, and computed in one place instead of two that can drift.
            var cfgPath = Snippets.ConfigPath();
            {
                // No file at all: the feature is invisible, and says nothing.
                if (System.IO.File.Exists(cfgPath)) System.IO.File.Delete(cfgPath);
                var absent = Snippets.Load();
                Check(absent.Missing && absent.Blocks.Count == 0 && absent.Problem == null,
                    "no config file: no blocks and no complaint");

                // A clean config.
                System.IO.File.WriteAllText(cfgPath, GoodConfig);
                var set = Snippets.Load();
                Check(!set.Missing && set.Problem == null && set.Blocks.Count == 3,
                    $"a clean config loads every block (got {set.Blocks.Count}, problem '{set.Problem}')");
                Check(set.Blocks.Count == 3 && set.Blocks[0].Name == "Footnote"
                      && set.Blocks[1].Name == "Sidebar" && set.Blocks[2].Name == "Prefixed",
                    "blocks keep the order the file lists them in");
                Check(set.Blocks.Count > 0 && set.Blocks[0].Template.LocalName == "block"
                      && set.Blocks[0].Template.GetAttribute("kind") == "footnote",
                    "a block keeps its element name and attributes");

                // One bad snippet must not cost the others. Two are wrong here for different
                // reasons (no name, two element children); both are skipped, both good ones stay.
                System.IO.File.WriteAllText(cfgPath, MixedConfig);
                var mixed = Snippets.Load();
                Check(mixed.Blocks.Count == 2 && mixed.Blocks[0].Name == "Keeper"
                      && mixed.Blocks[1].Name == "AlsoKeeper",
                    $"a partly broken config still loads its good blocks (got {mixed.Blocks.Count})");
                Check(mixed.Problem != null && mixed.Problem.Contains("2"),
                    $"...and reports how many it skipped ('{mixed.Problem}')");

                // A file that will not parse at all: a complaint, no blocks, and NOT an
                // exception — a typo in a config must never stop fux opening a document.
                System.IO.File.WriteAllText(cfgPath, "<snippets><snippet name='x'></snippets>");
                var broken = Snippets.Load();
                Check(!broken.Missing && broken.Blocks.Count == 0 && broken.Problem != null,
                    $"an unparseable config reports instead of throwing ('{broken.Problem}')");

                // Inserting one. The host is an element the drill just made, so its layout is
                // known on every fixture: freshly inserted and childless, which is the case
                // XmlLayout indents into.
                System.IO.File.WriteAllText(cfgPath, GoodConfig);
                var blocks = Snippets.Load().Blocks;
                // The LIVE document, not the `doc` captured at the top of Run: §13c and §14a
                // both reopen the file, so that reference is a detached document by now. Every
                // DOM assertion below would pass on it while the saved bytes showed nothing —
                // which is exactly how this was caught.
                var rootB = Program.Model.Document?.DocumentElement;
                if (rootB != null && blocks.Count == 3)
                {
                    Check(Program.TryInsert(ui, rootB, InsertKind.Element, InsertPos.Child, "fuxblockhost") == null,
                        "block drill: host element inserted");
                    var host = LastShown(rootB) as XmlElement;

                    Check(Program.TryInsertBlock(ui, host, InsertPos.Child, blocks[1]) == null,
                        "a named block inserts");
                    var sidebar = host == null ? null : LastShown(host) as XmlElement;
                    Check(sidebar != null && sidebar.LocalName == "sidebar",
                        "the block's root element lands in the document");
                    Check(sidebar != null && ChildElementNames(sidebar) == "title|body",
                        $"...with its whole subtree, in order (got '{(sidebar == null ? "" : ChildElementNames(sidebar))}')");
                    Check(sidebar != null && sidebar.SelectSingleNode("*[local-name()='body']")?.InnerText == "here",
                        "...and the text inside it");

                    // Layout: every level on its own line, each deeper than its parent. Read
                    // from the SAVED BYTES, because that is the only place indentation is
                    // observable — a DOM comparison would pass on a fragment written flat.
                    var probeB = System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(Program.Model.FileName), "fux_drill_block.xml");
                    Program.Model.SaveCopy(probeB);
                    var savedB = System.IO.File.ReadAllText(probeB);
                    int iSide = IndentOf(savedB, "<sidebar"), iTitle = IndentOf(savedB, "<title");
                    Check(iSide >= 0 && iTitle > iSide,
                        $"an inserted block is indented level by level (sidebar at {iSide}, title at {iTitle})");
                    System.IO.File.Delete(probeB);

                    // One command, however many nodes: a single ^Z takes the whole subtree.
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Check(host != null && XmlLayout.IsChildless(host),
                        "one undo removes the whole block, not one node of it");
                    Check(host != null && host.IsEmpty, "...and the host is spelled <a/> again");

                    // A prefix declared in the config rides along with the block.
                    Check(Program.TryInsertBlock(ui, host, InsertPos.Child, blocks[2]) == null,
                        "a namespaced block inserts");
                    var tag = host == null ? null : LastShown(host) as XmlElement;
                    Check(tag != null && tag.NamespaceURI == "uri:block" && tag.Prefix == "z",
                        $"...keeping its namespace (got '{tag?.NamespaceURI}')");
                    Check(tag != null && tag.GetAttribute("xmlns:z") == "uri:block",
                        "...and carrying its declaration into the document");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl); // undo the namespaced block
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl); // undo the host
                }

                // --- The Snippets panel. A LONG config on purpose: the first version of
                // this feature put the list in a corner of the Insert dialog and silently
                // clipped it to two entries, and the drill stayed green because its fixture
                // had three blocks and the scroll behaviour was only ever asserted as
                // arithmetic. So the fixture here overflows the panel, and the oracle is the
                // rows actually on screen.
                System.IO.File.WriteAllText(cfgPath, ManyConfig(40));
                var many = Snippets.Load();
                Check(many.Blocks.Count == 40, $"a 40-block config loads whole (got {many.Blocks.Count})");

                // The shared vocabulary means what it says. Direct oracle: the indirect one
                // (an insert landing in the wrong parent) catches a broken mapping only by
                // side effect, and says nothing about which way it broke.
                Check(Program.PosAt(0) == InsertPos.After, "Below inserts after the selection");
                Check(Program.PosAt(1) == InsertPos.Before, "Above inserts before it");
                Check(Program.PosAt(2) == InsertPos.Child, "Child inserts into it");
                Check(Program.PosLabels.Length == 3 && Program.PosLabels[0] == "Below",
                    "...and Below is the default, being first");

                string shown = null;
                int rowsShown = 0, listTop = -1, listBottom = -1;
                bool opened = RunSnippetPanel(app, ui, () =>
                {
                    app.LayoutAndDraw(true);
                    shown = ScreenText(app);
                    for (int i = 0; i < 40; i++)
                    {
                        if (CountOnScreen(app, SnippetName(i)) <= 0) continue;
                        rowsShown++;
                        if (listTop < 0) listTop = i;
                        listBottom = i;
                    }
                });
                Check(opened, $"the Snippets panel opens [{_lastDialogDiag}]");
                Check(shown != null && shown.Contains(Program.PosLabels[0])
                      && shown.Contains(Program.PosLabels[2]),
                    "...with the position row");
                // The bug this replaces showed TWO of nineteen. Ten is a floor, not a target:
                // it says the list got a panel's worth of height rather than a corner's.
                Check(rowsShown >= 10,
                    $"...and a list deep enough to work in ({rowsShown} of 40 rows on screen)");
                Check(listTop == 0, $"...starting at the first snippet in file order (row 0 was {listTop})");

                // Reachable: End jumps to the last entry, and it must be ON SCREEN afterwards —
                // the selection moving is not the same as the user being able to see it.
                bool lastVisible = false, lastSelected = false;
                RunSnippetPanel(app, ui, () =>
                {
                    var list = FindById(app.TopRunnable as View, Program.SnippetListId) as ListView;
                    if (list == null) return;
                    list.MoveEnd();
                    app.LayoutAndDraw(true);
                    lastSelected = list.SelectedItem == 39;
                    lastVisible = CountOnScreen(app, SnippetName(39)) > 0;
                });
                Check(lastSelected, "the last of 40 snippets can be selected");
                Check(lastVisible, "...and is on screen when it is");

                // Enter on the list commits, so the whole gesture is ^B, arrow, Enter — no Tab
                // and no button. 2.4.17 has no OpenSelectedItem, so this rides on Accept
                // bubbling to the default button, which is worth pinning rather than assuming.
                // Anchored on a CHILD, not the document element: the default position is
                // Below, and a sibling of the root is refused — which would fail this check
                // for a reason that has nothing to do with Enter, and would leave an error
                // modal open with no timeout armed to answer it.
                XmlElement anchorE = null;
                foreach (XmlNode c in Program.Model.Document.DocumentElement.ChildNodes)
                    if (c is XmlElement ce) { anchorE = ce; break; }
                var parentE = (XmlElement)(anchorE?.ParentNode ?? Program.Model.Document.DocumentElement);
                var beforeEnter = parentE.ChildNodes.Count;
                ui.Tree.SelectedObject = (XmlNode)anchorE ?? Program.Model.Document.DocumentElement;
                string enterDiag = "";
                RunSnippetPanel(app, ui, () =>
                {
                    var list = FindById(app.TopRunnable as View, Program.SnippetListId) as ListView;
                    var focused = (app.TopRunnable as View)?.MostFocused;
                    enterDiag = $"list={(list == null ? "null" : "found")} listFocused={list?.HasFocus} "
                              + $"focused={focused?.GetType().Name}/{focused?.Id}";
                    if (list != null) list.SelectedItem = 0;
                    bool handled = app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                    enterDiag += $" enterHandled={handled} depthAfter={ui.ModalDepth}";
                }, escOut: false);
                Check(parentE.ChildNodes.Count > beforeEnter,
                    $"Enter on the snippet list inserts without touching a button [{enterDiag}]");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);

                // And the Insert dialog is its old self again: no block group, no snippets.
                string insert = null;
                RunInsertDialog(app, ui, () => { app.LayoutAndDraw(true); insert = ScreenText(app); });
                Check(insert != null && !insert.Contains(SnippetName(0)),
                    "the Insert dialog carries no snippets");
                Check(insert != null && insert.Contains("Name (element/attribute/PI):")
                      && insert.Contains(Program.PosLabels[0]),
                    "...and is the Insert dialog, with the shared position words");

                // Undo leaves the model dirty even when the content matches, and §15's ^Q has
                // no timeout armed to answer a prompt — it would hang the run, not fail it.
                Check(Program.TryOpen(ui, file) == null, "block drill: the document reopens clean");
            }

            // --- 15. F9 focuses the menu bar; ^Q requests stop. The document is clean by
            // now (§14a reopened it), so ^Q must not prompt — and with no timeout armed to
            // answer one, a prompt here would hang the run rather than quietly pass.
            bool f9Handled = app.Keyboard.RaiseKeyDownEvent(Key.F9);
            app.RaiseIteration(); // popover show is processed by the main loop
            app.LayoutAndDraw(true);
            var popover = app.Popovers.GetActivePopover();
            Check(ui.Menu.Active || ui.Menu.IsOpen() || ui.Menu.HasFocus || popover != null,
                $"F9 activates the menu bar (handled={f9Handled}, popover={(popover == null ? "none" : popover.GetType().Name)})");
            app.Keyboard.RaiseKeyDownEvent(Key.Q.WithCtrl);
            Check(ui.Top.StopRequested, "^Q requests stop");

            // Last, because it can only be judged once every save in the run has happened: an
            // imported source file must still be byte-for-byte what it was. The earlier
            // retarget check states the intent; this states the consequence, and it is the one
            // that would catch a ^S path that ignored the retarget and wrote the .csv anyway.
            if (file != null && sourceBytes != null
                && Import.IsImport(System.IO.Path.GetExtension(file).ToLowerInvariant()))
            {
                Check(System.IO.File.Exists(file) && SameBytes(sourceBytes, System.IO.File.ReadAllBytes(file)),
                    "no save in the whole run touched the imported source file");
            }

            app.End(token);
            app.Dispose();

            Console.Error.WriteLine(_failures == 0 ? "DRILL: PASS" : $"DRILL: FAIL ({_failures} failed)");
            return _failures == 0 ? 0 : 1;
        }

        // Every cell must carry an attribute (painted) whose background is one of the theme's —
        // the v2 equivalent of the v1 harness's "0 unpainted cells" check, plus proof that the
        // pinned TrueColor palette (not the terminal's) is what's on screen.
        private static void CheckCells(Terminal.Gui.App.IApplication app)
        {
            var allowed = new HashSet<Color>
            {
                Theme.Content.Normal.Background, Theme.Content.Focus.Background,
                Theme.Bar.Normal.Background, Theme.Bar.Focus.Background,
                Theme.Flat.Normal.Background, Theme.Flat.Focus.Background,
            };
            var cells = app.Driver.GetOutputBuffer().Contents;
            int unpainted = 0;
            var foreign = new HashSet<Color>();
            for (int r = 0; r < cells.GetLength(0); r++)
            {
                for (int c = 0; c < cells.GetLength(1); c++)
                {
                    var attr = cells[r, c].Attribute;
                    if (attr == null) { unpainted++; continue; }
                    if (!allowed.Contains(attr.Value.Background)) foreign.Add(attr.Value.Background);
                }
            }
            Check(unpainted == 0, $"all cells painted (unpainted: {unpainted})");
            Check(foreign.Count == 0, $"all backgrounds from the theme palette (foreign: {string.Join(" ", foreign)})");
        }

        // Prove the vim-xml accent mapping is live: element rows blue, attribute rows yellow
        // (also the hotkey color), and — when diagnostics exist — error rows red.
        private static void CheckAccents(Terminal.Gui.App.IApplication app, bool expectErrors)
        {
            var blue = new Color("#268bd2");
            var yellow = new Color("#b58900");
            var red = new Color("#dc322f");
            bool sawBlue = false, sawYellow = false, sawRed = false;
            var cells = app.Driver.GetOutputBuffer().Contents;
            for (int r = 0; r < cells.GetLength(0); r++)
            {
                for (int c = 0; c < cells.GetLength(1); c++)
                {
                    var attr = cells[r, c].Attribute;
                    if (attr == null) continue;
                    var fg = attr.Value.Foreground;
                    if (fg.Equals(blue)) sawBlue = true;
                    else if (fg.Equals(yellow)) sawYellow = true;
                    else if (fg.Equals(red)) sawRed = true;
                }
            }
            Check(sawBlue, "element rows render blue");
            Check(sawYellow, "attribute rows render yellow");
            if (expectErrors) Check(sawRed, "error rows render red");
        }

        // The rows the tree draws under a node, as "label|label|…" — the labels the user sees,
        // in the order they are drawn. Comparing the whole string makes an unexpected extra row
        // fail as loudly as a missing one, which a per-row Contains() would not.
        private static string Rows(XmlNode n)
            => string.Join("|", System.Linq.Enumerable.Select(Program.GetChildren(n), Program.GetLabel));

        /// <summary>The same rows' values, for asserting what each one carries.</summary>
        private static string Values(XmlNode n)
            => string.Join("|", System.Linq.Enumerable.Select(Program.GetChildren(n), Program.GetValue));

        /// <summary>Escape the line breaks in a failure message, so it stays on its own line.</summary>
        private static string Vis(string s) => (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n");

        private static string ScreenRow(Terminal.Gui.App.IApplication app, int row)
            => ScreenText(app).Split('\n')[row];

        /// <summary>The first screen row containing <paramref name="s"/>, or -1.</summary>
        private static int RowIndexOf(Terminal.Gui.App.IApplication app, string s)
        {
            var rows = ScreenText(app).Split('\n');
            for (int i = 0; i < rows.Length; i++)
                if (rows[i].Contains(s)) return i;
            return -1;
        }

        private static string ScreenText(Terminal.Gui.App.IApplication app)
        {
            var cells = app.Driver.GetOutputBuffer().Contents;
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < cells.GetLength(0); r++)
            {
                for (int c = 0; c < cells.GetLength(1); c++)
                    sb.Append(string.IsNullOrEmpty(cells[r, c].Grapheme) ? " " : cells[r, c].Grapheme);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // Rows the tree holds, scroll position irrelevant — v2 sizes its content from the line
        // map, one entry per branch. A move that rebuilt only one of its two containers leaves
        // the other holding a branch for a child it no longer has, so the node is branched
        // twice and this reads one too many. The on-screen count below can't see that on its
        // own: EnsureVisible scrolls to the *first* line-map match, which can leave the stale
        // twin below the fold.
        private static int TreeRows(Program.Ui ui) => ui.Tree.GetContentSize().Height;

        // Sibling adjacency *as the tree shows it*. Documents load with their whitespace
        // preserved, so a node's raw NextSibling is normally the next line's indentation, not
        // the next row. Reordering is defined on the display band, so that is what to assert on.
        private static XmlNode NextShown(XmlNode n)
        {
            for (var s = n?.NextSibling; s != null; s = s.NextSibling)
                if (Program.IsShown(s)) return s;
            return null;
        }

        // The first node with no rows under it, depth-first. Used to check the half of the
        // delete rule that says nothing should be asked.
        private static XmlNode FindLeaf(XmlNode n)
        {
            if (n == null) return null;
            bool any = false;
            foreach (var c in Program.GetChildren(n))
            {
                any = true;
                var hit = FindLeaf(c);
                if (hit != null) return hit;
            }
            return any ? null : n;
        }

        private static XmlNode PrevShown(XmlNode n)
        {
            for (var p = n?.PreviousSibling; p != null; p = p.PreviousSibling)
                if (Program.IsShown(p)) return p;
            return null;
        }

        private static XmlNode LastShown(XmlNode parent)
        {
            XmlNode last = null;
            foreach (XmlNode c in parent.ChildNodes)
                if (Program.IsShown(c)) last = c;
            return last;
        }

        // "yyyyMMdd-HHmmss", checked by shape. Enough to catch a stamp that lost its date half
        // or arrived as a tick count, without pinning the drill to the clock it runs on.
        private static bool IsStamp(string s)
        {
            if (s.Length != 15 || s[8] != '-') return false;
            for (int i = 0; i < s.Length; i++)
                if (i != 8 && (s[i] < '0' || s[i] > '9')) return false;
            return true;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // The whole source line that `needle` appears on, or "" — used to check that an inserted
        // node got a line to itself rather than being jammed onto a sibling's.
        private static string LineWith(string text, string needle)
        {
            int i = text.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return "";
            int start = text.LastIndexOf('\n', i) + 1;
            int end = text.IndexOf('\n', i);
            return end < 0 ? text.Substring(start) : text.Substring(start, end - start).TrimEnd('\r');
        }

        // The line holding the start tag of `name`, matched at a tag boundary. A plain substring
        // search is not enough: "<Employee" also matches "<Employees", which in emp.xml is the
        // root element sitting at column zero — enough to make an indentation check compare
        // against the wrong line.
        private static string LineWithTag(string text, string name)
        {
            string open = "<" + name;
            for (int i = text.IndexOf(open, StringComparison.Ordinal); i >= 0;
                     i = text.IndexOf(open, i + 1, StringComparison.Ordinal))
            {
                int after = i + open.Length;
                if (after >= text.Length) break;
                char c = text[after];
                if (c == '>' || c == '/' || c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    int start = text.LastIndexOf('\n', i) + 1;
                    int end = text.IndexOf('\n', i);
                    return end < 0 ? text.Substring(start) : text.Substring(start, end - start).TrimEnd('\r');
                }
            }
            return "";
        }

        // How many rows on screen draw a given label. Catches the opposite failure: a node that
        // landed inside a collapsed container is branched correctly but drawn nowhere, so it
        // reads 0. TreeView<T> exposes no enumeration of its branches, hence counting pixels.
        // Fixtures for the named-block drill. Written to the scratch config directory rather
        // than shipped as files: the config path is what is under test, so the test has to put
        // the file there itself.
        private const string GoodConfig = @"<snippets>
  <snippet name=""Footnote"">
    <block kind=""footnote""/>
  </snippet>
  <snippet name=""Sidebar"">
    <sidebar>
      <title/>
      <body>here</body>
    </sidebar>
  </snippet>
  <snippet name=""Prefixed"">
    <z:tag xmlns:z=""uri:block""/>
  </snippet>
</snippets>";

        // Two good blocks and two that cannot load, for two different reasons.
        private const string MixedConfig = @"<snippets>
  <snippet name=""Keeper""><good/></snippet>
  <snippet><noname/></snippet>
  <snippet name=""TooMany""><a/><b/></snippet>
  <snippet name=""AlsoKeeper""><fine/></snippet>
</snippets>";

        private static string ChildElementNames(XmlElement e)
        {
            var names = new List<string>();
            foreach (XmlNode c in e.ChildNodes)
                if (c is XmlElement ce) names.Add(ce.LocalName);
            return string.Join("|", names);
        }

        /// <summary>
        /// Leading spaces on the line holding <paramref name="marker"/>, or -1 if absent. The
        /// oracle for indentation has to be the written text: a DOM comparison passes just as
        /// happily on a fragment written flat.
        /// </summary>
        private static int IndentOf(string text, string marker)
        {
            int i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return -1;
            int lineStart = text.LastIndexOf('\n', i) + 1;
            int spaces = 0;
            for (int p = lineStart; p < i && (text[p] == ' ' || text[p] == '\t'); p++) spaces++;
            return spaces;
        }

        /// <summary>
        /// Open the Insert dialog, run <paramref name="probe"/> inside it, and Esc back out.
        /// ^N's nested Run blocks the key injector, but it is a real main loop, so a timeout
        /// armed beforehand fires inside it — the §14a recipe. The escape hatch means a dialog
        /// that will not close fails the run instead of hanging CI.
        /// </summary>
        private static string _lastDialogDiag = "";

        /// <summary>Row label for entry i of the generated long config. Unique per row, so a
        /// count over the screen is a count of exactly that row.</summary>
        private static string SnippetName(int i) => $"fuxsnip{i:D2}";

        /// <summary>A config with <paramref name="n"/> blocks — long enough to overflow the
        /// panel, which is the case the first version of this feature got wrong.</summary>
        private static string ManyConfig(int n)
        {
            var sb = new System.Text.StringBuilder("<snippets>\n");
            for (int i = 0; i < n; i++)
                sb.Append($"  <snippet name=\"{SnippetName(i)}\"><b{i:D2}/></snippet>\n");
            return sb.Append("</snippets>").ToString();
        }

        /// <summary>
        /// Open the Snippets panel, run <paramref name="probe"/> inside it, and (unless the
        /// probe closes it itself) Esc back out. Same recipe as RunInsertDialog.
        /// </summary>
        private static bool RunSnippetPanel(Terminal.Gui.App.IApplication app, Program.Ui ui,
                                            Action probe, bool escOut = true)
        {
            int ticks = 0;
            bool probed = false;
            var tok = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                ticks++;
                if (!probed && ui.ModalDepth > 0) { probed = true; probe(); }
                if (escOut) app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                if (ticks > 20 && !ReferenceEquals(app.TopRunnable, ui.Top))
                    app.RequestStop(app.TopRunnable);
                return ticks <= 30;
            });
            _lastDialogDiag = $"sel={(ui.Tree.SelectedObject == null ? "null" : "set")} depth={ui.ModalDepth}";
            Program.StartSnippets(ui);
            app.RemoveTimeout(tok); // never leave one armed for the next modal — see §14a
            _lastDialogDiag += $" ticks={ticks} probed={probed}";
            return probed;
        }

        private static bool RunInsertDialog(Terminal.Gui.App.IApplication app, Program.Ui ui, Action probe)
        {
            int ticks = 0;
            bool probed = false;
            var tok = app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
            {
                ticks++;
                if (!probed && ui.ModalDepth > 0) { probed = true; probe(); }
                app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                if (ticks > 20 && !ReferenceEquals(app.TopRunnable, ui.Top))
                    app.RequestStop(app.TopRunnable);
                return ticks <= 30;
            });
            // Called directly rather than through ^N: what is under test is the dialog the
            // config produces, and going through the key binding would fold a second question
            // into every assertion below. StartInsert blocks in RunModal until the timeout
            // above Escs out of it.
            _lastDialogDiag = $"sel={(ui.Tree.SelectedObject == null ? "null" : "set")} editing={ui.Editing} depthBefore={ui.ModalDepth}";
            Program.StartInsert(ui);
            app.RemoveTimeout(tok); // never leave one armed for the next modal — see §14a
            _lastDialogDiag += $" ticks={ticks} probed={probed}";
            return probed;
        }

        /// <summary>Depth-first search for a view by Id, for reaching into a live modal.</summary>
        private static View FindById(View root, string id)
        {
            if (root == null) return null;
            if (root.Id == id) return root;
            foreach (var sub in root.SubViews)
            {
                var hit = FindById(sub, id);
                if (hit != null) return hit;
            }
            return null;
        }

        private static int CountOnScreen(Terminal.Gui.App.IApplication app, string label)
        {
            var screen = ScreenText(app);
            int n = 0;
            for (int i = screen.IndexOf(label, StringComparison.Ordinal); i >= 0;
                 i = screen.IndexOf(label, i + label.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        private static int IndexOfAttr(XmlElement owner, XmlAttribute a)
        {
            for (int i = 0; i < owner.Attributes.Count; i++)
                if (ReferenceEquals(owner.Attributes[i], a)) return i;
            return -1;
        }

        // First node in the displayed tree whose value fux lets you edit (DFS in tree order,
        // so attributes come first, exactly as the tree presents them).
        private static XmlNode FindEditable(XmlNode n)
        {
            if (n == null) return null;
            if (EditNodeValue.CanEditValue(n)) return n;
            foreach (var c in Program.GetChildren(n))
            {
                var hit = FindEditable(c);
                if (hit != null) return hit;
            }
            return null;
        }

        // Like FindEditable, but insists on a node that actually holds text: the clipboard
        // checks need something to copy, and an empty value is the one case ^C refuses.
        private static XmlNode FindValuedNode(XmlNode n)
        {
            if (n == null) return null;
            if (EditNodeValue.CanEditValue(n) && !string.IsNullOrEmpty(EditNodeValue.GetNodeValue(n))) return n;
            foreach (var c in Program.GetChildren(n))
            {
                var hit = FindValuedNode(c);
                if (hit != null) return hit;
            }
            return null;
        }

        private static bool Check(bool ok, string what)
        {
            if (!ok) _failures++;
            Console.Error.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
            return ok;
        }
    }
}
