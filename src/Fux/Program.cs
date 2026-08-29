using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
        private static FuxCache _model;

        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
            {
                Console.WriteLine(UsageText());
                return 0;
            }
            if (Array.IndexOf(args, "--version") >= 0)
            {
                Console.WriteLine("fux " + VersionString);
                return 0;
            }

            var dump = Array.IndexOf(args, "--dump") >= 0;
            var validate = Array.IndexOf(args, "--validate") >= 0;
            var drill = Array.IndexOf(args, "--drill") >= 0;
            string file = null;
            // An unrecognised option must not fall through to the editor. This loop used to
            // skip anything starting with "--", so `fux --help` opened an empty document and
            // sat waiting for a keypress: on a downloaded binary, run by someone finding out
            // what it does, that is indistinguishable from a hang. Refuse it and say so.
            // Every argument is checked, not just up to the first file name, so a typo after
            // the document is caught too.
            foreach (var a in args)
            {
                if (a.Length > 0 && a[0] == '-')
                {
                    // The one option that carries a value, so it cannot be an exact match
                    // against KnownFlags. Checked before that list, or every use of it would
                    // be rejected as an unknown option.
                    if (a.StartsWith(SchemaTimeoutFlag, StringComparison.Ordinal))
                    {
                        var v = a.Substring(SchemaTimeoutFlag.Length);
                        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
                            || secs <= 0 || double.IsInfinity(secs))
                        {
                            Console.Error.WriteLine("fux: " + SchemaTimeoutFlag + " wants a positive number of seconds, not '" + v + "'");
                            return 2;
                        }
                        XmlProxyResolver.Timeout = TimeSpan.FromSeconds(secs);
                        continue;
                    }
                    if (Array.IndexOf(KnownFlags, a) < 0)
                    {
                        Console.Error.WriteLine("fux: unknown option '" + a + "'");
                        Console.Error.WriteLine("Try 'fux --help'.");
                        return 2;
                    }
                    continue;
                }
                if (file == null) file = a;
            }

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
                // The drill saves many times, and every save that changes a file leaves a
                // backup — so this directory fills with copies from previous runs. Clear them,
                // or the first thing a contributor sees when they open it to inspect a failure
                // is a hundred stale files. Only the leftovers: this run's are still made, and
                // section 12a still asserts on them.
                foreach (var stale in System.IO.Directory.GetFiles(dir, "*.bak"))
                    System.IO.File.Delete(stale);
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
            // Keep the document's own whitespace in the DOM. It is what makes a save reproduce
            // the file that was opened: without it the layout never reaches the tree at all and
            // every save re-indents the whole document from scratch. The tree is unaffected —
            // GetChildren already filters the layout whitespace out (see IsShown) and GetValue
            // still reads a container as having no scalar value.
            settings["PreserveWhitespace"] = true;
            _model = new FuxCache(site, new SchemaCache(site), new DelayedActions(a => a()));
            // Saving keeps the previous version of the file unless told not to. Opt-out rather
            // than opt-in: the one person who never gets the backup is the one who did not know
            // to ask for it, and that is the person who needs it. See Backup.
            _model.Backups = Array.IndexOf(args, "--no-backup") < 0;

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

        // Load a document the way XmlNotepad's FormMain.OpenFile does: sniff the type from the
        // extension (Model's FileEntity.SetMimeType mapping) and coerce HTML, JSON and CSV into
        // a DOM; everything else is a plain XML load. See Import for the per-format wiring.
        private static void LoadDocument(string file)
        {
            // Record how this file is written before parsing it, so a later save can reproduce
            // it. Done first, and unconditionally: if the parse throws, the model is left
            // holding nothing and the stale conventions of the previous document must not
            // outlive it. (For an import these describe the source while the save writes XML —
            // the newline, indent and BOM still carry over usefully, and there is no
            // declaration to find.)
            _model.Format = XmlFormat.Sniff(file);
            _model.PrettyPrint = false;

            // An imported document has no whitespace nodes of its own, so the writer has to
            // synthesize indentation for it or the whole thing lands on one line. This cannot
            // be inferred from "the DOM contains no whitespace" — a genuinely single-line XML
            // file has none either, and reformatting that would be the very churn this all
            // exists to avoid.
            _model.PrettyPrint = Import.Load(_model, file);
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
            // Synchronous and on this thread, deliberately: OfflineThread is set in BuildUi and
            // this path never builds a Ui, so a schema is fetched here and now. --validate is a
            // CI entry point and has to be deterministic — the same document must give the same
            // answer and the same exit code every run, which a background fetch cannot promise.
            var schemaFailures = Schemas.Settled(_model.SchemaFailures);
            Console.WriteLine(SummarizeValidation(items, hasFile, schemaFailures, false).Trim());
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
            // Three answers, not two. A document nothing could be checked against used to exit
            // 0, indistinguishable from one that passed, so a CI job read "the schema host is
            // down" as "the document is fine" (#36). Errors still win the exit code when there
            // are any: something did validate, and what it found is the more actionable news.
            return errors > 0 ? 1 : schemaFailures.Count > 0 ? 3 : 0;
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
            // Before BuildUi: the driver blanks the window title as it initialises, so this is
            // the last moment the user's own title still exists to be saved. UpdateTitle then
            // names the window for the document, here and on every later change. See
            // TerminalTitle.
            TerminalTitle.Push();
            var ui = BuildUi(file);

            // BuildUi has already named the window (UpdateTitle), but that name does not
            // survive: the driver buffers its init preamble — the blanking OSC 0 among it —
            // and flushes the lot when the loop starts, landing *after* anything written
            // straight to stdout beforehand. Measured under a PTY: the title arrives, then the
            // blank wipes it, and the window stays nameless until the first edit happens to
            // rewrite it. So say it again from the loop, where nothing is queued behind us.
            // A timeout cannot fire before the loop runs, which is the ordering this needs.
            ui.App.AddTimeout(TimeSpan.FromMilliseconds(1), () =>
            {
                TerminalTitle.Set(_model.FileName, _model.Dirty);
                // From here and not earlier, for two reasons. A dialog needs a driver that has
                // learned the terminal size — a MessageBox laid out before that dies on a
                // negative width — and Main loads the document before the app exists at all,
                // so nothing raised from the load path would have had a UI to appear in (#37).
                // A timeout cannot fire before the loop runs, which is exactly the ordering
                // this needs.
                StartSchemaPrefetch(ui);
                return false; // once
            });

            // Last-resort net for exceptions that bypass the main loop (e.g. the driver's
            // input thread): restore the terminal and leave a stack behind before dying.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { ui.App.Dispose(); } catch { }
                TerminalTitle.Pop();
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
                TerminalTitle.Pop(); // the window keeps fux's name otherwise, long after fux
                WriteCrashLog(file, ex);
                return 70; // EX_SOFTWARE
            }
            ui.App.Dispose();
            TerminalTitle.Pop();
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
            public MenuItem[] EditInertMenu; // rows disabled for the life of an edit (#22)
            public MenuItem[] EditLiveMenu;  // ...and the rows that must stay enabled through one
            public bool Editing;     // value-pane edit mode (F2 commits, Esc cancels)
            public XmlNode EditNode; // node whose value is being edited
            public int ModalDepth;   // >0 while a dialog/message box runs: app-wide keys stay inert

            // A background schema fetch is in flight. While it is, Revalidate does nothing:
            // that is the whole of the mutual exclusion keeping the two threads off the shared
            // schema cache at once (see Schemas). It is also what the pane title reports, so
            // "loading" is never mistaken for "checked and clean".
            public bool SchemaPending;

            // The set of schema failures the user has already been shown, as Schemas.FailureKey
            // renders it. The prompt fires once per condition, not once per validation — and
            // again when the condition changes, because a schema that has started failing for
            // a new reason is news. Null means nothing has been acknowledged.
            public string SchemaAckKey;

            // The standing find, so F3 can repeat it. Kept as the raw query rather than a
            // built Query: an XPath one caches the nodes it selected, which the next edit
            // would invalidate (see the note on Find).
            public string FindExpr;
            public FindFlags FindOptions;
            public SearchFilter FindIn;
        }

        // Drill introspection: the engine instance behind the UI.
        internal static FuxCache Model => _model;

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
            // From here to the end of the process, this thread does not go to the network.
            // A schema fetch on the UI thread is a freeze however short its timeout, and
            // validation runs after every command, so the only safe rule is the absolute one:
            // the UI thread sees what is already in the schema cache and nothing else, and a
            // background thread is what puts things there (#35, see Schemas). Set here rather
            // than in RunUi so that --drill gets exactly the same UI thread the editor does —
            // and so CI cannot reach the network through the front end at all.
            XmlProxyResolver.OfflineThread = true;

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
            // Menu rows that cannot run during a value edit (#22). Registered as they are
            // built, next to the command each one calls, so the set cannot quietly drift from
            // the guards it mirrors: every row wrapped here is one whose command returns early
            // while ui.Editing, and every such command has a row here. Save is deliberately
            // absent — it commits the edit and then saves (see SaveFile).
            var editInert = new List<MenuItem>();
            MenuItem Inert(MenuItem item) { editInert.Add(item); return item; }
            // ...and the other half of that table, for the same reason. These rows mean
            // something during an edit — Cut/Copy/Paste act on the text in the field, Edit
            // Value commits, Save commits and writes, Quit asks — so disabling them would be
            // the bug in the other direction, and a drill check says so by name.
            var editLive = new List<MenuItem>();
            MenuItem Live(MenuItem item) { editLive.Add(item); return item; }

            var menu = new MenuBar(new MenuItem[]
            {
                new MenuBarItem("_File", new View[]
                {
                    Inert(new MenuItem("_Open…", "^O", () => StartOpen(ui), new Key())),
                    Live(new MenuItem("_Save", "^S", () => SaveFile(ui), new Key())),
                    Inert(new MenuItem("Save _As…", "", () => StartSaveAs(ui), new Key())), // menu-only: see the key handler
                    // The dialog's Retry is a one-shot: dismiss it and there would otherwise be
                    // no way back, so a session that started offline would stay unvalidated
                    // even after the VPN came up. Same call, reachable for the rest of the run.
                    Inert(new MenuItem("_Reload Schemas", "", () => RetrySchemas(ui), new Key())),
                    Live(new MenuItem("_Quit", "^Q", () => RequestQuit(ui), new Key())),
                }),
                new MenuBarItem("_Edit", new View[]
                {
                    Live(new MenuItem("Edit _Value", "F2", () => ToggleValueEdit(ui), new Key())),
                    // Hotkeys t/C/a: the obvious C-u-t and P-a-ste letters are taken by this
                    // menu's other items (see the note below on the nudge rows).
                    Live(new MenuItem("Cu_t", "^X", () => CutValue(ui), new Key())),
                    Live(new MenuItem("_Copy", "^C", () => CopyValue(ui), new Key())),
                    Live(new MenuItem("P_aste", "^V", () => PasteValue(ui), new Key())),
                    Inert(new MenuItem("Re_name…", "^R", () => StartRename(ui), new Key())),
                    Inert(new MenuItem("_Insert…", "^N", () => StartInsert(ui), new Key())),
                    Inert(new MenuItem("_Snippet…", "^B", () => StartSnippets(ui), new Key())),
                    Inert(new MenuItem("_Delete", "Del", () => DeleteSelected(ui), new Key())),
                    // Hotkeys here avoid V/n/I/D/U/R, already taken by this menu's other items.
                    Inert(new MenuItem("Nudge U_p", "^Shift+Up", () => NudgeSelected(ui, NudgeDir.Up), new Key())),
                    Inert(new MenuItem("Nudge Do_wn", "^Shift+Down", () => NudgeSelected(ui, NudgeDir.Down), new Key())),
                    Inert(new MenuItem("Nudge _Left", "^Shift+Left", () => NudgeSelected(ui, NudgeDir.Left), new Key())),
                    Inert(new MenuItem("Nudge Ri_ght", "^Shift+Right", () => NudgeSelected(ui, NudgeDir.Right), new Key())),
                    Inert(new MenuItem("_Undo", "^Z", () => DoUndo(ui), new Key())),
                    Inert(new MenuItem("_Redo", "^Y", () => DoRedo(ui), new Key())),
                }),
                new MenuBarItem("_Search", new View[]
                {
                    Inert(new MenuItem("_Find…", "^F", () => StartFind(ui), new Key())),
                    Inert(new MenuItem("Find _Next", "F3", () => FindAgain(ui, false), new Key())),
                    Inert(new MenuItem("Find _Previous", "Shift+F3", () => FindAgain(ui, true), new Key())),
                }),
                new MenuBarItem("_View", new View[]
                {
                    Live(new MenuItem("_Toggle Light/Dark", "F5", () => ToggleTheme(ui), new Key())),
                }),
                new MenuBarItem("_Help", new View[]
                {
                    new MenuItem("_About", "", () => ModalQuery(ui, "About fux", AboutText(), "OK")),
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

            // Two-pane sync: the tree drives, the value pane reflects the selection — except
            // while an edit is live, when the pane's text belongs to the user rather than to
            // the DOM. Unguarded, a selection change mid-edit replaced what had been typed with
            // the newly selected node's value, and the commit that followed wrote *that* into
            // the node the edit had started on (#20). Focus containment below is what keeps the
            // selection still during an edit; this is the net for any route that gets past it.
            tree.SelectionChanged += (s, e) =>
            {
                if (ui != null && ui.Editing) return;
                var n = e.NewValue;
                valueView.Text = n == null ? "" : GetValue(n) ?? "";
            };

            // Enter on the tree starts editing the selected node's value (F2 does the same
            // app-wide; F2 again commits, Esc cancels).
            tree.Accepting += (s, e) =>
            {
                if (StartValueEdit(ui)) e.Handled = true;
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
                // ^C/^X/^V, centrally so the tree answers them too — see the clipboard section.
                // Taking them here outside an edit means the value pane's own copy never runs
                // then, which is deliberate: CopyValue does the same thing for a highlight and
                // something useful for the tree, where TextView's copy has nothing to act on.
                else if (!ui.Editing && e.KeyCode == Key.C.WithCtrl.KeyCode) { CopyValue(ui); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.X.WithCtrl.KeyCode) { CutValue(ui); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.V.WithCtrl.KeyCode) { PasteValue(ui); e.Handled = true; }
                else if (!ui.Editing && IsNudgeKey(e, out var nudge)) { NudgeSelected(ui, nudge); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.O.WithCtrl.KeyCode) { StartOpen(ui); e.Handled = true; }
                else if (!ui.Editing && e.KeyCode == Key.B.WithCtrl.KeyCode) { StartSnippets(ui); e.Handled = true; }
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
                // Esc must never take fux down. v2 binds Esc to Command.Quit at the
                // *application* scope, which runs last — after the focused view and any
                // popover have declined the key — so a stray Esc on the tree stopped the
                // top runnable, which is the editor itself. Two Escs out of an edit did it
                // every time: the first cancelled the edit (the value pane's own handler,
                // below), the second quit and took the unsaved document with it.
                //
                // The binding cannot simply be removed. Dialog and MessageBox carry no Esc
                // binding of their own (checked against 2.4.17, not assumed) and close
                // through that same app-scope Quit — unbinding it would strand every modal
                // fux opens. So the key is swallowed here instead, where the two things
                // that legitimately want it are still visible to us and can be left alone:
                // a modal (the early return above) and an open menu.
                //
                // Decided in every state, not merely outside an edit. The old `!ui.Editing`
                // guard let the value pane take its own Esc — right when the pane held the
                // keyboard, which is true of every keyboard route into edit mode and false the
                // moment a click moved focus. In that gap Esc reached Command.Quit and took a
                // session's unsaved work with it, never passing RequestQuit, so ConfirmDiscard
                // never ran (#24). Cancelling here works whoever holds focus.
                else if (e.KeyCode == Key.Esc.KeyCode && !MenuIsOpen(ui))
                {
                    if (ui.Editing) CancelValueEdit(ui);
                    else SetValueStatus(ui, "Esc does not quit — ^Q does");
                    e.Handled = true;
                }
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
                Undo = undo, EditInertMenu = editInert.ToArray(), EditLiveMenu = editLive.ToArray(),
                // Set before the first Revalidate, not after: with a remote hint outstanding
                // that pass could only report what it cannot yet know. The pane says "loading
                // schema" until StartSchemaPrefetch's callback has a real answer.
                SchemaPending = Schemas.RemoteHints(_model).Count > 0,
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
            SetEditContainment(ui, true);
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

        // Note on what is deliberately NOT here: an arrow-confining branch in the app-wide key
        // handler, dispatching Command.Up/Down to the pane and swallowing the key. It was
        // written, and mutation-testing retired it — with it disabled the drill still passed
        // 331/331, because containment below already stops a declined arrow from reaching the
        // tree. It would have re-implemented caret movement through InvokeCommand, which is
        // not identical to the TextView's own key handling (vertical movement there remembers
        // a desired column), for no behaviour the drill could tell apart.
        //
        // An edit owns the keyboard until it ends. Terminal.Gui hands a key the focused view
        // declined to focus navigation, so an Up on a value's first line — or a mouse click on
        // the tree — moved focus out of a live edit while ui.Editing stayed true. That state,
        // edit live but pane unfocused, is what #24 quit from and #20 corrupted from: it is the
        // same rule CycleFocus already applies to F6 ("don't yank focus out of a live edit"),
        // applied to every other way in.
        //
        // Done by taking the other panes out of the focus chain, not by vetoing the value
        // pane's own HasFocusChanging. That veto reads well and does not hold — an explicit
        // SetFocus goes through it regardless (measured against 2.4.17 in the drill) — and a
        // guard that looks like containment without being it is worse than none. CanFocus is
        // consulted by every route, keyboard navigation and mouse alike.
        //
        // Order matters at both ends, and both callers respect it: the value pane takes focus
        // before the tree gives up CanFocus, and gets it back before EndValueEdit hands focus
        // on, so neither transition runs against a pane that cannot be focused.
        private static void SetEditContainment(Ui ui, bool editing)
        {
            ui.Tree.CanFocus = !editing;
            ui.ErrorList.CanFocus = !editing;
            // ...and stop the menu offering what it will not do. The commands guard themselves
            // already, correctly and invisibly: the row highlighted, the menu closed, and
            // nothing happened or was said (#22). MenuItem derives from View, so Enabled
            // applies, and Theme.Content.Disabled already renders it.
            if (ui.EditInertMenu != null)
                foreach (var item in ui.EditInertMenu) item.Enabled = !editing;
        }

        // One place to say why a command declined, so a chord and its menu row give the same
        // answer. The disabled row covers the menu; the keyboard has no affordance at all, and
        // ^B mid-edit was exactly as silent as the row it mirrors (#22). The value pane's
        // border title is already the channel for transient status.
        private static bool BusyEditing(Ui ui)
        {
            if (ui == null || !ui.Editing) return false;
            SetValueStatus(ui, "finish the edit first — F2 commits, Esc cancels");
            return true;
        }

        // Leave edit mode and restore the read-only value pane from the DOM.
        private static void EndValueEdit(Ui ui)
        {
            var node = ui.EditNode;
            ui.Editing = false;
            ui.EditNode = null;
            SetEditContainment(ui, false);
            var tv = (TextView)ui.ValueView;
            tv.ReadOnly = true;
            tv.Title = "Value";
            tv.Text = node == null ? "" : GetValue(node) ?? "";
            ui.Tree.SetFocus();
        }

        // --------------------------------------------------------------------
        // Clipboard: ^X/^C/^V and the matching Edit menu rows.
        //
        // Inside a live F2 edit these are Terminal.Gui's own, acting on the text
        // in the pane — that is what they mean in any editor, and the value pane
        // is a text editor at that moment. Everywhere else they act on the
        // selected node's *value*, which is what makes them work with the tree
        // focused, where there is no caret for a text copy to act on. Same split
        // ^Z/^Y/Del already use (see the app-wide key handler).
        //
        // The keys are routed centrally rather than bound to the value pane, so
        // the tree answers them; dialogs stay exempt because ModalDepth makes
        // that handler inert, leaving a Find field's own ^C/^V intact.
        //
        // The decision each one makes is a separate function from the OS write,
        // so --drill can assert on it without a clipboard at all. That split earns
        // its keep because the no-clipboard case is real — a headless box, or an
        // X11 one with no xclip — and no CI runner reproduces it: ubuntu-latest
        // and macos-latest both turned out to have a working clipboard, and the
        // drill skips Windows. It is covered by forcing the write to fail instead.
        // --------------------------------------------------------------------

        // What ^C should put on the clipboard: the highlight if the user made one in
        // the value pane, otherwise the whole value of the selected node. Null when
        // there is nothing to copy — an empty copy would silently wipe whatever the
        // user already had on the clipboard, which is worse than doing nothing.
        //
        // The highlight wins because it is the gesture the user just performed. It
        // survives losing focus, which is what makes Edit▸Copy (menu focused) agree
        // with the key, and it cannot go stale: moving the tree selection reassigns
        // the pane's Text, and TextView drops its selection when Text is set.
        internal static string CopyText(Ui ui)
        {
            var tv = (TextView)ui.ValueView;
            var n = ui.Tree.SelectedObject;
            string text = tv.IsSelecting ? tv.SelectedText : (n == null ? null : GetValue(n));
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static void CopyValue(Ui ui)
        {
            if (ui == null) return;
            if (ui.Editing) { ui.ValueView.InvokeCommand(Terminal.Gui.Input.Command.Copy); return; }

            var text = CopyText(ui);
            if (text == null) { SetValueStatus(ui, "nothing to copy"); return; }
            if (TrySetClipboard(ui, text)) SetValueStatus(ui, "copied");
        }

        // ^X copies the node's value and then clears it, as one undoable edit. It is
        // not a node cut: fux has no node clipboard, and deleting a subtree on a key
        // that everywhere else means "cut this text" is how people lose work. The
        // clipboard write comes first and a failed one aborts — cutting into a
        // clipboard that didn't take the text would destroy the only copy.
        private static void CutValue(Ui ui)
        {
            if (ui == null) return;
            if (ui.Editing) { ui.ValueView.InvokeCommand(Terminal.Gui.Input.Command.Cut); return; }

            var n = ui.Tree.SelectedObject;
            if (n == null || !EditNodeValue.CanEditValue(n)) return;
            var text = GetValue(n);
            if (string.IsNullOrEmpty(text)) { SetValueStatus(ui, "nothing to cut"); return; }
            if (!TrySetClipboard(ui, text)) return;
            ui.Undo.Push(new EditNodeValue(n, ""));
            SetValueStatus(ui, "cut");
        }

        // ^V replaces the selected node's value with the clipboard text, undoably.
        private static void PasteValue(Ui ui)
        {
            if (ui == null) return;
            if (ui.Editing) { ui.ValueView.InvokeCommand(Terminal.Gui.Input.Command.Paste); return; }

            var n = ui.Tree.SelectedObject;
            if (n == null || !EditNodeValue.CanEditValue(n)) return;
            string text = null;
            try { ui.App.Clipboard?.TryGetClipboardData(out text); } catch { text = null; }
            if (string.IsNullOrEmpty(text)) { SetValueStatus(ui, "clipboard empty"); return; }
            ui.Undo.Push(new EditNodeValue(n, text));
            SetValueStatus(ui, "pasted");
        }

        // The OS clipboard, through whatever the driver found for this platform:
        // NSPasteboard on macOS, xclip on X11, powershell.exe under WSL, the Win32
        // clipboard on Windows. Try* rather than the throwing pair — on a box with no
        // clipboard at all a copy should say so, not take the editor down with it.
        private static bool TrySetClipboard(Ui ui, string text)
        {
            bool ok;
            try { ok = ui.App.Clipboard?.TrySetClipboardData(text) ?? false; }
            catch { ok = false; }
            if (!ok) SetValueStatus(ui, "no clipboard");
            return ok;
        }

#pragma warning restore CS0618

        // MessageBox/dialog wrappers: ModalDepth keeps the app-wide key handler inert while
        // a modal runs (otherwise Del in a dialog's text field would delete the tree node).
        // The About box is the only attribution a user who never opens the repo will see, so it
        // names both copyright holders and disclaims endorsement — see LICENSE and
        // THIRD-PARTY-NOTICES.md. A method rather than an inline string so --drill can assert on
        // it: this text is a license-compliance artifact, and it should break the build loudly
        // rather than quietly lose the Microsoft notice. Lines are hard-wrapped narrow enough to
        // fit an 80-column terminal, since MessageBox sizes itself to the longest one. The
        // version comes from the assembly (set in Fux.csproj) so it cannot drift from the build.
        // Every option Main accepts. "--drill" is deliberately absent from UsageText below —
        // it is the interactive self-test, useful to a contributor and noise to everyone
        // else — but it belongs here, or the argument check would reject the thing CI runs.
        private static readonly string[] KnownFlags =
            { "--dump", "--validate", "--drill", "--no-backup", "--help", "-h", "--version" };

        // Not in KnownFlags: it takes a value, so it is matched by prefix in Main instead.
        private const string SchemaTimeoutFlag = "--schema-timeout=";

        // What `fux --help` prints. Kept to plain stdout on purpose: the first thing anyone
        // does with an unfamiliar binary is ask it what it is, and that answer has to arrive
        // without a TTY, a document, or a running UI. Version comes from the assembly, so it
        // matches the release the binary was downloaded from.
        // The one version string every surface prints: --version, the About box and the usage
        // text. InformationalVersion rather than AssemblyVersion, because AssemblyVersion is a
        // four-part numeric System.Version and cannot hold what identifies a build from source —
        // `git describe` output like 0.3.0-5-g87dd03e, or -dirty when the tree matches no commit
        // at all. Every local build used to report 0.0.0, which is consistent with every local
        // build ever made and so excluded nothing on a bug report (#26).
        //
        // A release passes it explicitly (release.yml), so released binaries print exactly the
        // tag as they always have. The fallback is for an assembly built without the property.
        internal static string VersionString { get; } = ReadVersion();

        private static string ReadVersion()
        {
            var info = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                    typeof(Program).Assembly)?.InformationalVersion;
            return string.IsNullOrWhiteSpace(info)
                ? typeof(Program).Assembly.GetName().Version.ToString(3)
                : info;
        }

        internal static string UsageText()
        {
            var version = VersionString;
            return "fux " + version + " — a cross-platform terminal XML editor.\n"
                + "\n"
                + "Usage:\n"
                + "  fux <file>              open the editor on a document\n"
                + "  fux --no-backup <file>  edit without keeping backups (see below)\n"
                + "  fux --dump <file>       print the document structure and exit\n"
                + "  fux --validate <file>   report XSD validation errors and exit\n"
                + "  fux --version           print the version and exit\n"
                + "  fux --help              print this message and exit\n"
                + "\n"
                + "  --schema-timeout=N      seconds to wait for a schema fetch (default 5)\n"
                + "\n"
                + "--validate exits 1 if the document has validation errors, 3 if a schema it\n"
                + "declares could not be fetched or was not a schema — so nothing checked it —\n"
                + "and 0 only when the document was validated and found clean.\n"
                + "\n"
                + "Before overwriting a file, fux copies its previous contents next to it as\n"
                + "<name>.<YYYYMMDD-HHMMSS>.bak. A save that changes nothing writes no backup,\n"
                + "and nothing removes old ones for you.\n"
                + "\n"
                + ".htm, .html, .json and .csv files are converted to XML on open. Saving an\n"
                + "imported document writes XML to a sibling file rather than overwriting it.\n"
                + "\n"
                + "Keys: F9 menu · F6 cycle panes · F2/Enter edit · ^F find · ^S save · ^Q quit\n"
                + "\n"
                + "https://github.com/MarcelInTO/fux";
        }

        internal static string AboutText()
        {
            return "fux " + VersionString + "\n"
                + "A cross-platform terminal XML editor.\n"
                + "\n"
                + "Copyright (c) 2026 Marcel Samek. MIT licensed.\n"
                + "\n"
                + "Built on the document engine from Microsoft XML\n"
                + "Notepad, Copyright (c) Microsoft Corporation,\n"
                + "MIT licensed. Not endorsed by or affiliated\n"
                + "with Microsoft.\n"
                + "\n"
                + "See LICENSE and THIRD-PARTY-NOTICES.md.";
        }

        internal static int? ModalQuery(Ui ui, string title, string message, params string[] buttons)
        {
            if (ui == null) return null;
            ui.ModalDepth++;
            try { return MessageBox.Query(ui.App, title, message, buttons); }
            finally { ui.ModalDepth--; }
        }

        // Runnable, not Dialog: the file pickers derive from Dialog<T>, which is not a Dialog.
        //
        // The caller owns the view and must dispose it in a `finally`, not on the last line of
        // the method — every site here does, and a seventh should too. Nothing between
        // constructing a dialog and tearing it down is guaranteed to return: building the view,
        // adding a button, taking focus and running the modal can all throw, and an unguarded
        // Dispose is then skipped and the whole subtree leaks for the life of the session (#25,
        // code scanning alert 1050). Dialog layout throwing is not hypothetical in this
        // codebase — a MessageBox dies on `width ('-3')` when the driver has not learned the
        // terminal size, which is exactly when a user would be opening dialogs that all leak.
        //
        // Read whatever the dialog is being asked for into locals inside the `try`; the values
        // have to outlive the view, and disposing it first would read from a torn-down view.
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
            if (ui == null || BusyEditing(ui)) return;
            var n = ui.Tree.SelectedObject;
            if (n == null || !RenameNode.CanRename(n)) return;

            var field = new TextField { X = 1, Y = 1, Width = Dim.Fill(1), Text = n.Name };
            bool ok = false;
            string newName = null;
            var d = new Dialog
            {
                Title = $"Rename {n.NodeType}",
                Width = 46, Height = 8,
            };
            try
            {
                d.Add(new Label { X = 1, Y = 0, Text = "New name:" }, field);
                d.AddButton(new Button { Text = "Cancel" }); // Result 0
                d.AddButton(new Button { Text = "OK" });     // Result 1; last added = default (Enter)
                field.SetFocus();
                field.MoveEnd(); // type to append; cursor at 0 would prepend into the prefilled name
                RunModal(ui, d);
                ok = d.Result is 1;
                newName = field.Text;
            }
            finally { d.Dispose(); }
            if (!ok || newName == n.Name) return;

            var err = TryRename(ui, n, newName);
            if (err != null)
                ModalQuery(ui, "Rename failed", err, "OK");
        }

        // ^N inserts a new node relative to the selection. The dialog collects kind,
        // position and name; TryInsert (headless-testable) is the commit path. Named blocks
        // are NOT here — they are their own panel (StartSnippets), because stamping a prepared
        // structure and naming a new empty node are different operations, and folding them
        // into one dialog meant a mode switch inside it.
        internal static void StartInsert(Ui ui)
        {
            if (ui == null || BusyEditing(ui)) return;
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
                Labels = PosLabels,
                Value = 0,
            };
            var field = new TextField { X = 1, Y = 1, Width = Dim.Fill(1) };
            bool ok = false;
            InsertKind kind = InsertKind.Element;
            InsertPos pos = InsertPos.Child;
            string name = null;
            var d = new Dialog
            {
                Title = $"Insert at {GetLabel(n)}",
                Width = 50, Height = 12,
            };
            try
            {
                d.HotKeySpecifier = NoHotKey; // the title carries a node name — see NoHotKey
                d.Add(new Label { X = 1, Y = 0, Text = "Name (element/attribute/PI):" }, field, kindSel, posSel);
                d.AddButton(new Button { Text = "Cancel" }); // Result 0
                d.AddButton(new Button { Text = "OK" });     // Result 1; last added = default (Enter)
                field.SetFocus();
                RunModal(ui, d);
                ok = d.Result is 1;
                kind = (InsertKind)(kindSel.Value ?? 0);
                pos = PosAt(posSel.Value ?? 0);
                name = field.Text;
            }
            finally { d.Dispose(); }
            if (!ok) return;

            var err = TryInsert(ui, n, kind, pos, name);
            if (err != null)
                ModalQuery(ui, "Insert failed", err, "OK");
        }

        // How the two panels name the three positions, and what each one means to the DOM.
        // "Below"/"Above" are what a reader sees in the tree; Before/After are document-order
        // words that leave you working out which is which. One vocabulary, in both panels.
        internal static readonly string[] PosLabels = { "Below", "Above", "Child" };
        private static readonly InsertPos[] PosOrder = { InsertPos.After, InsertPos.Before, InsertPos.Child };

        internal static InsertPos PosAt(int index)
            => PosOrder[index < 0 || index >= PosOrder.Length ? 0 : index];

        // Identifiers for the Snippets panel's controls, so --drill can reach them inside the
        // modal where it has no reference to them.
        internal const string SnippetListId = "fux-snippet-list";
        internal const string SnippetPosId = "fux-snippet-pos";

        // Session memory. Inserting the same block many times running is the normal case when
        // marking up a document — twelve verse-lines is twelve of these — so the panel reopens
        // where it was left rather than at the top of the list every time.
        private static int _lastSnippet;
        private static int _lastSnippetPos;

        // ^B: insert one of the user's named blocks. Its own panel, so the list gets the height
        // a real config needs; the position row is one line rather than a column for the same
        // reason. The list takes focus on open, so the whole gesture is ^B, arrow, Enter.
        internal static void StartSnippets(Ui ui)
        {
            if (ui == null || BusyEditing(ui)) return;
            var n = ui.Tree.SelectedObject;
            if (n == null) return;

            var set = Snippets.Load();
            if (set.Blocks.Count == 0)
            {
                // Nothing usable: say where the file goes rather than opening an empty list.
                ModalQuery(ui, "Snippets",
                    set.Problem ?? "No snippets are defined.\n\nPut them in " + Snippets.ConfigPath(),
                    "OK");
                return;
            }

            var posSel = new OptionSelector
            {
                X = 1, Y = 0,
                Orientation = Orientation.Horizontal,
                Labels = PosLabels,
                Value = _lastSnippetPos,
                Id = SnippetPosId,
            };

            var names = new ObservableCollection<string>();
            foreach (var b in set.Blocks) names.Add(b.Name);

            var list = new ListView
            {
                X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(2),
                Id = SnippetListId,
            };
            list.SetSource(names);
            list.HotKeySpecifier = NoHotKey; // the rows are the user's own text
            list.SelectedItem = _lastSnippet < names.Count ? _lastSnippet : 0;

            // Height from the screen, not from arithmetic about how much room a Dialog has:
            // guessing that is exactly how the first attempt at this ended up showing two
            // snippets out of nineteen. Ask for what the terminal has and let the list scroll.
            int screenH = ui.Top.Viewport.Height;
            int wanted = names.Count + 6;                 // position row, borders, buttons
            int height = Math.Max(10, Math.Min(wanted, Math.Max(10, screenH - 4)));
            int width = 0;
            foreach (var name in names) width = Math.Max(width, name.Length);
            width = Math.Max(44, Math.Min(width + 8, Math.Max(44, ui.Top.Viewport.Width - 6)));

            var d = new Dialog
            {
                Title = $"Snippet at {GetLabel(n)}",
                Width = width, Height = height,
            };
            bool ok = false;
            int chosen = 0, posIndex = 0;
            try
            {
                d.HotKeySpecifier = NoHotKey; // the title carries a node name — see NoHotKey
                d.Add(posSel, list);
                d.AddButton(new Button { Text = "Cancel" }); // Result 0
                d.AddButton(new Button { Text = "OK" });     // Result 1; last added = default (Enter)

                // Enter on the list commits, so the whole gesture is ^B, arrow, Enter — no Tab,
                // no button, no mouse. Bound explicitly: 2.4.17 has no OpenSelectedItem event, and
                // Accept does NOT bubble from a ListView to the dialog's default button (drilled —
                // the assumption that it did failed the check that pins this).
                bool accepted = false;
                list.KeyDown += (s, e) =>
                {
                    if (e.KeyCode != Key.Enter.KeyCode) return;
                    accepted = true;
                    e.Handled = true;
                    ui.App.RequestStop(d);
                };

                list.SetFocus();
                RunModal(ui, d);
                ok = accepted || d.Result is 1;
                chosen = list.SelectedItem ?? 0;
                posIndex = posSel.Value ?? 0;
            }
            finally { d.Dispose(); }
            if (!ok || chosen < 0 || chosen >= set.Blocks.Count) return;

            _lastSnippet = chosen;
            _lastSnippetPos = posIndex;

            var err = TryInsertBlock(ui, n, PosAt(posIndex), set.Blocks[chosen]);
            if (err != null)
                ModalQuery(ui, "Insert failed", err, "OK");
        }

        // Push a named-block insert through the undo stack. Separate from TryInsert because a
        // block carries its own name, kind and attributes — there is nothing to type. Returns
        // null on success, else the reason.
        internal static string TryInsertBlock(Ui ui, XmlNode anchor, InsertPos pos, Block block)
        {
            InsertNewNode cmd;
            try
            {
                cmd = new InsertNewNode(anchor, pos, block?.Template, _model.Format.IndentChars);
            }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException)
            {
                return ex.Message;
            }
            ui.Undo.Push(cmd);
            return null;
        }

        // Push an insert through the undo stack. Returns null on success, else the reason.
        internal static string TryInsert(Ui ui, XmlNode anchor, InsertKind kind, InsertPos pos, string name)
        {
            InsertNewNode cmd;
            try
            {
                cmd = new InsertNewNode(anchor, kind, pos, name, _model.Format.IndentChars);
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
            if (ui == null || BusyEditing(ui)) return;
            var n = ui.Tree.SelectedObject;
            if (n == null) return;
            if (!ConfirmDelete(ui, n)) return;
            var err = TryDelete(ui, n);
            if (err != null)
                ModalQuery(ui, "Delete failed", err, "OK");
        }

        // Del sits beside the navigation keys, takes no modifier, and the cursor is already in
        // the tree — and the tree gives a row to every element *and every attribute*, so one
        // keypress on a collapsed <division> can take 1899 rows of a book with it. That the
        // delete is undoable was the original reason not to ask, and it is not wrong; the case
        // for asking is that undo only helps someone who notices, and a stray Del leaves nothing
        // on screen to notice (#21).
        //
        // Asked only when something goes with the node. A leaf — an attribute, an empty element,
        // an element whose only content is its own text — is the most common delete there is and
        // the most trivially restored, and a modal in front of it would train people to dismiss
        // the modal. Naming the count is the point: "Delete this node?" teaches the reflex that
        // makes the prompt useless.
        //
        // Esc makes ModalQuery return null, which is not the confirming index either, so backing
        // out of the prompt is as safe as choosing Cancel.
        //
        // Cancel goes LAST, because that is what makes it the default button: MessageBox marks
        // the last label as default, not the first. Measured, not assumed — the ^Q prompt renders
        // "Save, Discard, Cancel" with the default brackets on Cancel, and this prompt was
        // written the other way round first and rendered ⟦► Delete ◄⟧, arming exactly the
        // reflexive Enter the confirmation exists to stop.
        //
        // DeleteConfirmed is the index that proceeds, and it sits next to the array because the
        // two only mean anything together: reorder one without the other and the prompt inverts,
        // with Cancel deleting. The drill cannot catch that by pressing the button — MessageBox's
        // Dialog exposes no SubViews in 2.4.17 (its adornments are lightweight settings objects,
        // not views), and injected keys reach only app-scope bindings, which is why Esc appears
        // to work while Tab and the arrows do nothing — so it asserts the pairing from here.
        internal static readonly string[] DeleteButtons = { "Delete", "Cancel" };
        internal const int DeleteConfirmed = 0;

        // Does deleting this node take anything else with it? The one rule behind the prompt.
        internal static bool NeedsDeleteConfirm(XmlNode n) => n != null && TreeRows(n) > 1;

        internal static string DeletePrompt(XmlNode n)
        {
            int under = TreeRows(n) - 1;
            return $"Delete {GetLabel(n)} and the {under} row{(under == 1 ? "" : "s")} under it?";
        }

        private static bool ConfirmDelete(Ui ui, XmlNode n)
        {
            if (!NeedsDeleteConfirm(n)) return true;
            return ModalQuery(ui, "Delete", DeletePrompt(n), DeleteButtons) == DeleteConfirmed;
        }

        // Rows the tree draws for this node and everything beneath it. Counted through
        // GetChildren, the same rule that draws them, so the number in the prompt is the number
        // of rows that will actually disappear — attributes included, and they are the bulk of
        // a marked-up book.
        private static int TreeRows(XmlNode n)
        {
            int rows = 1;
            foreach (var c in GetChildren(n)) rows += TreeRows(c);
            return rows;
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
            if (ui == null || BusyEditing(ui)) return;

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
            bool ok = false;
            string expr = null;
            var flags = FindFlags.Normal;
            var filter = SearchFilter.Everything;
            try
            {
                d.Add(new Label { X = 1, Y = 0, Text = "Find what:" }, field,
                      new Label { X = 1, Y = 3, Text = "Mode:" },
                      new Label { X = 16, Y = 3, Text = "Look in:" },
                      modeSel, filterSel, caseBox, wordBox);
                d.AddButton(new Button { Text = "Cancel" }); // Result 0
                d.AddButton(new Button { Text = "Find" });   // Result 1; last added = default (Enter)
                field.SetFocus();
                field.MoveEnd();
                RunModal(ui, d);

                ok = d.Result is 1;
                expr = field.Text;
                if (modeSel.Value == 1) flags |= FindFlags.Regex;
                else if (modeSel.Value == 2) flags |= FindFlags.XPath;
                if (caseBox.Value == CheckState.Checked) flags |= FindFlags.MatchCase;
                if (wordBox.Value == CheckState.Checked) flags |= FindFlags.WholeWord;
                filter = (SearchFilter)(filterSel.Value ?? 0);
            }
            finally { d.Dispose(); }
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
            if (ui == null || BusyEditing(ui)) return;
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

        // Transient status reports into the value pane's border title, next to whatever the
        // command just acted on. Not a message box: at the end of a find ring, F3 popping a
        // dialog on every press would be unusable. Not the status bar either — that row is
        // already full at 100 columns, and an extra slot there renders clipped to two
        // characters (measured, not guessed). Editing takes the title back (EndValueEdit),
        // which is the right precedence. An empty text restores the plain title.
        internal static void SetValueStatus(Ui ui, string text)
        {
            if (ui?.ValueView == null || ui.Editing) return;
            ui.ValueView.Title = string.IsNullOrEmpty(text) ? "Value" : $"Value — {text}";
        }

        // Find labels its own reports so "1/3" can't be mistaken for anything else.
        private static void SetFindStatus(Ui ui, string text)
            => SetValueStatus(ui, string.IsNullOrEmpty(text) ? "" : $"find {text}");

        // True while the menu bar is showing, in any of the ways v2 reports it. Esc belongs
        // to the menu then — it is how a menu opened by accident is dismissed — so the
        // no-quit rule above steps aside. Deliberately a disjunction rather than the one
        // "correct" flag: which of these is set depends on whether the popover has been
        // through a main-loop iteration yet, and being wrong in the cautious direction
        // costs an Esc that quietly does nothing instead of one that cannot close a menu.
        internal static bool MenuIsOpen(Ui ui)
            => ui?.Menu != null
               && (ui.Menu.Active || ui.Menu.IsOpen() || ui.App?.Popovers?.GetActivePopover() != null);

        // Ctrl+Shift+Arrow nudges the selected node: up/down reorder it among its siblings,
        // left/right change its level. Refusals are quiet (see TryNudge) — running into the
        // edge of a document should feel like running into the end of a list.
        private static void NudgeSelected(Ui ui, NudgeDir dir)
        {
            if (ui == null || BusyEditing(ui)) return;
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
                cmd = new NudgeNode(node, dir, _model.Format.IndentChars);
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
            if (ui == null || BusyEditing(ui)) return;
            ui.Undo.Undo(); // no-ops when the stack is empty
        }

        private static void DoRedo(Ui ui)
        {
            if (ui == null || BusyEditing(ui)) return;
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
            ScrollIntoView(ui, node);
            ui.Tree.SelectedObject = node;
        }

        // EnsureVisible is a *minimum* scroll by design: it moves the view exactly far enough to
        // bring the row inside the viewport, which parks the node on whichever edge it came in
        // from. A search hit landing there has no structure to read it against and is one cursor
        // press from scrolling back out of sight (#10). Aim for the middle instead, and let the
        // view stop at the first and last screenful rather than scrolling past them — no blank
        // space above the root, none below the last node.
        //
        // A node already on screen is left exactly where it is, edge or not. That is decided,
        // not an oversight: re-centring on every reveal would make F3 through a run of
        // neighbouring matches jump the pane about, which is worse than the problem. It is also
        // what keeps a delete from moving the view at all, now that the delete lands on a
        // neighbour of the row it removed (#18).
        private static void ScrollIntoView(Ui ui, XmlNode node)
        {
            var tree = ui.Tree;
            int row = tree.GetScrollOffsetOf(node);
            if (row < 0) { tree.EnsureVisible(node); return; } // not exposed: let v2 decide
            int height = tree.Viewport.Height;
            if (height <= 0) { tree.EnsureVisible(node); return; } // not laid out yet
            int top = tree.ScrollOffsetVertical;
            if (row >= top && row < top + height) return;         // already on screen: leave it
            // Assigned raw: ScrollOffsetVertical clamps both ends itself. The low end is
            // documented ("a value of less than 0 will result in an offset of 0") and the high
            // end was measured — assigning an offset 500 past the end parks the view at exactly
            // total - height. An explicit Math.Clamp was written here first, and mutation
            // testing retired it: removing it changed no check, because the framework was
            // already doing the work. The behaviour stays pinned either way, since §13d asserts
            // the last screenful by offset — if a Terminal.Gui update ever stops clamping, that
            // check is what says so.
            tree.ScrollOffsetVertical = row - height / 2;
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
            bool hasFile = _model.Document?.DocumentElement != null;

            // A fetch is in flight. Validating now would be both wrong and unsafe: wrong
            // because the schemas it would report on are the ones not yet loaded, and unsafe
            // because the background thread is writing to the schema cache this pass reads.
            // Not validating is the mutual exclusion — see the concurrency rule in Schemas.
            // The previous pass's rows stay put; only the title changes, so the pane cannot
            // read "0 errors" while the answer is still being fetched.
            if (ui.SchemaPending)
            {
                ui.ErrorList.Title = SummarizeValidation(ui.Errors, hasFile, null, true).Trim();
                return;
            }

            var items = RunValidation();
            ui.Errors.Clear();
            ui.Errors.AddRange(items);
            ui.ErrorList.Title = SummarizeValidation(ui.Errors, hasFile, Schemas.Settled(_model.SchemaFailures), false).Trim();
            ui.ErrorList.SetSource(new ObservableCollection<string>(BuildErrorLines(ui.Errors)));
        }

        // Start (or restart) resolution of the document's schemas, and report on the result.
        //
        // The single entry point for both halves of "is this document being checked at all":
        // the remote hints go to a background thread, and when that settles — immediately, if
        // there are none — the pane and the prompt are brought up to date. Called on open, on
        // opening another document, and on Retry.
        private static void StartSchemaPrefetch(Ui ui)
        {
            if (ui == null) return;
            var remote = Schemas.RemoteHints(_model);
            ui.SchemaPending = remote.Count > 0;
            if (ui.SchemaPending) Revalidate(ui); // repaint as "loading" before the wait starts
            Schemas.Prefetch(_model, remote, ui.App, () =>
            {
                ui.SchemaPending = false;
                Revalidate(ui);
                WarnIfSchemaUnavailable(ui);
            });
        }

        // Tell the user, once, that this document is not being validated and let them decide
        // what to do about it.
        //
        // The dialog is half the answer and the pane title is the other half (#37). A modal
        // fires once; the condition lasts the session, and after this is dismissed the title
        // is the only thing still saying the document is unchecked — which is why
        // SummarizeValidation must not go back to reading "0 errors".
        //
        // Headless callers never reach this: ModalQuery returns null when ui is null, and
        // --validate and --dump never build a Ui at all. §16 of the drill asserts that rather
        // than assuming it, since a regression here would hang CI instead of failing it.
        internal static void WarnIfSchemaUnavailable(Ui ui)
        {
            if (ui == null || ui.SchemaPending) return;
            var failures = Schemas.Settled(_model.SchemaFailures);
            if (failures.Count == 0)
            {
                ui.SchemaAckKey = null; // the schemas resolved; a later failure is news again
                return;
            }
            // A fetch settles on the main loop, and a modal runs a nested one — so this can
            // arrive while the user is in the middle of another dialog. Stacking a second box
            // on top of it would be both rude and unreadable. Come back when the screen is
            // theirs again; the acknowledgement is deliberately not recorded yet, so nothing
            // is lost by waiting.
            if (ui.ModalDepth > 0)
            {
                ui.App.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
                {
                    WarnIfSchemaUnavailable(ui);
                    return false; // once; if a dialog is still up this re-arms from the top
                });
                return;
            }
            var key = Schemas.FailureKey(failures);
            if (key == ui.SchemaAckKey) return; // already said so, and nothing has changed
            ui.SchemaAckKey = key;

            // Esc closes a MessageBox with -1, which lands here as neither Retry nor Quit —
            // i.e. as Continue, the same as the button. That is the right reading of Esc: it
            // dismisses the dialog, it does not quit the editor and it does not refetch.
            int choice = ModalQuery(ui, "Schema unavailable", Schemas.Describe(failures),
                                    SchemaButtons) ?? SchemaContinue;
            if (choice == SchemaRetry) RetrySchemas(ui);
            else if (choice == SchemaQuit) RequestQuit(ui);
        }

        // Quit last, and therefore the default: MessageBox binds Enter to the LAST button, the
        // trap #21 walked into. Of the two reflexes that is the safe one — a reflexive Enter
        // that quits costs a relaunch, while a reflexive Enter that dismisses lands the user
        // editing a document nothing is checking, which is the exact state this prompt exists
        // to prevent. Retry is first because the usual causes are transient: wifi not up yet,
        // VPN not connected, captive portal not signed into.
        //
        // The indices sit next to the array for the same reason DeleteButtons' do: reorder one
        // without the other and the prompt inverts. The drill asserts the pairing from here,
        // because MessageBox's Dialog exposes no SubViews in 2.4.17 and its buttons cannot be
        // pressed by an injected key.
        internal static readonly string[] SchemaButtons = { "Retry", "Continue", "Quit" };
        internal const int SchemaRetry = 0;
        internal const int SchemaContinue = 1;
        internal const int SchemaQuit = 2;

        // Forget every remembered failure and resolve the document's schemas again.
        //
        // Clearing the acknowledgement too: the user asked for a fresh answer, so a fresh
        // answer — including the same failure a second time — is worth showing. The prompt
        // that follows is raised from the prefetch callback, never from inside this call, so
        // repeated retries unwind one dialog before opening the next instead of nesting.
        internal static void RetrySchemas(Ui ui)
        {
            (_model.SchemaResolver as SchemaResolver)?.ClearFailures();
            ui.SchemaAckKey = null;
            StartSchemaPrefetch(ui);
        }

        // The tree pane title doubles as the document title: file name + dirty marker.
        private static void UpdateTitle(Ui ui)
        {
            var name = _model.FileName == null ? "Tree" : System.IO.Path.GetFileName(_model.FileName);
            ui.Tree.Title = _model.Dirty ? name + " *" : name;
            // The terminal window carries the same two facts. One place computes them, so the
            // pane title and the window title cannot drift apart.
            TerminalTitle.Set(_model.FileName, _model.Dirty);
        }

        private static void SaveFile(Ui ui)
        {
            if (ui == null) return;
            // Save while typing means "commit this and write the file" — that is what the
            // keystroke means to someone mid-paragraph, and it is why Save is the one command
            // here that is not disabled during an edit. Refusing silently was #22's worst
            // case: no save, no error, no dirty marker cleared, and every reason to believe
            // the work was on disk. An implicit commit is the lesser surprise, and it is
            // undoable — CommitValueEdit pushes through the UndoManager like any other edit.
            if (ui.Editing) CommitValueEdit(ui);
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
            // A live edit is unsaved work the model cannot see. The text is in the value pane
            // and has not reached the DOM, so Dirty is false and the gate waved a quit straight
            // through with the paragraph still in it (#22). Asking is the whole answer here:
            // Save commits and writes (SaveFile does the commit), Discard drops it because the
            // user said so, and Cancel leaves the edit exactly as it was — which is why this
            // does not commit up front. Committing before asking would make Cancel destructive.
            if (!_model.Dirty && !(ui != null && ui.Editing)) return true;
            var name = _model.FileName == null ? "this document" : System.IO.Path.GetFileName(_model.FileName);
            int choice = ModalQuery(ui, "Unsaved changes", $"Save changes to {name}?", "Save", "Discard", "Cancel") ?? 2;
            if (choice == 0) { SaveFile(ui); return !_model.Dirty; } // save failed → stay
            return choice == 1;                                      // Discard; anything else cancels
        }

        // The only way out of fux, and it asks first. Every key and menu item that quits
        // comes through here, which is why Esc must not reach the framework's own
        // Esc-stops-the-top-runnable binding (see the Esc note in BuildUi) — that binding
        // tore the session down without passing this way, and unsaved work went with it.
        //
        // The tempting-looking alternative — override Runnable.OnIsRunningChanging and
        // prompt there, which is what Terminal.Gui's own documentation suggests, so that no
        // exit could ever skip the prompt — does not work in 2.4.17 and was tried: that
        // event is raised from inside ApplicationImpl.End, by which point the screen is
        // being torn down, and the message box dies laying itself out on a negative width.
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
            if (ui == null || BusyEditing(ui)) return;
            if (!ConfirmDiscard(ui)) return;

            var d = new OpenDialog { Title = "Open", OpenMode = OpenMode.File, AllowsMultipleSelection = false };
            string picked = null;
            try
            {
                d.HotKeySpecifier = NoHotKey; // paths carry underscores — see NoHotKey
                if (_model.FileName != null) d.Path = _model.FileName;
                RunModal(ui, d);
                picked = d.FilePaths.Count > 0 ? d.FilePaths[0] : null; // empty when cancelled
            }
            finally { d.Dispose(); }
            if (string.IsNullOrWhiteSpace(picked)) return;

            var err = TryOpen(ui, picked);
            if (err != null) ModalQuery(ui, "Open failed", err, "OK");
        }

        private static void StartSaveAs(Ui ui)
        {
            if (ui == null || BusyEditing(ui)) return;

            var d = new SaveDialog { Title = "Save As" };
            string picked = null;
            try
            {
                d.HotKeySpecifier = NoHotKey;
                if (_model.FileName != null) d.Path = _model.FileName;
                RunModal(ui, d);
                picked = d.FileName; // null when cancelled
            }
            finally { d.Dispose(); }
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
            // Whatever was acknowledged was about the document being replaced. Cleared before
            // the prefetch, so the new document's schemas get their own prompt if they need it.
            ui.SchemaAckKey = null;
            ui.SchemaPending = false;
            Revalidate(ui);
            StartSchemaPrefetch(ui);
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

        // The error pane's title, and the first line of `--validate`'s output.
        //
        // Its job is not to count things, it is to answer "was this document checked?". It
        // used to conflate the two: with an unreachable schema it said "0 errors", which reads
        // as a pass and is the wrong direction to fail in (#36). A document nothing validated
        // now says so, here and for as long as the condition lasts — the dialog fires once,
        // this is what is still on screen afterwards (#37).
        private static string SummarizeValidation(List<ErrorItem> items, bool hasFile,
                                                  IList<SchemaLoadFailure> schemaFailures, bool pending)
        {
            if (!hasFile) return " (no file loaded)";
            if (pending) return " Validation: loading schema…";

            int failed = schemaFailures == null ? 0 : schemaFailures.Count;
            string Plural(int n, string w) => $"{n} {w}{(n == 1 ? "" : "s")}";

            int errors = 0, warnings = 0;
            foreach (var it in items)
            {
                if (it.Severity == Severity.Error) errors++;
                else if (it.Severity == Severity.Warning) warnings++;
            }

            if (failed > 0)
            {
                // Every hint the document declares failed, so nothing was checked against
                // anything: lead with that instead of a count, which would only describe the
                // handful of things validation can find without a schema.
                int hints = Schemas.HintCount(_model);
                string what = failed >= hints
                    ? $" Not validated: {Plural(failed, "schema")} unavailable"
                    : $" Validation: {Plural(errors, "error")}, {Plural(warnings, "warning")}"
                      + $" — {Plural(failed, "schema")} unavailable";
                return what + "   (Enter: go to node)";
            }

            if (items.Count == 0) return " Validation: no issues";
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
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        // Only in mixed content; elsewhere the text is folded into its element's
                        // value and the element is the row to land on.
                        if (IsShown(n)) return n;
                        break;
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
            {
                // Whether the text children get rows depends on the parent, not on each child,
                // so it is settled once here rather than re-derived per child. An element with
                // thousands of interleaved text and element children is drawn in one pass over
                // them instead of one pass per text node.
                bool container = IsContainer(n);
                foreach (XmlNode c in n.ChildNodes)
                    if (IsShown(c, container))
                        yield return c;
            }
        }

        // Does the tree give this child a row of its own?
        //
        // An element with nothing but text-ish children has that text folded into its own value
        // (see GetValue), so the text nodes stay invisible — `<name>Fred</name>` is one row, not
        // two. A *container* has no scalar value to fold into, so its text children get rows of
        // their own; without that, the prose in mixed content (`<p>Call me <i>Ishmael</i>. Some
        // years ago…</p>`) would appear nowhere in the tree at all, and so would be invisible to
        // find as well. Whitespace between elements is layout rather than content and stays
        // hidden either way — it is the document's indentation, kept in the DOM only so a save
        // can reproduce the file that was opened. It needs no guard of its own here: a reader
        // reports a whitespace-only run as Whitespace/SignificantWhitespace, never as Text, so
        // indentation inside mixed content is caught by the case above rather than by inspecting
        // the value of every text node the tree draws.
        internal static bool IsShown(XmlNode n) => IsShown(n, IsContainer(n.ParentNode));

        // The same rule with the parent's container-ness already in hand, for callers walking a
        // whole child list. Kept as one implementation so the two entry points cannot drift.
        private static bool IsShown(XmlNode n, bool parentIsContainer)
        {
            switch (n.NodeType)
            {
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    return false;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                    return parentIsContainer;
                default:
                    return true;
            }
        }

        // Has this node children that are more than text — element, comment or PI — making it a
        // structure to descend into rather than something with a scalar value? This is the one
        // rule behind both halves of the fold: IsShown gives a container's text children rows,
        // and GetValue declines to give the container itself a value.
        internal static bool IsContainer(XmlNode n)
        {
            if (n == null || (n.NodeType != XmlNodeType.Element && n.NodeType != XmlNodeType.Document))
                return false;
            foreach (XmlNode c in n.ChildNodes)
                if (!IsTextish(c)) return true;
            return false;
        }

        private static bool IsTextish(XmlNode n)
            => n.NodeType == XmlNodeType.Text || n.NodeType == XmlNodeType.CDATA ||
               n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace;

        internal static string GetLabel(XmlNode n)
        {
            switch (n.NodeType)
            {
                case XmlNodeType.Element: return "<" + n.Name + ">";
                case XmlNodeType.Attribute: return "@" + n.Name;
                case XmlNodeType.Comment: return "<!-- comment -->";
                case XmlNodeType.ProcessingInstruction: return "<?" + n.Name + "?>";
                case XmlNodeType.CDATA: return "<![CDATA[]]>";
                case XmlNodeType.Text: return "#text";
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
                    // A container's text is shown on rows of its own (IsShown), so folding it in
                    // here as well would print it twice.
                    if (IsContainer(n)) return "";
                    string text = null;
                    foreach (XmlNode c in n.ChildNodes)
                        text = (text ?? "") + c.Value;
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
