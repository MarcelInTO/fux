using System.Collections.Generic;
using System.IO;
using XmlNotepad;

namespace Fux
{
    // One validation diagnostic collected from the engine's Checker.
    internal sealed class ErrorItem
    {
        public Severity Severity;
        public string Reason;
        public string File;
        public int Line;
        public int Col;

        // Row text for the ListView, e.g. "[E] emp.xml:12,5  The element ... is invalid".
        // The file name is dropped when the engine didn't attribute the error to a file
        // (line/col are then usually 0 too, so there is nothing to jump to).
        public override string ToString()
        {
            string tag;
            switch (Severity)
            {
                case Severity.Error: tag = "[E]"; break;
                case Severity.Warning: tag = "[W]"; break;
                case Severity.Hint: tag = "[H]"; break;
                default: tag = "[ ]"; break;
            }
            var where = string.IsNullOrEmpty(File) ? "" : Path.GetFileName(File);
            if (Line > 0) where = where.Length > 0 ? $"{where}:{Line},{Col}" : $"{Line},{Col}";
            return where.Length > 0 ? $"{tag} {where}  {Reason}" : $"{tag} {Reason}";
        }
    }

    // ErrorHandler sink that accumulates diagnostics for the error pane. Same shape as
    // sandbox/smoke's ConsoleErrorHandler, but it keeps the items instead of printing them.
    internal sealed class ErrorCollector : ErrorHandler
    {
        public readonly List<ErrorItem> Items = new List<ErrorItem>();
        public int Errors;
        public int Warnings;

        public override void HandleError(Severity sev, string reason, string filename, int line, int col, object data)
        {
            if (sev == Severity.Error) Errors++;
            else if (sev == Severity.Warning) Warnings++;
            Items.Add(new ErrorItem { Severity = sev, Reason = reason, File = filename, Line = line, Col = col });
        }
    }
}
