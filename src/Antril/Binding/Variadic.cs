using Antril.Syntax;
using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>
    /// Variadic generics, by expansion before binding. A template is a type or a namespace-level function whose last type
    /// parameter is a pack (<c>Tuple&lt;TArgs...&gt;</c>). For every number of pack arguments the program uses, the template
    /// becomes an ordinary generic declaration with that many parameters in the pack's place (<c>Tuple&lt;TArgs0, TArgs1&gt;</c>;
    /// the arity is part of a type's identity, so each coexists with the others):
    /// <list type="bullet">
    /// <item>a field or parameter whose type is the pack (<c>TArgs item</c>) becomes one per element (<c>item0</c>, <c>item1</c>…);</item>
    /// <item><c>static loop (T in TArgs) { members }</c> among a type's members declares them once per element, with <c>T</c>
    /// that element's type and each name numbered (<c>T item;</c> gives <c>item0</c>, <c>item1</c>…);</item>
    /// <item><c>static loop (T in TArgs) statement</c> in a body repeats the statement per element, with <c>T</c> that
    /// element's type and the pack's fields and parameters (and the members a static loop declared) naming that element's;</item>
    /// <item>the pack as a type argument (<c>Tuple&lt;TArgs&gt;</c>) stands for all its elements.</item>
    /// </list>
    /// A function template is expanded per argument count of its calls (an overload each). A pack size that has a
    /// declaration of its own (<c>ValueTuple&lt;T1, T2&gt;</c> beside <c>ValueTuple&lt;TArgs...&gt;</c>) uses it: the template is
    /// the fallback for the other sizes. A compiled library carries its templates' text (<see cref="Recorded"/>), so a
    /// program expands them for itself.
    /// </summary>
    internal static class Variadic
    {
        private sealed record Template(MemberDecl Declaration, string Namespace)
        {
            public GenericParamSyntax[] Generics => Declaration is TypeDecl t ? t.Generics : ((FunctionDecl)Declaration).Generics;
            public string Name => Declaration is TypeDecl t ? t.Name : ((FunctionDecl)Declaration).Name;
            public int Fixed => Generics.Length - 1;
            public string Pack => Generics[^1].Name;
        }

        /// <summary>Where a library keeps a template's text (<see cref="H.HirAssembly.LanguageData"/>): this, then the file it is from.</summary>
        public const string DataPrefix = "antril.template:";

        /// <summary>
        /// The text of each template <paramref name="units"/> declare, to keep with the assembly: the file's imports and the
        /// declaration in its namespace, enough to parse it alone where the library is used.
        /// </summary>
        public static IEnumerable<H.HirResource> Recorded(CompilationUnit[] units)
        {
            var templates = new List<(Template Template, CompilationUnit Unit)>();
            var quiet = new DiagnosticBag();
            foreach (var unit in units)
                Collect(unit.Members, unit, "", templates, quiet);
            foreach (var (template, unit) in templates)
            {
                var text = new System.Text.StringBuilder();
                foreach (var import in unit.Imports)
                    text.Append(unit.Source[import.Span]).Append('\n');
                string declaration = unit.Source[template.Declaration.Span];
                if (template.Namespace.Length == 0)
                    text.Append(declaration).Append('\n');
                else
                    text.Append("into namespace ").Append(template.Namespace).Append("\n{\n").Append(declaration).Append("\n}\n");
                yield return new H.HirResource(DataPrefix + unit.Source.Path, System.Text.Encoding.UTF8.GetBytes(text.ToString()));
            }
        }

        /// <summary>The units with every template replaced by the expansions the program uses (the units as they are without templates).</summary>
        public static CompilationUnit[] Expand(CompilationUnit[] units, DiagnosticBag diagnostics, IReadOnlyList<H.HirAssembly>? references = null, OperatorTable? table = null)
        {
            var templates = new List<(Template Template, CompilationUnit Unit)>();
            foreach (var unit in units)
                Collect(unit.Members, unit, "", templates, diagnostics);
            // The templates of the libraries the program references: parsed from the text they carry.
            foreach (var assembly in references ?? [])
            {
                foreach (var data in assembly.LanguageData)
                {
                    if (!data.Name.StartsWith(DataPrefix, StringComparison.Ordinal))
                        continue;
                    var source = new SourceText(System.Text.Encoding.UTF8.GetString(data.Data), $"{assembly.Name}:{data.Name[DataPrefix.Length..]}");
                    var unit = Parser.Parse(source, diagnostics, table);
                    Collect(unit.Members, unit, "", templates, diagnostics);
                }
            }
            if (templates.Count == 0)
                return units;
            // Pack sizes declared on their own: a type of the template's name with that many parameters.
            var declared = new HashSet<(string, int)>();
            foreach (var unit in units)
                Declared(unit.Members, declared);
            foreach (var assembly in references ?? [])
            {
                foreach (var type in assembly.Types)
                {
                    if (!type.Generics.Any(g => g.Variadic))
                        declared.Add((type.Name, type.Generics.Count));
                }
            }
            var removed = templates.Select(t => (object)t.Template.Declaration).ToHashSet(ReferenceEqualityComparer.Instance);
            var result = units.Select(u => u with { Members = Without(u.Members, removed) }).ToList();

            var done = new HashSet<(Template, int)>();
            var expansions = new List<(Template Template, CompilationUnit Unit, MemberDecl Declaration)>();
            var pending = new List<Node>(result);
            var all = templates.Select(t => t.Template).ToList();
            while (pending.Count > 0)
            {
                var found = new List<(Template, int)>();
                foreach (var root in pending)
                    found.AddRange(Uses(root, all, declared));
                pending.Clear();
                foreach (var (template, count) in found.Distinct())
                {
                    if (count < 0 || !done.Add((template, count)))
                        continue;
                    var unit = templates.First(t => t.Template == template).Unit;
                    var expanded = Instantiate(template, count, unit.Source, diagnostics);
                    expansions.Add((template, unit, expanded));
                    pending.Add(expanded);
                }
            }

            // One unit per template's file: its text (spans stay valid), its imports, the expansions in their namespaces.
            foreach (var group in expansions.GroupBy(e => e.Unit))
            {
                var members = group.Select(e => Wrap(e.Template.Namespace, e.Declaration)).ToArray();
                result.Add(new CompilationUnit(group.Key.Source, group.Key.Imports, members, group.Key.Span));
            }
            return [.. result];
        }

        private static void Collect(MemberDecl[] members, CompilationUnit unit, string ns, List<(Template, CompilationUnit)> templates, DiagnosticBag diagnostics)
        {
            foreach (var member in members)
            {
                switch (member)
                {
                    case NamespaceDecl inner:
                        Collect(inner.Members, unit, ns.Length == 0 ? inner.Name.ToString() : $"{ns}.{inner.Name}", templates, diagnostics);
                        break;
                    case TypeDecl type when type.Generics.Any(g => g.Variadic):
                        if (Valid(type.Generics, unit.Source, diagnostics))
                            templates.Add((new Template(type, ns), unit));
                        break;
                    case TypeDecl type when type.Members.OfType<StaticLoopDecl>().FirstOrDefault() is { } loop:
                        diagnostics.Error("AN1420", $"`static loop` repeats members per type of a pack: `{type.Name}` needs one (`<{loop.Pack}...>`)", unit.Source, loop.Span);
                        break;
                    case FunctionDecl function when function.Generics.Any(g => g.Variadic):
                        if (Valid(function.Generics, unit.Source, diagnostics))
                            templates.Add((new Template(function, ns), unit));
                        break;
                    case StaticLoopDecl loop:
                        diagnostics.Error("AN1420", $"`static loop` repeats members of a variadic template: the type needs a pack (`<{loop.Pack}...>`)", unit.Source, loop.Span);
                        break;
                }
            }
        }

        private static void Declared(MemberDecl[] members, HashSet<(string, int)> declared)
        {
            foreach (var member in members)
            {
                if (member is NamespaceDecl inner)
                    Declared(inner.Members, declared);
                else if (member is TypeDecl type && !type.Generics.Any(g => g.Variadic))
                    declared.Add((type.Name, type.Generics.Length));
            }
        }

        private static bool Valid(GenericParamSyntax[] generics, SourceText source, DiagnosticBag diagnostics)
        {
            for (int g = 0; g < generics.Length - 1; g++)
            {
                if (generics[g].Variadic)
                {
                    diagnostics.Error("AN1420", "only the last type parameter is a pack (`T...`)", source, generics[g].Span);
                    return false;
                }
            }
            return true;
        }

        private static MemberDecl[] Without(MemberDecl[] members, HashSet<object> removed) =>
            [.. members.Where(m => !removed.Contains(m)).Select(m => m is NamespaceDecl ns ? ns with { Members = Without(ns.Members, removed) } : m)];

        private static MemberDecl Wrap(string ns, MemberDecl declaration)
        {
            if (ns.Length == 0)
                return declaration;
            var at = declaration.Span;
            return new NamespaceDecl(new QualifiedName(ns.Split('.'), at), [declaration], at);
        }

        /// <summary>Uses of templates under a node, with their pack size: types by argument count, functions by call arity.</summary>
        private static IEnumerable<(Template, int)> Uses(Node root, List<Template> templates, HashSet<(string, int)> declared)
        {
            foreach (var node in AstRewriter.Descendants(root))
            {
                switch (node)
                {
                    case NamedTypeSyntax named:
                        foreach (var t in templates.Where(t => t.Declaration is TypeDecl && t.Name == named.Name.Parts[^1]))
                        {
                            if (named.Arguments.Length > t.Fixed && !declared.Contains((t.Name, named.Arguments.Length))
                                && !named.Arguments.Any(a => a is NamedTypeSyntax { Name.Parts: [var only], Arguments.Length: 0 } && only == t.Pack))
                                yield return (t, named.Arguments.Length - t.Fixed);
                        }
                        break;
                    case TupleExpr tuple:
                        // `(a, b, c)` makes a System.ValueTuple of that many elements.
                        foreach (var t in templates.Where(t => t.Declaration is TypeDecl && t.Name == "ValueTuple" && t.Namespace == "System"))
                        {
                            if (tuple.Items.Length > t.Fixed && !declared.Contains((t.Name, tuple.Items.Length)))
                                yield return (t, tuple.Items.Length - t.Fixed);
                        }
                        break;
                    case CallExpr { Target: NameExpr or MemberExpr } call:
                    {
                        string name = call.Target is NameExpr n ? n.Name : ((MemberExpr)call.Target).Name;
                        foreach (var t in templates.Where(t => t.Declaration is FunctionDecl f && f.Name == name))
                        {
                            var function = (FunctionDecl)t.Declaration;
                            int before = function.Parameters.TakeWhile(p => !IsPack(p.Type, t.Pack)).Count();
                            if (before < function.Parameters.Length)
                                yield return (t, call.Arguments.Length - before);
                        }
                        break;
                    }
                }
            }
        }

        private static bool IsPack(TypeSyntax type, string pack) =>
            type is NamedTypeSyntax { Name.Parts: [var only], Arguments.Length: 0 } && only == pack;

        // ── One expansion ──

        private static MemberDecl Instantiate(Template template, int count, SourceText source, DiagnosticBag diagnostics)
        {
            var pack = template.Generics[^1];
            string[] names = [.. Enumerable.Range(0, count).Select(i => $"{pack.Name}{i}")];
            GenericParamSyntax[] generics = [.. template.Generics[..^1], .. names.Select(n => new GenericParamSyntax(n, pack.Span) { Constraints = pack.Constraints })];

            // The pack's members: fields of the pack's type, and the members `static loop`s declare (numbered per element,
            // from the loop's `from`), each with the number its first element's member has.
            var fields = new Dictionary<string, int>(StringComparer.Ordinal);
            if (template.Declaration is TypeDecl declared)
            {
                foreach (var member in declared.Members)
                {
                    if (member is FieldDecl field && IsPack(field.Type, pack.Name))
                        fields[field.Name] = 0;
                    else if (member is StaticLoopDecl loop && loop.Pack == pack.Name)
                    {
                        foreach (var name in loop.Members.Select(NameOf).OfType<string>())
                            fields[name] = loop.First;
                    }
                }
            }

            MemberDecl Member(MemberDecl member)
            {
                switch (member)
                {
                    case FunctionDecl function:
                    {
                        var (parameters, packed) = Parameters(function.Parameters);
                        return Body(function with { Parameters = parameters }, packed);
                    }
                    case ConstructorDecl constructor:
                    {
                        var (parameters, packed) = Parameters(constructor.Parameters);
                        return Body(constructor with { Parameters = parameters }, packed);
                    }
                    default:
                        return Body(member, []);
                }
            }

            (ParameterSyntax[], Dictionary<string, int>) Parameters(ParameterSyntax[] parameters)
            {
                var result = new List<ParameterSyntax>();
                var packed = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var parameter in parameters)
                {
                    if (!IsPack(parameter.Type, pack.Name))
                    {
                        result.Add(parameter);
                        continue;
                    }
                    packed[parameter.Name] = 0;
                    for (int i = 0; i < count; i++)
                        result.Add(parameter with { Name = $"{parameter.Name}{i}", Type = Named(names[i], parameter.Type.Span) });
                }
                return ([.. result], packed);
            }

            // Bodies: `static loop` over the pack unrolls; the pack as a type argument spreads.
            T Body<T>(T node, Dictionary<string, int> parameters) where T : Node
            {
                var packMembers = new Dictionary<string, int>(fields, StringComparer.Ordinal);
                foreach (var (name, first) in parameters)
                    packMembers[name] = first;
                return AstRewriter.Rewrite(node, n => Rewrite(n, packMembers));
            }

            Node? Rewrite(Node node, Dictionary<string, int> packMembers)
            {
                switch (node)
                {
                    case StaticLoopStmt loop when loop.Pack == pack.Name:
                    {
                        var copies = new List<Stmt>();
                        for (int i = 0; i < count; i++)
                        {
                            int index = i;
                            var body = AstRewriter.Rewrite(loop.Body, n => Element(n, loop.Name, index, packMembers));
                            copies.Add(AstRewriter.Rewrite(body, n => Rewrite(n, packMembers)));
                        }
                        return new BlockStmt([.. copies], loop.Span);
                    }
                    case StaticLoopStmt other:
                        diagnostics.Error("AN1422", $"`static loop` goes over the template's pack (`{pack.Name}`), not `{other.Pack}`", source, other.Span);
                        return new BlockStmt([], other.Span);
                    case NamedTypeSyntax named when named.Arguments.Any(a => IsPack(a, pack.Name)):
                    {
                        var arguments = named.Arguments.SelectMany(a => IsPack(a, pack.Name)
                            ? names.Select(n => (TypeSyntax)Named(n, a.Span))
                            : [AstRewriter.Rewrite(a, n => Rewrite(n, packMembers))]).ToArray();
                        return named with { Arguments = arguments };
                    }
                    case NamedTypeSyntax named when IsPack(named, pack.Name):
                        diagnostics.Error("AN1421", $"the pack `{pack.Name}` is a list of types: use it inside `static loop(T in {pack.Name})` or as a type argument",
                            source, named.Span);
                        return named;
                    default:
                        return null;
                }
            }

            // Inside one unrolled element: the element type, and the pack members of element `index`.
            Node? Element(Node node, string variable, int index, Dictionary<string, int> packMembers) => node switch
            {
                NamedTypeSyntax { Name.Parts: [var only], Arguments.Length: 0 } named when only == variable => Named(names[index], named.Span),
                NameExpr name when packMembers.TryGetValue(name.Name, out int first) => name with { Name = $"{name.Name}{index + first}" },
                MemberExpr member when packMembers.TryGetValue(member.Name, out int first) =>
                    member with { Name = $"{member.Name}{index + first}", Target = AstRewriter.Rewrite(member.Target, n => Element(n, variable, index, packMembers)) },
                _ => null,
            };

            switch (template.Declaration)
            {
                case TypeDecl declaration:
                {
                    var members = new List<MemberDecl>();
                    foreach (var member in declaration.Members)
                    {
                        switch (member)
                        {
                            case FieldDecl field when IsPack(field.Type, pack.Name):
                                for (int i = 0; i < count; i++)
                                    members.Add(field with { Name = $"{field.Name}{i}", Type = Named(names[i], field.Type.Span) });
                                break;
                            case StaticLoopDecl loop when loop.Pack == pack.Name:
                                // Its members once per element: the element's type, each name numbered.
                                for (int i = 0; i < count; i++)
                                {
                                    int index = i;
                                    var packMembers = new Dictionary<string, int>(fields, StringComparer.Ordinal);
                                    foreach (var written in loop.Members)
                                    {
                                        // The section the loop is written in (`public:`) is its members'.
                                        var inner = (written.Modifiers & Modifiers.Access) == 0 ? written with { Modifiers = written.Modifiers | (loop.Modifiers & Modifiers.Access) } : written;
                                        var element = AstRewriter.Rewrite(inner, n => Element(n, loop.Name, index, packMembers));
                                        members.Add(Member(Renamed(element, index + loop.First)));
                                    }
                                }
                                break;
                            case StaticLoopDecl other:
                                diagnostics.Error("AN1422", $"`static loop` goes over the template's pack (`{pack.Name}`), not `{other.Pack}`", source, other.Span);
                                break;
                            default:
                                members.Add(Member(member));
                                break;
                        }
                    }
                    return declaration with { Generics = generics, Members = [.. members], Bases = [.. declaration.Bases.Select(b => Body(b, []))] };
                }
                case FunctionDecl function:
                    return (FunctionDecl)Member(function) with { Generics = generics };
                default:
                    throw new InvalidOperationException();
            }
        }

        /// <summary>The name a member declares (a field, property, function), or null.</summary>
        private static string? NameOf(MemberDecl member) => member switch
        {
            FieldDecl f => f.Name,
            PropertyDecl p => p.Name,
            FunctionDecl f => f.Name,
            _ => null,
        };

        /// <summary>A member declared once per element, its name numbered.</summary>
        private static MemberDecl Renamed(MemberDecl member, int index) => member switch
        {
            FieldDecl f => f with { Name = $"{f.Name}{index}" },
            PropertyDecl p => p with { Name = $"{p.Name}{index}" },
            FunctionDecl f => f with { Name = $"{f.Name}{index}" },
            _ => member,
        };

        private static NamedTypeSyntax Named(string name, TextSpan span) => new(new QualifiedName([name], span), [], span);
    }
}
