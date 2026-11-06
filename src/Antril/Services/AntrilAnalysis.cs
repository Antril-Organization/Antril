using System.Diagnostics;
using Antril.Binding;
using Antril.Syntax;
using Larva.Hir.Services;
using H = Larva.Hir;

namespace Antril.Services
{
    /// <summary>
    /// The libraries a set of Antril documents is analyzed against (System.Core and others, as HIR), and the editor
    /// features over them: analyze a document, then ask <see cref="Service"/> for completions, hovers, definitions.
    /// </summary>
    public sealed class AntrilWorkspace(IReadOnlyList<H.HirAssembly> references)
    {
        public IReadOnlyList<H.HirAssembly> References { get; } = references;

        /// <summary>The references built for another GC (their own domain): using their objects is reported (AN1079).</summary>
        public HashSet<string> ExternalAssemblies { get; } = new(StringComparer.Ordinal);

        public LanguageService Service { get; } = new(AntrilView.Instance);

        /// <summary>What the project sets (its default access, whether borrows are on).</summary>
        public AntrilOptions Options { get; init; } = AntrilOptions.Default;

        /// <summary>
        /// Parses and binds <paramref name="document"/> (with <paramref name="others"/>, the rest of its program) as it is,
        /// errors and all, recording what each part of it is.
        /// </summary>
        public AntrilAnalysis Analyze(SourceText document, IReadOnlyList<SourceText>? others = null, string assemblyName = "Program")
        {
            var watch = Stopwatch.StartNew();
            var sources = (others ?? []).Where(o => o.Path != document.Path).Prepend(document).ToArray();
            var diagnostics = new DiagnosticBag();
            var bags = new DiagnosticBag[sources.Length];
            var (units, table) = Parser.ParseAll(sources, bags, References, diagnostics, parallel: false);
            foreach (var bag in bags)
                diagnostics.AddRange(bag);
            var recorder = new BindingRecorder();
            var binder = new Binder(assemblyName, Variadic.Expand(units, diagnostics, References, table), diagnostics, parallel: true, References) { Recorder = recorder, Operators = table, Options = Options };
            binder.ExternalAssemblies.UnionWith(ExternalAssemblies);
            var assembly = binder.Bind([]);
            var catalog = HirCatalog.Create([assembly, .. References]);
            return new AntrilAnalysis(document, units[0], catalog, catalog.Assemblies[0], recorder, diagnostics, watch.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>One Antril document analyzed: what is at each position, for <see cref="LanguageService"/>.</summary>
    public sealed class AntrilAnalysis : IDocumentAnalysis
    {
        private readonly SourceText source;
        private readonly CompilationUnit unit;
        private readonly CatalogAssembly program;
        private readonly List<RecordedSpan> spans;
        private readonly List<RecordedLocal> locals;
        private readonly List<RecordedBody> bodies;
        private readonly List<RecordedCall> calls;
        private readonly DiagnosticBag diagnostics;

        internal AntrilAnalysis(SourceText source, CompilationUnit unit, HirCatalog catalog, CatalogAssembly program, BindingRecorder recorder,
            DiagnosticBag diagnostics, double milliseconds)
        {
            this.source = source;
            this.unit = unit;
            Catalog = catalog;
            this.program = program;
            spans = [.. recorder.Spans.Where(s => s.Source == source)];
            locals = [.. recorder.Locals.Where(l => l.Source == source)];
            bodies = [.. recorder.Bodies.Where(b => b.Source == source)];
            calls = [.. recorder.Calls.Where(c => c.Source == source)];
            this.diagnostics = diagnostics;
            Milliseconds = milliseconds;
        }

        public HirCatalog Catalog { get; }

        /// <summary>How long parsing, binding and making the catalog took.</summary>
        public double Milliseconds { get; }

        public string Text => source.Text;

        /// <summary>Where an `import` goes: the start of the line after the last one (the start of the file without any).</summary>
        public int ImportOffset
        {
            get
            {
                if (unit.Imports.Length == 0)
                    return 0;
                int end = unit.Imports.Max(i => i.Span.Start + i.Span.Length);
                int newline = source.Text.IndexOf('\n', end);
                return newline < 0 ? source.Text.Length : newline + 1;
            }
        }

        /// <summary>Whether the document imports anything yet (a first import is followed by a blank line).</summary>
        public bool HasImports => unit.Imports.Length > 0;

        private H.HirType Global(H.HirType type) => Catalog.Globalize(type, program);

        private SourceSpot Spot(TextSpan span)
        {
            var (line, column) = source.LineColumn(span.Start);
            return new SourceSpot(source.Path, line, column);
        }

        // ── Where a position is ────────────────────────────

        private RecordedBody? BodyAt(int offset)
        {
            RecordedBody? best = null;
            foreach (var body in bodies)
                if (body.Span.Start <= offset && offset <= body.Span.End && (best is null || body.Span.Length < best.Span.Length))
                    best = body;
            return best;
        }

        /// <summary>The namespace and type declarations around a position, outermost first.</summary>
        private (string Namespace, TypeDecl? Type) DeclarationsAt(int offset)
        {
            string ns = "";
            TypeDecl? type = null;
            var members = unit.Members;
            bool found = true;
            while (found)
            {
                found = false;
                foreach (var member in members)
                {
                    if (member.Span.Start > offset || offset > member.Span.End)
                        continue;
                    if (member is NamespaceDecl n)
                    {
                        ns = ns.Length == 0 ? n.Name.ToString() : $"{ns}.{n.Name}";
                        members = n.Members;
                        found = true;
                    }
                    else if (member is TypeDecl t)
                    {
                        type = t;
                        members = t.Members;
                        found = true;
                    }
                    break;
                }
            }
            return (ns, type);
        }

        private CatalogType? TypeAt(int offset)
        {
            if (BodyAt(offset) is { } body)
                return Catalog.Resolve(body.Method.Owner.Token, program);
            var (ns, type) = DeclarationsAt(offset);
            return type is null ? Catalog.Find(program.Name, ns, "$Module", 0) : Catalog.Find(program.Name, ns, type.Name, type.Generics.Length);
        }

        /// <summary>The namespaces whose names a position sees: its own and its parents, and the file's imports.</summary>
        private List<string> VisibleNamespaces(int offset)
        {
            var (ns, _) = DeclarationsAt(offset);
            var visible = new List<string>();
            for (string n = ns; n.Length > 0; n = n.Contains('.') ? n[..n.LastIndexOf('.')] : "")
                visible.Add(n);
            visible.AddRange(unit.Imports.Where(i => i.Alias is null && i.Picked.Length == 0).Select(i => i.Name.ToString()));
            return visible;
        }

        /// <summary>
        /// A name the file's imports give (<c>import D as X</c>: X is D; <c>import A from D as E</c>: E is D.A), as the full
        /// name it stands for; null when it is neither.
        /// </summary>
        private string? ImportedName(string name)
        {
            foreach (var import in unit.Imports)
            {
                if (import.Alias == name)
                    return import.Name.ToString();
                foreach (var picked in import.Picked)
                {
                    if (picked.Visible == name)
                        return $"{import.Name}.{picked.Name}";
                }
            }
            return null;
        }

        /// <summary>The names the file's imports give, for completion: namespaces' aliases, and the names taken (with what they are).</summary>
        private IEnumerable<CompletionItem> ImportedItems()
        {
            foreach (var import in unit.Imports)
            {
                if (import.Alias is { } alias)
                    yield return new CompletionItem(alias, CompletionKind.Namespace, $"namespace {import.Name}");
                foreach (var picked in import.Picked)
                {
                    string full = $"{import.Name}.{picked.Name}";
                    if (Catalog.IsNamespace(full))
                        yield return new CompletionItem(picked.Visible, CompletionKind.Namespace, $"namespace {full}");
                    else if (Catalog.Find(full) is { } type)
                        yield return new CompletionItem(picked.Visible, LanguageService.Kind(type), AntrilView.Instance.Header(type), type.Doc);
                    else if (Catalog.Find(import.Name.ToString(), "$Module", 0)?.Declared.FirstOrDefault(m => m.Name == picked.Name) is { } member)
                        yield return new CompletionItem(picked.Visible, LanguageService.Kind(member), AntrilView.Instance.Signature(member), member.Doc);
                }
            }
        }

        private List<RecordedLocal> LocalsAt(int offset)
        {
            var visible = new Dictionary<string, RecordedLocal>(StringComparer.Ordinal);
            foreach (var local in locals)
            {
                if (local.Scope.Start <= offset && offset <= local.Scope.End && (local.Parameter || local.Declared.Start < offset))
                {
                    // The innermost declaration of a name wins.
                    if (!visible.TryGetValue(local.Name, out var other) || local.Scope.Length <= other.Scope.Length)
                        visible[local.Name] = local;
                }
            }
            return [.. visible.Values];
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Whether a position is in a comment or inside a string or character literal.</summary>
        private bool InCommentOrText(int offset)
        {
            string text = source.Text;
            int lineStart = offset;
            while (lineStart > 0 && text[lineStart - 1] != '\n')
                lineStart--;
            bool inString = false, inChar = false;
            for (int i = lineStart; i < offset; i++)
            {
                char c = text[i];
                if (inString || inChar)
                {
                    if (c == '\\')
                        i++;
                    else if (inString && c == '"')
                        inString = false;
                    else if (inChar && c == '\'')
                        inChar = false;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                    return true;
                if (c == '"')
                    inString = true;
                else if (c == '\'')
                    inChar = true;
            }
            if (inString || inChar)
                return true;
            // A block comment open before the position.
            int open = text.LastIndexOf("/*", Math.Max(0, offset - 1), StringComparison.Ordinal);
            return open >= 0 && text.IndexOf("*/", open, StringComparison.Ordinal) is var close && (close < 0 || close >= offset);
        }

        // ── Completion ─────────────────────────────────────

        public CompletionRequest? CompletionAt(int offset)
        {
            string text = source.Text;
            offset = Math.Clamp(offset, 0, text.Length);
            if (InCommentOrText(offset))
                return new CompletionRequest(new CompletionContext.Nothing(), "", offset, 0);
            int start = offset;
            while (start > 0 && IsWord(text[start - 1]))
                start--;
            string prefix = text[start..offset];
            var from = TypeAt(offset);
            // `import A.B.`: namespaces.
            int lineStart = start;
            while (lineStart > 0 && text[lineStart - 1] != '\n')
                lineStart--;
            string line = text[lineStart..start].TrimStart();
            if (line.StartsWith("import ", StringComparison.Ordinal) || line.StartsWith("into namespace ", StringComparison.Ordinal))
            {
                string written = line[(line.IndexOf(' ', line.StartsWith("into") ? 5 : 0) + 1)..].Trim();
                // `import A, B from D.`: the namespace after `from`.
                if (line.StartsWith("import ", StringComparison.Ordinal) && line.LastIndexOf(" from ", StringComparison.Ordinal) is var at and >= 0)
                    written = line[(at + 6)..].Trim();
                string parent = written.EndsWith('.') ? written[..^1] : "";
                return new CompletionRequest(new CompletionContext.Namespace(parent), prefix, start, offset - start) { From = from };
            }
            int dot = start - 1;
            if (dot >= 0 && text[dot] == '.' && !(dot > 0 && text[dot - 1] == '.'))
            {
                var context = Receiver(dot, offset, out var extra);
                return context is null ? null : new CompletionRequest(context, prefix, start, offset - start) { From = from, Extra = extra };
            }
            var body = BodyAt(offset);
            bool typesOnly = body is null || PreviousWord(start) is "new" or "is" or "as";
            var scopeLocals = body is null ? [] : LocalsAt(offset).Select(l => new ScopeSymbol(l.Name,
                l.Parameter ? CompletionKind.Parameter : CompletionKind.Local, Global(l.Type), Spot(l.Declared))).ToList();
            var typeParameters = TypeParametersAt(offset, body);
            return new CompletionRequest(new CompletionContext.Scope([.. scopeLocals, .. typeParameters], VisibleNamespaces(offset), typesOnly, body?.Method.IsStatic ?? false),
                prefix, start, offset - start) { From = from, Extra = [.. ImportedItems()] };
        }

        private IEnumerable<ScopeSymbol> TypeParametersAt(int offset, RecordedBody? body)
        {
            var (_, type) = DeclarationsAt(offset);
            if (type is not null)
                foreach (var g in type.Generics)
                    yield return new ScopeSymbol(g.Name, CompletionKind.TypeParameter, new H.GenericParamType(H.GenericOwner.Type, 0), Spot(g.Span));
            if (body?.Method.Function is { } function)
                foreach (var g in function.Generics)
                    yield return new ScopeSymbol(g.Name, CompletionKind.TypeParameter, new H.GenericParamType(H.GenericOwner.Method, 0), Spot(g.Span));
        }

        private string PreviousWord(int position)
        {
            string text = source.Text;
            int end = position;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
                end--;
            int begin = end;
            while (begin > 0 && IsWord(text[begin - 1]))
                begin--;
            return text[begin..end];
        }

        /// <summary>What is before a dot: a value (its members), a type (its static members), a namespace (what is in it).</summary>
        private CompletionContext? Receiver(int dot, int offset, out IReadOnlyList<CompletionItem> extra)
        {
            extra = [];
            RecordedSpan? best = null;
            foreach (var span in spans)
            {
                if (span.Span.End != dot || span.Kind is not (RecordedKind.Expression or RecordedKind.Type))
                    continue;
                if (best is null || span.Span.Length > best.Span.Length || span.Span.Length == best.Span.Length && span.Kind == RecordedKind.Type)
                    best = span;
            }
            if (best is { Kind: RecordedKind.Type, Type: { } typeName })
                return new CompletionContext.Members(Global(typeName), Static: true);
            if (best is { Type: { } valueType })
                return ValueMembers(Global(valueType), out extra);
            // `(a + b).`, `(x as T).`: the value in the brackets (a bracket records no span of its own).
            if (Bracketed(dot) is { } inner)
                return ValueMembers(Global(inner), out extra);
            // Not bound (the code around does not parse yet): read the name chain before the dot.
            var chain = new List<string>();
            int end = dot;
            while (true)
            {
                int begin = end;
                while (begin > 0 && IsWord(source.Text[begin - 1]))
                    begin--;
                if (begin == end)
                    return null;
                chain.Insert(0, source.Text[begin..end]);
                if (begin > 0 && source.Text[begin - 1] == '.')
                {
                    end = begin - 1;
                    continue;
                }
                break;
            }
            return Chain(chain, offset, out extra);
        }

        /// <summary>The type of the expression in the brackets that close right before <paramref name="dot"/>, or null.</summary>
        private H.HirType? Bracketed(int dot)
        {
            string text = source.Text;
            int close = dot - 1;
            while (close >= 0 && char.IsWhiteSpace(text[close]))
                close--;
            if (close < 0 || text[close] != ')')
                return null;
            int depth = 0, open = close;
            for (; open >= 0; open--)
            {
                if (text[open] == ')')
                    depth++;
                else if (text[open] == '(' && --depth == 0)
                    break;
            }
            if (open < 0)
                return null;
            // A call's brackets (`F(x).`) are the call's: its span ends at the dot and was found already.
            int before = open - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before]))
                before--;
            if (before >= 0 && (IsWord(text[before]) || text[before] is ']' or '>'))
                return null;
            RecordedSpan? widest = null;
            foreach (var span in spans)
            {
                if (span.Kind != RecordedKind.Expression || span.Type is null || span.Span.Start <= open || span.Span.End > close)
                    continue;
                if (widest is null || span.Span.Length > widest.Span.Length)
                    widest = span;
            }
            // The widest expression must be the whole of what is in the brackets.
            if (widest is null || text.AsSpan(open + 1, widest.Span.Start - open - 1).Trim().Length > 0
                || text.AsSpan(widest.Span.End, close - widest.Span.End).Trim().Length > 0)
                return null;
            return widest.Type;
        }

        private CompletionContext ValueMembers(H.HirType type, out IReadOnlyList<CompletionItem> extra)
        {
            var view = AntrilView.Instance;
            extra = type switch
            {
                H.ArrayType => [new CompletionItem("Length", CompletionKind.Property, "i32 Length { get; }", "How many elements the array has.")],
                H.NullableType { Inner: var inner } when Catalog.TypeOf(inner) is not { IsClass: true } => [
                    new CompletionItem("HasValue", CompletionKind.Property, "bool HasValue { get; }", "Whether it holds a value (it is not null)."),
                    new CompletionItem("Value", CompletionKind.Property, $"{view.Type(inner, Catalog, GenericNames.None)} Value {{ get; }}", "The value it holds (an error when it is null)."),
                ],
                _ => [],
            };
            // A nullable has no members of its own until it is checked (a nullable value: HasValue and Value).
            return new CompletionContext.Members(type is H.NullableType ? H.HirType.Void : type, Static: false);
        }

        /// <summary>A dotted name read as text: a local and its members, a type, or a namespace.</summary>
        private CompletionContext? Chain(List<string> chain, int offset, out IReadOnlyList<CompletionItem> extra)
        {
            extra = [];
            // `X.` with `import D as X;`, `E.` with `import A from D as E;`: what they stand for.
            if (ImportedName(chain[0]) is { } imported)
                chain = [.. imported.Split('.'), .. chain.Skip(1)];
            string full = string.Join(".", chain);
            if (Catalog.IsNamespace(full))
                return new CompletionContext.Namespace(full);
            H.HirType? current = null;
            bool isStatic = false;
            int first = 1;
            if (chain[0] == "this" && TypeAt(offset) is { } self)
                current = self.SelfType;
            else if (LocalsAt(offset).FirstOrDefault(l => l.Name == chain[0]) is { } local)
                current = Global(local.Type);
            else if (TypeNamed(chain[0], offset) is { } type)
            {
                // `str` (and `String` written alone) is text in UTF-8: String<u8>.
                current = type.FullName == "System.String" && type.Arity == 1 ? new H.NamedType(H.TypeToken.Def(type.Id), [H.HirType.U8]) : type.SelfType;
                isStatic = true;
            }
            else
            {
                // A namespace prefix then a type: `System.IO.File`.
                for (int n = chain.Count - 1; n > 0 && current is null; n--)
                {
                    string ns = string.Join(".", chain.Take(n));
                    if (Catalog.TypesIn(ns).FirstOrDefault(t => t.Name == chain[n]) is { } qualified)
                    {
                        current = qualified.SelfType;
                        isStatic = true;
                        first = n + 1;
                    }
                }
                if (current is null)
                    return null;
            }
            for (int i = first; i < chain.Count; i++)
            {
                if (Catalog.TypeOf(current) is not { } owner)
                    return null;
                var arguments = current is H.NamedType named ? named.Arguments : [];
                var member = owner.Members(arguments).FirstOrDefault(m => m.Name == chain[i] && m.Member.Kind is CatalogMemberKind.Field or CatalogMemberKind.Property or CatalogMemberKind.Constant);
                if (member is null)
                    return null;
                current = member.ValueType;
                isStatic = false;
            }
            return isStatic ? new CompletionContext.Members(current, true) : ValueMembers(current, out extra);
        }

        private CatalogType? TypeNamed(string name, int offset)
        {
            switch (name)
            {
                case "str":
                    return Catalog.Find("System", "String", 1) ?? Catalog.Find("System", "String", 0);
                case "any":
                    return Catalog.Find("System", "Any", 0);
            }
            // `i32.` and the other primitive keywords: the provider that holds their static members and extensions.
            if (Binder.PrimitiveNamed(name) is { } primitive && Catalog.TypeOf(H.HirType.Of(primitive)) is { } provider)
                return provider;
            foreach (var ns in VisibleNamespaces(offset))
                if (Catalog.TypesIn(ns).FirstOrDefault(t => t.Name == name) is { } type)
                    return type;
            if (ImportedName(name) is { } imported && Catalog.Find(imported) is { } named)
                return named;
            if (Catalog.TypesIn("").FirstOrDefault(t => t.Name == name) is { } global)
                return global;
            return null;
        }

        // ── Symbols ────────────────────────────────────────

        public SymbolInfo? SymbolAt(int offset)
        {
            RecordedSpan? best = null;
            foreach (var span in spans)
            {
                if (span.Kind == RecordedKind.Expression || span.Span.Start > offset || offset > span.Span.End)
                    continue;
                if (best is null || span.Span.Length < best.Span.Length)
                    best = span;
            }
            if (best is not null)
                return Symbol(best);
            // A keyword that names a type (`i32`, `str`, `any`): the type it stands for (System.Int32…).
            if (KeywordTypeAt(offset) is { } keyword)
                return new SymbolInfo(SymbolKind.Type, keyword.Name) { CatalogType = keyword };
            // A declaration's name: the type or member declared there.
            return Declaration(offset);
        }

        private CatalogType? KeywordTypeAt(int offset)
        {
            string text = source.Text;
            int start = Math.Min(offset, text.Length), end = start;
            while (start > 0 && char.IsAsciiLetterOrDigit(text[start - 1]))
                start--;
            while (end < text.Length && char.IsAsciiLetterOrDigit(text[end]))
                end++;
            if (end - start is < 2 or > 7 || start > 0 && (text[start - 1] is '_' or '$' or '.'))
                return null;
            string word = text[start..end];
            return word is "str" or "any" || Binder.PrimitiveNamed(word) is not null ? TypeNamed(word, offset) : null;
        }

        private SymbolInfo? Symbol(RecordedSpan span)
        {
            switch (span.Kind)
            {
                case RecordedKind.Local or RecordedKind.Parameter:
                    return new SymbolInfo(span.Kind == RecordedKind.Local ? SymbolKind.Local : SymbolKind.Parameter, span.Name ?? "?")
                    {
                        Type = span.Type is { } t ? Global(t) : null,
                        Declared = Spot(span.Declared),
                    };
                case RecordedKind.Type when Catalog.Resolve(span.Token, program) is { } type:
                    return new SymbolInfo(SymbolKind.Type, type.Name) { CatalogType = type };
                case RecordedKind.Member when Catalog.Resolve(span.Member, span.MemberKind, program) is { } member:
                {
                    MemberView? view = null;
                    if (span.Receiver is { } receiver && Global(receiver) is var global && Catalog.TypeOf(global) is { } owner)
                        view = owner.Members(global is H.NamedType n ? n.Arguments : []).FirstOrDefault(v => v.Member == member);
                    return new SymbolInfo(SymbolKind.Member, member.Name) { Member = member, View = view, CatalogType = member.Type };
                }
            }
            return null;
        }

        private SymbolInfo? Declaration(int offset)
        {
            string ns = "";
            var members = unit.Members;
            CatalogType? owner = null;
            while (true)
            {
                var member = members.FirstOrDefault(m => m.Span.Start <= offset && offset <= m.Span.End);
                switch (member)
                {
                    case NamespaceDecl n:
                        ns = ns.Length == 0 ? n.Name.ToString() : $"{ns}.{n.Name}";
                        members = n.Members;
                        continue;
                    case TypeDecl t:
                        owner = Catalog.Find(program.Name, ns, t.Name, t.Generics.Length);
                        if (t.NameSpan.Start <= offset && offset <= t.NameSpan.End)
                            return owner is null ? null : new SymbolInfo(SymbolKind.Type, owner.Name) { CatalogType = owner };
                        members = t.Members;
                        continue;
                    case FunctionDecl f when f.NameSpan.Start <= offset && offset <= f.NameSpan.End:
                        return MemberNamed(owner ?? Catalog.Find(program.Name, ns, "$Module", 0), f.Name, f.NameSpan);
                    case FieldDecl f when f.NameSpan.Start <= offset && offset <= f.NameSpan.End:
                        return MemberNamed(owner ?? Catalog.Find(program.Name, ns, "$Module", 0), f.Name, f.NameSpan);
                    case PropertyDecl p when p.NameSpan.Start <= offset && offset <= p.NameSpan.End:
                        return MemberNamed(owner, p.Name, p.NameSpan);
                }
                return null;
            }
        }

        private SymbolInfo? MemberNamed(CatalogType? owner, string name, TextSpan span)
        {
            var (line, column) = source.LineColumn(span.Start);
            var member = owner?.Declared.FirstOrDefault(m => m.Name == name && m.Location is { } at && at.Line == line && at.Column == column)
                ?? owner?.Declared.FirstOrDefault(m => m.Name == name);
            return member is null ? null : new SymbolInfo(SymbolKind.Member, name) { Member = member, CatalogType = owner };
        }

        // ── Calls ──────────────────────────────────────────

        public CallSite? CallAt(int offset)
        {
            string text = source.Text;
            offset = Math.Clamp(offset, 0, text.Length);
            // The '(' of the call the position is in, and which argument (commas at that depth before it).
            int depth = 0, commas = 0, open = -1;
            for (int i = offset - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c is ')' or ']' or '}')
                    depth++;
                else if (c is '(' or '[' or '{')
                {
                    if (depth == 0)
                    {
                        if (c == '(')
                            open = i;
                        break;
                    }
                    depth--;
                }
                else if (c == ',' && depth == 0)
                    commas++;
                else if (c == ';' && depth == 0)
                    break;
            }
            if (open <= 0)
                return null;
            RecordedCall? best = null;
            foreach (var call in calls)
            {
                if (call.Span.Start < open && open < call.Span.End && (best is null || call.Span.Length < best.Span.Length))
                    best = call;
            }
            var candidates = new List<MemberView>();
            if (best is not null)
            {
                foreach (var (method, declaring) in best.Candidates)
                {
                    if (Catalog.Resolve(method.Owner.Token, program) is not { } owner || owner.Member(H.MemberKind.Method, method.Index) is not { } member)
                        continue;
                    candidates.Add(new MemberView(member, [.. declaring.Select(Global)], false));
                }
            }
            else if (CompletionAt(open) is { Context: var context } && WordBefore(open) is { Length: > 0 } name)
            {
                // Not bound yet: the methods of that name where the call's target is.
                IEnumerable<MemberView> found = context switch
                {
                    CompletionContext.Members m when Catalog.TypeOf(m.Type) is { } t => t.Members(m.Type is H.NamedType n ? n.Arguments : []),
                    CompletionContext.Scope when TypeAt(open) is { } t => t.Members(),
                    _ => [],
                };
                candidates.AddRange(found.Where(v => v.Name == name && v.Member.Kind is CatalogMemberKind.Method or CatalogMemberKind.Constructor));
                if (candidates.Count == 0 && PreviousWord(open - name.Length) == "new" && TypeNamed(name, open) is { } created)
                    candidates.AddRange(created.Members().Where(v => v.Member.Kind == CatalogMemberKind.Constructor));
            }
            return candidates.Count == 0 ? null : new CallSite(candidates, commas, commas + 1);
        }

        private string WordBefore(int position)
        {
            int end = position;
            int begin = end;
            while (begin > 0 && IsWord(source.Text[begin - 1]))
                begin--;
            return source.Text[begin..end];
        }

        // ── Outline and problems ───────────────────────────

        public IReadOnlyList<DocumentSymbol> Outline => outline ??= [.. unit.Members.SelectMany(Outlined)];
        private DocumentSymbol[]? outline;

        private IEnumerable<DocumentSymbol> Outlined(MemberDecl member)
        {
            var (line, column) = source.LineColumn(member.Span.Start);
            switch (member)
            {
                case NamespaceDecl n:
                    yield return new DocumentSymbol(n.Name.ToString(), CompletionKind.Namespace, "namespace", line, column, [.. n.Members.SelectMany(Outlined)]);
                    break;
                case TypeDecl t:
                {
                    var kind = t.Kind switch
                    {
                        Syntax.TypeKind.Interface => CompletionKind.Interface,
                        Syntax.TypeKind.Enum or Syntax.TypeKind.BitflagEnum => CompletionKind.Enum,
                        Syntax.TypeKind.Value => CompletionKind.Struct,
                        _ => CompletionKind.Class,
                    };
                    (line, column) = source.LineColumn(t.NameSpan.Start);
                    yield return new DocumentSymbol(t.Name, kind, t.Kind.ToString().ToLowerInvariant(), line, column, [.. t.Members.SelectMany(Outlined)]);
                    break;
                }
                case FunctionDecl f:
                    (line, column) = source.LineColumn(f.NameSpan.Start);
                    yield return new DocumentSymbol(f.Name, CompletionKind.Method, source[f.Span].Split('\n')[0].Trim(), line, column, []);
                    break;
                case ConstructorDecl c:
                    yield return new DocumentSymbol("new", CompletionKind.Constructor, source[c.HeadSpan.Length > 0 ? c.HeadSpan : c.Span].Split('\n')[0].Trim(), line, column, []);
                    break;
                case FieldDecl f:
                    (line, column) = source.LineColumn(f.NameSpan.Start);
                    yield return new DocumentSymbol(f.Name, CompletionKind.Field, source[f.Type.Span] + " " + f.Name, line, column, []);
                    break;
                case PropertyDecl p:
                    (line, column) = source.LineColumn(p.NameSpan.Start);
                    yield return new DocumentSymbol(p.Name, CompletionKind.Property, source[p.Type.Span] + " " + p.Name, line, column, []);
                    break;
                case EnumMemberDecl e:
                    yield return new DocumentSymbol(e.Name, CompletionKind.EnumValue, e.Name, line, column, []);
                    break;
            }
        }

        // ── Semantic tokens ────────────────────────────────

        public IReadOnlyList<SemanticToken> Tokens => tokens ??= MakeTokens();
        private SemanticToken[]? tokens;

        private SemanticToken[] MakeTokens()
        {
            var list = new List<SemanticToken>();
            string text = source.Text;
            void Add(int offset, int length, SemanticKind kind, SemanticModifiers modifiers = SemanticModifiers.None)
            {
                if (length > 0 && offset >= 0 && offset + length <= text.Length)
                    list.Add(new SemanticToken(offset, length, kind, modifiers));
            }
            // `A.B.C`: the namespaces, then the last name as what it is.
            void Qualified(TextSpan span, SemanticKind last, SemanticModifiers modifiers)
            {
                int start = span.Start;
                for (int i = span.Start; i <= span.End && i <= text.Length; i++)
                {
                    bool word = i < span.End && i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
                    if (word)
                        continue;
                    bool isLast = i >= span.End || i < text.Length && text[i] == '<';
                    if (i > start)
                        Add(start, i - start, isLast ? last : SemanticKind.Namespace, isLast ? modifiers : SemanticModifiers.None);
                    if (isLast || i < text.Length && char.IsWhiteSpace(text[i]))
                        break;
                    start = i + 1;
                }
            }
            SemanticModifiers Library(CatalogType type) => type.Assembly != program ? SemanticModifiers.DefaultLibrary : SemanticModifiers.None;
            foreach (var span in spans)
            {
                switch (span.Kind)
                {
                    case RecordedKind.Local:
                        Add(span.Span.Start, span.Span.Length, SemanticKind.Variable);
                        break;
                    case RecordedKind.Parameter:
                        Add(span.Span.Start, span.Span.Length, SemanticKind.Parameter);
                        break;
                    case RecordedKind.Type when span.Type is H.GenericParamType:
                        Add(span.Span.Start, span.Span.Length, SemanticKind.TypeParameter);
                        break;
                    // `any` and `str` are keywords (System.Any, System.String<u8>), as `i32` is: the grammar colors them.
                    case RecordedKind.Type when text.AsSpan(span.Span.Start, span.Span.Length) is "any" or "str":
                        break;
                    case RecordedKind.Type when Catalog.Resolve(span.Token, program) is { } type:
                        Qualified(span.Span, Semantic.Of(type), Library(type));
                        break;
                    case RecordedKind.Member when Catalog.Resolve(span.Member, span.MemberKind, program) is { } member:
                        if (member.Kind == CatalogMemberKind.Constructor)
                            Qualified(span.Span, Semantic.Of(member.Type), Library(member.Type));
                        else if (span.Span.Length >= member.Name.Length)
                            Add(span.Span.End - member.Name.Length, member.Name.Length, Semantic.Of(member), Semantic.Modifiers(member, program));
                        break;
                }
            }
            foreach (var local in locals)
            {
                if (!local.Parameter && local.Declared.Length == local.Name.Length && text.AsSpan(local.Declared.Start, local.Declared.Length).SequenceEqual(local.Name))
                    Add(local.Declared.Start, local.Declared.Length, SemanticKind.Variable, SemanticModifiers.Declaration);
            }
            // Words that are keywords only where they stand (`const i32 x`, `import A from B`, `nameof(x)`): marked here, so
            // they read as keywords whatever the grammar makes of them.
            var words = Lexer.Tokenize(source, []);
            bool inImport = false;
            for (int t = 0; t < words.Count; t++)
            {
                var word = words[t];
                if (word.Kind == TokenKind.Import)
                    inImport = true;
                else if (word.Kind == TokenKind.Semicolon)
                    inImport = false;
                if (word.Kind != TokenKind.Identifier || t + 1 >= words.Count)
                    continue;
                var next = words[t + 1].Kind;
                bool keyword = word.Text switch
                {
                    "const" => next is TokenKind.Identifier or TokenKind.OpenParen || Lexer.IsPrimitive(next),
                    "from" => inImport && next == TokenKind.Identifier,
                    "nameof" => next == TokenKind.OpenParen,
                    _ => false,
                };
                if (keyword)
                    Add(word.Span.Start, word.Span.Length, SemanticKind.Keyword);
            }
            foreach (var import in unit.Imports)
            {
                Qualified(import.Name.Span, SemanticKind.Namespace, SemanticModifiers.None);
                if (import.Alias is { } alias)
                    Add(import.AliasSpan.Start, alias.Length, SemanticKind.Namespace);
                foreach (var picked in import.Picked)
                {
                    string full = $"{import.Name}.{picked.Name}";
                    var kind = Catalog.IsNamespace(full) ? SemanticKind.Namespace : Catalog.Find(full) is { } type ? Semantic.Of(type)
                        : Catalog.Find(import.Name.ToString(), "$Module", 0)?.Declared.FirstOrDefault(m => m.Name == picked.Name) is { } member ? Semantic.Of(member) : SemanticKind.Type;
                    Add(picked.Span.Start, picked.Name.Length, kind);
                    if (picked.Alias is { } pickedAlias)
                        Add(picked.AliasSpan.Start, pickedAlias.Length, kind);
                }
            }
            void Parameters(ParameterSyntax[] parameters)
            {
                foreach (var p in parameters)
                    Add(p.Span.End - p.Name.Length, p.Name.Length, SemanticKind.Parameter, SemanticModifiers.Declaration);
            }
            // The parameters, and the type-group words of their constraints (`T : copy, unmanaged`).
            void Generics(GenericParamSyntax[] generics)
            {
                foreach (var g in generics)
                {
                    Add(g.Span.Start, g.Name.Length, SemanticKind.TypeParameter, SemanticModifiers.Declaration);
                    foreach (var constraint in g.Constraints)
                        if (constraint.Word is { } word)
                            Add(constraint.Span.Start, word.Length, SemanticKind.Keyword);
                }
            }
            CatalogType? DeclaredType(TypeDecl t, string ns) => Catalog.Find(program.Name, ns, t.Name, t.Generics.Length);
            // `[Tag]`: the attribute applied (TagAttribute, or a type named Tag), colored as what it is.
            void Applied(AttributeSyntax[] attributes)
            {
                foreach (var attribute in attributes)
                {
                    string last = attribute.Name.Parts[^1];
                    if ((TypeNamed(last + "Attribute", attribute.Span.Start) ?? TypeNamed(last, attribute.Span.Start)) is { } applied)
                        Qualified(attribute.Name.Span, Semantic.Of(applied), Library(applied));
                }
            }
            void Declarations(MemberDecl[] members, bool inModule, string ns)
            {
                foreach (var member in members)
                {
                    Applied(member.Attributes);
                    switch (member)
                    {
                        case NamespaceDecl n:
                            Qualified(n.Name.Span, SemanticKind.Namespace, SemanticModifiers.Declaration);
                            Declarations(n.Members, true, ns.Length == 0 ? n.Name.ToString() : $"{ns}.{n.Name}");
                            break;
                        case TypeDecl t:
                        {
                            // What the catalog says the type is (a class derived from an exception or an attribute through a
                            // base of its own too), else what its declaration says.
                            var kind = DeclaredType(t, ns) is { } declared ? Semantic.Of(declared) : t.Kind switch
                            {
                                Syntax.TypeKind.Interface => SemanticKind.Interface,
                                Syntax.TypeKind.Enum => SemanticKind.Enum,
                                Syntax.TypeKind.BitflagEnum => SemanticKind.Bitflags,
                                Syntax.TypeKind.Value or Syntax.TypeKind.RecordValue => SemanticKind.Struct,
                                _ when t.Implied == "Exception" => SemanticKind.Exception,
                                _ when t.Implied == "Attribute" => SemanticKind.Attribute,
                                _ => SemanticKind.Class,
                            };
                            Add(t.NameSpan.Start, t.NameSpan.Length, kind, SemanticModifiers.Declaration);
                            Generics(t.Generics);
                            Declarations(t.Members, false, ns);
                            break;
                        }
                        case FunctionDecl f:
                            if (!f.Name.StartsWith('$'))
                                Add(f.NameSpan.Start, f.NameSpan.Length, inModule ? SemanticKind.Function : SemanticKind.Method,
                                    SemanticModifiers.Declaration | ((f.Modifiers & Syntax.Modifiers.Static) != 0 ? SemanticModifiers.Static : 0));
                            Generics(f.Generics);
                            Parameters(f.Parameters);
                            break;
                        case ConstructorDecl c:
                            Parameters(c.Parameters);
                            break;
                        case FieldDecl f when (f.Modifiers & Syntax.Modifiers.Const) != 0:
                            Add(f.NameSpan.Start, f.NameSpan.Length, SemanticKind.Variable, SemanticModifiers.Declaration | SemanticModifiers.Readonly | SemanticModifiers.Static);
                            break;
                        case FieldDecl f:
                            Add(f.NameSpan.Start, f.NameSpan.Length, inModule ? SemanticKind.Variable : SemanticKind.Field,
                                SemanticModifiers.Declaration | ((f.Modifiers & Syntax.Modifiers.Static) != 0 ? SemanticModifiers.Static : 0)
                                | ((f.Modifiers & Syntax.Modifiers.Readonly) != 0 ? SemanticModifiers.Readonly : 0));
                            break;
                        case PropertyDecl p:
                            Add(p.NameSpan.Start, p.NameSpan.Length, SemanticKind.Property, SemanticModifiers.Declaration);
                            break;
                        case EnumMemberDecl e:
                            Add(e.Span.Start, e.Name.Length, SemanticKind.EnumMember, SemanticModifiers.Declaration | SemanticModifiers.Readonly);
                            if (e.Value is NameExpr { Name: "declbits" or "allbits" } bits)
                                Add(bits.Span.Start, bits.Span.Length, SemanticKind.Keyword);
                            break;
                    }
                }
            }
            Declarations(unit.Members, true, "");
            // In order; of overlapping ones the first is kept (a name is one token), and none crosses a line.
            list.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Length.CompareTo(b.Length));
            var result = new List<SemanticToken>(list.Count);
            int end = 0;
            foreach (var token in list)
            {
                if (token.Offset < end || text.AsSpan(token.Offset, token.Length).ContainsAny('\r', '\n'))
                    continue;
                result.Add(token);
                end = token.Offset + token.Length;
            }
            return [.. result];
        }


        public IReadOnlyList<Problem> Problems => problems ??= [.. diagnostics.Where(d => d.Source == source).Select(d =>
        {
            var (line, column) = source.LineColumn(d.Span.Start);
            var (endLine, endColumn) = source.LineColumn(d.Span.End);
            return new Problem(d.IsError ? ProblemSeverity.Error : ProblemSeverity.Warning, d.Code, d.Message, line, column, endLine, endColumn);
        })];
        private Problem[]? problems;
    }
}
