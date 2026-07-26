using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Xml;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using XmlNotepad;

namespace Fux
{
    internal static class Program
    {
        private static XmlCache _model;

        private static int Main(string[] args)
        {
            var dump = Array.IndexOf(args, "--dump") >= 0;
            var validate = Array.IndexOf(args, "--validate") >= 0;
            var drill = Array.IndexOf(args, "--drill") >= 0;
            string file = null;
            foreach (var a in args)
                if (!a.StartsWith("--")) { file = a; break; }

            // Resolve to an absolute path up front. The engine builds its base URI from the
            // working directory without a trailing slash, so a relative path would resolve
            // against the PARENT of the cwd. Handing it a full path sidesteps that entirely.
            if (file != null) file = System.IO.Path.GetFullPath(file);

            // --drill edits and saves the document; run it against a scratch copy so the
            // self-test never touches the caller's file. Sibling schemas travel with the
            // document so relative xsi:schemaLocation hints still resolve.
            if (drill && file != null)
            {
                var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fux-drill");
                System.IO.Directory.CreateDirectory(dir);
                foreach (var xsd in System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(file), "*.xsd"))
                    System.IO.File.Copy(xsd, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(xsd)), true);
                // The underscore in the copy's name is deliberate: the tree pane's title is the
                // file name, and Terminal.Gui would read '_' as a hotkey marker and swallow it
                // (see NoHotKey). Naming the scratch copy this way makes the section-1 title
                // check a standing regression test for that, on every fixture and in CI —
                // no fixture in the repo happens to have an underscore in its name.
                var tmp = System.IO.Path.Combine(dir, "fux_drill_" + System.IO.Path.GetFileName(file));
                System.IO.File.Copy(file, tmp, true);
                file = tmp;
            }

            // --- Build the reused XmlNotepad engine, headless ---
            var settings = new Settings();
            settings.SetDefaults();
            settings.StartupPath = AppContext.BaseDirectory;
            settings.Resolver = new XmlUrlResolver();
            var site = new EngineSite(settings);
            _model = new XmlCache(site, new SchemaCache(site), new DelayedActions(a => a()));

            if (file != null)
            {
                try
                {
                    LoadDocument(file);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"fux: cannot load '{file}': {ex.Message}");
                    return 2;
                }
            }

            return dump ? Dump() : validate ? Validate() : drill ? Drill.Run(file) : RunUi(file);
        }

        // Load a document the way XmlNotepad's FormMain.OpenFile does: sniff the type from
        // the extension (Model's FileEntity.SetMimeType mapping) and coerce HTML into a DOM
        // via SgmlReader (FormMain.ImportHtml settings: HTML doctype, lower-case folding,
        // significant whitespace). Everything else is a plain XML load. Note the same
        // parity gap as upstream: saving an HTML-imported document writes XML.
        private static void LoadDocument(string file)
        {
            var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".htm" || ext == ".html")
            {
                using var text = new System.IO.StreamReader(file); // BOM-sniffing, UTF-8 default
                using var reader = new Sgml.SgmlReader
                {
                    DocType = "HTML",
                    CaseFolding = Sgml.CaseFolding.ToLower,
                    InputStream = text,
                    WhitespaceHandling = WhitespaceHandling.Significant,
                };
                _model.Load(reader, file);
            }
            else
            {
                _model.Load(file);
            }
        }

        // --------------------------------------------------------------------
        // Headless mode: validate + print diagnostics (the scriptable counterpart of the
        // interactive error pane). For each positioned diagnostic it also prints the tree
        // node that pressing Enter on that row would jump to — exercising the same
        // FindNodeAt + MapToTree path the UI uses.
        // --------------------------------------------------------------------
        private static int Validate()
        {
            var hasFile = _model.Document?.DocumentElement != null;
            var items = RunValidation();
            Console.WriteLine(SummarizeValidation(items, hasFile).Trim());
            int errors = 0;
            foreach (var it in items)
            {
                if (it.Severity == Severity.Error) errors++;
                Console.WriteLine("  " + it);
                if (it.Line > 0)
                {
                    var node = MapToTree(_model.FindNodeAt(it.Line, it.Col));
                    Console.WriteLine("      -> " + (node == null ? "(no node)" : GetLabel(node)));
                }
            }
            return errors == 0 ? 0 : 1;
        }

        // --------------------------------------------------------------------
        // Headless mode: print the tree to stdout (scripting + self-verification)
        // --------------------------------------------------------------------
        private static int Dump()
        {
            var root = _model.Document?.DocumentElement;
            if (root == null) { Console.WriteLine("(empty document)"); return 0; }
            DumpNode(root, 0);
            return 0;
        }

        private static void DumpNode(XmlNode n, int depth)
        {
            var val = OneLine(GetValue(n));
            Console.WriteLine($"{new string(' ', depth * 2)}{GetLabel(n)}{(val.Length > 0 ? "  = " + val : "")}");
            foreach (var c in GetChildren(n)) DumpNode(c, depth + 1);
        }

        // --------------------------------------------------------------------
        // Interactive mode: two-pane Terminal.Gui view over the DOM
        // --------------------------------------------------------------------
        private static int RunUi(string file)
        {
            var ui = BuildUi(file);

            // Last-resort net for exceptions that bypass the main loop (e.g. the driver's
            // input thread): restore the terminal and leave a stack behind before dying.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { ui.App.Dispose(); } catch { }
                WriteCrashLog(file, e.ExceptionObject as Exception);
            };

            try
            {
                ui.App.Run(ui.Top, null);
            }
            catch (Exception ex)
            {
                // Restore the terminal FIRST (leave alt screen, mouse tracking off, cooked
                // mode) — an unhandled exception must never strand the user in raw mode —
                // then leave the stack somewhere findable: stderr scrolls away with the
                // wreckage, the crash log survives.
                try { ui.App.Dispose(); } catch { /* the driver may already be wedged */ }
                WriteCrashLog(file, ex);
                return 70; // EX_SOFTWARE
            }
            ui.App.Dispose();
            return 0;
        }

        private static void WriteCrashLog(string file, Exception ex)
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fux-crash.log");
            try { System.IO.File.WriteAllText(log, $"{DateTime.Now:O} fux crash\nfile: {file}\n\n{ex}\n"); } catch { }
            Console.Error.WriteLine($"fux: unhandled error: {ex?.Message}");
            Console.Error.WriteLine(ex?.StackTrace);
            Console.Error.WriteLine($"fux: full details written to {log}");
        }

        // The assembled interactive UI: what RunUi runs and what the --drill self-test drives
        // step-by-step (Begin / inject keys / read the output buffer / End).
        internal sealed class Ui
        {
            public IApplication App;
            public Runnable Top;
            public MenuBar Menu;
            public StatusBar Status;
            public TreeView<XmlNode> Tree;
            public View ValueView;   // typed View: TextView is obsolete-flagged, see BuildUi
            public ListView ErrorList;
            public List<ErrorItem> Errors;
            public UndoManager Undo;
            public bool Editing;     // value-pane edit mode (F2 commits, Esc cancels)
            public XmlNode EditNode; // node whose value is being edited
            public int ModalDepth;   // >0 while a dialog/message box runs: app-wide keys stay inert

            // The standing find, so F3 can repeat it. Kept as the raw query rather than a
            // built Query: an XPath one caches the nodes it selected, which the next edit
            // would invalidate (see the note on Find).
            public string FindExpr;
            public FindFlags FindOptions;
            public SearchFilter FindIn;
        }

        // Drill introspection: the engine instance behind the UI.
        internal static XmlCache Model => _model;

        // Turns the '_'-means-hotkey convention off for one view. Any pane or dialog whose
        // title carries document text — a file name, a node name — needs this, or Terminal.Gui
        // eats the underscore and the character after it stops being plain text. The switch is
        // per-view by design, and it is the right lever rather than doubling the underscores in
        // the string: escaping would be a rule every future title has to remember, and it isn't
        // even uniform across widgets — TextView ships with hotkeys already disabled, so a
        // doubled '_' renders doubled there. Menus keep the convention: theirs is deliberate.
        private static readonly System.Text.Rune NoHotKey = new System.Text.Rune('￿');

        internal static Ui BuildUi(string file)
        {
            // v2 is instance-based (the static Application facade is marked obsolete).
            var app = Application.Create(null);
            app.Init(null);

            // v2 has no Toplevel/Window split: a Runnable is a plain View that app.Run drives.
            // Views without their own Scheme inherit from the superview; ApplyTheme (below)
            // sets the content scheme here, theming everything we don't explicitly override.
            var top = new Runnable();

            // Captured by the menu/status toggle actions before it's assigned; by the time a
            // keypress can fire them the Ui is built. (The Theme toggle needs the whole Ui.)
            Ui ui = null;

            // F9 activates the menu bar, as in v1. Must be set via the static default BEFORE
            // construction: the MenuBar binds its activation key once in its constructor, and
            // the instance Key setter does not rebind (verified against the 2.4.17 source).
            MenuBar.DefaultKey = Key.F9;
            // Menu items carry hint text only (new Key()): every key gets exactly one live,
            // app-wide binding via the StatusBar shortcuts below — a second binding here
            // could double-fire (fatal for the F2 edit toggle: start + instant commit).
            var menu = new MenuBar(new MenuItem[]
            {
                new MenuBarItem("_File", new View[]
                {
                    new MenuItem("_Open…", "^O", () => StartOpen(ui), new Key()),
                    new MenuItem("_Save", "^S", () => SaveFile(ui), new Key()),
                    new MenuItem("Save _As…", "", () => StartSaveAs(ui), new Key()), // menu-only: see the key handler
                    new MenuItem("_Quit", "^Q", () => RequestQuit(ui), new Key()),
                }),
                new MenuBarItem("_Edit", new View[]
                {
                    new MenuItem("Edit _Value", "F2", () => ToggleValueEdit(ui), new Key()),
                    new MenuItem("Re_name…", "^R", () => StartRename(ui), new Key()),
                    new MenuItem("_Insert…", "^N", () => StartInsert(ui), new Key()),
                    new MenuItem("_Delete", "Del", () => DeleteSelected(ui), new Key()),
                    // Hotkeys here avoid V/n/I/D/U/R, already taken by this menu's other items.
                    new MenuItem("Nudge U_p", "^Shift+Up", () => NudgeSelected(ui, NudgeDir.Up), new Key()),
                    new MenuItem("Nudge Do_wn", "^Shift+Down", () => NudgeSelected(ui, NudgeDir.Down), new Key()),
                    new MenuItem("Nudge _Left", "^Shift+Left", () => NudgeSelected(ui, NudgeDir.Left), new Key()),
                    new MenuItem("Nudge Ri_ght", "^Shift+Right", () => NudgeSelected(ui, NudgeDir.Right), new Key()),
                    new MenuItem("_Undo", "^Z", () => DoUndo(ui), new Key()),
                    new MenuItem("_Redo", "^Y", () => DoRedo(ui), new Key()),
                }),
                new MenuBarItem("_Search", new View[]
                {
                    new MenuItem("_Find…", "^F", () => StartFind(ui), new Key()),
                    new MenuItem("Find _Next", "F3", () => FindAgain(ui, false), new Key()),
                    new MenuItem("Find _Previous", "Shift+F3", () => FindAgain(ui, true), new Key()),
                }),
                new MenuBarItem("_View", new View[]
                {
                    new MenuItem("_Toggle Light/Dark", "F5", () => ToggleTheme(ui), new Key()),
                }),
                new MenuBarItem("_Help", new View[]
                {
                    new MenuItem("_About", "", () =>
                        ModalQuery(ui, "About fux", "A terminal XML editor over the XmlNotepad engine.", "OK")),
                }),
            })
            {
                X = 0, Y = 0, Width = Dim.Fill(),
            };

            // Bordered panes (v2 Adornments): each pane draws its own border + title, so the v1
            // title-strip labels and the tree/value divider line are gone. The bottom ErrorPaneH
            // rows are the validation pane; its border title carries the summary. Dim.Fill leaves
            // room for it plus the status bar row.
            const int ErrorPaneH = 8; // border (2) + 6 visible error rows

            var tree = new TreeView<XmlNode>
            {
                X = 0, Y = 1, Width = Dim.Percent(33), Height = Dim.Fill(ErrorPaneH + 1),
                Title = "Tree", BorderStyle = LineStyle.Single,
            };
            // The tree pane's title is the file name, so it must not be read as a hotkey hint:
            // Terminal.Gui treats '_' as the marker for the next character's accelerator and
            // swallows it, which drew "under_score.xml" as "underscore.xml". See NoHotKey.
            tree.HotKeySpecifier = NoHotKey;
            tree.TreeBuilder = new DelegateTreeBuilder<XmlNode>(n => GetChildren(n), n => System.Linq.Enumerable.Any(GetChildren(n)));
            tree.AspectGetter = n => GetLabel(n);
            tree.ColorGetter = n => NodeScheme(n); // per-kind row colors, vim xml.vim style

            // TextView is marked obsolete in favor of the external tui-cs/Editor package. For a
            // read-only, word-wrapped value display it remains the right-sized built-in; revisit
            // when fux grows editing (Editor brings undo/multi-caret/find — that decision is
            // tracked with the editing architecture question).
#pragma warning disable CS0618
            var valueView = new TextView
            {
                X = Pos.Right(tree), Y = 1, Width = Dim.Fill(), Height = Dim.Fill(ErrorPaneH + 1),
                ReadOnly = true, WordWrap = true,
                Title = "Value", BorderStyle = LineStyle.Single,
            };
#pragma warning restore CS0618

            // Find reports into this pane's title, which can carry the user's search term.
            // TextView already ships with hotkeys disabled; saying so keeps that from being a
            // silent dependency on the widget's default.
            valueView.HotKeySpecifier = NoHotKey;

            // Two-pane sync: the tree drives, the value pane reflects the selection.
            tree.SelectionChanged += (s, e) =>
            {
                var n = e.NewValue;
                valueView.Text = n == null ? "" : GetValue(n) ?? "";
            };

            // Enter on the tree starts editing the selected node's value (F2 does the same
            // app-wide; F2 again commits, Esc cancels).
            tree.Accepting += (s, e) =>
            {
                if (StartValueEdit(ui)) e.Handled = true;
            };

            // Esc in the value pane abandons a live edit. The KeyDown event fires before the
            // TextView's own key processing, so this wins while editing and is inert otherwise.
            valueView.KeyDown += (s, e) =>
            {
                if (ui != null && ui.Editing && e.KeyCode == Key.Esc.KeyCode)
                {
                    CancelValueEdit(ui);
                    e.Handled = true;
                }
            };

            // --- Bottom: validation/error pane. Its border title carries the summary.
            var errorList = new ListView
            {
                X = 0, Y = Pos.Bottom(tree), Width = Dim.Fill(), Height = Dim.Fill(1),
                BorderStyle = LineStyle.Single,
            };

            var root = _model.Document?.DocumentElement;
            if (root != null)
            {
                tree.AddObject(root);
                tree.ExpandAll();
                tree.SelectedObject = root;
            }

            // Filled by Revalidate (initially below, then after every edit/undo/redo). The
            // RowRender/Accepting closures hold this list reference, so it's mutated in place.
            var errors = new List<ErrorItem>();

            // Severity row colors (vim Error red; yellow for warnings, since vim's WarningMsg
            // red would hide the distinction). The selected row keeps its Visual bar.
            errorList.RowRender += (s, e) =>
            {
                if (e.Row < 0 || e.Row >= errors.Count) return;
                if (errorList.HasFocus && errorList.SelectedItem == e.Row) return;
                if (errors[e.Row].Severity == Severity.Error) e.RowAttribute = Theme.ErrorRow;
                else if (errors[e.Row].Severity == Severity.Warning) e.RowAttribute = Theme.WarningRow;
            };

            // Enter on an error row jumps to the offending node in the tree. Errors carry the
            // source line/col; FindNodeAt binary-searches the DomLoader line table, then
            // MapToTree walks up to the nearest node the tree actually shows.
            errorList.Accepting += (s, e) =>
            {
                int i = errorList.SelectedItem ?? -1;
                if (i < 0 || i >= errors.Count) return;
                var item = errors[i];
                if (item.Line <= 0) return; // diagnostic isn't tied to a source position
                var node = MapToTree(_model.FindNodeAt(item.Line, item.Col));
                if (node == null) return;
                tree.SelectedObject = node;
                tree.EnsureVisible(node);
                tree.SetFocus();
                e.Handled = true;
            };

            // F6 cycles focus tree -> value -> errors. Bound application-wide via the StatusBar
            // shortcut (BindKeyToApplication below), so it works from any pane.
            var focusRing = new View[] { tree, valueView, errorList };
            void CycleFocus()
            {
                if (ui != null && ui.Editing) return; // don't yank focus out of a live edit
                int cur = Array.FindIndex(focusRing, v => v.HasFocus);
                focusRing[(cur + 1) % focusRing.Length].SetFocus();
            }

            // The status F9 entry is a hint only, with no live key, so it can't race the MenuBar
            // for the keypress. Alt+F/Alt+H also work when the terminal sends Option as Meta —
            // off by default on macOS, so F9 is the reliable path.
            // ^Z/^Y/F5/Del have no status-bar slot (no room at 100 cols; they stay
            // discoverable in the Edit/View menus) and can't be bound elsewhere: a menu-item
            // Key does NOT bind app-wide in v2, and an invisible Shortcut doesn't dispatch
            // (drill-proven). Handle them centrally, pre-routing. Inert while a modal dialog
            // is open (Del in a dialog's text field must not delete the tree node!), and
            // while a value edit is live ^Z/^Y/Del fall through to the TextView's own keys.
            app.Keyboard.KeyDown += (s, e) =>
            {
                if (e.Handled || ui == null || ui.ModalDepth > 0) return;
                if (!ui.Editing && e.KeyCode == Key.Z.WithCtrl.KeyCode) { DoUndo(ui); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.Y.WithCtrl.KeyCode) { DoRedo(ui); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.DeleteChar.KeyCode) { DeleteSelected(ui); e.Handled = true; }
                else if (!ui.Editing && IsNudgeKey(e, out var nudge)) { NudgeSelected(ui, nudge); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.O.WithCtrl.KeyCode) { StartOpen(ui); e.Handled = true; }
                // No Save As chord on purpose: Ctrl+Shift+<letter> is indistinguishable from
                // Ctrl+<letter> in legacy terminal encoding — both arrive as one control byte
                // (^S is 0x13) — so a terminal that doesn't speak the kitty protocol or
                // modifyOtherKeys would turn an advertised "^Shift+S" into a plain Save,
                // quietly writing the current file when the user asked to write another one.
                // Save As lives in the File menu, where it can't misfire. (Ctrl+Shift+Arrow
                // for nudge is fine: arrows are CSI sequences carrying an explicit modifier.)
                else if (!ui.Editing && e.KeyCode == Key.F.WithCtrl.KeyCode) { StartFind(ui); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.F3.WithShift.KeyCode) { FindAgain(ui, true); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.F3.KeyCode) { FindAgain(ui, false); e.Handled = true; }
                else if (e.KeyCode == Key.F5.KeyCode) { ToggleTheme(ui); e.Handled = true; }
            };

            var status = new StatusBar(new Shortcut[]
            {
                new Shortcut(Key.Q.WithCtrl, "Quit", () => RequestQuit(ui), null),
                new Shortcut(new Key(), "F9 Menu", null, null), // hint only: a live F9 binding here would swallow the MenuBar's key
                new Shortcut(Key.F6, "Focus", CycleFocus, null),
                new Shortcut(Key.F2, "Edit", () => ToggleValueEdit(ui), null),
                new Shortcut(Key.R.WithCtrl, "Rename", () => StartRename(ui), null),
                new Shortcut(Key.N.WithCtrl, "Insert", () => StartInsert(ui), null),
                new Shortcut(Key.S.WithCtrl, "Save", () => SaveFile(ui), null),
            })
            {
                X = 0, Y = Pos.AnchorEnd(), Width = Dim.Fill(),
            };
            // Bind the actionable shortcuts application-wide so ^Q/F6 work from any pane.
            foreach (var v in status.SubViews)
                if (v is Shortcut sc && sc.Action != null) sc.BindKeyToApplication = true;

            top.Add(menu, tree, valueView, errorList, status);
            tree.SetFocus();

            // The undo stack: commands mutate the DOM; these events drive the view refresh
            // (reselect the touched node, sync value pane, revalidate, dirty marker).
            var undo = new UndoManager(1000);
            undo.CommandDone += (s, e) => AfterUndoableChange(ui, e.Command);
            undo.CommandUndone += (s, e) => AfterUndoableChange(ui, e.Command);
            undo.CommandRedone += (s, e) => AfterUndoableChange(ui, e.Command);

            ui = new Ui
            {
                App = app, Top = top, Menu = menu, Status = status,
                Tree = tree, ValueView = valueView, ErrorList = errorList, Errors = errors,
                Undo = undo,
            };
            Revalidate(ui);
            UpdateTitle(ui);
            ApplyTheme(ui);
            return ui;
        }

        // Take the current Theme schemes onto the views. Called at startup and again on every
        // light/dark toggle — Theme.Load rebuilds its Scheme objects, so each themed view must
        // re-take its reference; everything else inherits Content from the top view. (The tree's
        // per-row ColorGetter and the error list's RowRender read Theme at draw time.)
        internal static void ApplyTheme(Ui ui)
        {
            ui.Top.SetScheme(Theme.Content);
            ui.Menu.SetScheme(Theme.Bar);
            ui.Status.SetScheme(Theme.Bar);
            ui.ValueView.SetScheme(Theme.Flat); // TextView paints its whole area with Focus; Flat keeps the bg stable
            ui.Top.SetNeedsDraw();
        }

        private static void ToggleTheme(Ui ui)
        {
            if (ui == null) return;
            Theme.Load(!Theme.IsDark);
            ApplyTheme(ui);
        }

        // --------------------------------------------------------------------
        // Editing: node values, in place in the value pane. F2 (or Enter on the
        // tree) starts an edit, F2 commits it through the UndoManager, Esc
        // abandons it. ^Z/^Y walk history; ^S saves.
        // --------------------------------------------------------------------
#pragma warning disable CS0618 // TextView: see the BuildUi note on the obsolete flag
        private static void ToggleValueEdit(Ui ui)
        {
            if (ui == null) return;
            if (ui.Editing) CommitValueEdit(ui);
            else StartValueEdit(ui);
        }

        private static bool StartValueEdit(Ui ui)
        {
            if (ui == null || ui.Editing) return false;
            var n = ui.Tree.SelectedObject;
            if (n == null || !EditNodeValue.CanEditValue(n)) return false;
            ui.Editing = true;
            ui.EditNode = n;
            var tv = (TextView)ui.ValueView;
            tv.ReadOnly = false;
            tv.Title = "Value — editing (F2: commit, Esc: cancel)";
            tv.SetFocus();
            return true;
        }

        private static void CommitValueEdit(Ui ui)
        {
            var node = ui.EditNode;
            var text = ((TextView)ui.ValueView).Text;
            EndValueEdit(ui);
            // Push executes Do() and fires CommandDone → AfterUndoableChange refreshes the
            // view. An unchanged value is a no-op push: nothing fires, nothing goes dirty.
            ui.Undo.Push(new EditNodeValue(node, text));
        }

        private static void CancelValueEdit(Ui ui) => EndValueEdit(ui);

        // Leave edit mode and restore the read-only value pane from the DOM.
        private static void EndValueEdit(Ui ui)
        {
            var node = ui.EditNode;
            ui.Editing = false;
            ui.EditNode = null;
            var tv = (TextView)ui.ValueView;
            tv.ReadOnly = true;
            tv.Title = "Value";
            tv.Text = node == null ? "" : GetValue(node) ?? "";
            ui.Tree.SetFocus();
        }

#pragma warning restore CS0618

        // MessageBox/dialog wrappers: ModalDepth keeps the app-wide key handler inert while
        // a modal runs (otherwise Del in a dialog's text field would delete the tree node).
        private static int? ModalQuery(Ui ui, string title, string message, params string[] buttons)
        {
            if (ui == null) return null;
            ui.ModalDepth++;
            try { return MessageBox.Query(ui.App, title, message, buttons); }
            finally { ui.ModalDepth--; }
        }

        // Runnable, not Dialog: the file pickers derive from Dialog<T>, which is not a Dialog.
        private static void RunModal(Ui ui, Runnable d)
        {
            ui.ModalDepth++;
            try { ui.App.Run(d, null); }
            finally { ui.ModalDepth--; }
        }

        // ^R renames the selected element/attribute/PI via a modal prompt. The commit path
        // is TryRename (headless-testable); the dialog is only the text collector.
        private static void StartRename(Ui ui)
        {
            if (ui == null || ui.Editing) return;
            var n = ui.Tree.SelectedObject;
            if (n == null || !RenameNode.CanRename(n)) return;

            var field = new TextField { X = 1, Y = 1, Width = Dim.Fill(1), Text = n.Name };
            var d = new Dialog
            {
                Title = $"Rename {n.NodeType}",
                Width = 46, Height = 8,
            };
            d.Add(new Label { X = 1, Y = 0, Text = "New name:" }, field);
            d.AddButton(new Button { Text = "Cancel" }); // Result 0
            d.AddButton(new Button { Text = "OK" });     // Result 1; last added = default (Enter)
            field.SetFocus();
            field.MoveEnd(); // type to append; cursor at 0 would prepend into the prefilled name
            RunModal(ui, d);
            bool ok = d.Result is 1;
            var newName = field.Text;
            d.Dispose();
            if (!ok || newName == n.Name) return;

            var err = TryRename(ui, n, newName);
            if (err != null)
                ModalQuery(ui, "Rename failed", err, "OK");
        }

        // ^N inserts a new node relative to the selection. The dialog collects kind,
        // position and name; TryInsert (headless-testable) is the commit path.
        private static void StartInsert(Ui ui)
        {
            if (ui == null || ui.Editing) return;
            var n = ui.Tree.SelectedObject;
            if (n == null) return;

            var kindSel = new OptionSelector
            {
                X = 1, Y = 2,
                Labels = new[] { "Element", "Attribute", "Comment", "Processing instr." },
                Value = 0,
            };
            var posSel = new OptionSelector
            {
                X = 26, Y = 2,
                Labels = new[] { "Child", "Before", "After" },
                Value = 0,
            };
            var field = new TextField { X = 1, Y = 1, Width = Dim.Fill(1) };
            var d = new Dialog
            {
                Title = $"Insert at {GetLabel(n)}",
                Width = 50, Height = 12,
            };
            d.HotKeySpecifier = NoHotKey; // the title carries a node name — see NoHotKey
            d.Add(new Label { X = 1, Y = 0, Text = "Name (element/attribute/PI):" }, field, kindSel, posSel);
            d.AddButton(new Button { Text = "Cancel" }); // Result 0
            d.AddButton(new Button { Text = "OK" });     // Result 1; last added = default (Enter)
            field.SetFocus();
            RunModal(ui, d);
            bool ok = d.Result is 1;
            var kind = (InsertKind)(kindSel.Value ?? 0);
            var pos = (InsertPos)(posSel.Value ?? 0);
            var name = field.Text;
            d.Dispose();
            if (!ok) return;

            var err = TryInsert(ui, n, kind, pos, name);
            if (err != null)
                ModalQuery(ui, "Insert failed", err, "OK");
        }

        // Push an insert through the undo stack. Returns null on success, else the reason.
        internal static string TryInsert(Ui ui, XmlNode anchor, InsertKind kind, InsertPos pos, string name)
        {
            InsertNewNode cmd;
            try
            {
                cmd = new InsertNewNode(anchor, kind, pos, name);
            }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException)
            {
                return ex.Message;
            }
            ui.Undo.Push(cmd);
            return null;
        }

        // Del deletes the selected node (undoable, so no confirmation).
        private static void DeleteSelected(Ui ui)
        {
            if (ui == null || ui.Editing) return;
            var n = ui.Tree.SelectedObject;
            if (n == null) return;
            var err = TryDelete(ui, n);
            if (err != null)
                ModalQuery(ui, "Delete failed", err, "OK");
        }

        // Push a delete through the undo stack. Returns null on success, else the reason.
        internal static string TryDelete(Ui ui, XmlNode node)
        {
            DeleteNode cmd;
            try
            {
                cmd = new DeleteNode(node);
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
            ui.Undo.Push(cmd);
            return null;
        }

        // --------------------------------------------------------------------
        // Find: ^F sets the query, F3 / Shift+F3 walk the ring. Matching lives in Find.cs;
        // this is the view half — reveal the hit and report the ring position.
        // --------------------------------------------------------------------

        // ^F collects a query via a modal prompt, then jumps to the first hit. As with rename
        // and insert, the dialog is only the collector: TryFind is the headless-testable path.
        private static void StartFind(Ui ui)
        {
            if (ui == null || ui.Editing) return;

            var field = new TextField { X = 1, Y = 1, Width = Dim.Fill(1), Text = ui.FindExpr ?? "" };
            var modeSel = new OptionSelector
            {
                X = 1, Y = 4,
                Labels = new[] { "Text", "Regex", "XPath" },
                Value = (ui.FindOptions & FindFlags.XPath) != 0 ? 2
                      : (ui.FindOptions & FindFlags.Regex) != 0 ? 1 : 0,
            };
            var filterSel = new OptionSelector
            {
                X = 16, Y = 4,
                Labels = new[] { "Everything", "Names", "Values", "Comments" },
                Value = (int)ui.FindIn,
            };
            var caseBox = new CheckBox
            {
                X = 34, Y = 4, Text = "Match case",
                Value = (ui.FindOptions & FindFlags.MatchCase) != 0 ? CheckState.Checked : CheckState.UnChecked,
            };
            var wordBox = new CheckBox
            {
                X = 34, Y = 5, Text = "Whole word",
                Value = (ui.FindOptions & FindFlags.WholeWord) != 0 ? CheckState.Checked : CheckState.UnChecked,
            };
            var d = new Dialog { Title = "Find", Width = 62, Height = 14 };
            d.Add(new Label { X = 1, Y = 0, Text = "Find what:" }, field,
                  new Label { X = 1, Y = 3, Text = "Mode:" },
                  new Label { X = 16, Y = 3, Text = "Look in:" },
                  modeSel, filterSel, caseBox, wordBox);
            d.AddButton(new Button { Text = "Cancel" }); // Result 0
            d.AddButton(new Button { Text = "Find" });   // Result 1; last added = default (Enter)
            field.SetFocus();
            field.MoveEnd();
            RunModal(ui, d);

            bool ok = d.Result is 1;
            var expr = field.Text;
            var flags = FindFlags.Normal;
            if (modeSel.Value == 1) flags |= FindFlags.Regex;
            else if (modeSel.Value == 2) flags |= FindFlags.XPath;
            if (caseBox.Value == CheckState.Checked) flags |= FindFlags.MatchCase;
            if (wordBox.Value == CheckState.Checked) flags |= FindFlags.WholeWord;
            var filter = (SearchFilter)(filterSel.Value ?? 0);
            d.Dispose();
            if (!ok) return;

            ui.FindExpr = expr;
            ui.FindOptions = flags;
            ui.FindIn = filter;
            TryFind(ui, false);
        }

        // F3 / Shift+F3 repeat the standing query. With nothing to repeat, they open the prompt
        // rather than doing nothing quietly.
        private static void FindAgain(Ui ui, bool backwards)
        {
            if (ui == null || ui.Editing) return;
            if (string.IsNullOrEmpty(ui.FindExpr)) { StartFind(ui); return; }
            TryFind(ui, backwards);
        }

        // Step the standing query, reveal the hit and report: "3/17", "no match", or why the
        // expression itself was rejected. The status slot is updated here rather than by the
        // callers, so no find path can forget to; the string is returned as well for the drill.
        // Nothing throws out of a keypress, and nothing is pushed on the undo stack — a find
        // changes no DOM.
        internal static string TryFind(Ui ui, bool backwards)
        {
            var status = RunQuery(ui, backwards);
            SetFindStatus(ui, status);
            return status;
        }

        private static string RunQuery(Ui ui, bool backwards)
        {
            var root = _model.Document?.DocumentElement;
            if (root == null) return "no document";

            Query q;
            try
            {
                q = new Query(_model.Document, ui.FindExpr, ui.FindOptions, ui.FindIn);
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
            if (q.IsEmpty) return "";

            var hit = Find.Step(root, ui.Tree.SelectedObject, q, backwards, out int index, out int total);
            if (hit == null)
            {
                var shown = ui.FindExpr.Length > 24 ? ui.FindExpr.Substring(0, 24) + "…" : ui.FindExpr;
                return $"no match: {shown}"; // the title has a border's worth of room, not a line's
            }

            RevealNode(ui, hit);
            ui.ValueView.Text = GetValue(hit) ?? "";
            return $"{index}/{total}";
        }

        // Find reports into the value pane's border title, next to the hit it just selected.
        // Not a message box: at the end of a ring, F3 popping a dialog on every press would be
        // unusable. Not the status bar either — that row is already full at 100 columns, and an
        // extra slot there renders clipped to two characters (measured, not guessed). Editing
        // takes the title back (EndValueEdit), which is the right precedence.
        private static void SetFindStatus(Ui ui, string text)
        {
            if (ui?.ValueView == null || ui.Editing) return;
            ui.ValueView.Title = string.IsNullOrEmpty(text) ? "Value" : $"Value — find {text}";
        }

        // Ctrl+Shift+Arrow nudges the selected node: up/down reorder it among its siblings,
        // left/right change its level. Refusals are quiet (see TryNudge) — running into the
        // edge of a document should feel like running into the end of a list.
        private static void NudgeSelected(Ui ui, NudgeDir dir)
        {
            if (ui == null || ui.Editing) return;
            var err = TryNudge(ui, ui.Tree.SelectedObject, dir);
            if (err != null)
                ModalQuery(ui, "Move failed", err, "OK");
        }

        // Push a nudge through the undo stack. Returns null when it moved *or* when there was
        // simply nowhere to go (NudgeBlocked), else the reason worth interrupting the user for
        // — in practice only an attribute name that already exists on the parent element.
        internal static string TryNudge(Ui ui, XmlNode node, NudgeDir dir)
        {
            NudgeNode cmd;
            try
            {
                cmd = new NudgeNode(node, dir);
            }
            catch (NudgeBlocked)
            {
                return null;
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
            ui.Undo.Push(cmd);
            return null;
        }

        // Upstream's nudge chord is Ctrl+Shift+Arrow (XmlNotepad/XmlTreeView.cs, OnKeyDown).
        // Ctrl+Arrow is accepted as an alias because plenty of terminals send no CSI modifier
        // at all for Ctrl+Shift+Arrow (macOS Terminal.app among them), and neither combination
        // means anything else in fux.
        private static bool IsNudgeKey(Key key, out NudgeDir dir)
        {
            dir = NudgeDir.Up;
            if (Matches(key, Key.CursorUp)) { dir = NudgeDir.Up; return true; }
            if (Matches(key, Key.CursorDown)) { dir = NudgeDir.Down; return true; }
            if (Matches(key, Key.CursorLeft)) { dir = NudgeDir.Left; return true; }
            if (Matches(key, Key.CursorRight)) { dir = NudgeDir.Right; return true; }
            return false;

            static bool Matches(Key key, Key arrow)
                => key.KeyCode == arrow.WithCtrl.WithShift.KeyCode || key.KeyCode == arrow.WithCtrl.KeyCode;
        }

        // Push a rename through the undo stack. Returns null on success, or the reason the
        // name was rejected (RenameNode's constructor validates via XmlConvert.VerifyName).
        internal static string TryRename(Ui ui, XmlNode node, string newName)
        {
            RenameNode cmd;
            try
            {
                cmd = new RenameNode(node, newName);
            }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException)
            {
                return ex.Message;
            }
            ui.Undo.Push(cmd);
            return null;
        }

        private static void DoUndo(Ui ui)
        {
            if (ui == null || ui.Editing) return;
            ui.Undo.Undo(); // no-ops when the stack is empty
        }

        private static void DoRedo(Ui ui)
        {
            if (ui == null || ui.Editing) return;
            ui.Undo.Redo();
        }

        // After any Do/Undo/Redo: reveal + reselect the touched node, sync the value pane,
        // revalidate, and refresh the dirty marker. (XmlCache watches the DOM and maintains
        // Dirty/ModelChanged on its own; this is purely the view side.)
        private static void AfterUndoableChange(Ui ui, XmlNotepad.Command cmd)
        {
            if (ui == null) return;
            // A command that moved a node between containers has to rebuild both ends: v2's
            // Branch.Refresh is single-level, so refreshing only the node's new parent would
            // leave the old one holding a branch for a child it no longer has — the node would
            // render in both places at once. Doing this first means RefreshTreeFor below still
            // has the last word on selection (dropping a branch that holds SelectedObject makes
            // v2 fall the selection back to its parent).
            if (cmd is IContainerCommand moved)
                foreach (var c in moved.Containers)
                    if (c is XmlElement ce) ui.Tree.RefreshObject(ce, false);
            if ((cmd as INodeCommand)?.Node is XmlNode node)
                RefreshTreeFor(ui, node);
            var sel = ui.Tree.SelectedObject;
            ui.ValueView.Text = sel == null ? "" : GetValue(sel) ?? "";
            Revalidate(ui);
            UpdateTitle(ui);
        }

        // Rebuild the tree around a (possibly brand-new) node. Commands that rename or
        // restructure swap node instances, so refresh is anchored at the parent — which
        // survives every command — rather than at the node itself; a structural change at
        // the document root rebinds the root object outright.
        private static void RefreshTreeFor(Ui ui, XmlNode node)
        {
            var parent = node is XmlAttribute a ? (XmlNode)a.OwnerElement : node.ParentNode;
            if (parent is XmlElement pe)
            {
                ui.Tree.RefreshObject(pe, false);
            }
            else
            {
                ui.Tree.ClearObjects();
                var root = _model.Document?.DocumentElement;
                if (root != null)
                {
                    ui.Tree.AddObject(root);
                    ui.Tree.ExpandAll();
                }
            }
            ExpandSubtree(ui, node);
            RevealNode(ui, node);
        }

        // Open every container above a node, scroll it into view and select it. Find uses this
        // without the refresh: it moves the selection around a document it hasn't changed.
        internal static void RevealNode(Ui ui, XmlNode node)
        {
            ExpandTo(ui, node);
            ui.Tree.EnsureVisible(node);
            ui.Tree.SelectedObject = node;
        }

        // Open every container above the node. Without this a node that lands inside a collapsed
        // element — a fresh insert, or a demote into a sibling that was never expanded (ExpandAll
        // only ever ran over the document as loaded) — is silently invisible: v2 resolves an
        // object to a branch through the visible line map, so EnsureVisible and SelectedObject
        // both no-op on it. Expand top-down, since child branches are built lazily as their
        // parent opens.
        private static void ExpandTo(Ui ui, XmlNode node)
        {
            var chain = new List<XmlNode>();
            for (var p = node is XmlAttribute a ? (XmlNode)a.OwnerElement : node.ParentNode;
                 p is XmlElement; p = p.ParentNode)
                chain.Add(p);
            for (int i = chain.Count - 1; i >= 0; i--)
                ui.Tree.Expand(chain[i]);
        }

        // fux shows a document fully expanded (the tree ExpandAll's at load), so a node that has
        // just been re-branched — moved to another container, or put back by an undo — needs its
        // subtree reopened as well: v2 builds those branches collapsed, which would swallow
        // everything underneath it. Top-down again, for the same lazy-branch reason.
        private static void ExpandSubtree(Ui ui, XmlNode node)
        {
            if (node is not XmlElement) return;
            ui.Tree.Expand(node);
            foreach (var c in GetChildren(node))
                ExpandSubtree(ui, c);
        }

        // Re-run validation over the (possibly edited) DOM and refresh the error pane.
        // Mutates ui.Errors in place: the RowRender/Accepting closures hold the list reference.
        private static void Revalidate(Ui ui)
        {
            var items = RunValidation();
            ui.Errors.Clear();
            ui.Errors.AddRange(items);
            ui.ErrorList.Title = SummarizeValidation(ui.Errors, _model.Document?.DocumentElement != null).Trim();
            ui.ErrorList.SetSource(new ObservableCollection<string>(BuildErrorLines(ui.Errors)));
        }

        // The tree pane title doubles as the document title: file name + dirty marker.
        private static void UpdateTitle(Ui ui)
        {
            var name = _model.FileName == null ? "Tree" : System.IO.Path.GetFileName(_model.FileName);
            ui.Tree.Title = _model.Dirty ? name + " *" : name;
        }

        private static void SaveFile(Ui ui)
        {
            if (ui == null || ui.Editing) return;
            if (_model.FileName == null) { StartSaveAs(ui); return; } // nowhere to save yet: ask
            try
            {
                _model.Save(_model.FileName);
            }
            catch (Exception ex)
            {
                ModalQuery(ui, "Save failed", ex.Message, "OK");
            }
            UpdateTitle(ui);
        }

        // Anything that replaces or abandons the open document goes through here first.
        // Returns true to proceed; false means the user cancelled, or chose Save and it failed —
        // either way the document must stay put.
        private static bool ConfirmDiscard(Ui ui)
        {
            if (!_model.Dirty) return true;
            var name = _model.FileName == null ? "this document" : System.IO.Path.GetFileName(_model.FileName);
            int choice = ModalQuery(ui, "Unsaved changes", $"Save changes to {name}?", "Save", "Discard", "Cancel") ?? 2;
            if (choice == 0) { SaveFile(ui); return !_model.Dirty; } // save failed → stay
            return choice == 1;                                      // Discard; anything else cancels
        }

        private static void RequestQuit(Ui ui)
        {
            if (ui == null) return;
            if (!ConfirmDiscard(ui)) return;
            ui.App.RequestStop(ui.Top);
        }

        // --------------------------------------------------------------------
        // Opening and saving elsewhere. The pickers only collect a path; TryOpen and
        // TrySaveAs are the commit paths, and are what the drill exercises.
        // --------------------------------------------------------------------

        private static void StartOpen(Ui ui)
        {
            if (ui == null || ui.Editing) return;
            if (!ConfirmDiscard(ui)) return;

            var d = new OpenDialog { Title = "Open", OpenMode = OpenMode.File, AllowsMultipleSelection = false };
            d.HotKeySpecifier = NoHotKey; // paths carry underscores — see NoHotKey
            if (_model.FileName != null) d.Path = _model.FileName;
            RunModal(ui, d);
            var picked = d.FilePaths.Count > 0 ? d.FilePaths[0] : null; // empty when cancelled
            d.Dispose();
            if (string.IsNullOrWhiteSpace(picked)) return;

            var err = TryOpen(ui, picked);
            if (err != null) ModalQuery(ui, "Open failed", err, "OK");
        }

        private static void StartSaveAs(Ui ui)
        {
            if (ui == null || ui.Editing) return;

            var d = new SaveDialog { Title = "Save As" };
            d.HotKeySpecifier = NoHotKey;
            if (_model.FileName != null) d.Path = _model.FileName;
            RunModal(ui, d);
            var picked = d.FileName; // null when cancelled
            d.Dispose();
            if (string.IsNullOrWhiteSpace(picked)) return;

            var err = TrySaveAs(ui, picked);
            if (err != null) ModalQuery(ui, "Save failed", err, "OK");
        }

        // Load a document in place of the current one. Returns null, or why it could not be
        // read. The view is rebound either way: after a failed load the model may hold nothing
        // or a fragment, and a tree still showing the previous document's nodes would be a lie —
        // and a live handle on nodes the model has let go of.
        internal static string TryOpen(Ui ui, string path)
        {
            string err = null;
            try
            {
                LoadDocument(System.IO.Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                err = $"cannot load '{System.IO.Path.GetFileName(path)}': {ex.Message}";
            }
            RebindDocument(ui);
            return err;
        }

        internal static string TrySaveAs(Ui ui, string path)
        {
            try
            {
                _model.Save(System.IO.Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
            UpdateTitle(ui); // Save retargets the model, so the pane title follows the new name
            return null;
        }

        // Point the whole view at whatever the model now holds. The undo stack goes with the old
        // document: its commands close over nodes that are no longer in any document, so undoing
        // one would either do nothing visible or reattach an orphan. The standing find goes too.
        private static void RebindDocument(Ui ui)
        {
            if (ui == null) return;
            ui.Undo.Clear();
            ui.FindExpr = null;
            SetFindStatus(ui, "");

            ui.Tree.ClearObjects();
            var root = _model.Document?.DocumentElement;
            if (root != null)
            {
                ui.Tree.AddObject(root);
                ui.Tree.ExpandAll();
            }
            ui.Tree.SelectedObject = root; // null when the load left nothing behind
            ui.ValueView.Text = root == null ? "" : GetValue(root) ?? "";
            Revalidate(ui);
            UpdateTitle(ui);
            ui.Tree.SetFocus();
        }

        // vim xml.vim's group links, via Theme: elements blue (Function -> Identifier),
        // attributes and processing instructions yellow (Type), comments base01 italic,
        // CDATA cyan (String). Everything else reads as plain content.
        private static Terminal.Gui.Drawing.Scheme NodeScheme(XmlNode n)
        {
            switch (n.NodeType)
            {
                case XmlNodeType.Element: return Theme.NodeElement;
                case XmlNodeType.Attribute: return Theme.NodeAttribute;
                case XmlNodeType.ProcessingInstruction: return Theme.NodeAttribute;
                case XmlNodeType.Comment: return Theme.NodeComment;
                case XmlNodeType.CDATA: return Theme.NodeCdata;
                default: return Theme.Content;
            }
        }

        // --------------------------------------------------------------------
        // Validation: drive the reused Checker over the loaded DOM and collect
        // its diagnostics for the error pane.
        // --------------------------------------------------------------------
        private static List<ErrorItem> RunValidation()
        {
            var collector = new ErrorCollector();
            if (_model.Document?.DocumentElement == null) return collector.Items;
            try
            {
                _model.ValidateModel(collector);
            }
            catch (Exception ex)
            {
                // A malformed/uncompilable schema can throw rather than report; show it as one error.
                collector.Items.Add(new ErrorItem { Severity = Severity.Error, Reason = "validation failed: " + ex.Message });
                collector.Errors++;
            }
            return collector.Items;
        }

        private static string SummarizeValidation(List<ErrorItem> items, bool hasFile)
        {
            if (!hasFile) return " (no file loaded)";
            if (items.Count == 0) return " Validation: no issues";
            int errors = 0, warnings = 0;
            foreach (var it in items)
            {
                if (it.Severity == Severity.Error) errors++;
                else if (it.Severity == Severity.Warning) warnings++;
            }
            string Plural(int n, string w) => $"{n} {w}{(n == 1 ? "" : "s")}";
            return $" Validation: {Plural(errors, "error")}, {Plural(warnings, "warning")}   (Enter: go to node)";
        }

        private static List<string> BuildErrorLines(List<ErrorItem> items)
        {
            var lines = new List<string>(items.Count);
            foreach (var it in items) lines.Add(it.ToString());
            return lines;
        }

        // Map an arbitrary DOM node (e.g. a text node an error points at) up to the nearest
        // ancestor the tree actually displays; falls back to the document element.
        private static XmlNode MapToTree(XmlNode n)
        {
            while (n != null)
            {
                switch (n.NodeType)
                {
                    case XmlNodeType.Element:
                    case XmlNodeType.Attribute:
                    case XmlNodeType.Comment:
                    case XmlNodeType.ProcessingInstruction:
                        return n;
                }
                // Attributes report ParentNode == null in System.Xml, so climb via OwnerElement.
                n = n is XmlAttribute a ? a.OwnerElement : n.ParentNode;
            }
            return _model.Document?.DocumentElement;
        }

        // --------------------------------------------------------------------
        // Shared node → (label, value, children) mapping. This is the little bit
        // of "view model" that turns the XmlNotepad DOM into a two-pane tree.
        // --------------------------------------------------------------------
        internal static IEnumerable<XmlNode> GetChildren(XmlNode n)
        {
            if (n is XmlElement el && el.Attributes != null)
                foreach (XmlAttribute a in el.Attributes)
                    yield return a;

            if (n.NodeType == XmlNodeType.Element || n.NodeType == XmlNodeType.Document)
                foreach (XmlNode c in n.ChildNodes)
                    if (IsShown(c))
                        yield return c;
        }

        // Does the tree give this child a row of its own? Text-ish content is folded into its
        // element's value instead (see GetValue), so it never appears as a node — which also
        // means nudging must step over it (NudgeNode.PrevInBand/NextInBand).
        internal static bool IsShown(XmlNode n)
            => n.NodeType != XmlNodeType.Text && n.NodeType != XmlNodeType.CDATA &&
               n.NodeType != XmlNodeType.Whitespace && n.NodeType != XmlNodeType.SignificantWhitespace;

        internal static string GetLabel(XmlNode n)
        {
            switch (n.NodeType)
            {
                case XmlNodeType.Element: return "<" + n.Name + ">";
                case XmlNodeType.Attribute: return "@" + n.Name;
                case XmlNodeType.Comment: return "<!-- comment -->";
                case XmlNodeType.ProcessingInstruction: return "<?" + n.Name + "?>";
                case XmlNodeType.CDATA: return "<![CDATA[]]>";
                default: return n.Name;
            }
        }

        // The "value" of a node for the right pane: attribute value, or an element's
        // simple text content (an element whose only children are text/CDATA).
        internal static string GetValue(XmlNode n)
        {
            switch (n.NodeType)
            {
                case XmlNodeType.Attribute:
                case XmlNodeType.Comment:
                case XmlNodeType.CDATA:
                case XmlNodeType.Text:
                case XmlNodeType.ProcessingInstruction: // Value aliases the PI's Data
                    return n.Value;
                case XmlNodeType.Element:
                    string text = null;
                    foreach (XmlNode c in n.ChildNodes)
                    {
                        if (c.NodeType == XmlNodeType.Text || c.NodeType == XmlNodeType.CDATA ||
                            c.NodeType == XmlNodeType.Whitespace || c.NodeType == XmlNodeType.SignificantWhitespace)
                            text = (text ?? "") + c.Value;
                        else
                            return ""; // has element children → a container, no scalar value
                    }
                    return text ?? "";
                default:
                    return "";
            }
        }

        private static string OneLine(string s)
            => string.IsNullOrEmpty(s) ? "" : s.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    // Minimal service container: the engine only asks the site for the Settings service.
    internal sealed class EngineSite : IServiceProvider
    {
        private readonly Settings _settings;
        public EngineSite(Settings settings) { _settings = settings; }
        public object GetService(Type serviceType)
            => serviceType == typeof(Settings) ? _settings : null;
    }
}
