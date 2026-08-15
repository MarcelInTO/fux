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
                // True of a symlink whose target is missing, too, so a name someone else planted
                // is stepped over rather than followed.
                if (File.Exists(candidate)) continue;
                CopyTo(path, candidate);
                return candidate;
            }

            throw new IOException($"cannot back up '{Path.GetFileName(path)}': too many backups from this second");
        }

        /// <summary>
        /// The copy itself: read the file, write a new one, leaving the original in place until
        /// the save replaces it — never a move, or an interrupted save would leave nothing at
        /// the name the user is editing.
        /// </summary>
        private static void CopyTo(string path, string candidate)
        {
            // Not File.Copy, which follows a symlink at the destination when the link's target
            // does not exist yet (measured on .NET 10; it refuses one whose target does). The
            // backup name is derived from the document's, so in a directory someone else can
            // write to it is predictable a second at a time — enough to plant a link and have
            // the document written wherever it points. The Exists check above already steps over
            // a planted name; this closes the window between that check and the write.
            //
            // FileMode.CreateNew is O_CREAT|O_EXCL: it refuses any symlink at the path, dangling
            // or not, in the same syscall that creates the file. The permission bits then have
            // to be carried over by hand — File.Copy did that for us, and a document its owner
            // keeps to themselves must not acquire a copy the rest of the machine can read.
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = File.GetUnixFileMode(path);

            using var from = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var to = new FileStream(candidate, options);
            from.CopyTo(to); // streamed, so a large document is not held in memory a second time
        }
    }
}
