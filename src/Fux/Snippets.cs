using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace Fux
{
    /// <summary>One named block: the label the dialog shows, and the XML it inserts.</summary>
    internal sealed class Block
    {
        public Block(string name, XmlElement template, string section = null, string source = null)
        {
            Name = name;
            Template = template;
            Section = section;
            Source = source;
        }

        /// <summary>
        /// The file this block was read from. Only the panel's grouping uses it: two files can
        /// each have a section called "Blocks", and they are still two groups.
        /// </summary>
        public string Source { get; }

        /// <summary>The radio label, as written in the config.</summary>
        public string Name { get; }

        /// <summary>
        /// The <c>&lt;section&gt;</c> this block was written inside, or null when the config
        /// lists it at the top level. Only a grouping label for the dialog — it has no bearing
        /// on what gets inserted.
        /// </summary>
        public string Section { get; }

        /// <summary>
        /// The element this block inserts, still owned by the config's own document. It is
        /// copied into the target document on insert (XmlDocument.ImportNode), never moved, so
        /// the same block can be inserted any number of times.
        /// </summary>
        public XmlElement Template { get; }
    }

    /// <summary>
    /// One line of the snippet panel: either a section heading, or a block to insert.
    /// </summary>
    /// <remarks>
    /// Built here rather than in the dialog so the mapping — which rows are headings, where
    /// the indent goes, which block a row commits to — can be asserted headlessly. The dialog
    /// only turns these into a ListView.
    /// </remarks>
    internal sealed class SnippetRow
    {
        /// <summary>Exactly what the list shows, indent included.</summary>
        public string Text;

        /// <summary>Index into <see cref="SnippetSet.Blocks"/>, or -1 for a heading.</summary>
        public int BlockIndex;

        public bool IsHeading => BlockIndex < 0;
    }

    /// <summary>
    /// One snippet file fux found, and what reading it produced. For <c>fux --snippets</c>,
    /// which exists to answer "why is my library not in the panel?" without a debugger.
    /// </summary>
    internal sealed class SnippetFile
    {
        public SnippetFile(string path, int count, string reason, string shadowedBy = null)
        {
            Path = path;
            Count = count;
            Reason = reason;
            ShadowedBy = shadowedBy;
        }

        public string Path { get; }

        /// <summary>Blocks this file put in the panel.</summary>
        public int Count { get; }

        /// <summary>What went wrong reading it, without the file name, or null.</summary>
        public string Reason { get; }

        /// <summary>
        /// The library of the same name that loaded instead, or null. A file shadowed this
        /// way is not read at all — see <see cref="Snippets.Libraries"/>.
        /// </summary>
        public string ShadowedBy { get; }
    }

    /// <summary>
    /// What one read of the config produced. A config is allowed to be absent, and allowed to
    /// be partly wrong, and neither may stop fux opening a document — so the failures are data
    /// here rather than exceptions.
    /// </summary>
    internal sealed class SnippetSet
    {
        private static readonly string[] NoProblems = new string[0];

        public static readonly SnippetSet None = new SnippetSet(new List<Block>(), NoProblems, true, new List<SnippetFile>());

        public SnippetSet(IReadOnlyList<Block> blocks, IReadOnlyList<string> problems, bool missing,
                          IReadOnlyList<SnippetFile> files)
        {
            Blocks = blocks;
            Problems = problems ?? NoProblems;
            Missing = missing;
            Files = files;
        }

        /// <summary>
        /// The usable blocks: the user's own file first, in the order it lists them, then each
        /// library in file-name order.
        /// </summary>
        public IReadOnlyList<Block> Blocks { get; }

        /// <summary>
        /// One line per file that did not load cleanly, each naming its file, phrased for the
        /// panel to show under the list. Covers a file that failed to parse at all (it then
        /// contributes no blocks) and one whose individual snippets were skipped while the
        /// rest loaded.
        /// </summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary>Every problem on its own line, or null when everything loaded cleanly.</summary>
        public string Problem => Problems.Count == 0 ? null : string.Join("\n", Problems);

        /// <summary>
        /// No snippet file at all — neither <c>snippets.xml</c> nor any library. Distinct from
        /// "files that yielded nothing": the panel then says where a config goes, so someone
        /// who has never written one is told how rather than shown an empty list.
        /// </summary>
        public bool Missing { get; }

        /// <summary>Every file found, in the order its blocks reach the panel.</summary>
        public IReadOnlyList<SnippetFile> Files { get; }
    }

    /// <summary>
    /// Named blocks: the user's own, read from <c>snippets.xml</c> under the config directory,
    /// and drop-in libraries, read from every <c>snippets.d/*.xml</c> (#41).
    ///
    /// The file is XML because what it holds is XML: a block is written literally, with
    /// nothing to escape, and the file opens in fux itself. It is re-read on every Insert
    /// dialog open rather than cached at startup — the files are a few KB, and it means
    /// editing the config in fux and reopening ^B picks the change up, with no reload command
    /// to build and none to explain.
    ///
    /// A library is a file in the same format that a package installs beside fux rather than
    /// into the user's file, which it could only do by overwriting it. Homebrew cannot write
    /// under ~ at all (a formula's install is sandboxed), so a library also has to be found
    /// under the prefix fux is installed in — see <see cref="LibraryDirs"/>.
    ///
    /// Reading is separate from the UI so --drill can point at a fixture directory and assert
    /// on the result, the same split TerminalTitle and the clipboard use.
    /// </summary>
    internal static class Snippets
    {
        internal const string FileName = "snippets.xml";

        /// <summary>The directory a library is dropped into, in each place one is looked for.</summary>
        internal const string LibraryDirName = "snippets.d";

        /// <summary>
        /// Points fux at one directory for everything, and away from every other: see
        /// <see cref="ConfigDir"/> and <see cref="LibraryDirs"/>.
        /// </summary>
        internal const string ConfigDirVar = "FUX_CONFIG_DIR";

        /// <summary>
        /// Where the config lives. Resolved explicitly rather than through
        /// Environment.SpecialFolder, whose Unix mapping is not worth depending on:
        /// $FUX_CONFIG_DIR wins outright (which is what lets --drill and a bug report run
        /// against a fixture instead of the user's real file), then $XDG_CONFIG_HOME/fux,
        /// then ~/.config/fux, and %APPDATA%\fux on Windows.
        /// </summary>
        internal static string ConfigDir()
        {
            var over = Environment.GetEnvironmentVariable(ConfigDirVar);
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

        /// <summary>
        /// Every directory a library is read from, in order of precedence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The config dir's <c>snippets.d</c> first, so a copy the user keeps there overrides
        /// a package's library of the same name (see <see cref="Libraries"/>). Then the one
        /// under the prefix fux is installed in, which is where a Homebrew formula's library
        /// ends up: brew links a keg's <c>share/</c> into the prefix, and a formula cannot
        /// write anywhere under ~. Then <c>$HOMEBREW_PREFIX</c>'s, for a fux that did not come
        /// from brew — `make install`, a tarball, a build from source — next to a library that
        /// did.
        /// </para>
        /// <para>
        /// $FUX_CONFIG_DIR, when set, is the only place read. It exists to make a run
        /// reproducible — the drill, a bug report — and a library that happens to be installed
        /// on the machine would make the result depend on the machine.
        /// </para>
        /// </remarks>
        internal static List<string> LibraryDirs()
        {
            var dirs = new List<string>();
            var cfg = ConfigDir();
            if (cfg != null) dirs.Add(System.IO.Path.Combine(cfg, LibraryDirName));
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConfigDirVar))) return dirs;

            AddOnce(ShareDir(InstallPrefix(AppContext.BaseDirectory)));
            AddOnce(ShareDir(Environment.GetEnvironmentVariable("HOMEBREW_PREFIX")));
            return dirs;

            // A brew-installed fux finds the same directory both ways. Reading it twice would
            // be harmless — the second copy of each file is shadowed by the first — but it
            // would be listed twice by --snippets, which is there to be read.
            void AddOnce(string dir)
            {
                if (dir == null) return;
                var norm = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir));
                foreach (var d in dirs)
                    if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(d)),
                                      norm, StringComparison.Ordinal))
                        return;
                dirs.Add(dir);
            }
        }

        /// <summary><c>&lt;prefix&gt;/share/fux/snippets.d</c>, or null for no prefix.</summary>
        internal static string ShareDir(string prefix)
            => string.IsNullOrEmpty(prefix) ? null
             : System.IO.Path.Combine(prefix, "share", "fux", LibraryDirName);

        /// <summary>
        /// The prefix fux is installed in, given the directory its executable is in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Under Homebrew the binary is <c>&lt;prefix&gt;/Cellar/fux/&lt;version&gt;/bin/fux</c>,
        /// reached through a symlink in <c>&lt;prefix&gt;/bin</c>, so the prefix is whatever
        /// comes before <c>/Cellar/</c>. Anywhere else it is the executable's directory's
        /// parent — <c>/usr/local</c> for <c>/usr/local/bin/fux</c>.
        /// </para>
        /// <para>
        /// The Cellar rule is needed because the path fux sees is the RESOLVED one. Measured on
        /// macOS (#41): a single-file apphost run through <c>bin/fux -&gt; ../Cellar/…</c>, through
        /// PATH, through a symlink to that symlink, and through <c>/tmp</c> (itself a link to
        /// <c>/private/tmp</c>) reported the real file in every case, in both
        /// Environment.ProcessPath and AppContext.BaseDirectory. Without the rule the prefix
        /// would come out as the keg, <c>Cellar/fux/&lt;version&gt;</c>, which holds no library.
        /// </para>
        /// <para>
        /// AppContext.BaseDirectory rather than ProcessPath: they agree for the shipped binary,
        /// and under `dotnet run` or `dotnet fux.dll` ProcessPath names the dotnet host.
        /// </para>
        /// </remarks>
        internal static string InstallPrefix(string appDir)
        {
            if (string.IsNullOrEmpty(appDir)) return null;
            var dir = System.IO.Path.TrimEndingDirectorySeparator(appDir);
            var sep = System.IO.Path.DirectorySeparatorChar;
            // The last one: the Cellar a binary runs from is the innermost.
            int cellar = dir.LastIndexOf(sep + "Cellar" + sep, StringComparison.Ordinal);
            if (cellar > 0) return dir.Substring(0, cellar);
            return System.IO.Path.GetDirectoryName(dir);
        }

        /// <summary>
        /// The library files in <paramref name="dirs"/>, in the order their blocks reach the
        /// panel, each with the same-named file that shadows it if one does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// File-name order across every directory, not directory by directory: the name is
        /// the one thing about a library its author controls wherever it gets installed, so it
        /// is the only order an author can plan for. Case is ignored, with an ordinal
        /// tie-break, so the order is the same on every file system.
        /// </para>
        /// <para>
        /// A name already taken by an earlier directory is not read. That is what lets a user
        /// override a package's library — or switch it off, with an empty
        /// <c>&lt;snippets/&gt;</c> of the same name — without touching the package.
        /// </para>
        /// </remarks>
        internal static List<(string Path, string ShadowedBy)> Libraries(IReadOnlyList<string> dirs)
        {
            var found = new List<string>();
            foreach (var dir in dirs ?? new List<string>()) found.AddRange(XmlFilesIn(dir));

            var winner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in found)
                winner.TryAdd(System.IO.Path.GetFileName(path), path); // first directory wins

            var libraries = new List<(string, string)>();
            foreach (var path in InFileNameOrder(found))
            {
                var w = winner[System.IO.Path.GetFileName(path)];
                libraries.Add((path, w == path ? null : w));
            }
            return libraries;
        }

        /// <summary>
        /// Paths sorted by file name, case ignored, then ordinal. Stable, so files that share a
        /// name keep the precedence order they arrived in.
        /// </summary>
        internal static List<string> InFileNameOrder(IEnumerable<string> paths)
        {
            var sorted = new List<string>(paths);
            // Not List.Sort: it is unstable, and the precedence order has to survive it.
            return sorted
                .Select((p, i) => (p, i))
                .OrderBy(t => System.IO.Path.GetFileName(t.p), StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => System.IO.Path.GetFileName(t.p), StringComparer.Ordinal)
                .ThenBy(t => t.i)
                .Select(t => t.p)
                .ToList();
        }

        // A fresh EnumerationOptions, whose defaults skip hidden files — on Unix, the dotfiles.
        // That is what *.xml means as a shell glob, and it keeps an editor's lock file
        // (emacs's .#fread.xml) from being read as a library. The extension matches in any
        // case, so FREAD.XML loads on Linux as it would on Windows.
        private static IEnumerable<string> XmlFilesIn(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return new string[0];
            try
            {
                return System.IO.Directory.GetFiles(dir, "*.xml",
                    new System.IO.EnumerationOptions { MatchCasing = System.IO.MatchCasing.CaseInsensitive });
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        /// <summary>
        /// Read the user's config and every library: <c>snippets.xml</c> from wherever
        /// ConfigPath points, then <c>snippets.d/*.xml</c> from each of LibraryDirs.
        /// </summary>
        internal static SnippetSet Load() => LoadAll(ConfigPath(), LibraryDirs());

        /// <summary>
        /// What <c>fux --snippets</c> prints: where fux is running from, every place it looks,
        /// and what each file it found put in the panel. The question it answers is "why is
        /// my library not there?", and every answer to that is a line here — the prefix came
        /// out wrong, the directory is not where the package put it, the file does not parse,
        /// or another file of the same name came first.
        /// </summary>
        internal static string Report(string userFile, IReadOnlyList<string> dirs, SnippetSet set)
        {
            var sb = new System.Text.StringBuilder();
            bool pinned = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConfigDirVar));
            sb.Append("Executable:      ").Append(Environment.ProcessPath).Append('\n');
            sb.Append("Install prefix:  ")
              .Append(pinned ? $"not searched, because {ConfigDirVar} is set"
                             : InstallPrefix(AppContext.BaseDirectory) ?? "(none)")
              .Append('\n');

            sb.Append("\nSearched, in order:\n");
            sb.Append("  ").Append(userFile ?? "(no config directory)")
              .Append(userFile != null && System.IO.File.Exists(userFile) ? "" : "  (none)").Append('\n');
            foreach (var d in dirs)
                sb.Append("  ").Append(d).Append(System.IO.Directory.Exists(d) ? "" : "  (none)").Append('\n');

            sb.Append("\nLoaded, in the order the ^B panel lists them:\n");
            if (set.Files.Count == 0) sb.Append("  nothing: no snippet file was found\n");
            foreach (var f in set.Files)
            {
                sb.Append("  ").Append(f.Path).Append(": ");
                if (f.ShadowedBy != null)
                    sb.Append("not read; ").Append(f.ShadowedBy).Append(" has the same name and comes first");
                else
                {
                    sb.Append(f.Count == 1 ? "1 snippet" : $"{f.Count} snippets");
                    if (f.Reason != null) sb.Append("; ").Append(f.Reason);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// The user's file, then each library in file-name order. A file that fails costs
        /// only itself, the same rule a snippet inside one follows: the rest still load, and
        /// the failure is reported naming the file.
        /// </summary>
        internal static SnippetSet LoadAll(string userFile, IReadOnlyList<string> libraryDirs)
        {
            var blocks = new List<Block>();
            var problems = new List<string>();
            var files = new List<SnippetFile>();
            bool missing = true;

            Take(LoadFrom(userFile));
            foreach (var (path, shadowedBy) in Libraries(libraryDirs))
            {
                if (shadowedBy != null) { files.Add(new SnippetFile(path, 0, null, shadowedBy)); continue; }
                // Named snippets.d/<file> rather than by its full path, which in a panel row
                // ran out of width before it reached the file name, let alone the reason. The
                // name is enough to tell libraries apart — shadowing leaves one per name — and
                // `fux --snippets` gives the directory.
                Take(LoadFrom(path, System.IO.Path.Combine(LibraryDirName, System.IO.Path.GetFileName(path))));
            }
            return new SnippetSet(blocks, problems, missing, files);

            void Take(SnippetSet s)
            {
                if (s.Missing) return;
                missing = false;
                blocks.AddRange(s.Blocks);
                problems.AddRange(s.Problems);
                foreach (var f in s.Files) files.Add(f);
            }
        }

        /// <summary>
        /// Read one config file. Never throws: an absent file is SnippetSet.None, a file that
        /// will not parse comes back as a Problem with no blocks, and a snippet that will not
        /// load is skipped while the rest of the file still loads. A config is something a
        /// user hand-edits — half of one working is worth more than none of it working, and a
        /// typo in it must never be a reason fux cannot open a document.
        /// </summary>
        /// <param name="label">How a problem names the file: the user's own is just
        /// <c>snippets.xml</c>, as it always has been.</param>
        internal static SnippetSet LoadFrom(string path, string label = FileName)
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
                return Result(new List<Block>(), ex.Message);
            }

            var root = doc.DocumentElement;
            if (root == null || root.LocalName != "snippets")
                return Result(new List<Block>(), "expected a <snippets> root element");

            var blocks = new List<Block>();
            int skipped = 0;
            string firstReason = null;

            ReadInto(root, null);

            // Both counts matter to the reader: how much of the file was ignored, and one
            // concrete reason to start from. Listing every reason would outgrow the dialog.
            return Result(blocks, skipped == 0 ? null
                : skipped == 1 ? $"skipped 1 — {firstReason}"
                : $"skipped {skipped}, first — {firstReason}");

            // XmlException carries the line number in its message; the file name is added
            // here, once, for every kind of failure.
            SnippetSet Result(List<Block> got, string reason)
                => new SnippetSet(got,
                                  reason == null ? null : new[] { $"{label}: {reason}" },
                                  false,
                                  new[] { new SnippetFile(path, got.Count, reason) });

            // Walk one container's element children, attributing every snippet it finds to
            // `section`. Called on <snippets> with null, and on each <section> with its name.
            void ReadInto(XmlElement container, string section)
            {
                foreach (XmlNode child in container.ChildNodes)
                {
                    if (!(child is XmlElement e)) continue; // comments and stray text: ignored
                    if (e.LocalName == "section") { ReadSection(e, section); continue; }
                    if (e.LocalName != "snippet")
                    {
                        Skip($"<{e.Name}> is not a <snippet>");
                        continue;
                    }
                    ReadSnippet(e, section);
                }
            }

            // A <section> groups the snippets under it, and that is all it does.
            //
            // Both ways of getting it wrong keep the snippets. An unnamed section is a typo in
            // the wrapper, and a nested one is a shape fux does not have a second level to show
            // — neither is a reason to lose what is inside, so the contents are reported and
            // then read into whatever section encloses them. Same rule as a bad snippet costing
            // only itself: half a config working beats none of it working.
            void ReadSection(XmlElement e, string outer)
            {
                if (outer != null)
                {
                    Skip($"'{outer}' holds a nested <section>; its snippets join '{outer}'");
                    ReadInto(e, outer);
                    return;
                }
                var name = e.GetAttribute("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    Skip("a <section> has no name; its snippets are left ungrouped");
                    ReadInto(e, null);
                    return;
                }
                ReadInto(e, name.Trim());
            }

            void ReadSnippet(XmlElement snippet, string section)
            {
                var name = snippet.GetAttribute("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    Skip("a <snippet> has no name");
                    return;
                }

                XmlElement template = null;
                bool twoChildren = false;
                foreach (XmlNode c in snippet.ChildNodes)
                {
                    if (!(c is XmlElement e)) continue;
                    if (template != null) { twoChildren = true; break; }
                    template = e;
                }

                if (twoChildren) { Skip($"'{name}' holds more than one element"); return; }
                if (template == null) { Skip($"'{name}' holds no element"); return; }

                blocks.Add(new Block(name.Trim(), template, section, path));
            }

            void Skip(string reason)
            {
                skipped++;
                if (firstReason == null) firstReason = reason;
            }
        }

        /// <summary>
        /// The indent a sectioned snippet carries in the panel. Two spaces, in the row text
        /// itself: ListView has no per-row offset, and RowRender can set a row's colour but
        /// not its content.
        /// </summary>
        internal const string RowIndent = "  ";

        /// <summary>
        /// Turn the blocks into the lines the panel shows: a heading wherever the section
        /// changes, and every sectioned snippet indented under it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// File order throughout, as before — sections group, they do not sort, so ordering the
        /// file still orders the panel.
        /// </para>
        /// <para>
        /// A config with no sections in it produces exactly the rows it always did, unindented
        /// and heading-free. That is the point: the feature has to be invisible to someone who
        /// has not asked for it, the same way an absent config draws no group at all.
        /// </para>
        /// <para>
        /// A section contributing no usable snippets gets no heading. A heading over nothing is
        /// noise, and after a snippet is skipped for being malformed that is exactly what would
        /// otherwise be left behind.
        /// </para>
        /// <para>
        /// A new file starts a new group even when its first section has the name the last
        /// file's ended on. Two libraries may well both say "Blocks"; without the heading, the
        /// second one's snippets would read as more of the first one's.
        /// </para>
        /// </remarks>
        internal static List<SnippetRow> Rows(IReadOnlyList<Block> blocks)
        {
            var rows = new List<SnippetRow>();
            if (blocks == null) return rows;

            // No "does this config use sections?" test: a block outside one has no section, so
            // it takes no indent, and a config with none of them lands on the old rows exactly.
            string current = null, currentSource = null;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                if (b.Section != null && (b.Section != current || b.Source != currentSource))
                    rows.Add(new SnippetRow { Text = b.Section, BlockIndex = -1 });
                current = b.Section;
                currentSource = b.Source;
                rows.Add(new SnippetRow
                {
                    Text = (b.Section != null ? RowIndent : "") + b.Name,
                    BlockIndex = i,
                });
            }
            return rows;
        }

    }
}
