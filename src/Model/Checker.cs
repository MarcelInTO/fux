using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace XmlNotepad
{
    public enum Severity { None, Hint, Warning, Error }

    /// <summary>
    /// One <c>xsi:schemaLocation</c> / <c>xsi:noNamespaceSchemaLocation</c> entry, as written.
    /// </summary>
    /// <remarks>
    /// Enumerated separately from loading them so that a caller who wants to resolve the
    /// document's schemas somewhere other than inside a validation pass — off the UI thread,
    /// say — reads the hints the same way <see cref="Checker"/> does rather than reimplementing
    /// the parse and drifting from it.
    /// </remarks>
    public sealed class SchemaHint
    {
        /// <summary>The xsi:* attribute the hint was written on; the error's position.</summary>
        public XmlAttribute Context;
        /// <summary>Target namespace, or "" for noNamespaceSchemaLocation.</summary>
        public string Namespace;
        /// <summary>The location exactly as written — relative path or absolute URL.</summary>
        public string Location;
    }

    /// <summary>
    /// A schema hint the document declares that did not produce a usable schema.
    /// </summary>
    /// <remarks>
    /// The reason this is a record and not just the warning text: "nothing validated this
    /// document" and "this document is valid" have to be distinguishable by the caller, and a
    /// warning in a list of warnings is not (#36). Collected per validation pass, deduplicated
    /// by resolved URI — LoadSchemas reaches the same hint twice, once by URI and once by
    /// namespace.
    /// </remarks>
    public sealed class SchemaLoadFailure
    {
        /// <summary>The hint as written in the document.</summary>
        public string Location;
        /// <summary>What it resolved to, or the location itself when it would not resolve.</summary>
        public string ResolvedUri;
        /// <summary>Why it failed, in the terms the user should see.</summary>
        public string Message;
        /// <summary>Where the hint is, for the error pane's jump-to-node.</summary>
        public int Line, Col;

        /// <summary>
        /// The fetch was declined, not attempted: this thread may not block on the network
        /// and a background one is resolving it. Nothing is yet known about the URL, so this
        /// is neither a diagnostic nor grounds for telling the user the schema is broken.
        /// </summary>
        public bool Pending;
    }

    public abstract class ErrorHandler
    {
        public abstract void HandleError(Severity sev, string reason, string filename, int line, int col, object data);
    }

    public enum IntellisensePosition { OnNode, AfterNode, FirstChild }

    public class Checker : IDisposable
    {
        private XmlCache _cache;
        private XmlSchemaValidator _validator;
        private XmlSchemaInfo _info;
        private ErrorHandler _eh;
        private MyXmlNamespaceResolver _nsResolver;
        private Uri _baseUri;
        private Dictionary<XmlNode, XmlSchemaInfo> _typeInfo = new Dictionary<XmlNode, XmlSchemaInfo>();
        private XmlSchemaAttribute[] _expectedAttributes;
        private XmlSchemaParticle[] _expectedParticles;
        private XmlElement _node;
        private Hashtable _parents;
        private IntellisensePosition _position;
        private readonly List<SchemaLoadFailure> _schemaFailures = new List<SchemaLoadFailure>();

        internal const int SurHighStart = 0xd800;
        internal const int SurHighEnd = 0xdbff;
        internal const int SurLowStart = 0xdc00;
        internal const int SurLowEnd = 0xdfff;

        // Construct a checker for getting expected information about the given element.
        public Checker(XmlElement node, IntellisensePosition position)
        {
            this._node = node;
            this._position = position;
            _parents = new Hashtable();
            XmlNode p = node.ParentNode;
            while (p != null)
            {
                _parents[p] = p;
                p = p.ParentNode;
            }
        }

        public Checker(ErrorHandler eh)
        {
            this._eh = eh;
        }

        /// <summary>
        /// The document's schema hints that did not load, from the last validation pass.
        /// </summary>
        /// <remarks>
        /// Empty is the only thing that means "everything the document asked for was
        /// checked". A caller reporting "0 errors" without consulting this is reporting that
        /// an unvalidated document passed.
        /// </remarks>
        public IList<SchemaLoadFailure> SchemaFailures
        {
            get { return this._schemaFailures; }
        }

        public XmlSchemaAttribute[] GetExpectedAttributes()
        {
            return this._expectedAttributes;
        }

        public XmlSchemaParticle[] GetExpectedParticles()
        {
            return this._expectedParticles;
        }

        public void ValidateContext(XmlCache xcache)
        {
            this._cache = xcache;
            if (string.IsNullOrEmpty(_cache.FileName))
            {
                _baseUri = null;
            }
            else
            {
                _baseUri = new Uri(new Uri(xcache.FileName), new Uri(".", UriKind.Relative));
            }

            SchemaResolver resolver = xcache.SchemaResolver as SchemaResolver;
            resolver.Handler = OnValidationEvent;
            XmlDocument doc = xcache.Document;
            this._info = new XmlSchemaInfo();
            this._nsResolver = new MyXmlNamespaceResolver(doc.NameTable);
            XmlSchemaSet set = new XmlSchemaSet();
            set.XmlResolver = resolver;
            // Make sure the SchemaCache is up to date with document.
            SchemaCache sc = xcache.SchemaCache;
            foreach (XmlSchema s in doc.Schemas.Schemas())
            {
                sc.Add(s);
            }

            if (LoadSchemas(doc, set, resolver))
            {
                set.ValidationEventHandler += OnValidationEvent;
                set.Compile();
                set.ValidationEventHandler -= OnValidationEvent;
            }

            try
            {
                this._validator = new XmlSchemaValidator(doc.NameTable, set, _nsResolver,
                    XmlSchemaValidationFlags.AllowXmlAttributes |
                    XmlSchemaValidationFlags.ProcessIdentityConstraints |
                    XmlSchemaValidationFlags.ProcessInlineSchema);
            } 
            catch (Exception ex)
            {
                ReportError(Severity.Error, ex.Message, doc);
                this._validator = null;
            }

            if (this._validator != null)
            {
                this._validator.ValidationEventHandler += OnValidationEvent;
                this._validator.XmlResolver = resolver;
                this._validator.Initialize();

                this._nsResolver.Context = doc;
                if (doc.DocumentElement == null)
                {
                    GetExpectedRootElements(sc);
                }
                else
                {
                    ValidateContent(doc);
                }
                this._nsResolver.Context = doc;

                this._validator.EndValidation();
            }
        }

        private void GetExpectedRootElements(SchemaCache cache)
        {
            List<XmlSchemaParticle> expected = new List<XmlSchemaParticle>();
            try
            {
                foreach (XmlSchemaElement root in cache.GetPossibleTopLevelElements())
                {
                    expected.Add(root);
                }
            } 
            catch (Exception)
            {
                // ignore compile errors
                // todo: add them as task list errors?
            }
            this._expectedParticles = expected.ToArray();
        }

        public void Validate(XmlCache xcache)
        {
            this.ValidateContext(xcache);
            xcache.TypeInfoMap = _typeInfo; // save schema type information for intellisense.
        }

        public XmlSchemaInfo GetTypeInfo(XmlNode node)
        {
            if (node == null) return null;
            XmlSchemaInfo si;
            _typeInfo.TryGetValue(node, out si);
            return si;
        }

        bool LoadSchemas(XmlDocument doc, XmlSchemaSet set, SchemaResolver resolver)
        {
            XmlElement root = doc.DocumentElement;
            if (root != null)
            {
                // Give Xsi schemas highest priority.
                bool result = LoadXsiSchemas(doc, set, resolver);
                SchemaCache sc = this._cache.SchemaCache;
                foreach (string nsuri in this._cache.AllNamespaces)
                {
                    result |= LoadSchemasForNamespace(set, resolver, sc, nsuri, root);
                }
                result |= LoadSchemasForNamespace(set, resolver, sc, doc.DocumentElement.NamespaceURI, root);
            }
            // Make sure all the required includes or imports are there. 
            // This is making up for a possible bug in XmlSchemaSet where it
            // refuses to load an XmlSchema containing a DTD.  Our XmlSchemaResolver
            // doesn't have that problem.
            var visited = new HashSet<XmlSchema>();
            foreach (XmlSchema s in doc.Schemas.Schemas())
            {
                CopyImports(s, set, visited);
            }

            return true;
        }

        private void CopyImports(XmlSchema s, XmlSchemaSet set, HashSet<XmlSchema> visited)
        {
            visited.Add(s);
            set.Add(s);
            foreach (var o in s.Includes)
            {
                if (o is XmlSchemaInclude i && i.Schema != null && !visited.Contains(i.Schema))
                {
                    CopyImports(i.Schema, set, visited);
                }
                else if (o is XmlSchemaImport j)
                {
                    XmlSchema js = j.Schema;
                    if (js == null && !string.IsNullOrEmpty(j.Namespace))
                    {
                        js = this._cache.SchemaCache.FindSchemasByNamespace(j.Namespace)?.Schema;
                    }
                    if (js != null && !visited.Contains(js))
                    {
                        CopyImports(js, set, visited);
                    }
                }
            }
        }
             
        private bool LoadSchemasForNamespace(XmlSchemaSet set, SchemaResolver resolver, SchemaCache sc, string nsuri, XmlNode ctx)
        {
            bool result = false;
            if (set.Schemas(nsuri).Count == 0)
            {
                CacheEntry ce = sc.FindSchemasByNamespace(nsuri);
                while (ce != null)
                {
                    if (!ce.Disabled)
                    {
                        if (!ce.HasUpToDateSchema)
                        {
                            // delay loaded!
                            LoadSchema(set, resolver, ctx, nsuri, ce.Location.AbsoluteUri);
                        }
                        else
                        {
                            set.Add(ce.Schema);
                        }
                        result = true;
                    }
                    ce = ce.Next;
                }
            }
            return result;
        }

        /// <summary>
        /// The schema hints on a document's root element, in document order.
        /// </summary>
        /// <remarks>
        /// Public and static so that resolving a document's schemas outside a validation pass
        /// reads the hints through this, not through a second copy of the parse. Pair it with
        /// <see cref="ResolveSchemaLocation"/> to get the URI a load would actually go to.
        /// </remarks>
        public static IEnumerable<SchemaHint> GetSchemaHints(XmlDocument doc)
        {
            if (doc == null || doc.DocumentElement == null) yield break;
            foreach (XmlAttribute a in doc.DocumentElement.Attributes)
            {
                if (a.NamespaceURI != "http://www.w3.org/2001/XMLSchema-instance") continue;
                if (a.LocalName == "noNamespaceSchemaLocation")
                {
                    if (!string.IsNullOrEmpty(a.Value))
                    {
                        yield return new SchemaHint { Context = a, Namespace = "", Location = a.Value };
                    }
                }
                else if (a.LocalName == "schemaLocation")
                {
                    // Whitespace-separated namespace/location pairs. An odd trailing word is
                    // a namespace with no location and is skipped, as `i + 1 < n` says.
                    string[] words = a.Value.Split(new char[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0, n = words.Length; i + 1 < n; i++)
                    {
                        string nsuri = words[i];
                        string location = words[++i];
                        yield return new SchemaHint { Context = a, Namespace = nsuri, Location = location };
                    }
                }
            }
        }

        /// <summary>
        /// Where a hint's location resolves to: against the context node's own base URI if it
        /// has one, else against <paramref name="fallbackBase"/> (the document's directory).
        /// </summary>
        public static Uri ResolveSchemaLocation(XmlNode ctx, Uri fallbackBase, string location)
        {
            Uri baseUri = fallbackBase;
            if (ctx != null && !string.IsNullOrEmpty(ctx.BaseURI))
            {
                baseUri = new Uri(ctx.BaseURI);
            }
            return baseUri != null
                ? new Uri(baseUri, location)
                : new Uri(location, UriKind.RelativeOrAbsolute);
        }

        bool LoadXsiSchemas(XmlDocument doc, XmlSchemaSet set, SchemaResolver resolver)
        {
            bool result = false;
            foreach (SchemaHint hint in GetSchemaHints(doc))
            {
                result |= LoadSchema(set, resolver, hint.Context, hint.Namespace, hint.Location);
            }
            return result;
        }

        bool LoadSchema(XmlSchemaSet set, SchemaResolver resolver, XmlNode ctx, string nsuri, string filename)
        {
            Uri resolved = null;
            try
            {
                if (set.Contains(nsuri))
                {
                    return false;
                }
                resolved = ResolveSchemaLocation(ctx, this._baseUri, filename);
                XmlSchema s = null;
                SchemaCache sc = this._cache.SchemaCache;
                var ce = sc.FindSchemaByUri(resolved.AbsoluteUri);
                if (ce != null && ce.Schema != null)
                {
                    s = ce.Schema;
                }
                else
                {
                    s = resolver.GetEntity(resolved, "", typeof(XmlSchema)) as XmlSchema;
                }
                if ((s.TargetNamespace + "") != (nsuri + ""))
                {
                    ReportError(Severity.Warning, Strings.TNSMismatch, ctx);
                }
                else if (!set.Contains(s))
                {
                    set.Add(s);
                    return true;
                }
            }
            catch (Exception e) when (SchemaOfflineException.IsIn(e))
            {
                // Not a failure: this thread may not block on the network and something else
                // is fetching it. Recorded so the caller can say "loading" rather than "0
                // errors", but deliberately not reported as a diagnostic — the fetch has not
                // happened yet, so there is nothing to tell the user about the schema.
                RecordSchemaFailure(ctx, filename, resolved, null, true);
            }
            catch (Exception e)
            {
                string reason = Unwrap(e).Message;
                ReportError(Severity.Warning, string.Format(Strings.SchemaLoadError, filename, reason), ctx);
                RecordSchemaFailure(ctx, filename, resolved, reason, false);
            }
            return false;
        }

        // A hint that produced no schema. Kept alongside the warning rather than instead of it:
        // the warning is what the user reads, this is what the caller can act on — a count of
        // warnings cannot answer "was this document actually checked against anything?".
        //
        // Deduplicated by resolved URI because LoadSchemas reaches the same hint twice, once
        // through LoadXsiSchemas and once through LoadSchemasForNamespace; a pending record is
        // upgraded to a real failure if the second attempt gets a real answer.
        void RecordSchemaFailure(XmlNode ctx, string location, Uri resolved, string message, bool pending)
        {
            string uri = resolved == null ? location : resolved.AbsoluteUri;
            foreach (var existing in this._schemaFailures)
            {
                if (existing.ResolvedUri == uri)
                {
                    if (existing.Pending && !pending)
                    {
                        existing.Pending = false;
                        existing.Message = message;
                    }
                    return;
                }
            }
            int line = 0, col = 0;
            LineInfo li = _cache == null ? null : _cache.GetLineInfo(ctx);
            if (li != null)
            {
                line = li.LineNumber;
                col = li.LinePosition;
            }
            this._schemaFailures.Add(new SchemaLoadFailure
            {
                Location = location,
                ResolvedUri = uri,
                Message = message,
                Pending = pending,
                Line = line,
                Col = col,
            });
        }

        // What HttpClient actually said, not AggregateException's "One or more errors occurred."
        static Exception Unwrap(Exception e)
        {
            while (e is AggregateException agg && agg.InnerExceptions.Count == 1)
            {
                e = agg.InnerExceptions[0];
            }
            return e;
        }

        void ReportError(Severity sev, string msg, XmlNode ctx)
        {
            if (_eh == null) return;
            int line = 0, col = 0;
            string filename = _cache.FileName;
            LineInfo li = _cache.GetLineInfo(ctx);
            if (li != null)
            {
                line = li.LineNumber;
                col = li.LinePosition;
                filename = GetRelative(li.BaseUri);
            }
            _eh.HandleError(sev, msg, filename, line, col, ctx);
        }

        void ValidateContent(XmlNode container)
        {
            foreach (XmlNode n in container.ChildNodes)
            {
                // If we are validating up to a given node for intellisense info, then
                // we can prune out any nodes that are not connected to the same parent chain.
                if (_parents == null || _parents.Contains(n.ParentNode))
                {
                    ValidateNode(n);
                }
                if (n == this._node)
                {
                    break; // we're done!
                }
            }
        }

        void ValidateNode(XmlNode node)
        {
            XmlElement e = node as XmlElement;
            if (e != null)
            {
                ValidateElement(e);
                return;
            }
            XmlText t = node as XmlText;
            if (t != null)
            {
                ValidateText(t);
                return;
            }
            XmlCDataSection cd = node as XmlCDataSection;
            if (cd != null)
            {
                ValidateText(cd);
                return;
            }
            XmlWhitespace w = node as XmlWhitespace;
            if (w != null)
            {
                ValidateWhitespace(w);
                return;
            }
        }

        XmlSchemaInfo GetInfo()
        {
            XmlSchemaInfo i = this._info;
            XmlSchemaInfo copy = new XmlSchemaInfo();
            copy.ContentType = i.ContentType;            
            copy.IsDefault = i.IsDefault;
            copy.IsNil = i.IsNil;
            copy.MemberType = i.MemberType;
            copy.SchemaAttribute = i.SchemaAttribute;
            copy.SchemaElement = i.SchemaElement;
            copy.SchemaType = i.SchemaType;
            copy.Validity = i.Validity;
            return copy;
        }

        void ValidateElement(XmlElement e)
        {
            this._nsResolver.Context = e;
            if (this._node == e && _position == IntellisensePosition.OnNode)
            {
                this._expectedParticles = _validator.GetExpectedParticles();
            }
            string xsiType = null;
            string xsiNil = null;
            foreach (XmlAttribute a in e.Attributes)
            {
                if (XmlHelpers.IsXsiAttribute(a))
                {
                    string name = a.LocalName;
                    if (name == "type")
                    {
                        xsiType = a.Value;
                    }
                    else if (name == "nil")
                    {
                        xsiNil = a.Value;
                    }
                }
            }
            _validator.ValidateElement(e.LocalName, e.NamespaceURI, this._info, xsiType, xsiNil, null, null);
            if (this._info.SchemaType != null)
            {
                _typeInfo[e] = GetInfo();
            }
            foreach (XmlAttribute a in e.Attributes)
            {
                if (!XmlHelpers.IsXmlnsNode(a))
                {
                    ValidateAttribute(a);
                }
            }
            if (this._node == e)
            {
                this._expectedAttributes = _validator.GetExpectedAttributes();
            }
            this._nsResolver.Context = e;
            _validator.ValidateEndOfAttributes(this._info);
            if (this._node == e && _position == IntellisensePosition.FirstChild)
            {
                this._expectedParticles = _validator.GetExpectedParticles();
            }
            if (this._node != e)
            {
                ValidateContent(e);
            }
            this._nsResolver.Context = e;
            _validator.ValidateEndElement(this._info);
            if (this._node == e && _position == IntellisensePosition.AfterNode)
            {
                this._expectedParticles = _validator.GetExpectedParticles();
            }

        }
        void ValidateText(XmlCharacterData text)
        {
            this._nsResolver.Context = text;
            CheckCharacters();
            _validator.ValidateText(new XmlValueGetter(GetText));
        }

        /// <summary>
        /// We turned off Character checking on the XmlReader so we could load more
        /// XML documents, so here we implement that part of the W3C spec:
        /// [2]    Char    ::=    #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | 
        ///                       [#x10000-#x10FFFF] 
        /// </summary>
        /// <param name="text"></param>
        void CheckCharacters()
        {
            if (_eh == null) return;

            XmlNode node = this._nsResolver.Context;
            if (node == null) return;
            string text = node.InnerText;
            if (text == null) return;
            XmlNode ctx = node.ParentNode;
            if (ctx == null) ctx = node;

            for (int i = 0, n = text.Length; i < n; i++)
            {
                char ch = text[i];
                if ((ch < 0x20 && ch != 0x9 && ch != 0xa && ch != 0xd) || ch > 0xfffe)
                {
                    ReportError(Severity.Error, string.Format(Strings.InvalidCharacter, ((int)ch).ToString(), i), ctx);
                }
                else if (ch >= SurHighStart && ch <= SurHighEnd)
                {
                    if (i + 1 < n)
                    {
                        char nc = text[i + 1];
                        if (nc < SurLowStart || nc > SurLowEnd)
                        {
                            ReportError(Severity.Error, string.Format(Strings.IllegalSurrogatePair, Convert.ToInt32(ch).ToString("x", CultureInfo.CurrentUICulture), Convert.ToInt32(nc).ToString("x", CultureInfo.CurrentUICulture), i), ctx);
                        }
                        else
                        {
                            i++;
                        }
                    }
                }
                else if (ch >= 0xd800 && ch < 0xe000)
                {
                    ReportError(Severity.Error, string.Format(Strings.InvalidCharacter, ((int)ch).ToString(), i), ctx);
                }
            }
        }

        object GetText()
        {
            return this._nsResolver.Context.InnerText; ;
        }

        void ValidateWhitespace(XmlWhitespace w)
        {
            this._nsResolver.Context = w;
            _validator.ValidateWhitespace(w.InnerText);
        }

        void ValidateAttribute(XmlAttribute a)
        {
            this._nsResolver.Context = a;
            CheckCharacters();
            _validator.ValidateAttribute(a.LocalName, a.NamespaceURI, a.Value, this._info);
            _typeInfo[a] = GetInfo();
        }

        void OnValidationEvent(object sender, ValidationEventArgs e)
        {
            if (_eh != null)
            {
                string filename = _cache.FileName;
                int line = 0;
                int col = 0;
                XmlNode node = this._nsResolver.Context;
                Severity sev = e.Severity == XmlSeverityType.Error ? Severity.Error : Severity.Warning;
                XmlSchemaException se = e.Exception;
                if (se != null && !string.IsNullOrEmpty(se.SourceUri))
                {
                    filename = GetRelative(se.SourceUri);
                    line = se.LineNumber;
                    col = se.LinePosition;
                }
                else
                {
                    LineInfo li = _cache.GetLineInfo(node);
                    if (li != null)
                    {
                        line = li.LineNumber;
                        col = li.LinePosition;
                        filename = GetRelative(li.BaseUri);
                    }
                }
                _eh.HandleError(sev, e.Message, filename, line, col, node);
                Exception inner = e.Exception.InnerException;
                while (inner != null)
                {
                    _eh.HandleError(sev, inner.Message, filename, line, col, node);
                    inner = inner.InnerException;
                }
            }
        }

        string GetRelative(string s)
        {
            if (_baseUri == null) return s;
            if (string.IsNullOrEmpty(s)) return s;
            Uri uri = new Uri(s);
            Uri rel = this._baseUri.MakeRelativeUri(uri);
            return rel.GetComponents(UriComponents.SerializationInfoString, UriFormat.SafeUnescaped);
        }

        public void Dispose()
        {
            if (_validator != null)
            {
                this._validator.ValidationEventHandler -= OnValidationEvent;
            }
        }

    }

}
