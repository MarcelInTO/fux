using System;
using System.Xml;
using System.Runtime.InteropServices;
using XmlNotepad;

namespace Fux.Smoke
{
    // Minimal service container: XmlCache/SchemaCache only ask the site for the Settings service.
    internal sealed class SmokeSite : IServiceProvider
    {
        private readonly Settings _settings;
        public SmokeSite(Settings settings) { _settings = settings; }
        public object GetService(Type serviceType)
            => serviceType == typeof(Settings) ? _settings : null;
    }

    // Sink for validation errors/warnings from the Checker.
    internal sealed class ConsoleErrorHandler : ErrorHandler
    {
        public int Errors;
        public int Warnings;
        public override void HandleError(Severity sev, string reason, string filename, int line, int col, object data)
        {
            if (sev == Severity.Error) Errors++;
            else if (sev == Severity.Warning) Warnings++;
            Console.WriteLine($"    [{sev}] {System.IO.Path.GetFileName(filename)}:{line},{col}  {reason}");
        }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            var file = args.Length > 0 ? args[0] : "sandbox/testdata/emp.xml";
            // Resolve relative -> absolute: the engine resolves a relative path against the
            // parent of the working directory, so hand it a full path (same fix as Fux).
            file = System.IO.Path.GetFullPath(file);

            Console.WriteLine("fux — headless engine smoke test");
            Console.WriteLine($"  runtime : .NET {Environment.Version}  ({RuntimeInformation.OSDescription.Trim()}, {RuntimeInformation.OSArchitecture})");
            Console.WriteLine($"  target  : {file}");
            Console.WriteLine();

            // --- Wire up the reused XmlNotepad engine, headless ---
            var settings = new Settings();      // ctor registers itself as Settings.Instance
            settings.SetDefaults();             // indexer throws on missing keys, so this is required
            settings.StartupPath = AppContext.BaseDirectory;
            settings.Resolver = new XmlUrlResolver();  // resolve schemaLocation .xsd files from disk

            var site = new SmokeSite(settings);
            var schemaCache = new SchemaCache(site);
            // Headless dispatcher: run debounced actions inline instead of marshaling to a UI thread.
            var model = new XmlCache(site, schemaCache, new DelayedActions(a => a()));

            // --- Load ---
            try
            {
                model.Load(file);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LOAD FAILED: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }

            var doc = model.Document;
            Console.WriteLine($"loaded OK — root <{doc.DocumentElement?.Name}>, {CountNodes(doc)} DOM nodes, {schemaCache.GetSchemas().Count} schema(s) associated");
            Console.WriteLine();
            Console.WriteLine("tree (depth <= 2):");
            DumpTree(doc.DocumentElement, 1, 2);
            Console.WriteLine();

            // --- Validate (drives XmlSchemaValidator over the DOM via the Checker) ---
            var handler = new ConsoleErrorHandler();
            try
            {
                model.ValidateModel(handler);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"VALIDATION FAILED: {ex.GetType().Name}: {ex.Message}");
                return 3;
            }

            Console.WriteLine($"validation complete: {handler.Errors} error(s), {handler.Warnings} warning(s)");
            return handler.Errors == 0 ? 0 : 1;
        }

        private static int CountNodes(XmlNode n)
        {
            var count = 1;
            foreach (XmlNode child in n.ChildNodes) count += CountNodes(child);
            return count;
        }

        private static void DumpTree(XmlNode node, int depth, int maxDepth)
        {
            if (node == null || depth > maxDepth) return;
            var indent = new string(' ', depth * 2);
            if (node.NodeType == XmlNodeType.Element)
            {
                var attrs = node.Attributes != null && node.Attributes.Count > 0 ? $" ({node.Attributes.Count} attr)" : "";
                Console.WriteLine($"{indent}<{node.Name}>{attrs}");
                foreach (XmlNode child in node.ChildNodes) DumpTree(child, depth + 1, maxDepth);
            }
            else if (node.NodeType == XmlNodeType.Text || node.NodeType == XmlNodeType.Comment)
            {
                var v = (node.Value ?? "").Replace("\n", " ").Trim();
                if (v.Length > 50) v = v.Substring(0, 50) + "…";
                if (v.Length > 0) Console.WriteLine($"{indent}{node.NodeType}: {v}");
            }
        }
    }
}
