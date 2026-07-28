using System;
using System.Xml;

namespace Fux
{
    /// <summary>
    /// Whitespace bookkeeping for structural edits.
    ///
    /// fux loads documents with PreserveWhitespace on, so indentation is not something the
    /// writer invents at save time — it is nodes in the tree, and a structural edit has to
    /// maintain them. A block-formatted container's children alternate
    /// <c>ws, item, ws, item, …, ws</c>: every item carries the line break and indent that
    /// precede it, and a final ws sits before the end tag. Insert, delete and nudge each keep
    /// that shape, which is what stops a new node landing jammed against its sibling and stops
    /// a delete leaving a blank line behind.
    ///
    /// Every helper here reads the tree and returns a decision; none of them mutate. The
    /// commands own the mutation, because only they can undo it exactly.
    /// </summary>
    internal static class XmlLayout
    {
        public static bool IsWhitespaceNode(XmlNode n)
            => n != null && (n.NodeType == XmlNodeType.Whitespace
                          || n.NodeType == XmlNodeType.SignificantWhitespace);

        /// <summary>The whitespace directly in front of <paramref name="node"/> — its indentation.</summary>
        public static XmlNode LeadingWhitespace(XmlNode node)
        {
            XmlNode p = node == null ? null : node.PreviousSibling;
            return IsWhitespaceNode(p) ? p : null;
        }

        /// <summary>
        /// Whether a new child of this container should get a line of its own. False when the
        /// container holds text — there the whitespace is content, and breaking it would change
        /// the value the user sees — and false when the container is written inline
        /// (<c>&lt;a&gt;&lt;b/&gt;&lt;c/&gt;&lt;/a&gt;</c>), where expanding it would be a
        /// reformat nobody asked for.
        /// </summary>
        public static bool ShouldIndent(XmlNode container)
        {
            if (container == null || HasTextContent(container)) return false;

            bool hasWhitespace = false, hasItems = false;
            foreach (XmlNode c in container.ChildNodes)
            {
                if (IsWhitespaceNode(c)) hasWhitespace = true;
                else hasItems = true;
            }
            return !hasItems || hasWhitespace;
        }

        /// <summary>Does this container hold text, making its whitespace content rather than layout?</summary>
        public static bool HasTextContent(XmlNode container)
        {
            for (XmlNode c = container.FirstChild; c != null; c = c.NextSibling)
                if (c.NodeType == XmlNodeType.Text || c.NodeType == XmlNodeType.CDATA
                    || c.NodeType == XmlNodeType.EntityReference)
                    return true;
            return false;
        }

        /// <summary>
        /// The whitespace before a container's end tag, if it has one. A node moving into the
        /// container belongs in front of this, not after it.
        /// </summary>
        public static XmlNode TrailingWhitespace(XmlNode container)
        {
            XmlNode last = container == null ? null : container.LastChild;
            return IsWhitespaceNode(last) ? last : null;
        }

        /// <summary>True when the container has no node the tree would give a row to.</summary>
        public static bool IsChildless(XmlNode container)
        {
            for (XmlNode c = container.FirstChild; c != null; c = c.NextSibling)
                if (!IsWhitespaceNode(c)) return false;
            return true;
        }

        /// <summary>
        /// The whitespace a child of <paramml name="container"/> should be preceded by, as a
        /// line break plus indent. An existing child's indentation is copied verbatim when there
        /// is one, so a document that indents four spaces keeps indenting four spaces regardless
        /// of what was sniffed from the file as a whole.
        /// </summary>
        public static string ChildIndent(XmlNode container, string indentChars)
        {
            for (XmlNode c = container.FirstChild; c != null; c = c.NextSibling)
                if (IsWhitespaceNode(c) && c.NextSibling != null)
                {
                    string existing = LastLine(c.Value);
                    if (existing != null) return existing;
                }
            return OwnIndent(container) + (indentChars ?? "  ");
        }

        /// <summary>
        /// The whitespace that belongs in front of this container's end tag: a line break plus
        /// the container's own indentation.
        /// </summary>
        public static string OwnIndent(XmlNode container)
        {
            XmlNode ws = LeadingWhitespace(container);
            return (ws == null ? null : LastLine(ws.Value)) ?? "\n";
        }

        /// <summary>
        /// Re-indent one whitespace node to sit at <paramref name="indent"/>, keeping any blank
        /// lines in front of it. Returns the new text. Used when a node changes depth, which is
        /// the only time a promote or demote should disturb the layout it moves through.
        /// </summary>
        public static string Reindent(string whitespace, string indent)
        {
            if (string.IsNullOrEmpty(whitespace)) return indent;
            int i = whitespace.LastIndexOf('\n');
            return i < 0 ? indent : whitespace.Substring(0, i) + indent;
        }

        /// <summary>A line break plus everything after the final one, or null if there is none.</summary>
        private static string LastLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.LastIndexOf('\n');
            return i < 0 ? null : "\n" + s.Substring(i + 1);
        }
    }
}
