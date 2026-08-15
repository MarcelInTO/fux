using System;
using System.Globalization;
using System.IO;

namespace Fux
{
    /// <summary>
    /// Keeping what a save is about to destroy.
    ///
    /// A save replaces a document wholesale, and the editor's undo stack only reaches back to
    /// the last time the file was opened — so the version that was on disk a moment ago is
    /// otherwise unrecoverable. Before overwriting a file, fux copies its current contents to a
    /// sibling named <c>&lt;name&gt;.&lt;yyyyMMdd-HHmmss&gt;.bak</c>.
    ///
    /// Next to the original, because a backup filed somewhere central is one nobody finds when
    /// they need it. Suffixed rather than renamed (<c>doc.xml.20260815-142530.bak</c>, not
    /// <c>doc.20260815-142530.xml</c>) so the copies stay out of the <c>*.xml</c> glob the
    /// original answers to, and stamped with the local wall-clock time of the save that
    /// displaced them, so they list in the order they were made. Nothing prunes them: deciding
    /// on the user's behalf which of their old versions is no longer worth keeping is exactly
    /// the judgement this feature exists to avoid making.
    /// </summary>
    internal static class Backup
    {
        // One second of resolution, and saves land faster than that, so the stamp alone is not
        // a unique name. The counter is the tie-break; the cap keeps a pathological directory
        // (every candidate taken) from spinning instead of reporting.
        private const int MaxPerSecond = 1000;

        /// <summary>
        /// Preserve <paramref name="path"/>'s current contents ahead of a write of
        /// <paramref name="replacement"/>. Returns where they were put, or null when there was
        /// nothing to keep: no such file yet, or a write that leaves it unchanged.
        /// </summary>
        internal static string Rotate(string path, byte[] replacement)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            // A write that changes nothing destroys nothing, so it preserves nothing. Without
            // this, an idle ^S — the reflex of anyone who has ever lost work — would leave
            // another identical copy behind every time. Comparing lengths first settles the
            // interesting case (a document that actually grew or shrank) without reading a byte.
            if (info.Length == replacement.LongLength &&
                File.ReadAllBytes(path).AsSpan().SequenceEqual(replacement))
                return null;

            // Local time, because the name is read by someone looking at their own clock — but
            // an invariant calendar, because a file name is not a place to render dates the
            // reader's way: a culture whose default calendar counts years from a different era
            // would stamp a different year for the same instant, and the copies would neither
            // sort together nor match a script written against them.
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (int n = 0; n < MaxPerSecond; n++)
            {
                var candidate = n == 0
                    ? $"{path}.{stamp}.bak"
                    : $"{path}.{stamp}-{n}.bak";
                if (File.Exists(candidate)) continue;
                // Copy, never move: the file has to stay put until the new contents replace it,
                // or an interrupted save would leave nothing at the name the user is editing.
                // No overwrite flag, so losing a race for the name raises rather than clobbering
                // the very thing this is protecting.
                File.Copy(path, candidate);
                return candidate;
            }

            throw new IOException($"cannot back up '{Path.GetFileName(path)}': too many backups from this second");
        }
    }
}
