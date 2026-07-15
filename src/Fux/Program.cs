using System;
using System.Collections.Generic;
using System.Xml;
using Terminal.Gui;
using Terminal.Gui.Graphs;
using Terminal.Gui.Trees;
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

            return dump ? Dump() : validate ? Validate() : RunUi(file);
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
            Application.Init();
            Theme.Apply();
            var top = Application.Top;
            top.ColorScheme = Theme.Content;

            var menu = new MenuBar(new MenuBarItem[]
            {
                new MenuBarItem("_File", new MenuItem[]
                {
                    new MenuItem("_Quit", "", () => Application.RequestStop(), null, null, Key.CtrlMask | Key.Q),
                }),
                new MenuBarItem("_Help", new MenuItem[]
                {
                    new MenuItem("_About", "", () =>
                        MessageBox.Query("About fux", "A terminal XML editor over the XmlNotepad engine.", "OK")),
                }),
            });

            // Title is just the app name; the file path lives in the status bar (long paths
            // would overflow the top border on a narrow terminal).
            var win = new Window("fux")
            {
                X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1),
            };

            // The tree/value split occupies all but the bottom PaneH rows, which hold the
            // validation/error pane (a one-line summary header on a contrasting bar + the
            // scrollable error list). Dim.Fill(PaneH) leaves exactly that gap at the bottom.
            const int PaneH = 7; // header (1) + 6 visible error rows

            // No per-pane FrameViews: in v1 their titles corrupt under a closed menu and they
            // don't clip child content. Instead the tree, a vertical divider, and the value
            // view are direct children of the Window, which clips its children correctly.
            var tree = new TreeView<XmlNode>
            {
                X = 0, Y = 0, Width = Dim.Percent(33), Height = Dim.Fill(PaneH),
            };
            tree.TreeBuilder = new DelegateTreeBuilder<XmlNode>(n => GetChildren(n));
            tree.AspectGetter = n => GetLabel(n);

            var divider = new LineView(Orientation.Vertical)
            {
                X = Pos.Right(tree), Y = 0, Width = 1, Height = Dim.Fill(PaneH),
            };

            var valueView = new TextView
            {
                X = Pos.Right(divider), Y = 0, Width = Dim.Fill(), Height = Dim.Fill(PaneH),
                ReadOnly = true, WordWrap = true,
            };

            // Two-pane sync: the tree drives, the value pane reflects the selection.
            tree.SelectionChanged += (s, e) =>
            {
                var n = e.NewValue;
                valueView.Text = n == null ? "" : GetValue(n) ?? "";
            };

            // --- Bottom: validation/error pane ---
            // The header sits on the contrasting Bar color scheme, so it doubles as the visual
            // separator between the tree/value split above and the error list below.
            // AutoSize off so Width=Fill takes effect and the Bar background paints the full row.
            var errorHeader = new Label("")
            {
                X = 0, Y = Pos.Bottom(tree), Width = Dim.Fill(), Height = 1, AutoSize = false,
            };
            var errorList = new ListView
            {
                X = 0, Y = Pos.Bottom(errorHeader), Width = Dim.Fill(), Height = Dim.Fill(),
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
            errorHeader.Text = SummarizeValidation(errors, root != null);
            errorList.SetSource(BuildErrorLines(errors));

            // Enter on an error row jumps to the offending node in the tree. Errors carry the
            // source line/col; FindNodeAt binary-searches the DomLoader line table, then
            // MapToTree walks up to the nearest node the tree actually shows.
            errorList.OpenSelectedItem += (args) =>
            {
                int i = args.Item;
                if (i < 0 || i >= errors.Count) return;
                var item = errors[i];
                if (item.Line <= 0) return; // diagnostic isn't tied to a source position
                var node = MapToTree(_model.FindNodeAt(item.Line, item.Col));
                if (node == null) return;
                tree.SelectedObject = node;
                tree.EnsureVisible(node);
                tree.SetFocus();
                tree.SetNeedsDisplay();
            };

            win.Add(tree, divider, valueView, errorHeader, errorList);

            // F6 cycles focus tree -> value -> errors. Driven from a StatusBar hotkey so it
            // works from any pane (none of the three views consume F6 themselves).
            var focusRing = new View[] { tree, valueView, errorList };
            void CycleFocus()
            {
                int cur = Array.FindIndex(focusRing, v => v.HasFocus);
                focusRing[(cur + 1) % focusRing.Length].SetFocus();
            }

            var status = new StatusBar(new StatusItem[]
            {
                new StatusItem(Key.CtrlMask | Key.Q, "~^Q~ Quit", () => Application.RequestStop()),
                new StatusItem(Key.F6, "~F6~ Focus", CycleFocus),
                new StatusItem(Key.Null, file ?? "(no file)", null),
            });

            // Dark theme on the key views (the globals set in Theme.Apply cover the rest).
            menu.ColorScheme = Theme.Bar;
            // Terminal.Gui v1 can leave border artifacts under a just-closed menu overlay
            // (e.g. the "Value" pane title). Force a full repaint whenever menus close.
            menu.MenuAllClosed += () => { Application.Top.SetNeedsDisplay(); Application.Refresh(); };
            status.ColorScheme = Theme.Bar;
            win.ColorScheme = Theme.Content;
            tree.ColorScheme = Theme.Content;
            divider.ColorScheme = Theme.Content;
            valueView.ColorScheme = Theme.Content;
            errorHeader.ColorScheme = Theme.Bar;   // a sub-bar strip separating the panes
            errorList.ColorScheme = Theme.Content;

            top.Add(menu, win, status);
            tree.SetFocus();
            Application.Run();
            Application.Shutdown();
            return 0;
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

        private static string GetLabel(XmlNode n)
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
        private static string GetValue(XmlNode n)
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
