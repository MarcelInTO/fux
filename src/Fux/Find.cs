using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using XmlNotepad;

namespace Fux
{
    // Find over the loaded document. The vocabulary — FindFlags, SearchFilter — is Model's
    // (Model/FindTarget.cs), reused as-is; the matching rules are cribbed from upstream's
    // XmlTreeViewFindTarget (MatchNode / MatchStrings / Tokenize). What is deliberately NOT
    // reused is Model's IFindTarget contract itself: it is built for the WinForms editor
    // overlay, handing back a screen Rectangle for the matched character span and supporting
    // replace-in-place. fux moves the tree selection instead, so a hit here is a whole node
    // rather than a character range.
    //
    // Two other fux-shaped departures, both load-bearing:
    //   * matching runs over what fux actually draws: the tree's display order (an element's
    //     attributes first, then its shown children) and Program.GetValue for a node's value,
    //     which folds an element's text children into the element itself. Upstream walks
    //     //node() and would happily match a text node fux never gives a row to — the hit
    //     would look like a no-op jump.
    //   * the hit list is rebuilt on every step instead of being cached and invalidated on
    //     ModelChanged the way upstream does it. Editing between finds is the normal case, and
    //     a stale index into a list holding deleted nodes is exactly the class of bug this
    //     codebase can do without; a full walk of a document that fits in a terminal tree
    //     costs less than the keypress that asked for it.
    internal sealed class Query
    {
        public readonly string Expression;
        public readonly FindFlags Flags;
        public readonly SearchFilter Filter;
        private readonly Regex _regex;                 // null unless Regex mode
        private readonly HashSet<XmlNode> _xpathHits;  // null unless XPath mode
        private readonly StringComparison _comp;

        // Throws ArgumentException with a user-facing message when the expression itself is
        // bad, so a typo'd regex or XPath reports instead of throwing out of a keypress.
        public Query(XmlDocument doc, string expression, FindFlags flags, SearchFilter filter)
        {
            Expression = expression ?? "";
            Flags = flags;
            Filter = filter;
            _comp = (flags & FindFlags.MatchCase) != 0
                ? StringComparison.CurrentCulture
                : StringComparison.CurrentCultureIgnoreCase;

            if ((flags & FindFlags.XPath) != 0)
            {
                _xpathHits = new HashSet<XmlNode>();
                if (doc == null) return;
                try
                {
                    // No prefixes are declared: a document's own prefixes are not necessarily
                    // the ones a user would type, so local-name() is the portable spelling.
                    foreach (XmlNode n in doc.SelectNodes(Expression, new XmlNamespaceManager(doc.NameTable)))
                        _xpathHits.Add(n);
                }
                catch (XPathException ex) { throw new ArgumentException($"bad XPath: {ex.Message}"); }
                catch (ArgumentException ex) { throw new ArgumentException($"bad XPath: {ex.Message}"); }
            }
            else if ((flags & FindFlags.Regex) != 0)
            {
                var opts = (flags & FindFlags.MatchCase) != 0 ? RegexOptions.None : RegexOptions.IgnoreCase;
                try { _regex = new Regex(Expression, opts); }
                catch (ArgumentException ex) { throw new ArgumentException($"bad regex: {ex.Message}"); }
            }
        }

        public bool IsEmpty => !IsXPath && string.IsNullOrEmpty(Expression);
        private bool IsXPath => _xpathHits != null;

        // Which nodes count as a hit, following upstream's MatchNode: a name match only applies
        // to nodes that have a meaningful one, and Comments narrows to comment values alone.
        public bool IsMatch(XmlNode n)
        {
            if (IsXPath) return _xpathHits.Contains(n);

            if (Filter == SearchFilter.Comments)
                return n.NodeType == XmlNodeType.Comment && Contains(Program.GetValue(n));

            bool named = n.NodeType == XmlNodeType.Element || n.NodeType == XmlNodeType.Attribute ||
                         n.NodeType == XmlNodeType.ProcessingInstruction;
            if (named && (Filter == SearchFilter.Names || Filter == SearchFilter.Everything) && Contains(n.Name))
                return true;
            if (Filter == SearchFilter.Text || Filter == SearchFilter.Everything)
                return Contains(Program.GetValue(n));
            return false;
        }

        private bool Contains(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (_regex != null) return _regex.IsMatch(value);
            if ((Flags & FindFlags.WholeWord) != 0)
            {
                foreach (var word in Words(value))
                    if (string.Compare(Expression, word, _comp) == 0) return true;
                return false;
            }
            return value.IndexOf(Expression, _comp) >= 0;
        }

        // Upstream's Tokenize, same delimiter set: whole-word means the expression equals one
        // of these runs, not merely that it sits on a word boundary.
        private static IEnumerable<string> Words(string value)
        {
            const string delims = " \t\r\n.,;!'\"+=-<>()";
            int start = -1;
            for (int i = 0; i < value.Length; i++)
            {
                if (delims.IndexOf(value[i]) >= 0)
                {
                    if (start >= 0) { yield return value.Substring(start, i - start); start = -1; }
                }
                else if (start < 0)
                {
                    start = i;
                }
            }
            if (start >= 0) yield return value.Substring(start);
        }
    }

    internal static class Find
    {
        // Every node the tree draws, in the order it draws them.
        internal static IEnumerable<XmlNode> DisplayOrder(XmlNode root)
        {
            if (root == null) yield break;
            yield return root;
            foreach (var c in Program.GetChildren(root))
                foreach (var d in DisplayOrder(c))
                    yield return d;
        }

        // The next match after `from` (or the previous one, backwards), wrapping around the end
        // of the document. `from` need not be a match itself — stepping is defined by position
        // in the display order, so a find still moves sensibly after the user clicks elsewhere.
        // Reports the 1-based ring position and the total so the caller can say "3/17".
        internal static XmlNode Step(XmlNode root, XmlNode from, Query q, bool backwards,
                                     out int index, out int total)
        {
            index = 0;
            total = 0;
            if (root == null || q == null || q.IsEmpty) return null;

            var order = new List<XmlNode>(DisplayOrder(root));
            var hits = new List<int>();
            for (int i = 0; i < order.Count; i++)
                if (q.IsMatch(order[i])) hits.Add(i);

            total = hits.Count;
            if (total == 0) return null;

            // XmlNode does not override Equals, so this is reference identity — the same
            // instance the tree holds. A selection that is no longer in the document reads as
            // -1, which starts the ring from the top (or the bottom, going backwards).
            int cur = from == null ? -1 : order.IndexOf(from);

            int pick = backwards
                ? hits.FindLastIndex(i => i < cur)
                : hits.FindIndex(i => i > cur);
            if (pick < 0) pick = backwards ? total - 1 : 0; // wrapped

            index = pick + 1;
            return order[hits[pick]];
        }
    }
}
