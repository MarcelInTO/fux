using System;
using Terminal.Gui.Drivers;

namespace Fux
{
    /// <summary>
    /// The name of the window fux is running in.
    ///
    /// Left alone, fux is a bad citizen of the terminal: the driver blanks the title during
    /// Init (an empty OSC 0, verified against 2.4.17 under a PTY) and nothing ever puts
    /// anything back, so the one field the foreground program owns sits empty. What the user
    /// then reads in the title bar is whatever the terminal can infer — the cwd, the command
    /// line, the window size — mixed with whatever the program that held the tab before fux
    /// happened to leave there. The editor actually drawing the screen says nothing about
    /// itself or the document it has open.
    ///
    /// So: take the title for the session, keep it in step with the document, and hand it
    /// back on the way out. The handback goes through the terminal's title stack (CSI 22/23
    /// t) because there is no reliable way to read the current title and put it back
    /// verbatim; a terminal without the stack ignores both and simply keeps fux's title,
    /// which is no worse than today.
    ///
    /// Composition is separate from the write, so --drill can assert on the string without a
    /// terminal to write it to — the same split the clipboard uses (Program.CopyText).
    /// </summary>
    internal static class TerminalTitle
    {
        // Only the interactive session owns the title. --dump/--validate never build a UI,
        // and --drill deliberately does not claim it: a self-test that renamed the window
        // and left it renamed would be the very rudeness this class exists to fix.
        private static bool _owned;

        /// <summary>
        /// What the window should be called. The document comes first because a tab title is
        /// truncated from the right, and the file name is the half worth keeping.
        /// </summary>
        internal static string Compose(string fileName, bool dirty)
        {
            if (string.IsNullOrEmpty(fileName)) return "fux";
            var name = Sanitize(System.IO.Path.GetFileName(fileName));
            if (name.Length == 0) return "fux";
            return dirty ? name + " * — fux" : name + " — fux";
        }

        /// <summary>
        /// A file name is untrusted text as far as the terminal is concerned: it may legally
        /// hold anything but '/' and NUL, and an ESC or BEL in one would terminate the OSC
        /// string early and leave the rest of the name running as escape sequences. Drop the
        /// C0 and C1 controls rather than the whole name — a control character is a reason to
        /// clean the title, not to give up on having one.
        /// </summary>
        private static string Sanitize(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
                if (!char.IsControl(ch)) sb.Append(ch);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Claim the title for this session, saving the user's on the terminal's stack first.
        /// Must run before the driver initialises, which is where the blanking OSC 0 is
        /// written — after that there is nothing left to save.
        /// </summary>
        internal static void Push()
        {
            _owned = true;
            Write("\x1b[22;0t"); // XTPUSHTITLE: icon and window title
        }

        /// <summary>Give the user's title back. Safe to call twice, and a no-op if never claimed.</summary>
        internal static void Pop()
        {
            if (!_owned) return;
            _owned = false;
            Write("\x1b[23;0t"); // XTPOPTITLE
        }

        /// <summary>Name the window for the document as it now stands.</summary>
        internal static void Set(string fileName, bool dirty)
        {
            if (!_owned) return;
            Write(EscSeqUtils.OSC_SetWindowTitle(Compose(fileName, dirty), 0));
        }

        // Straight to stdout, past the driver's screen buffer — a title is not a cell. This is
        // only safe because every caller is on the main loop thread, where the driver is not
        // midway through flushing a frame; do not call it from a background thread.
        private static void Write(string s)
        {
            try
            {
                Console.Out.Write(s);
                Console.Out.Flush();
            }
            catch (System.IO.IOException)
            {
                // A closed or redirected stdout is not a reason to fail an edit.
            }
        }
    }
}
