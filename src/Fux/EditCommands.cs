using System;
using System.Collections.Generic;
using System.Xml;
using XmlNotepad;

namespace Fux
{
    // What an undoable fux edit can tell the UI about itself: the DOM node it touched,
    // so the tree can reveal + reselect it after Do/Undo/Redo.
    internal interface INodeCommand
    {
        XmlNode Node { get; }
    }

    // A command that moves a node between two containers reports both, because v2's
    // Branch.Refresh rebuilds one level only (verified against Terminal.Gui 2.4.17: it
    // re-fetches its own children and, with startAtTop, its ancestors — never its
    // descendants). Refreshing just the node's new parent would leave the container it left
    // holding a stale branch, so the node would appear in both places at once.
    internal interface IContainerCommand
    {
        IEnumerable<XmlNode> Containers { get; }
    }

    // Pure-DOM value edit, driven by the reused Model UndoManager (Model/UndoManager.cs).
    // The DOM logic mirrors XmlNotepad's EditNodeValue (XmlNotepad/Commands.cs), minus the
    // WinForms XmlTreeView sync woven through it — fux refreshes its TreeView from the
    // UndoManager's CommandDone/Undone/Redone events instead (Program.AfterUndoableChange),
    // and XmlCache maintains Dirty/ModelChanged by watching the DOM directly.
    internal sealed class EditNodeValue : Command, INodeCommand
    {
        private readonly XmlNode _node;
        private readonly string _newValue;
        private readonly string _oldValue;

        public EditNodeValue(XmlNode node, string newValue)
        {
            _node = node;
            _newValue = newValue;
            _oldValue = GetNodeValue(node);
        }

        public XmlNode Node => _node;
        public override string Name => "Edit Value";
        public override bool IsNoop => _oldValue == _newValue;
        public override void Do() => SetNodeValue(_node, _newValue);
        public override void Undo() => SetNodeValue(_node, _oldValue);
        public override void Redo() => SetNodeValue(_node, _newValue);

        // An element's value is its text content; everything else the tree offers for
        // editing keeps its value in Node.Value (a PI's Value aliases its Data).
        internal static string GetNodeValue(XmlNode n) => n is XmlElement e ? e.InnerText : n.Value;

        private static void SetNodeValue(XmlNode n, string value)
        {
            if (n is XmlElement e) e.InnerText = value; // replaces the text children, as upstream does
            else n.Value = value;
        }

        // Editable when writing the value back can't destroy structure the tree shows:
        // attribute/comment/CDATA/text/PI always; an element only while its children are
        // all text-ish — the same rule Program.GetValue uses to show a scalar value.
        internal static bool CanEditValue(XmlNode n)
        {
            switch (n.NodeType)
            {
                case XmlNodeType.Attribute:
                case XmlNodeType.Comment:
                case XmlNodeType.CDATA:
                case XmlNodeType.Text:
                case XmlNodeType.ProcessingInstruction:
                    return true;
                case XmlNodeType.Element:
                    foreach (XmlNode c in n.ChildNodes)
                        if (c.NodeType != XmlNodeType.Text && c.NodeType != XmlNodeType.CDATA &&
                            c.NodeType != XmlNodeType.Whitespace && c.NodeType != XmlNodeType.SignificantWhitespace)
                            return false;
                    return true;
                default:
                    return false;
            }
        }
    }

    // Pure-DOM rename for elements, attributes and PIs. The DOM can rename none of them
    // in place, so each is the upstream swap dance (XmlNotepad/Commands.cs:
    // EditElementName / EditAttributeName / EditProcessingInstructionName): create a
    // replacement, migrate content, swap in the parent — minus the WinForms view sync.
    // Node returns whichever instance is live, so the tree reselects correctly.
    //
    // The constructor parses/validates the name (XmlConvert.VerifyName inside ParseName
    // throws on garbage — callers surface that, nothing is pushed). A prefixed name with
    // no in-scope namespace gets an auto-generated xmlns:prefix declaration, upstream
    // style: on the renamed element itself, or on the owner element for an attribute.
    internal sealed class RenameNode : Command, INodeCommand
    {
        private readonly XmlNode _old;
        private readonly XmlName _name;   // parsed element/attribute name (null for PI)
        private readonly string _piTarget;
        private XmlNode _new;             // replacement, created in Do
        private XmlNode _parent;          // parent node / owner element
        private XmlAttribute _nsDecl;     // auto-generated xmlns:prefix, if needed
        private XmlNode _current;

        public RenameNode(XmlNode node, string newName)
        {
            _old = _current = node;
            switch (node.NodeType)
            {
                case XmlNodeType.Element:
                    _name = XmlHelpers.ParseName(node, newName, XmlNodeType.Element);
                    break;
                case XmlNodeType.Attribute:
                    _name = XmlHelpers.ParseName(((XmlAttribute)node).OwnerElement, newName, XmlNodeType.Attribute);
                    break;
                case XmlNodeType.ProcessingInstruction:
                    XmlConvert.VerifyName(newName);
                    _piTarget = newName;
                    break;
                default:
                    throw new ArgumentException($"cannot rename a {node.NodeType} node");
            }
        }

        internal static bool CanRename(XmlNode n)
            => n != null && (n.NodeType == XmlNodeType.Element || n.NodeType == XmlNodeType.Attribute ||
                             n.NodeType == XmlNodeType.ProcessingInstruction);

        public XmlNode Node => _current;
        public override string Name => "Rename";

        public override bool IsNoop
        {
            get
            {
                if (_old is XmlProcessingInstruction pi) return pi.Target == _piTarget;
                return _old.LocalName == _name.LocalName && _old.Prefix == _name.Prefix &&
                       _old.NamespaceURI == (_name.NamespaceUri ?? "");
            }
        }

        public override void Do()
        {
            var doc = _old.OwnerDocument;
            switch (_old.NodeType)
            {
                case XmlNodeType.Element:
                    _parent = _old.ParentNode;
                    if (XmlHelpers.MissingNamespace(_name))
                        _nsDecl = XmlHelpers.GenerateNamespaceDeclaration((XmlElement)_old, _name); // also assigns _name.NamespaceUri
                    _new = doc.CreateElement(_name.Prefix, _name.LocalName, _name.NamespaceUri);
                    break;
                case XmlNodeType.Attribute:
                    _parent = ((XmlAttribute)_old).OwnerElement;
                    if (XmlHelpers.MissingNamespace(_name))
                        _nsDecl = XmlHelpers.GenerateNamespaceDeclaration((XmlElement)_parent, _name);
                    var na = doc.CreateAttribute(_name.Prefix, _name.LocalName, _name.NamespaceUri);
                    na.Value = _old.Value;
                    _new = na;
                    break;
                case XmlNodeType.ProcessingInstruction:
                    _parent = _old.ParentNode;
                    _new = doc.CreateProcessingInstruction(_piTarget, ((XmlProcessingInstruction)_old).Data);
                    break;
            }
            Redo();
        }

        public override void Redo()
        {
            switch (_old.NodeType)
            {
                case XmlNodeType.Element:
                    MoveContent((XmlElement)_old, (XmlElement)_new);
                    _parent.ReplaceChild(_new, _old);
                    if (_nsDecl != null) ((XmlElement)_new).SetAttributeNode(_nsDecl);
                    break;
                case XmlNodeType.Attribute:
                    var owner = (XmlElement)_parent;
                    owner.Attributes.InsertBefore((XmlAttribute)_new, (XmlAttribute)_old); // keep position
                    owner.RemoveAttributeNode((XmlAttribute)_old);
                    if (_nsDecl != null) owner.SetAttributeNode(_nsDecl);
                    break;
                case XmlNodeType.ProcessingInstruction:
                    _parent.InsertBefore(_new, _old);
                    _parent.RemoveChild(_old);
                    break;
            }
            _current = _new;
        }

        public override void Undo()
        {
            switch (_old.NodeType)
            {
                case XmlNodeType.Element:
                    if (_nsDecl != null) ((XmlElement)_new).RemoveAttributeNode(_nsDecl); // before content moves back
                    MoveContent((XmlElement)_new, (XmlElement)_old);
                    _parent.ReplaceChild(_old, _new);
                    break;
                case XmlNodeType.Attribute:
                    var owner = (XmlElement)_parent;
                    if (_nsDecl != null) owner.RemoveAttributeNode(_nsDecl);
                    owner.Attributes.InsertBefore((XmlAttribute)_old, (XmlAttribute)_new);
                    owner.RemoveAttributeNode((XmlAttribute)_new);
                    break;
                case XmlNodeType.ProcessingInstruction:
                    _parent.InsertBefore(_old, _new);
                    _parent.RemoveChild(_new);
                    break;
            }
            _current = _old;
        }

        // Migrate specified attributes and all children (upstream EditElementName.Move).
        private static void MoveContent(XmlElement from, XmlElement to)
        {
            var move = new List<XmlAttribute>();
            foreach (XmlAttribute a in from.Attributes)
                if (a.Specified)
                    move.Add(a);
            foreach (var a in move)
            {
                from.Attributes.Remove(a);
                to.Attributes.Append(a);
            }
            while (from.HasChildNodes)
                to.AppendChild(from.FirstChild);
        }
    }

    internal enum InsertKind { Element, Attribute, Comment, Pi }
    internal enum InsertPos { Child, Before, After }

    // Pure-DOM insert of a brand-new node relative to the selected one. Text/CDATA are
    // deliberately absent: the fux tree folds them into element values, so an inserted one
    // would be invisible — F2 value editing is how text content gets in. All validation
    // (names, positions, duplicate attributes) happens in the constructor, which throws
    // with a user-facing message and pushes nothing.
    internal sealed class InsertNewNode : Command, INodeCommand
    {
        private readonly XmlNode _node;         // the created node
        private readonly XmlElement _container; // element that receives it
        private readonly XmlNode _ref;          // sibling anchor for Before/After (null = append)
        private readonly XmlAttribute _refAttr; // attribute sibling anchor
        private readonly bool _before;
        private readonly XmlAttribute _nsDecl;  // auto-generated xmlns:prefix, if needed
        private XmlNode _current;

        public InsertNewNode(XmlNode anchor, InsertKind kind, InsertPos pos, string name)
        {
            if (anchor == null) throw new ArgumentException("nothing is selected");
            var doc = anchor.OwnerDocument;
            name = name?.Trim();

            if (kind == InsertKind.Attribute)
            {
                _container = anchor as XmlElement ?? (anchor as XmlAttribute)?.OwnerElement
                    ?? throw new ArgumentException("select an element (or one of its attributes) to add an attribute to");
                RequireName(kind, name);
                var xn = XmlHelpers.ParseName(_container, name, XmlNodeType.Attribute);
                if (XmlHelpers.MissingNamespace(xn))
                    _nsDecl = XmlHelpers.GenerateNamespaceDeclaration(_container, xn); // assigns xn.NamespaceUri
                var qualified = string.IsNullOrEmpty(xn.Prefix) ? xn.LocalName : xn.Prefix + ":" + xn.LocalName;
                if (_container.HasAttribute(qualified))
                    throw new ArgumentException($"attribute '{qualified}' already exists on this element");
                var na = doc.CreateAttribute(xn.Prefix, xn.LocalName, xn.NamespaceUri);
                _node = na;
                _refAttr = pos != InsertPos.Child ? anchor as XmlAttribute : null;
                _before = pos == InsertPos.Before;
            }
            else
            {
                var anchorNode = anchor is XmlAttribute at ? at.OwnerElement : anchor;
                if (pos == InsertPos.Child)
                {
                    _container = anchorNode as XmlElement
                        ?? throw new ArgumentException("select an element to insert into");
                }
                else
                {
                    if (anchor is XmlAttribute)
                        throw new ArgumentException("cannot insert before/after an attribute — pick 'Child' to insert into its element");
                    _container = anchorNode.ParentNode as XmlElement
                        ?? throw new ArgumentException("cannot insert siblings of the document root");
                    _ref = anchorNode;
                    _before = pos == InsertPos.Before;
                }

                switch (kind)
                {
                    case InsertKind.Element:
                        RequireName(kind, name);
                        var xn = XmlHelpers.ParseName(_container, name, XmlNodeType.Element);
                        if (XmlHelpers.MissingNamespace(xn))
                            _nsDecl = XmlHelpers.GenerateNamespaceDeclaration(_container, xn);
                        _node = doc.CreateElement(xn.Prefix, xn.LocalName, xn.NamespaceUri);
                        break;
                    case InsertKind.Comment:
                        _node = doc.CreateComment("");
                        break;
                    case InsertKind.Pi:
                        RequireName(kind, name);
                        XmlConvert.VerifyName(name);
                        _node = doc.CreateProcessingInstruction(name, "");
                        break;
                }
            }
            _current = _node;
        }

        private static void RequireName(InsertKind kind, string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"a name is required for a new {kind}");
        }

        public XmlNode Node => _current;
        public override string Name => "Insert";
        public override bool IsNoop => false;

        public override void Do() => Redo();

        public override void Redo()
        {
            if (_node is XmlAttribute a)
            {
                if (_refAttr != null)
                {
                    if (_before) _container.Attributes.InsertBefore(a, _refAttr);
                    else _container.Attributes.InsertAfter(a, _refAttr);
                }
                else
                {
                    _container.SetAttributeNode(a);
                }
                if (_nsDecl != null) _container.SetAttributeNode(_nsDecl);
            }
            else
            {
                if (_ref != null)
                {
                    if (_before) _container.InsertBefore(_node, _ref);
                    else _container.InsertAfter(_node, _ref);
                }
                else
                {
                    _container.AppendChild(_node);
                }
                // a generated declaration rides the new element (and vanishes with it on undo)
                if (_nsDecl != null && _node is XmlElement ne) ne.SetAttributeNode(_nsDecl);
            }
            _current = _node;
        }

        public override void Undo()
        {
            if (_node is XmlAttribute a)
            {
                if (_nsDecl != null) _container.RemoveAttributeNode(_nsDecl);
                _container.RemoveAttributeNode(a);
            }
            else
            {
                _container.RemoveChild(_node);
            }
            _current = _container;
        }
    }

    // Pure-DOM delete with exact-position undo: the successor sibling (or successor
    // attribute) is the anchor for reinsertion. The document root is off limits — an
    // empty document would ripple through every pane for little gain.
    internal sealed class DeleteNode : Command, INodeCommand
    {
        private readonly XmlNode _node;
        private XmlElement _container;   // parent element / attribute owner
        private XmlNode _ref;            // sibling that followed the node (null = was last)
        private XmlAttribute _refAttr;   // attribute that followed (null = was last)
        private XmlNode _current;

        public DeleteNode(XmlNode node)
        {
            if (node == null) throw new ArgumentException("nothing is selected");
            if (node == node.OwnerDocument?.DocumentElement)
                throw new ArgumentException("cannot delete the document root");
            _node = _current = node;
        }

        public XmlNode Node => _current;
        public override string Name => "Delete";
        public override bool IsNoop => false;

        public override void Do()
        {
            if (_node is XmlAttribute a)
            {
                _container = a.OwnerElement;
                var attrs = _container.Attributes;
                int i = 0;
                while (i < attrs.Count && !ReferenceEquals(attrs[i], a)) i++;
                _refAttr = i + 1 < attrs.Count ? attrs[i + 1] : null;
                _container.RemoveAttributeNode(a);
            }
            else
            {
                _container = (XmlElement)_node.ParentNode;
                _ref = _node.NextSibling;
                _container.RemoveChild(_node);
            }
            _current = _container;
        }

        public override void Redo() => Do();

        public override void Undo()
        {
            if (_node is XmlAttribute a)
            {
                if (_refAttr != null) _container.Attributes.InsertBefore(a, _refAttr);
                else _container.SetAttributeNode(a);
            }
            else
            {
                _container.InsertBefore(_node, _ref); // null _ref appends at the end
            }
            _current = _node;
        }
    }

    internal enum NudgeDir { Up, Down, Left, Right }

    // A nudge with nowhere to go: already first/last in its band, no preceding element to
    // move into, nothing above the document root to promote to. Callers swallow these
    // silently — running into the edge should feel like pressing Down at the end of a list,
    // not like an error. Every other refusal is a plain ArgumentException and reaches the
    // user as a message.
    internal sealed class NudgeBlocked : ArgumentException
    {
        public NudgeBlocked(string message) : base(message) { }
    }

    // Pure-DOM reorder (Up/Down) and re-level (Left promotes out of the parent, Right demotes
    // into the preceding sibling), cribbed from upstream's NudgeNode + MoveNode pair
    // (XmlNotepad/Commands.cs). Only their DOM moves and position rules carry over: upstream's
    // MoveNode is written against XmlTreeNode/TreeParent and cannot be lifted.
    //
    // Two deliberate departures from upstream:
    //   * a nudge stays inside the selected node's own display band — an attribute among its
    //     owner's attributes, everything else among the parent's shown children — where
    //     upstream's Up/Down walk can cross into a neighbouring parent. Staying in the band
    //     makes every nudge exactly undone by its opposite, which is what makes the operation
    //     safe to hold a key down on.
    //   * promoting out of the document element is refused. fux's tree is rooted at
    //     DocumentElement, so a node moved to document level would silently vanish from view.
    //
    // Target resolution and validation happen in the constructor, so a refused nudge pushes
    // nothing. Undo re-anchors on the successor the node had before the move — the raw DOM
    // successor, so it is exact even when the model preserves whitespace — the same
    // exact-position trick DeleteNode uses.
    internal sealed class NudgeNode : Command, INodeCommand, IContainerCommand
    {
        private readonly XmlNode _node;
        private readonly XmlElement _from;   // container it leaves
        private readonly XmlNode _fromRef;   // its successor there (null = it was last)
        private readonly XmlElement _to;     // container it joins (== _from for Up/Down)
        private readonly XmlNode _toRef;     // insert before this (null = append)

        public NudgeNode(XmlNode node, NudgeDir dir)
        {
            if (node == null) throw new ArgumentException("nothing is selected");
            if (node == node.OwnerDocument?.DocumentElement)
                throw new NudgeBlocked("cannot nudge the document root");

            var attr = node as XmlAttribute;
            // Attributes report ParentNode == null in System.Xml, hence OwnerElement.
            _from = (attr != null ? attr.OwnerElement : node.ParentNode as XmlElement)
                ?? throw new NudgeBlocked("cannot nudge a node outside the document root");
            _node = node;
            _fromRef = attr != null ? NextAttribute(_from, attr) : node.NextSibling;

            switch (dir)
            {
                case NudgeDir.Up:
                {
                    var prev = PrevInBand(node)
                        ?? throw new NudgeBlocked($"already the first {BandName(node)}");
                    _to = _from;
                    _toRef = prev; // landing in front of the predecessor swaps the two
                    break;
                }

                case NudgeDir.Down:
                {
                    var next = NextInBand(node)
                        ?? throw new NudgeBlocked($"already the last {BandName(node)}");
                    _to = _from;
                    // To land after `next`, anchor on whatever follows it (null appends).
                    _toRef = attr != null
                        ? NextAttribute(_from, (XmlAttribute)next)
                        : next.NextSibling;
                    break;
                }

                case NudgeDir.Left:
                {
                    // The document element's parent is the XmlDocument, so this also rejects
                    // promoting a top-level node to document level.
                    _to = _from.ParentNode as XmlElement
                        ?? throw new NudgeBlocked("cannot promote out of the document root");
                    if (attr != null)
                    {
                        if (_to.Attributes.GetNamedItem(attr.LocalName, attr.NamespaceURI) != null)
                            throw new ArgumentException(
                                $"<{_to.Name}> already has an attribute named '{attr.Name}'");
                        _toRef = null; // attributes can only land among attributes: append
                    }
                    else
                    {
                        // Upstream's rule: the first of several children lands *before* its old
                        // parent, so left-then-right is a round trip; anything else lands after.
                        bool firstOfSeveral = PrevInBand(node) == null && NextInBand(node) != null;
                        _toRef = firstOfSeveral ? _from : _from.NextSibling;
                    }
                    break;
                }

                default: // NudgeDir.Right
                {
                    // The preceding sibling becomes the new parent. Attributes have only other
                    // attributes in front of them, so they can never demote.
                    _to = PrevInBand(node) as XmlElement
                        ?? throw new NudgeBlocked(
                            $"no preceding sibling element to move this {BandName(node)} into");
                    _toRef = null; // append as its last child (upstream's InsertPosition.Child)
                    break;
                }
            }
        }

        public XmlNode Node => _node; // a move never swaps node instances
        public override string Name => "Nudge";
        public override bool IsNoop => false; // the constructor refuses the no-op cases
        public IEnumerable<XmlNode> Containers { get { yield return _from; yield return _to; } }

        public override void Do() => Move(_from, _to, _toRef);
        public override void Redo() => Do();
        public override void Undo() => Move(_to, _from, _fromRef);

        // Detach from `from`, reattach into `to` directly before `before` (null appends).
        private void Move(XmlElement from, XmlElement to, XmlNode before)
        {
            if (_node is XmlAttribute a)
            {
                from.RemoveAttributeNode(a);
                if (before is XmlAttribute ba) to.Attributes.InsertBefore(a, ba);
                else to.SetAttributeNode(a);
            }
            else
            {
                from.RemoveChild(_node);
                if (before != null) to.InsertBefore(_node, before);
                else to.AppendChild(_node);
            }
        }

        // The two display bands the tree presents under an element, in order.
        private static string BandName(XmlNode n) => n is XmlAttribute ? "attribute" : "child";

        // Neighbours within the node's own band: attributes step through the owner's attribute
        // collection, everything else through the shown siblings (Program.IsShown skips the
        // text-ish nodes the tree folds into element values, so a nudge never lands on one).
        private static XmlNode PrevInBand(XmlNode n)
        {
            if (n is XmlAttribute a)
            {
                var attrs = a.OwnerElement?.Attributes;
                int i = IndexOfAttr(attrs, a);
                return i > 0 ? attrs[i - 1] : null;
            }
            for (var p = n.PreviousSibling; p != null; p = p.PreviousSibling)
                if (Program.IsShown(p)) return p;
            return null;
        }

        private static XmlNode NextInBand(XmlNode n)
        {
            if (n is XmlAttribute a) return NextAttribute(a.OwnerElement, a);
            for (var s = n.NextSibling; s != null; s = s.NextSibling)
                if (Program.IsShown(s)) return s;
            return null;
        }

        private static XmlAttribute NextAttribute(XmlElement owner, XmlAttribute a)
        {
            var attrs = owner?.Attributes;
            int i = IndexOfAttr(attrs, a);
            return i >= 0 && i + 1 < attrs.Count ? attrs[i + 1] : null;
        }

        // By reference: two attributes can share a name only transiently, and Equals on
        // XmlAttribute is identity anyway — this keeps the intent explicit.
        private static int IndexOfAttr(XmlAttributeCollection attrs, XmlAttribute a)
        {
            if (attrs == null) return -1;
            for (int i = 0; i < attrs.Count; i++)
                if (ReferenceEquals(attrs[i], a)) return i;
            return -1;
        }
    }
}
