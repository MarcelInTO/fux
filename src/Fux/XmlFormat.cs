using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using XmlNotepad;

namespace Fux
{
    /// <summary>
    /// The textual conventions of one document as it was found on disk: byte order mark,
    /// line ending, indentation, trailing newline, and the exact bytes of its XML declaration.
    ///
    /// None of this survives the trip through <see cref="System.Xml.XmlDocument"/> — the DOM
    /// keeps the tree, not the text that expressed it. XmlNotepad papers over that by writing
    /// out whatever the global Settings say (CRLF, a BOM, two spaces, a regenerated
    /// declaration), which rewrites every line of a file it only meant to touch in one place.
    /// fux sniffs the original instead and hands this record to <see cref="XmlFormatWriter"/>,
    /// so a save reproduces the document the user actually opened.
    /// </summary>
    internal sealed class XmlFormat
    {
        /// <summary>Did the file start with a byte order mark? Re-emitted only if it did.</summary>
        public bool HasByteOrderMark;

        /// <summary>Encoding the bytes are written back in (declaration first, then BOM, else UTF-8).</summary>
        public Encoding Encoding = new UTF8Encoding(false);

        /// <summary>"\n" or "\r\n" — whichever the file predominantly used.</summary>
        public string NewLine = "\n";

        /// <summary>One indent level, e.g. "  " or "\t". Only ever applied to newly written nodes.</summary>
        public string IndentChars = "  ";

        /// <summary>Did the last line end with a newline? Most tools care; the DOM has no idea.</summary>
        public bool TrailingNewline = true;

        /// <summary>
        /// Does this file write self-closing tags as <c>&lt;b /&gt;</c> rather than <c>&lt;b/&gt;</c>?
        /// The DOM records only that an element was self-closed (<c>IsEmpty</c>), never which
        /// spelling was used, so this has to be read off the text. It matters more than it
        /// looks: guessing wrong puts a diff on every self-closing element in the document.
        /// XmlNotepad itself emits the spaced form, so files it has touched — including several
        /// in this repo — use it.
        /// </summary>
        public bool SpaceBeforeEmptyElementSlash;

        /// <summary>
        /// The declaration exactly as written, e.g. <c>&lt;?xml version="1.0" encoding="UTF-8"?&gt;</c>,
        /// or null if the document had none. Kept verbatim because a regenerated declaration
        /// differs in ways that are invisible to XML and loud in a diff: XmlWriter lower-cases
        /// the encoding name and adds an encoding attribute to declarations that omitted one.
        /// </summary>
        public string Declaration;

        /// <summary>Conventions for a document with no file behind it. LF and no BOM: fux is a
        /// terminal tool, and its output most often lands next to a git repo.</summary>
        public static XmlFormat Default => new XmlFormat();

        /// <summary>
        /// Read a file's conventions. Never throws: an unreadable or unsniffable file falls back
        /// to <see cref="Default"/>, because failing to characterize a document must not be able
        /// to stop it being opened.
        /// </summary>
        public static XmlFormat Sniff(string path)
        {
            try { return SniffBytes(File.ReadAllBytes(path)); }
            catch (Exception) { return Default; }
        }

        public static XmlFormat SniffBytes(byte[] bytes)
        {
            var f = new XmlFormat();
            if (bytes == null || bytes.Length == 0) return f;

            try
            {
                Encoding bom = SniffBom(bytes, out int bomLength);
                f.HasByteOrderMark = bom != null;

                // Decode with the BOM's encoding if there was one; otherwise UTF-8, which also
                // reads the ASCII of an XML declaration in any of the 8-bit encodings, and the
                // declaration is what tells us the real one.
                Encoding decode = bom ?? new UTF8Encoding(false);
                string text = decode.GetString(bytes, bomLength, bytes.Length - bomLength);

                f.Declaration = ReadDeclaration(text);
                f.Encoding = PickEncoding(f.Declaration, bom);
                f.NewLine = DominantNewLine(text);
                f.TrailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
                f.IndentChars = DominantIndent(text) ?? f.IndentChars;
                f.SpaceBeforeEmptyElementSlash = PrefersSpacedEmptyTag(text);
            }
            catch (Exception)
            {
                return Default; // a partial sniff is worse than a clean default
            }
            return f;
        }

        // --------------------------------------------------------------------

        /// <summary>Recognize a BOM and report how many bytes it occupies.</summary>
        private static Encoding SniffBom(byte[] b, out int length)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                length = 3;
                return new UTF8Encoding(true);
            }
            // UTF-32 must be tested before UTF-16: a little-endian UTF-32 BOM starts with the
            // same two bytes as a little-endian UTF-16 one.
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00)
            {
                length = 4;
                return new UTF32Encoding(false, true);
            }
            if (b.Length >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF)
            {
                length = 4;
                return new UTF32Encoding(true, true);
            }
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            {
                length = 2;
                return new UnicodeEncoding(false, true);
            }
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            {
                length = 2;
                return new UnicodeEncoding(true, true);
            }
            length = 0;
            return null;
        }

        /// <summary>
        /// The verbatim declaration, if the document opens with one. Deliberately textual: the
        /// point is to reproduce the original spelling, so nothing here parses or normalizes it.
        /// </summary>
        private static string ReadDeclaration(string text)
        {
            if (!text.StartsWith("<?xml", StringComparison.Ordinal)) return null;
            // "<?xml-stylesheet" is a processing instruction, not a declaration.
            if (text.Length > 5 && text[5] != ' ' && text[5] != '\t' && text[5] != '\r' && text[5] != '\n')
                return null;
            int end = text.IndexOf("?>", StringComparison.Ordinal);
            return end < 0 ? null : text.Substring(0, end + 2);
        }

        /// <summary>
        /// What to encode the saved bytes as. The declaration wins — it is a promise the file
        /// makes to every other reader — then the BOM, then UTF-8.
        /// </summary>
        private static Encoding PickEncoding(string declaration, Encoding bom)
        {
            string name = declaration == null ? null : AttributeValue(declaration, "encoding");
            if (!string.IsNullOrEmpty(name))
            {
                try
                {
                    // Ask for the named encoding but keep our own no-BOM preamble decision:
                    // whether a BOM is emitted is HasByteOrderMark's call, not the encoding's.
                    Encoding named = Encoding.GetEncoding(name);
                    if (named is UTF8Encoding) return new UTF8Encoding(false);
                    return named;
                }
                catch (Exception) { /* unknown encoding name: fall through */ }
            }
            if (bom != null) return bom;
            return new UTF8Encoding(false);
        }

        /// <summary>Pull <c>name="value"</c> (or single-quoted) out of a declaration.</summary>
        internal static string AttributeValue(string declaration, string name)
        {
            int i = declaration.IndexOf(name + "=", StringComparison.Ordinal);
            if (i < 0) return null;
            int q = i + name.Length + 1;
            if (q >= declaration.Length) return null;
            char quote = declaration[q];
            if (quote != '"' && quote != '\'') return null;
            int end = declaration.IndexOf(quote, q + 1);
            return end < 0 ? null : declaration.Substring(q + 1, end - q - 1);
        }

        /// <summary>
        /// Whether self-closing tags are written <c>&lt;b /&gt;</c>. Decided by majority so one
        /// odd tag in a large file cannot flip the convention for the rest of it. A document
        /// with no self-closing element at all reports false, the tighter spelling.
        /// </summary>
        private static bool PrefersSpacedEmptyTag(string text)
        {
            int spaced = 0, tight = 0;
            for (int i = text.IndexOf("/>", StringComparison.Ordinal); i >= 0;
                     i = text.IndexOf("/>", i + 2, StringComparison.Ordinal))
            {
                if (i == 0) continue;
                char prev = text[i - 1];
                if (prev == ' ' || prev == '\t') spaced++;
                else tight++;
            }
            return spaced > tight;
        }

        /// <summary>
        /// The byte order mark to re-emit, or an empty array. Derived from the chosen encoding
        /// rather than replayed from the source, so that editing the declaration's encoding
        /// produces a matching mark. Note the encodings handed out by <see cref="PickEncoding"/>
        /// are deliberately preamble-free — whether a mark is written is this record's decision,
        /// not the encoding's — so UTF-8's has to be spelled out here.
        /// </summary>
        public byte[] Preamble()
        {
            if (!HasByteOrderMark) return new byte[0];
            byte[] p = Encoding.GetPreamble();
            if (p.Length > 0) return p;
            if (Encoding is UTF8Encoding) return new byte[] { 0xEF, 0xBB, 0xBF };
            return p;
        }

        /// <summary>CRLF only if it outnumbers bare LF; a stray \r\n shouldn't convert a whole file.</summary>
        private static string DominantNewLine(string text)
        {
            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
            }
            if (crlf == 0 && lf == 0) return "\n";
            return crlf > lf ? "\r\n" : "\n";
        }

        /// <summary>
        /// One indent level, inferred from the shallowest indented line. Only lines whose
        /// indentation is followed by '&lt;' count, so a wrapped attribute list — which is
        /// indented to an arbitrary column — cannot be mistaken for an indent step.
        /// Returns null when nothing in the file is indented.
        /// </summary>
        private static string DominantIndent(string text)
        {
            int bestSpaces = int.MaxValue;
            bool sawTab = false;

            foreach (string line in text.Split('\n'))
            {
                int i = 0;
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i == 0 || i >= line.Length || line[i] != '<') continue;

                string lead = line.Substring(0, i);
                if (lead.IndexOf('\t') >= 0) { sawTab = true; continue; }
                if (i < bestSpaces) bestSpaces = i;
            }

            if (bestSpaces != int.MaxValue) return new string(' ', bestSpaces);
            if (sawTab) return "\t";
            return null;
        }
    }
}
