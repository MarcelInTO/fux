using System;
using System.Collections.Generic;
using System.Xml;

namespace Fux
{
    /// <summary>One named block: the label the dialog shows, and the XML it inserts.</summary>
    internal sealed class Block
    {
        public Block(string name, XmlElement template)
        {
            Name = name;
            Template = template;
        }

        /// <summary>The radio label, as written in the config.</summary>
        public string Name { get; }

        /// <summary>
        /// The element this block inserts, still owned by the config's own document. It is
        /// copied into the target document on insert (XmlDocument.ImportNode), never moved, so
        /// the same block can be inserted any number of times.
        /// </summary>
        public XmlElement Template { get; }
    }

    /// <summary>
    /// What one read of the config produced. A config is allowed to be absent, and allowed to
    /// be partly wrong, and neither may stop fux opening a document — so the failures are data
    /// here rather than exceptions.
    /// </summary>
    internal sealed class SnippetSet
    {
        public static readonly SnippetSet None = new SnippetSet(new List<Block>(), null, true);

        public SnippetSet(IReadOnlyList<Block> blocks, string problem, bool missing)
        {
            Blocks = blocks;
            Problem = problem;
            Missing = missing;
        }

        /// <summary>The usable blocks, in the order the file lists them.</summary>
        public IReadOnlyList<Block> Blocks { get; }

        /// <summary>
        /// What went wrong, phrased for the dialog to show under the group — or null when the
        /// file loaded cleanly. Set both when the whole file failed to parse (and Blocks is
        /// then empty) and when individual snippets were skipped and the rest loaded.
        /// </summary>
        public string Problem { get; }

        /// <summary>
        /// No config file at all. Distinct from "a config that yielded nothing": the dialog
        /// draws no block group whatsoever in this case, so someone who has never written a
        /// config sees exactly the dialog fux has always had.
        /// </summary>
        public bool Missing { get; }
    }

    /// <summary>
    /// User-defined named blocks, read from <c>snippets.xml</c> under the config directory.
    ///
    /// The file is XML because what it holds is XML: a block is written literally, with
    /// nothing to escape, and the file opens in fux itself. It is re-read on every Insert
    /// dialog open rather than cached at startup — the files are a few KB, and it means
    /// editing the config in fux and reopening ^N picks the change up, with no reload command
    /// to build and none to explain.
    ///
    /// Reading is separate from the UI so --drill can point at a fixture directory and assert
    /// on the result, the same split TerminalTitle and the clipboard use.
    /// </summary>
    internal static class Snippets
    {
        internal const string FileName = "snippets.xml";

        /// <summary>
        /// Where the config lives. Resolved explicitly rather than through
        /// Environment.SpecialFolder, whose Unix mapping is not worth depending on:
        /// $FUX_CONFIG_DIR wins outright (which is what lets --drill and a bug report run
        /// against a fixture instead of the user's real file), then $XDG_CONFIG_HOME/fux,
        /// then ~/.config/fux, and %APPDATA%\fux on Windows.
        /// </summary>
        internal static string ConfigDir()
        {
            var over = Environment.GetEnvironmentVariable("FUX_CONFIG_DIR");
            if (!string.IsNullOrEmpty(over)) return over;

            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var appData = Environment.GetEnvironmentVariable("APPDATA");
                if (!string.IsNullOrEmpty(appData))
                    return System.IO.Path.Combine(appData, "fux");
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdg)) return System.IO.Path.Combine(xdg, "fux");

            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
            return string.IsNullOrEmpty(home)
                ? null
                : System.IO.Path.Combine(home, ".config", "fux");
        }

        internal static string ConfigPath()
        {
            var dir = ConfigDir();
            return dir == null ? null : System.IO.Path.Combine(dir, FileName);
        }

        /// <summary>Read the config from wherever ConfigPath points.</summary>
        internal static SnippetSet Load() => LoadFrom(ConfigPath());

        /// <summary>
        /// Read one config file. Never throws: an absent file is SnippetSet.None, a file that
        /// will not parse comes back as a Problem with no blocks, and a snippet that will not
        /// load is skipped while the rest of the file still loads. A config is something a
        /// user hand-edits — half of one working is worth more than none of it working, and a
        /// typo in it must never be a reason fux cannot open a document.
        /// </summary>
        internal static SnippetSet LoadFrom(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return SnippetSet.None;

            var doc = new XmlDocument();
            try
            {
                // Whitespace is layout in this file, not content: the indentation around a
                // block belongs to the config, and carrying it into the document would insert
                // the config's formatting rather than the block. The insert re-indents to the
                // target depth from scratch.
                doc.PreserveWhitespace = false;
                doc.Load(path);
            }
            catch (Exception ex) when (ex is XmlException || ex is System.IO.IOException
                                       || ex is UnauthorizedAccessException)
            {
                return new SnippetSet(new List<Block>(), Describe(ex), false);
            }

            var root = doc.DocumentElement;
            if (root == null || root.LocalName != "snippets")
                return new SnippetSet(new List<Block>(), $"{FileName}: expected a <snippets> root element", false);

            var blocks = new List<Block>();
            int skipped = 0;
            string firstReason = null;

            foreach (XmlNode child in root.ChildNodes)
            {
                if (!(child is XmlElement snippet)) continue; // comments and stray text: ignored
                if (snippet.LocalName != "snippet")
                {
                    Skip($"<{snippet.Name}> is not a <snippet>");
                    continue;
                }

                var name = snippet.GetAttribute("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    Skip("a <snippet> has no name");
                    continue;
                }

                XmlElement template = null;
                bool twoChildren = false;
                foreach (XmlNode c in snippet.ChildNodes)
                {
                    if (!(c is XmlElement e)) continue;
                    if (template != null) { twoChildren = true; break; }
                    template = e;
                }

                if (twoChildren) { Skip($"'{name}' holds more than one element"); continue; }
                if (template == null) { Skip($"'{name}' holds no element"); continue; }

                blocks.Add(new Block(name.Trim(), template));
            }

            // Both counts matter to the reader: how much of the file was ignored, and one
            // concrete reason to start from. Listing every reason would outgrow the dialog.
            string problem = skipped == 0 ? null
                : skipped == 1 ? $"{FileName}: skipped 1 — {firstReason}"
                : $"{FileName}: skipped {skipped}, first — {firstReason}";
            return new SnippetSet(blocks, problem, false);

            void Skip(string reason)
            {
                skipped++;
                if (firstReason == null) firstReason = reason;
            }
        }

        // XmlException already carries the line number; the others do not, so name the file.
        private static string Describe(Exception ex)
            => ex is XmlException xe ? $"{FileName}: {xe.Message}" : $"{FileName}: {ex.Message}";
    }
}
