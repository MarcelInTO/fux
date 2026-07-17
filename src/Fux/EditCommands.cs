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
}
