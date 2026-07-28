using System;
using System.IO;
using System.Text;
using System.Xml;
using Microsoft.Xml; // XmlCsvReader — Model keeps it under its own namespace, not XmlNotepad
using Newtonsoft.Json;
using XmlNotepad;

namespace Fux
{
    /// <summary>
    /// Coercing non-XML documents into a DOM, following XmlNotepad's FormMain: the file
    /// extension picks a reader (Model's <c>FileEntity.SetMimeType</c> mapping), and everything
    /// else is a plain XML load. Upstream keeps this in its WinForms application layer rather
    /// than in Model, so fux has to keep its own copy of the wiring — but the readers
    /// themselves (SgmlReader, <see cref="XmlCsvReader"/>) are reused as they are.
    /// </summary>
    internal static class Import
    {
        /// <summary>Does this extension need coercing rather than parsing as XML?</summary>
        public static bool IsImport(string ext)
            => ext == ".htm" || ext == ".html" || ext == ".json" || ext == ".csv";

        /// <summary>
        /// Where an imported document should be saved. Everything here becomes XML on the way
        /// out, so the model is pointed at a sibling .xml file: leaving it aimed at the source
        /// would make ^S silently overwrite the user's .csv or .html with XML. Upstream's CSV
        /// import already does this; fux applies it to every import for the same reason.
        /// </summary>
        public static string XmlPathFor(string file)
            => Path.Combine(Path.GetDirectoryName(file) ?? "",
                            Path.GetFileNameWithoutExtension(file) + ".xml");

        /// <summary>
        /// Read <paramref name="file"/> into <paramref name="model"/>. Returns true if the file
        /// was imported (and so carries no layout of its own and needs pretty-printing on save),
        /// false if it was loaded as plain XML.
        /// </summary>
        public static bool Load(XmlCache model, string file)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            switch (ext)
            {
                case ".htm":
                case ".html":
                {
                    // FormMain.ImportHtml's settings exactly. Significant whitespace means the
                    // source's own line breaks arrive as nodes, so an HTML import keeps its
                    // layout on save and does NOT want to be re-indented.
                    using var text = new StreamReader(file); // BOM-sniffing, UTF-8 default
                    using var reader = new Sgml.SgmlReader
                    {
                        DocType = "HTML",
                        CaseFolding = Sgml.CaseFolding.ToLower,
                        InputStream = text,
                        WhitespaceHandling = WhitespaceHandling.Significant,
                    };
                    model.Load(reader, XmlPathFor(file));
                    return false; // layout came from the source
                }

                case ".json":
                {
                    // FormMain.ImportJson: a JSON document has no single root the way XML
                    // requires, so one named "root" is supplied.
                    var doc = JsonConvert.DeserializeXmlNode(File.ReadAllText(file), "root");
                    if (doc == null)
                        throw new XmlException("the file contains no JSON value to import");
                    using var reader = new XmlNodeReader(doc);
                    model.Load(reader, XmlPathFor(file));
                    return true;
                }

                case ".csv":
                {
                    // Upstream puts a dialog here to choose the delimiter and whether row one is
                    // a header. Blocking `fux data.csv` on a modal before anything is on screen
                    // would be poor for a terminal tool, so both are inferred from the text.
                    using var text = new StreamReader(file);
                    using var csv = new XmlCsvReader(text, new Uri(file), new NameTable())
                    {
                        Delimiter = SniffDelimiter(file),
                        FirstRowHasColumnNames = FirstRowLooksLikeHeader(file),
                    };
                    model.Load(csv, XmlPathFor(file));
                    return true;
                }

                default:
                    model.Load(file);
                    return false;
            }
        }

        // --------------------------------------------------------------------

        /// <summary>
        /// The delimiter this file uses. Chosen by which candidate appears most consistently
        /// across the first few lines rather than most often in total: a comma inside quoted
        /// prose can easily outnumber the real separator, but it will not appear the same
        /// number of times on every line.
        /// </summary>
        internal static char SniffDelimiter(string file)
        {
            string[] lines = FirstLines(file, 5);
            if (lines.Length == 0) return ',';

            char best = ',';
            int bestScore = 0;
            foreach (char candidate in new[] { ',', '\t', ';', '|' })
            {
                int first = CountOutsideQuotes(lines[0], candidate);
                if (first == 0) continue;

                int consistent = 0;
                foreach (string line in lines)
                    if (CountOutsideQuotes(line, candidate) == first) consistent++;

                // Weight by how many fields it produces, so a delimiter that splits every line
                // into 5 columns beats one that splits every line into 2.
                int score = consistent * 100 + first;
                if (score > bestScore) { bestScore = score; best = candidate; }
            }
            return best;
        }

        /// <summary>
        /// Whether row one names the columns. The heuristic is that a header row is text where
        /// the data rows are not: if any first-row cell is numeric, or the row has duplicate or
        /// empty cells, it is data. A single-row file is treated as data — reading its only row
        /// as column names would produce a document with no content at all.
        /// </summary>
        internal static bool FirstRowLooksLikeHeader(string file)
        {
            string[] lines = FirstLines(file, 2);
            if (lines.Length < 2) return false;

            char d = SniffDelimiter(file);
            string[] cells = SplitOutsideQuotes(lines[0], d);
            if (cells.Length == 0) return false;

            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in cells)
            {
                string cell = raw.Trim().Trim('"');
                if (cell.Length == 0) return false;
                if (double.TryParse(cell, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
                if (!seen.Add(cell)) return false; // duplicate names could not be element names
            }
            return true;
        }

        private static string[] FirstLines(string file, int count)
        {
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                using var r = new StreamReader(file);
                for (int i = 0; i < count; i++)
                {
                    string line = r.ReadLine();
                    if (line == null) break;
                    if (line.Length > 0) lines.Add(line);
                }
            }
            catch (Exception) { /* an unreadable file fails properly in the reader, not here */ }
            return lines.ToArray();
        }

        private static int CountOutsideQuotes(string line, char c)
        {
            int n = 0;
            bool quoted = false;
            foreach (char ch in line)
            {
                if (ch == '"') quoted = !quoted;
                else if (ch == c && !quoted) n++;
            }
            return n;
        }

        private static string[] SplitOutsideQuotes(string line, char d)
        {
            var cells = new System.Collections.Generic.List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char ch in line)
            {
                if (ch == '"') { quoted = !quoted; sb.Append(ch); }
                else if (ch == d && !quoted) { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            cells.Add(sb.ToString());
            return cells.ToArray();
        }
    }
}
