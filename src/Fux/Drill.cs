using System;
using System.Collections.Generic;
using System.Xml;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
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
            var ui = Program.BuildUi(file);
            var app = ui.App;
            app.Driver.SetScreenSize(100, 30);
            var token = app.Begin(ui.Top);
            app.LayoutAndDraw(true);

            // --- 1. Chrome: bordered panes render with their titles; summary on the error pane.
            // The tree pane title doubles as the document title (file basename).
            var docTitle = file == null ? "Tree" : System.IO.Path.GetFileName(file);
            var screen = ScreenText(app);
            // Row 1 is the tree pane's top border, where its title is drawn (row 0 is the menu).
            // Searching the whole screen would be a weak oracle: the validation pane quotes the
            // file name in its diagnostics, so a mangled title still "appears" somewhere. The
            // scratch copy's name carries an underscore on purpose — see the note in Main.
            Check(ScreenRow(app, 1).Contains(docTitle), $"tree pane titled '{docTitle}'");
            Check(screen.Contains("Value"), "value pane title renders");
            Check(screen.Contains("Validation:") || screen.Contains("(no file loaded)"), "validation summary renders");
            Check(screen.Contains("┌") && screen.Contains("│") && screen.Contains("┘"), "pane borders render");

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

                app.Keyboard.RaiseKeyDownEvent(Key.S.WithCtrl);
                Check(!Program.Model.Dirty, "^S saves and clears dirty");
                Check(!ui.Tree.Title.EndsWith(" *"), "dirty marker clears after save");
                Check(System.IO.File.ReadAllText(file).Contains(before + "-edited"), "saved file contains the edited value");

                // Enter on the tree is the discoverable alias for F2's start-edit.
                app.Keyboard.RaiseKeyDownEvent(Key.Enter);
                Check(ui.Editing, "Enter on the tree starts an edit");
                app.Keyboard.RaiseKeyDownEvent(Key.Esc);
                Check(!ui.Editing, "Esc closes it again");
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
                var fresh = root9.LastChild as XmlElement;
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
                Check(ReferenceEquals(root9.LastChild, fresh), "redo restores it at the same spot");
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
                    Check(sib != null && ReferenceEquals(sib.NextSibling, firstChild), "Before lands directly before the anchor");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                }

                Check(Program.TryInsert(ui, root9, InsertKind.Attribute, InsertPos.Child, "fuxattr") == null,
                    "attribute insert accepted");
                Check(root9.HasAttribute("fuxattr"), "attribute exists on the element");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(!root9.HasAttribute("fuxattr"), "undo removes the attribute");

                Check(Program.TryInsert(ui, root9, InsertKind.Comment, InsertPos.Child, null) == null,
                    "comment insert accepted (no name needed)");
                Check(root9.LastChild is XmlComment, "comment lands as last child");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);

                Check(Program.TryInsert(ui, root9, InsertKind.Pi, InsertPos.Child, "fuxpi") == null,
                    "processing instruction insert accepted");
                Check(root9.LastChild is XmlProcessingInstruction lastPi && lastPi.Target == "fuxpi",
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
                    var successor = firstChild.NextSibling;
                    ui.Tree.SelectedObject = firstChild;
                    ui.Tree.SetFocus();
                    app.Keyboard.RaiseKeyDownEvent(Key.DeleteChar);
                    Check(firstChild.ParentNode == null, "Del removes the selected element");
                    Check(ReferenceEquals(ui.Tree.SelectedObject, parent9), "selection falls back to the parent");
                    app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                    Check(ReferenceEquals(firstChild.ParentNode, parent9) && ReferenceEquals(firstChild.NextSibling, successor),
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
                Check(ReferenceEquals(fb.NextSibling, fa), "nudge up swaps with the previous sibling");
                Check(ReferenceEquals(ui.Tree.SelectedObject, fb), "the moved node stays selected");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(ReferenceEquals(fa.NextSibling, fb) && ReferenceEquals(rootD.LastChild, fb),
                    "undo restores the original sibling order exactly");
                app.Keyboard.RaiseKeyDownEvent(Key.Y.WithCtrl);
                Check(ReferenceEquals(fb.NextSibling, fa), "redo re-applies the move");
                app.Keyboard.RaiseKeyDownEvent(Key.Z.WithCtrl);
                Check(Program.TryNudge(ui, fa, NudgeDir.Down) == null, "nudge down accepted");
                Check(ReferenceEquals(fb.NextSibling, fa) && ReferenceEquals(rootD.LastChild, fa),
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
                Check(ReferenceEquals(fb.NextSibling, fa), "^Shift+Up nudges the selection up");
                app.Keyboard.RaiseKeyDownEvent(Key.CursorDown.WithCtrl);
                Check(ReferenceEquals(fa.NextSibling, fb), "^Down (alias) nudges it back down");

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
                Check(ReferenceEquals(fb.ParentNode, rootD) && ReferenceEquals(fa.NextSibling, fb),
                    "promote lifts it back out, after its old parent");
                app.LayoutAndDraw(true);
                Check(TreeRows(ui) == rowsP, $"promote adds no row (was {rowsP}, now {TreeRows(ui)})");
                Check(CountOnScreen(app, "<fuxb>") == 1,
                    $"the promoted node is on screen once (drawn: {CountOnScreen(app, "<fuxb>")})");

                // Tear the playground down and come out clean.
                Check(Program.TryDelete(ui, fa) == null && Program.TryDelete(ui, fb) == null,
                    "playground deleted");
                Check(rootD.ChildNodes.Count == kidsD, "the document is back to its original children");
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
                ui.Undo.Push(new EditNodeValue(h1, "a needle here"));
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("needle", FindFlags.Normal, SearchFilter.Text) == "1/1",
                    "a value search finds the element by its text");
                Check(ReferenceEquals(ui.Tree.SelectedObject, h1), "the value hit is the element holding it");
                Check(DoFind("needle", FindFlags.Normal, SearchFilter.Names)
                        .StartsWith("no match"), "a name search ignores values");
                ui.Tree.SelectedObject = rootF; // a miss leaves the selection where it was
                Check(DoFind("fuxfind", FindFlags.Normal, SearchFilter.Names) == "1/2",
                    "a name search still finds names");

                // Whole word matches a run between delimiters, not a prefix of one.
                ui.Tree.SelectedObject = rootF;
                Check(DoFind("needle", FindFlags.WholeWord, SearchFilter.Text) == "1/1",
                    "whole word matches a whole word");
                Check(DoFind("needl", FindFlags.WholeWord, SearchFilter.Text)
                        .StartsWith("no match"), "whole word rejects a partial word");
                Check(DoFind("needl", FindFlags.Normal, SearchFilter.Text) == "1/1",
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
                Check(rootF.ChildNodes.Count == kidsF, "the document is back to its original children");
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

            // --- 13. F9 focuses the menu bar; ^Q requests stop.
            bool f9Handled = app.Keyboard.RaiseKeyDownEvent(Key.F9);
            app.RaiseIteration(); // popover show is processed by the main loop
            app.LayoutAndDraw(true);
            var popover = app.Popovers.GetActivePopover();
            Check(ui.Menu.Active || ui.Menu.IsOpen() || ui.Menu.HasFocus || popover != null,
                $"F9 activates the menu bar (handled={f9Handled}, popover={(popover == null ? "none" : popover.GetType().Name)})");
            app.Keyboard.RaiseKeyDownEvent(Key.Q.WithCtrl);
            Check(ui.Top.StopRequested, "^Q requests stop");

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

        private static string ScreenRow(Terminal.Gui.App.IApplication app, int row)
            => ScreenText(app).Split('\n')[row];

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

        // How many rows on screen draw a given label. Catches the opposite failure: a node that
        // landed inside a collapsed container is branched correctly but drawn nowhere, so it
        // reads 0. TreeView<T> exposes no enumeration of its branches, hence counting pixels.
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

        private static bool Check(bool ok, string what)
        {
            if (!ok) _failures++;
            Console.Error.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
            return ok;
        }
    }
}
