using System;
using System.IO;
using XmlNotepad;

namespace Fux
{
    /// <summary>
    /// The reused <see cref="XmlCache"/> with fux's save path substituted for XmlNotepad's.
    ///
    /// Subclassing rather than replacing: the engine's DOM watchers are what give fux dirty
    /// tracking and revalidation for free, and <c>Save</c>'s bookkeeping (clearing the dirty
    /// flag, retargeting the file name, stamping the modified time so the file watcher doesn't
    /// mistake our own write for an external one, firing ModelChanged) is all private. Only
    /// the serialization itself is overridden, through the <c>WriteDocumentTo</c> seam.
    /// </summary>
    internal sealed class FuxCache : XmlCache
    {
        /// <summary>
        /// How the current document was written on disk. Set by the load path; reset to the
        /// defaults when there is no file behind the document, so a save still has conventions
        /// to follow. See <see cref="XmlFormat"/>.
        /// </summary>
        public XmlFormat Format { get; set; } = XmlFormat.Default;

        /// <summary>
        /// Whether this document needs indentation synthesized on save. True for JSON and CSV
        /// imports: their readers produce no whitespace nodes, so with nothing to preserve the
        /// document would be written on a single line. Set by the load path rather than
        /// inferred, because a one-line XML file also has no whitespace and must stay as it is.
        /// </summary>
        public bool PrettyPrint { get; set; }

        /// <summary>
        /// Whether writing a document keeps the previous contents of the file it lands on.
        /// On unless <c>--no-backup</c> says otherwise. See <see cref="Backup"/>.
        /// </summary>
        public bool Backups { get; set; } = true;

        /// <summary>
        /// Where the last write filed the contents it displaced, or null if it displaced none —
        /// the file was new, or the write left it unchanged.
        /// </summary>
        public string LastBackup { get; private set; }

        public FuxCache(IServiceProvider site, SchemaCache schemaCache, DelayedActions handler)
            : base(site, schemaCache, handler)
        {
        }

        protected override void WriteDocumentTo(string filename)
        {
            // Whitespace preservation and synthesized indentation are alternatives, never both:
            // when the document carries its own layout as whitespace nodes, adding more would
            // indent it a second time on every save. An import has no layout to preserve, so it
            // is the one case where preservation is on and indentation is still wanted.
            bool indent = PrettyPrint || Document == null || !Document.PreserveWhitespace;

            // Build the whole file before opening it, so a serialization failure leaves the
            // previous contents on disk intact — the same guarantee the base implementation
            // gets from writing through a MemoryStream.
            byte[] bytes = XmlFormatWriter.WriteToBytes(Document, Format ?? XmlFormat.Default, indent);

            // Keep whatever is already at this name before replacing it. This is the one place
            // fux overwrites a document, so it is the one place the rule has to hold — Save,
            // Save As and SaveCopy all arrive here, and so will anything added later.
            //
            // Ahead of the write, necessarily: afterwards the previous contents are gone. A
            // backup that cannot be written therefore fails the save, with the file on disk
            // still intact — saving anyway would quietly drop the protection the user asked
            // for at the only moment it was going to be used.
            LastBackup = Backups ? Backup.Rotate(filename, bytes) : null;

            File.WriteAllBytes(filename, bytes);
        }
    }
}
