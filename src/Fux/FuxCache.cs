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

        public FuxCache(IServiceProvider site, SchemaCache schemaCache, DelayedActions handler)
            : base(site, schemaCache, handler)
        {
        }

        protected override void WriteDocumentTo(string filename)
        {
            // Whitespace preservation and synthesized indentation are alternatives, never both:
            // when the document carries its own layout as whitespace nodes, adding more would
            // indent it a second time on every save.
            bool indent = Document == null || !Document.PreserveWhitespace;

            // Build the whole file before opening it, so a serialization failure leaves the
            // previous contents on disk intact — the same guarantee the base implementation
            // gets from writing through a MemoryStream.
            byte[] bytes = XmlFormatWriter.WriteToBytes(Document, Format ?? XmlFormat.Default, indent);
            File.WriteAllBytes(filename, bytes);
        }
    }
}
