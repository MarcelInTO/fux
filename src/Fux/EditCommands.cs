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
}
