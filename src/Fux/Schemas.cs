using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Schema;
using Terminal.Gui.App;
using XmlNotepad;

namespace Fux
{
    /// <summary>
    /// What fux does about a document's schemas that the engine does not: resolve the remote
    /// ones off the UI thread, and describe what came back in terms a person can act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine resolves a schema lazily, from inside the validation pass, on whatever
    /// thread is validating. In the editor that is the UI thread and validation runs after
    /// every command, so a hint pointing at a host that swallows packets — VPN down, captive
    /// portal, corporate firewall — froze fux for the length of the fetch, per keystroke
    /// (#35). Remote schemas are worth using: one published once and referenced by every
    /// document beats a copy sitting beside each file. Being transiently unable to reach one
    /// is therefore a routine state, not an exotic one, and it has to be survivable.
    /// </para>
    /// <para>
    /// So: <see cref="XmlProxyResolver.OfflineThread"/> is set on the UI thread for the life
    /// of the process, and the UI thread sees only what is already in the schema cache.
    /// Warming that cache is this class's job, on a background thread, once per document.
    /// </para>
    /// <para>
    /// <b>The concurrency rule, and it is the whole safety argument:</b> a warm run and a
    /// validation pass never overlap. Both write to the shared <see cref="SchemaCache"/> —
    /// plain dictionaries, no locking — so overlapping them would be a data race. They are
    /// kept apart by construction rather than by a lock: <c>Program.Revalidate</c> does
    /// nothing at all while <c>Ui.SchemaPending</c> is set, and the flag is cleared on the UI
    /// thread by the completion callback below, after the background thread is finished with
    /// the cache. Nothing else may touch the schema cache off the UI thread.
    /// </para>
    /// </remarks>
    internal static class Schemas
    {
        /// <summary>
        /// The http/https schema hints on the document's root element, resolved and deduped.
        /// </summary>
        /// <remarks>
        /// Only the remote ones: a sibling <c>.xsd</c> is a file read, and moving that off the
        /// UI thread would buy a document-open race in exchange for nothing measurable.
        /// </remarks>
        internal static List<Uri> RemoteHints(XmlCache model)
        {
            var uris = new List<Uri>();
            var doc = model?.Document;
            if (doc == null) return uris;

            // The document's directory, exactly as Checker.ValidateContext derives it — a
            // relative hint has to resolve to the same place here as it will there, or the
            // warm run would populate the cache under a URI the validation pass never asks for.
            Uri baseUri = null;
            if (!string.IsNullOrEmpty(model.FileName))
            {
                baseUri = new Uri(new Uri(model.FileName), new Uri(".", UriKind.Relative));
            }

            foreach (SchemaHint hint in Checker.GetSchemaHints(doc))
            {
                Uri resolved;
                try
                {
                    resolved = Checker.ResolveSchemaLocation(hint.Context, baseUri, hint.Location);
                }
                catch (UriFormatException)
                {
                    continue; // a malformed hint; the validation pass is what reports it
                }
                if (!resolved.IsAbsoluteUri) continue;
                if (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) continue;
                if (!uris.Contains(resolved)) uris.Add(resolved);
            }
            return uris;
        }

        /// <summary>
        /// How many schema hints the document declares, remote and local alike.
        /// </summary>
        /// <remarks>
        /// Only ever compared against the number that failed, to tell "some of this document's
        /// schemas are missing" from "none of them loaded, so nothing checked it".
        /// </remarks>
        internal static int HintCount(XmlCache model)
        {
            int n = 0;
            foreach (SchemaHint unused in Checker.GetSchemaHints(model?.Document)) n++;
            return n;
        }

        /// <summary>
        /// Fetch <paramref name="uris"/> into the shared schema cache on a background thread,
        /// then run <paramref name="onDone"/> on the UI thread. Never throws to the caller.
        /// </summary>
        /// <remarks>
        /// Results are not returned: success lands in the schema cache and failure in the
        /// resolver's session memory, which is where the next validation pass looks anyway.
        /// The callback's job is only to lift the pending flag and revalidate.
        /// The callback never runs inline, even when there is nothing to fetch — see below.
        /// </remarks>
        internal static void Prefetch(XmlCache model, IList<Uri> uris, IApplication app, Action onDone)
        {
            var resolver = model?.SchemaResolver as SchemaResolver;
            if (resolver == null || uris == null || uris.Count == 0)
            {
                // Deferred to the next loop iteration rather than called here. The callback
                // can open a dialog, and one of that dialog's buttons is Retry, which comes
                // straight back through this method: called inline, a run of retries would
                // nest one message box inside the last and grow the stack until the user
                // stopped pressing it. A timeout lets each dialog unwind before the next.
                // (IApplication.Invoke would not do — from the main thread it runs inline.)
                app.AddTimeout(TimeSpan.Zero, () => { onDone(); return false; });
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    Warm(model, resolver, uris);
                }
                catch
                {
                    // Every reason a schema did not load is already recorded — in the schema
                    // cache as an absence, in the resolver as a remembered failure. Nothing
                    // here is worth killing a background thread over, and the validation pass
                    // the callback triggers is what reports it.
                }
                try
                {
                    app.Invoke(onDone);
                }
                catch
                {
                    // The app can be torn down while a fetch is in flight — quit during the
                    // five seconds this is allowed to take. There is then no UI to update.
                }
            });
        }

        // Resolve each hint and compile it, on the calling (background) thread.
        //
        // Compile, not just resolve: <xs:include> and <xs:import> inside a fetched schema are
        // resolved by XmlSchemaSet at compile time, through this same resolver. Skipping it
        // would leave those nested fetches to be discovered by the validation pass on the UI
        // thread, which may not fetch — so a schema whose includes are remote would never
        // resolve at all, however many times it was retried.
        private static void Warm(XmlCache model, SchemaResolver resolver, IList<Uri> uris)
        {
            // Neither the reader nor the compiler may raise anything into fux's error pane
            // from here: this run's diagnostics are thrown away, and the validation pass that
            // follows produces the real ones against the real document. Without a handler the
            // engine throws on the first schema warning instead.
            resolver.Handler = (s, e) => { };

            var set = new XmlSchemaSet { XmlResolver = resolver };
            set.ValidationEventHandler += (s, e) => { };
            foreach (Uri uri in uris)
            {
                try
                {
                    if (resolver.GetEntity(uri, "", typeof(XmlSchema)) is XmlSchema schema)
                    {
                        set.Add(schema);
                    }
                }
                catch
                {
                    // Recorded by the resolver; the next hint still deserves its chance.
                }
            }
            try { set.Compile(); } catch { }
        }

        /// <summary>
        /// A stable identity for a set of load failures, so that a prompt can fire once per
        /// condition instead of once per validation — and again when the condition changes.
        /// </summary>
        internal static string FailureKey(IList<SchemaLoadFailure> failures)
        {
            if (failures == null || failures.Count == 0) return "";
            var uris = new List<string>();
            foreach (var f in failures)
            {
                if (!f.Pending) uris.Add(f.ResolvedUri + "|" + f.Message);
            }
            uris.Sort(StringComparer.Ordinal);
            return string.Join("\n", uris.ToArray());
        }

        /// <summary>
        /// The failures worth telling the user about: everything that actually failed. A
        /// pending record is a fetch in flight, not an answer, and never reaches the user.
        /// </summary>
        internal static List<SchemaLoadFailure> Settled(IList<SchemaLoadFailure> failures)
        {
            var settled = new List<SchemaLoadFailure>();
            if (failures != null)
            {
                foreach (var f in failures) if (!f.Pending) settled.Add(f);
            }
            return settled;
        }

        /// <summary>
        /// The body of the "schema unavailable" prompt: what could not be loaded and why.
        /// </summary>
        internal static string Describe(IList<SchemaLoadFailure> failures)
        {
            var sb = new StringBuilder();
            sb.Append(failures.Count == 1
                ? "This document declares a schema that could not be loaded,\nso nothing is validating it.\n"
                : $"This document declares {failures.Count} schemas that could not be\nloaded, so nothing is validating it.\n");
            foreach (var f in failures)
            {
                sb.Append('\n').Append(Ellipsize(f.Location, 60)).Append('\n');
                sb.Append("  ").Append(Ellipsize(f.Message, 60)).Append('\n');
            }
            return sb.ToString();
        }

        // A URL is exactly the kind of string that is both essential and arbitrarily long, and
        // MessageBox does not wrap: an untrimmed one silently widens the dialog past the
        // terminal and takes its buttons off-screen with it. Elide the middle — the host at the
        // front and the file name at the end are the two halves that identify it.
        internal static string Ellipsize(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.Length <= max) return s;
            int head = (max - 3) / 2;
            return s.Substring(0, head) + "..." + s.Substring(s.Length - (max - 3 - head));
        }
    }
}
