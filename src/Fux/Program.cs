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
                    _model.Load(file);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"fux: cannot load '{file}': {ex.Message}");
                    return 2;
                }
            }

            return dump ? Dump() : validate ? Validate() : drill ? Drill.Run(file) : RunUi(file);
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
            ui.App.Run(ui.Top, null);
            ui.App.Dispose();
            return 0;
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
        }

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
            var menu = new MenuBar(new MenuItem[]
            {
                new MenuBarItem("_File", new View[]
                {
                    new MenuItem("_Quit", "", () => app.RequestStop(top), Key.Q.WithCtrl),
                }),
                new MenuBarItem("_View", new View[]
                {
                    new MenuItem("_Toggle Light/Dark", "F5", () => ToggleTheme(ui), new Key()),
                }),
                new MenuBarItem("_Help", new View[]
                {
                    new MenuItem("_About", "", () =>
                        MessageBox.Query(app, "About fux", "A terminal XML editor over the XmlNotepad engine.", "OK")),
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

            // Two-pane sync: the tree drives, the value pane reflects the selection.
            tree.SelectionChanged += (s, e) =>
            {
                var n = e.NewValue;
                valueView.Text = n == null ? "" : GetValue(n) ?? "";
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

            // Validate the loaded document and surface the diagnostics in the pane.
            var errors = RunValidation();
            errorList.Title = SummarizeValidation(errors, root != null).Trim();
            errorList.SetSource(new ObservableCollection<string>(BuildErrorLines(errors)));

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
                int cur = Array.FindIndex(focusRing, v => v.HasFocus);
                focusRing[(cur + 1) % focusRing.Length].SetFocus();
            }

            // The status F9 entry is a hint only, with no live key, so it can't race the MenuBar
            // for the keypress. Alt+F/Alt+H also work when the terminal sends Option as Meta —
            // off by default on macOS, so F9 is the reliable path.
            var status = new StatusBar(new Shortcut[]
            {
                new Shortcut(Key.Q.WithCtrl, "Quit", () => app.RequestStop(top), null),
                new Shortcut(new Key(), "F9 Menu", null, null), // hint only: a live F9 binding here would swallow the MenuBar's key
                new Shortcut(Key.F6, "Focus", CycleFocus, null),
                new Shortcut(Key.F5, "Theme", () => ToggleTheme(ui), null),
            })
            {
                X = 0, Y = Pos.AnchorEnd(), Width = Dim.Fill(),
            };
            // Bind the actionable shortcuts application-wide so ^Q/F6 work from any pane.
            foreach (var v in status.SubViews)
                if (v is Shortcut sc && sc.Action != null) sc.BindKeyToApplication = true;

            top.Add(menu, tree, valueView, errorList, status);
            tree.SetFocus();
            ui = new Ui
            {
                App = app, Top = top, Menu = menu, Status = status,
                Tree = tree, ValueView = valueView, ErrorList = errorList, Errors = errors,
            };
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
        private static IEnumerable<XmlNode> GetChildren(XmlNode n)
        {
            if (n is XmlElement el && el.Attributes != null)
                foreach (XmlAttribute a in el.Attributes)
                    yield return a;

            if (n.NodeType == XmlNodeType.Element || n.NodeType == XmlNodeType.Document)
                foreach (XmlNode c in n.ChildNodes)
                    if (c.NodeType != XmlNodeType.Text && c.NodeType != XmlNodeType.CDATA &&
                        c.NodeType != XmlNodeType.Whitespace && c.NodeType != XmlNodeType.SignificantWhitespace)
                        yield return c;
        }

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
