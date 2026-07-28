using System;
using System.IO;
using System.Text;
using System.Xml;

namespace Fux
{
    /// <summary>
    /// Serializes a document back to text under the conventions it was loaded with
    /// (see <see cref="XmlFormat"/>), so that saving a file fux did not edit reproduces the
    /// original bytes, and saving one it did edit shows only the edit in a diff.
    ///
    /// Why not <see cref="XmlWriter"/>: it cannot express the fidelity this needs. It always
    /// writes an empty element as <c>&lt;b /&gt;</c> — the space is hardcoded in the raw text
    /// writers and no setting reaches it — it regenerates the XML declaration from its own
    /// encoding instead of emitting the document's, and NewLineHandling rewrites line endings
    /// inside text nodes. Writing the text directly also makes the namespace bookkeeping in
    /// <c>XmlCache.WriteElementTo</c> unnecessary: that scope-tracking exists purely because
    /// XmlWriter.Create rejects an xmlns attribute that redefines its parent's namespace.
    /// Emitting prefixes and xmlns attributes verbatim is both simpler and more faithful.
    ///
    /// Two known limits, both the same root cause — the DOM keeps the tree, not the text that
    /// expressed it — and both needing per-node source spans to fix, which is the separate
    /// lossless-DOM problem:
    ///   * whitespace *inside* a start tag is not a node, so an attribute list wrapped over
    ///     several lines is rejoined onto one (measured: 3 of 31 files in the corpus);
    ///   * how an entity was spelled is not recorded, so <c>&amp;#65;</c> or <c>&amp;apos;</c>
    ///     comes back as its resolved character.
    /// Everything else in that corpus round-trips byte-identically.
    /// </summary>
    internal sealed class XmlFormatWriter
    {
        private readonly XmlFormat _format;
        private readonly TextWriter _out;

        /// <summary>
        /// When false the document's own whitespace nodes carry the layout and nothing is
        /// synthesized — this is the byte-preserving mode, used when PreserveWhitespace is on.
        /// When true the writer pretty-prints, because a DOM loaded without whitespace
        /// preservation has no layout of its own and would otherwise come out on one line.
        /// </summary>
        private readonly bool _indent;

        private XmlFormatWriter(TextWriter output, XmlFormat format, bool indent)
        {
            _out = output;
            _format = format;
            _indent = indent;
        }

        /// <summary>Serialize <paramref name="doc"/> to the bytes that should land on disk.</summary>
        public static byte[] WriteToBytes(XmlDocument doc, XmlFormat format, bool indent)
        {
            var sw = new StringWriter { NewLine = format.NewLine };
            var w = new XmlFormatWriter(sw, format, indent);
            w.WriteDocument(doc);
            string text = sw.ToString();

            if (format.TrailingNewline && !text.EndsWith("\n", StringComparison.Ordinal))
                text += format.NewLine;

            byte[] body = format.Encoding.GetBytes(text);
            byte[] preamble = format.Preamble();
            if (preamble.Length == 0) return body;

            var all = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, all, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, all, preamble.Length, body.Length);
            return all;
        }

        // --------------------------------------------------------------------

        private void WriteDocument(XmlDocument doc)
        {
            bool first = true;
            for (XmlNode n = doc.FirstChild; n != null; n = n.NextSibling)
            {
                // In pretty-print mode the top level gets one node per line. In preserve mode
                // the document's own whitespace nodes already say where the breaks go.
                if (_indent && !first && !IsWhitespace(n)) _out.Write(_format.NewLine);
                WriteNode(n, 0);
                first = false;
            }
        }

        private void WriteNode(XmlNode n, int depth)
        {
            switch (n.NodeType)
            {
                case XmlNodeType.XmlDeclaration:
                    _out.Write(DeclarationText((XmlDeclaration)n));
                    break;

                case XmlNodeType.DocumentType:
                    WriteDocumentType((XmlDocumentType)n);
                    break;

                case XmlNodeType.Element:
                    WriteElement((XmlElement)n, depth);
                    break;

                case XmlNodeType.Text:
                    _out.Write(Nl(EscapeText(n.Value)));
                    break;

                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    _out.Write(Nl(n.Value)); // layout, verbatim — the whole point of preserve mode
                    break;

                case XmlNodeType.CDATA:
                    _out.Write("<![CDATA[" + Nl(n.Value) + "]]>");
                    break;

                case XmlNodeType.Comment:
                    _out.Write("<!--" + Nl(n.Value) + "-->");
                    break;

                case XmlNodeType.ProcessingInstruction:
                    // Always a space after the target, even when the data is empty. The DOM keeps
                    // only the data, so `<?pi ?>` and `<?pi?>` are indistinguishable by the time
                    // we get here; the spaced form is what .NET's own writer emits and what the
                    // files in the wild use.
                    _out.Write("<?" + n.Name + " " + Nl(n.Value) + "?>");
                    break;

                case XmlNodeType.EntityReference:
                    _out.Write("&" + n.Name + ";");
                    break;

                default:
                    // Nothing else can appear at document or element level in a loaded DOM;
                    // fall back to the framework rather than silently dropping content.
                    _out.Write(n.OuterXml);
                    break;
            }
        }

        private void WriteElement(XmlElement e, int depth)
        {
            _out.Write("<" + e.Name);
            if (e.HasAttributes)
            {
                foreach (XmlAttribute a in e.Attributes)
                    _out.Write(" " + a.Name + "=\"" + EscapeAttribute(a.Value) + "\"");
            }

            // IsEmpty is the DOM's memory of whether the source self-closed this element; it does
            // not record which spelling was used, so the space comes from the sniffed convention.
            // Getting this wrong is one spurious diff per self-closing element in the document.
            if (e.IsEmpty)
            {
                _out.Write(_format.SpaceBeforeEmptyElementSlash ? " />" : "/>");
                return;
            }
            _out.Write(">");

            if (!_indent)
            {
                for (XmlNode c = e.FirstChild; c != null; c = c.NextSibling)
                    WriteNode(c, depth + 1);
            }
            else
            {
                // Mirror the conventional rule: an element holding any text stays on one line,
                // because breaking it would change the text. Only element-only content is laid out.
                bool block = e.FirstChild != null && !HasTextContent(e);
                for (XmlNode c = e.FirstChild; c != null; c = c.NextSibling)
                {
                    if (block)
                    {
                        _out.Write(_format.NewLine);
                        WriteIndent(depth + 1);
                    }
                    WriteNode(c, depth + 1);
                }
                if (block)
                {
                    _out.Write(_format.NewLine);
                    WriteIndent(depth);
                }
            }

            _out.Write("</" + e.Name + ">");
        }

        private void WriteIndent(int depth)
        {
            for (int i = 0; i < depth; i++) _out.Write(_format.IndentChars);
        }

        private static bool HasTextContent(XmlElement e)
        {
            for (XmlNode c = e.FirstChild; c != null; c = c.NextSibling)
                if (c.NodeType == XmlNodeType.Text || c.NodeType == XmlNodeType.CDATA
                    || c.NodeType == XmlNodeType.SignificantWhitespace
                    || c.NodeType == XmlNodeType.EntityReference)
                    return true;
            return false;
        }

        private static bool IsWhitespace(XmlNode n)
            => n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace;

        /// <summary>
        /// Write line breaks in the document's convention. Needed even in preserve mode: XML
        /// line-ending normalization is a parser requirement, so a CRLF file arrives in the DOM
        /// with bare LFs and its preserved whitespace would otherwise save back as LF, rewriting
        /// every line of a CRLF document. Applied after escaping, so a literal carriage return
        /// in content — already turned into &amp;#xD; — is not mistaken for a line break.
        /// </summary>
        private string Nl(string s)
        {
            if (string.IsNullOrEmpty(s) || _format.NewLine == "\n") return s;
            return s.Replace("\n", _format.NewLine);
        }

        /// <summary>
        /// The declaration, verbatim from the source when the document still says the same
        /// thing it did on load. Comparing semantically rather than trusting the sniff means an
        /// edit to version/encoding/standalone is honored, while an untouched declaration keeps
        /// its original spelling — <c>UTF-8</c> does not silently become <c>utf-8</c>, and a
        /// declaration that omitted the encoding does not sprout one.
        /// </summary>
        private string DeclarationText(XmlDeclaration decl)
        {
            var sb = new StringBuilder("<?xml version=\"");
            sb.Append(string.IsNullOrEmpty(decl.Version) ? "1.0" : decl.Version).Append('"');
            if (!string.IsNullOrEmpty(decl.Encoding))
                sb.Append(" encoding=\"").Append(decl.Encoding).Append('"');
            if (!string.IsNullOrEmpty(decl.Standalone))
                sb.Append(" standalone=\"").Append(decl.Standalone).Append('"');
            sb.Append("?>");
            string rebuilt = sb.ToString();

            string original = _format.Declaration;
            if (original == null) return rebuilt;

            bool same = Same(XmlFormat.AttributeValue(original, "version"), decl.Version, "1.0")
                     && Same(XmlFormat.AttributeValue(original, "encoding"), decl.Encoding, "")
                     && Same(XmlFormat.AttributeValue(original, "standalone"), decl.Standalone, "");
            return same ? original : rebuilt;
        }

        private static bool Same(string a, string b, string whenMissing)
            => string.Equals(a ?? whenMissing, b ?? whenMissing, StringComparison.OrdinalIgnoreCase);

        private void WriteDocumentType(XmlDocumentType dt)
        {
            _out.Write("<!DOCTYPE " + dt.Name);
            if (!string.IsNullOrEmpty(dt.PublicId))
                _out.Write(" PUBLIC \"" + dt.PublicId + "\" \"" + (dt.SystemId ?? "") + "\"");
            else if (!string.IsNullOrEmpty(dt.SystemId))
                _out.Write(" SYSTEM \"" + dt.SystemId + "\"");
            if (!string.IsNullOrEmpty(dt.InternalSubset))
                _out.Write(" [" + dt.InternalSubset + "]");
            _out.Write(">");
        }

        // --------------------------------------------------------------------
        // Escaping. Only what XML requires, so the output stays as close to the source as the
        // DOM allows. Carriage returns become character references because a literal CR in
        // content is normalized away by any conformant parser on the way back in.

        private static string EscapeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '\r': sb.Append("&#xD;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        private static string EscapeAttribute(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    // Attribute-value normalization turns a literal tab or newline into a
                    // space on reload, so these have to go out as references to survive.
                    case '\t': sb.Append("&#x9;"); break;
                    case '\n': sb.Append("&#xA;"); break;
                    case '\r': sb.Append("&#xD;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
