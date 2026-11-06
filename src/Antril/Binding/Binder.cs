using System.Diagnostics;
using System.Numerics;
using Antril.Syntax;
using static Larva.Hir.Primitives;
using static Larva.Hir.HirTypes;
using H = Larva.Hir;

namespace Antril.Binding
{
    /// <summary>
    /// Binds parsed Antril to a Larva HIR assembly: declarations first (types, then their members and signatures), then
    /// every body, on several threads (each body reports into its own bag; the bags join in declaration order).
    /// </summary>
    internal sealed partial class Binder
    {
        internal readonly H.HirAssembly Assembly;
        internal readonly DiagnosticBag Diagnostics;
        private readonly CompilationUnit[] units;
        private readonly bool parallel;

        /// <summary>Types by full name.</summary>
        internal readonly Dictionary<string, TypeSymbol> Types = new(StringComparer.Ordinal);

        /// <summary>The synthetic class of each namespace's top-level functions and fields.</summary>
        internal readonly Dictionary<string, TypeSymbol> Modules = new(StringComparer.Ordinal);

        /// <summary>The extension blocks of the program and its references (<c>ext(i32 x) { … }</c>).</summary>
        internal readonly List<TypeSymbol> Extensions = [];

        /// <summary>The types that give primitives their members (<c>[Primitive("i32")] value Int32</c>).</summary>
        internal readonly Dictionary<H.Primitive, TypeSymbol> PrimitiveTypes = [];

        /// <summary>The core library's string (<c>str</c>) and root object (<c>any</c>) classes, when the program has them.</summary>
        internal TypeSymbol? StringSymbol => Types.GetValueOrDefault("System.String");
        internal TypeSymbol? ObjectSymbol => Types.GetValueOrDefault("System.Any");

        /// <summary>The core library's <c>System.Type</c> (what <c>typeof</c> gives), when the program has it.</summary>
        internal TypeSymbol? TypeSymbol => Types.GetValueOrDefault("System.Type");

        /// <summary>
        /// The type <c>str</c> names: <c>String&lt;u8&gt;</c> (text in UTF-8) when the core library's string is generic over
        /// its code unit, else the string class itself.
        /// </summary>
        internal H.HirType? StringType => StringSymbol is { } text
            ? text.GenericCount == 1 ? new H.NamedType(text.Token, [H.HirType.U8]) : text.Type
            : null;

        /// <summary>Every namespace something is declared in (to tell a namespace from a type in <c>A.B.C</c>).</summary>
        internal readonly HashSet<string> Namespaces = new(StringComparer.Ordinal) { "" };

        private readonly List<(TypeSymbol Owner, Node Declaration, DeclarationScope Scope)> pending = [];

        // ── Referenced assemblies ──
        private readonly IReadOnlyList<H.HirAssembly> references;

        /// <summary>The types of referenced assemblies, by their reference token.</summary>
        private readonly Dictionary<H.TypeToken, TypeSymbol> external = [];


        private readonly List<MethodSymbol> bodies = [];

        /// <summary>Records what binding finds where (set when analyzing a document for an editor).</summary>
        internal BindingRecorder? Recorder { get; init; }

        /// <summary>What the project sets (its default access, whether borrows are on).</summary>
        public AntrilOptions Options { get; init; } = AntrilOptions.Default;

        /// <summary>The operators the program and its references declare (their precedence goes into the HIR).</summary>
        internal OperatorTable Operators { get; init; } = OperatorTable.Empty;

        /// <summary>
        /// The names of the referenced assemblies built for another GC (a dependency with its own <c>gc:</c>): their types
        /// get a <see cref="TypeSymbol.Domain"/>, and the body binder reports what cannot cross to them (AN1079).
        /// </summary>
        public HashSet<string> ExternalAssemblies { get; } = new(StringComparer.Ordinal);

        public Binder(string assemblyName, CompilationUnit[] units, DiagnosticBag diagnostics, bool parallel, IReadOnlyList<H.HirAssembly>? references = null)
        {
            Assembly = new H.HirAssembly { Name = assemblyName };
            this.references = references ?? [];
            this.units = units;
            Diagnostics = diagnostics;
            this.parallel = parallel;
        }

        internal void Error(string code, string message, SourceText source, TextSpan span) => Diagnostics.Error(code, message, source, span);

        public H.HirAssembly Bind(Dictionary<string, long> timings)
        {
            var watch = Stopwatch.StartNew();
            LoadReferences();
            timings["references"] = watch.ElapsedMilliseconds;
            foreach (var unit in units)
            {
                Assembly.Files.Add(unit.Source.Path);
                Collect(unit.Members, ImportScope(unit));
            }
            timings["declarations.collect"] = watch.ElapsedMilliseconds;
            DeclareFriends();
            foreach (var type in Types.Values)
            {
                if (type.Declaration is { } declaration)
                {
                    for (int g = 0; g < declaration.Generics.Length; g++)
                        type.Definition.Generics[g] = Constrained(declaration.Generics[g], type.Scope, new H.GenericParamType(H.GenericOwner.Type, type.OuterGenericCount + g));
                }
            }
            foreach (var type in Types.Values)
                ResolveBase(type);
            // Constants first: enum values and other constants may name them, wherever they are declared.
            foreach (var (owner, declaration, scope) in pending)
                if (declaration is FieldDecl { Modifiers: var m } && (m & Modifiers.Const) != 0)
                    DeclareMember(owner, declaration, scope);
            foreach (var (owner, declaration, scope) in pending)
                if (!(declaration is FieldDecl { Modifiers: var m } && (m & Modifiers.Const) != 0))
                    DeclareMember(owner, declaration, scope);
            foreach (var constant in constants)
                ConstValue(constant);
            timings["declarations.members"] = watch.ElapsedMilliseconds;
            AddRecordMembers();
            foreach (var type in Types.Values.Where(t => t.IsClass))
            {
                CheckOverrides(type);
                CheckAbstract(type);
                CheckInPlace(type);
            }
            foreach (var type in Types.Values)
                CheckInterfaces(type);
            foreach (var unit in units)
                CheckImports(unit);
            if (FindEntry() is { } entry)
                Assembly.EntryPoint = entry.Token;
            timings["declarations"] = watch.ElapsedMilliseconds;
            watch.Restart();
            BindBodies();
            CheckInPlaceUses();
            timings["bodies"] = watch.ElapsedMilliseconds;
            return Assembly;
        }

        // ── Imports ────────────────────────────────────────

        /// <summary>
        /// A file's scope from its imports: the namespaces it imports whole (<c>import D;</c>), the namespaces it names by an
        /// alias (<c>import D as X;</c>) and the names it takes one by one (<c>import A, B from D as E, F;</c>: E is D.A).
        /// </summary>
        private static DeclarationScope ImportScope(CompilationUnit unit)
        {
            var whole = new List<string>();
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var picked = new Dictionary<string, (string Namespace, string Name)>(StringComparer.Ordinal);
            foreach (var import in unit.Imports)
            {
                string ns = import.Name.ToString();
                if (import.Picked.Length > 0)
                {
                    foreach (var name in import.Picked)
                        picked.TryAdd(name.Visible, (ns, name.Name));
                }
                else if (import.Alias is { } alias)
                    aliases.TryAdd(alias, ns);
                else if (!whole.Contains(ns))
                    whole.Add(ns);
            }
            return new DeclarationScope(unit.Source, "", [.. whole]) { NamespaceAliases = aliases, Picked = picked };
        }

        /// <summary>
        /// What a file imports exists, once every declaration is known: the namespaces it names by an alias or takes names
        /// from, and each name taken (a type, a function, a field or constant, or a namespace inside it); no name is
        /// brought in twice.
        /// </summary>
        private void CheckImports(CompilationUnit unit)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var import in unit.Imports)
            {
                string ns = import.Name.ToString();
                if ((import.Alias is not null || import.Picked.Length > 0) && !Namespaces.Contains(ns))
                {
                    Error("AN1084", $"there is no namespace `{ns}` to import from", unit.Source, import.Name.Span);
                    continue;
                }
                if (import.Alias is { } alias && !seen.TryAdd(alias, ns))
                    Error("AN1085", $"`{alias}` is imported twice (as `{seen[alias]}` and as `{ns}`)", unit.Source, import.AliasSpan);
                foreach (var name in import.Picked)
                {
                    string full = $"{ns}.{name.Name}";
                    bool exists = Namespaces.Contains(full) || Types.Keys.Any(k => k == full || k.StartsWith(full + "`", StringComparison.Ordinal))
                        || Modules.TryGetValue(ns, out var module) && (module.Methods.ContainsKey(name.Name) || module.Fields.ContainsKey(name.Name));
                    if (!exists)
                        Error("AN1084", $"`{ns}` has no `{name.Name}` to import (a type, a function, a constant or field, or a namespace)", unit.Source, name.Span);
                    else if (!seen.TryAdd(name.Visible, full))
                        Error("AN1085", $"`{name.Visible}` is imported twice (as `{seen[name.Visible]}` and as `{full}`)", unit.Source, name.Alias is null ? name.Span : name.AliasSpan);
                }
            }
        }

        /// <summary>
        /// A written name with its first part read through the file's imports: an alias of a namespace (<c>X.List</c> is
        /// <c>D.List</c>) or a name taken from one (<c>E</c> is <c>D.A</c>, <c>E.Inner</c> is <c>D.A.Inner</c>); null when the
        /// first part is neither.
        /// </summary>
        internal static string? Imported(string written, DeclarationScope scope)
        {
            int dot = written.IndexOf('.');
            string head = dot < 0 ? written : written[..dot];
            string rest = dot < 0 ? "" : written[dot..];
            if (scope.NamespaceAliases.TryGetValue(head, out var ns))
                return dot < 0 ? null : ns + rest;
            if (scope.Picked.TryGetValue(head, out var picked))
                return $"{picked.Namespace}.{picked.Name}{rest}";
            return null;
        }

        // ── Collecting types ───────────────────────────────

        private void Collect(MemberDecl[] members, DeclarationScope scope)
        {
            foreach (var member in members)
            {
                switch (member)
                {
                    case NamespaceDecl ns:
                    {
                        string name = scope.Namespace.Length == 0 ? ns.Name.ToString() : $"{scope.Namespace}.{ns.Name}";
                        for (string n = name; n.Length > 0; n = n.Contains('.') ? n[..n.LastIndexOf('.')] : "")
                            Namespaces.Add(n);
                        Collect(ns.Members, scope with { Namespace = name });
                        break;
                    }
                    case TypeDecl type:
                        CollectType(type, scope);
                        break;
                    case FunctionDecl or FieldDecl:
                        pending.Add((Module(scope), member, scope));
                        break;
                    default:
                        Error("AN1001", "only types, functions and fields go at the top level", scope.Source, member.Span);
                        break;
                }
            }
        }

        /// <summary>The class holding the top-level functions and fields of a namespace (made on first use).</summary>
        private TypeSymbol Module(DeclarationScope scope)
        {
            if (Modules.TryGetValue(scope.Namespace, out var known))
                return known;
            var definition = new H.TypeDef
            {
                Namespace = scope.Namespace,
                Name = "$Module",
                Kind = H.TypeKind.Class,
                Flags = H.TypeFlags.Static | H.TypeFlags.Abstract | H.TypeFlags.Generated,
                Access = H.Access.Public,
            };
            var token = Assembly.Add(definition);
            var module = new TypeSymbol(scope.Namespace, definition.Name, null, scope, definition, token) { IsModule = true };
            Modules[scope.Namespace] = module;
            return module;
        }

        /// <summary>
        /// A type of the program. A nested type is named after its outer types (<c>Outer.Inner</c>, in the outer type's
        /// namespace) and takes the outer types' type parameters first.
        /// </summary>
        private void CollectType(TypeDecl declaration, DeclarationScope scope, TypeSymbol? outer = null)
        {
            string typeName = outer is null ? declaration.Name : $"{outer.Name}.{declaration.Name}";
            string fullName = scope.Namespace.Length == 0 ? typeName : $"{scope.Namespace}.{typeName}";
            // Types may share a name when their numbers of type parameters differ (Task, Task<T>): the first keeps the
            // name as its key, the others are keyed with `N.
            int arity = (outer?.Scope.TypeParameters.Length ?? 0) + declaration.Generics.Length;
            string key = fullName;
            if (Types.TryGetValue(fullName, out var same))
            {
                key = $"{fullName}`{arity}";
                if (same.GenericCount == arity || Types.ContainsKey(key))
                {
                    Error("AN1002", $"`{fullName}` is declared twice", scope.Source, declaration.NameSpan);
                    return;
                }
            }
            var (kind, flags) = declaration.Kind switch
            {
                Syntax.TypeKind.Class => (H.TypeKind.Class, H.TypeFlags.None),
                Syntax.TypeKind.BaseClass => (H.TypeKind.Class, H.TypeFlags.Abstract),
                Syntax.TypeKind.RecordClass => (H.TypeKind.Class, H.TypeFlags.Record),
                Syntax.TypeKind.RecordValue => (H.TypeKind.Struct, H.TypeFlags.Record),
                Syntax.TypeKind.Value => (H.TypeKind.Struct, H.TypeFlags.None),
                Syntax.TypeKind.Interface => (H.TypeKind.Interface, H.TypeFlags.Abstract),
                Syntax.TypeKind.Enum => (H.TypeKind.Enum, H.TypeFlags.None),
                Syntax.TypeKind.Extension => (H.TypeKind.Class, H.TypeFlags.Abstract | H.TypeFlags.Generated | H.TypeFlags.Extension),
                _ => (H.TypeKind.Bitflags, H.TypeFlags.None),
            };
            if ((declaration.Modifiers & Modifiers.Static) != 0)
                flags |= H.TypeFlags.Static | H.TypeFlags.Abstract;
            if ((declaration.Modifiers & Modifiers.Final) != 0)
                flags |= H.TypeFlags.Final;
            if ((declaration.Modifiers & Modifiers.Unsafe) != 0)
            {
                flags |= H.TypeFlags.Unsafe;
                if (Options.DeniesUnsafe(scope.Source.Path))
                    Error("AN1104", $"`unsafe {declaration.Name}`: " + AntrilOptions.UnsafeDenied, scope.Source, declaration.NameSpan);
            }
            H.Primitive? primitive = null;
            foreach (var attribute in declaration.Attributes)
            {
                if (attribute.Name.ToString() is not ("Primitive" or "System.Primitive"))
                    continue;
                if (declaration.Kind != Syntax.TypeKind.Value || attribute.Arguments is not [StringLiteral { Value: var keyword }] || PrimitiveNamed(keyword) is not { } named)
                {
                    Error("AN1003", "`[Primitive(\"i32\")]` goes on a value type and names a primitive type", scope.Source, attribute.Span);
                    continue;
                }
                primitive = named;
                flags |= H.TypeFlags.Primitive;
            }
            var (line, column) = scope.Source.LineColumn(declaration.NameSpan.Start);
            var definition = new H.TypeDef
            {
                Namespace = scope.Namespace,
                Name = typeName,
                DeclaringType = outer is null ? -1 : outer.Token.Index,
                Kind = kind,
                Flags = flags,
                Access = AccessFor(declaration.Modifiers, H.Access.Internal),
                Location = new H.SourceLocation(Assembly.Files.Count - 1, line, column),
                Doc = scope.Source.DocBefore(declaration.Span.Start),
            };
            string[] outerParameters = outer?.Scope.TypeParameters ?? [];
            foreach (var g in outerParameters)
                definition.Generics.Add(new H.GenericParam(g));
            foreach (var g in declaration.Generics)
                definition.Generics.Add(new H.GenericParam(g.Name));
            if (primitive is { } p)
                definition.Underlying = H.HirType.Of(p);
            var token = Assembly.Add(definition);
            scope = scope with
            {
                TypeParameters = [.. outerParameters, .. declaration.Generics.Select(g => g.Name)],
                EnclosingTypes = [fullName, .. scope.EnclosingTypes],
            };
            var symbol = new TypeSymbol(scope.Namespace, typeName, declaration, scope, definition, token) { OuterGenericCount = outerParameters.Length, Outer = outer };
            Types[key] = symbol;
            scope.EnclosingSymbol = symbol;
            if (symbol.IsExtension)
            {
                symbol.ExtensionReceiver = declaration.ExtensionReceiver;
                Extensions.Add(symbol);
            }
            if (primitive is { } kindOf)
            {
                symbol.Type = H.HirType.Of(kindOf);
                if (!PrimitiveTypes.TryAdd(kindOf, symbol))
                    Error("AN1004", $"`{H.Text.HirPrinter.Print(symbol.Type, Assembly)}` already has its members in `{PrimitiveTypes[kindOf].FullName}`", scope.Source, declaration.NameSpan);
            }
            if (declaration.Kind is Syntax.TypeKind.Enum or Syntax.TypeKind.BitflagEnum)
            {
                definition.Underlying = H.HirType.I32;
                pending.Add((symbol, declaration, scope));
                return;
            }
            foreach (var member in declaration.Members)
            {
                if (member is TypeDecl nested)
                {
                    CollectType(nested, scope, symbol);
                    continue;
                }
                pending.Add((symbol, member, scope));
            }
        }

        /// <summary>The access a declaration says, else the project's default (BUILD <c>access.default</c>), else the language's.</summary>
        private H.Access AccessFor(Modifiers modifiers, H.Access language) => AccessOf(modifiers, Options.DefaultAccess ?? language);

        internal static H.Access AccessOf(Modifiers modifiers, H.Access otherwise) => (modifiers & Modifiers.Access) switch
        {
            Modifiers.Public => H.Access.Public,
            Modifiers.Friendly => H.Access.Friendly,
            Modifiers.Internal => H.Access.Internal,
            Modifiers.Inherited => H.Access.Inherited,
            Modifiers.File => H.Access.File,
            Modifiers.Private => H.Access.Private,
            _ => otherwise,
        };

        /// <summary>
        /// A class's base class (the first of its bases, when it is a class; else <c>System.Any</c> when the program has
        /// it) and the interfaces a type implements.
        /// </summary>
        private void ResolveBase(TypeSymbol type)
        {
            if (type.Declaration is not { } declaration)
                return;
            var bases = declaration.Bases;
            if (type.IsExtension && declaration.ExtensionTarget is { } extended)
            {
                // The extended type, in the block's type parameters (`ext<T : bitflag enum>(T flags)`).
                if (ResolveType(extended, type.Scope, Diagnostics) is { } target)
                {
                    CheckDeclared(target, OwnGeneric(type, null), type.Scope.Source, extended.Span, values: false);
                    type.Definition.Underlying = target;
                }
                return;
            }
            for (int i = 0; i < bases.Length; i++)
            {
                var syntax = bases[i];
                // `attribute SubTag : Tag`: the base is TagAttribute when Tag is not a type of its own.
                if (declaration.Implied is { } ending && syntax is NamedTypeSyntax { Name.Parts: [var only] } written && !only.EndsWith(ending, StringComparison.Ordinal)
                    && ResolveType(syntax, type.Scope, new DiagnosticBag()) is null)
                    syntax = written with { Name = new QualifiedName([only + ending], written.Name.Span) };
                if (ResolveType(syntax, type.Scope, Diagnostics) is not { } resolved)
                    continue;
                CheckDeclared(resolved, OwnGeneric(type, null), type.Scope.Source, syntax.Span, values: false);
                var found = resolved is H.NamedType named ? SymbolOf(named) : null;
                if (type.IsEnum)
                {
                    // `enum Small : u8`, `enum Word : str`: the base is what the members' values are.
                    if (i > 0)
                        Error("AN1063", "an enum has one base: an integer type, `str` or a `value`", type.Scope.Source, syntax.Span);
                    else if (resolved is H.PrimitiveType { Kind: var kind } && kind.IsInteger())
                        type.Definition.Underlying = resolved;
                    else if (StringType is { } text && resolved.Equals(text))
                    {
                        if (type.Definition.Kind == H.TypeKind.Bitflags)
                            Error("AN1064", "a bitflag enum's base is an integer type (its members are bits)", type.Scope.Source, syntax.Span);
                        else
                            type.Definition.Underlying = resolved;
                    }
                    else if (found is { IsStruct: true } && resolved is H.NamedType)
                    {
                        // `enum Color : Rgb { Red = new Rgb(255, 0, 0) }`: each member stands for a value of the base.
                        if (type.Definition.Kind == H.TypeKind.Bitflags)
                            Error("AN1064", "a bitflag enum's base is an integer type (its members are bits)", type.Scope.Source, syntax.Span);
                        else
                            type.Definition.Underlying = resolved;
                    }
                    else
                        Error("AN1006", $"`{type.Scope.Source[syntax.Span]}` is not an integer type, `str` or a `value`: an enum's base is one of those (an enum implements no interfaces)", type.Scope.Source, syntax.Span);
                    continue;
                }
                if (found is { IsInterface: true })
                {
                    type.Interfaces.Add((H.NamedType)resolved);
                    type.Definition.Interfaces.Add(resolved);
                    continue;
                }
                if (i > 0 || !type.IsClass || found is not { IsClass: true })
                {
                    Error("AN1008", type.IsClass
                        ? $"`{type.Scope.Source[syntax.Span]}` is not an interface: a class extends one class (first) and implements interfaces"
                        : $"`{type.Scope.Source[syntax.Span]}` is not an interface: only classes have a base class", type.Scope.Source, syntax.Span);
                    continue;
                }
                if ((found.Definition.Flags & H.TypeFlags.Final) != 0)
                    Error("AN1009", $"`{found.Name}` is final: no class can derive from it", type.Scope.Source, syntax.Span);
                if ((type.Definition.Flags & H.TypeFlags.Record) != 0 || (found.Definition.Flags & H.TypeFlags.Record) != 0)
                {
                    Error("AN1076", (type.Definition.Flags & H.TypeFlags.Record) != 0
                        ? $"a record class extends no class (`{type.Name}` may implement interfaces)"
                        : $"`{found.Name}` is a record class: no class derives from it", type.Scope.Source, syntax.Span);
                    continue;
                }
                bool cycle = false;
                for (var b = found; b is not null && !cycle; b = b.Base)
                    cycle = b == type;
                if (cycle)
                {
                    Error("AN1010", $"`{type.Name}` inherits from itself", type.Scope.Source, syntax.Span);
                    continue;
                }
                type.Base = found;
                type.BaseType = (H.NamedType)resolved;
                type.Definition.Base = resolved;
            }
            // `attribute Tag` / `exception NotFound`: System.Attribute / System.Exception when no class of that kind is named.
            if (declaration.Implied is { } impliedKind)
            {
                if (Types.GetValueOrDefault("System." + impliedKind) is not { IsClass: true } implied)
                    Error("AN1096", $"`{impliedKind.ToLowerInvariant()}` declares a class derived from `System.{impliedKind}`, which this program does not have (the core library has it)", type.Scope.Source, declaration.NameSpan);
                else if (type.Base is null)
                {
                    type.Base = implied;
                    type.BaseType = (H.NamedType)implied.Type;
                    type.Definition.Base = implied.Type;
                }
                else if (!type.Base.IsOrDerives(implied))
                    Error("AN1096", $"`{type.Name}` is {(impliedKind == "Attribute" ? "an attribute" : "an exception")}: its base is `System.{impliedKind}` or derives from it, not `{type.Base.Name}`",
                        type.Scope.Source, declaration.NameSpan);
            }
            if (type.IsClass && type.Base is null && ObjectSymbol is { } root && root != type)
            {
                type.Base = root;
                type.BaseType = (H.NamedType)root.Type;
                type.Definition.Base = root.Type;
            }
        }

        /// <summary>
        /// A type parameter with its constraints (types it implements or derives from, groups of types, a list, operators).
        /// <paramref name="self"/> is the parameter as a type: <c>number</c> is <c>INumber&lt;self&gt;</c>.
        /// </summary>
        internal H.GenericParam Constrained(GenericParamSyntax syntax, DeclarationScope scope, H.HirType self)
        {
            var types = new List<H.HirType>();
            var oneOf = new List<H.HirType>();
            var operators = new List<H.OperatorRequirement>();
            var convertsTo = new List<H.HirType>();
            var constructors = new List<H.HirType[]>();
            int fixedSize = 0;
            var kinds = H.ConstraintKinds.None;
            foreach (var constraint in syntax.Constraints)
            {
                if (constraint.Constructor is { } constructor)
                {
                    if (constructor.Length == 0)
                        kinds |= H.ConstraintKinds.New;
                    else
                    {
                        var parameterTypes = constructor.Select(t => ResolveType(t, scope, Diagnostics)).ToArray();
                        if (parameterTypes.All(t => t is not null))
                            constructors.Add([.. parameterTypes.Select(t => t!)]);
                    }
                }
                else if (constraint.FixedSize is { } size)
                {
                    fixedSize = size;
                    kinds |= H.ConstraintKinds.FixedSize;
                }
                else if (constraint.ConvertsTo is { } conversion)
                {
                    if (ResolveType(conversion, scope, Diagnostics) is { } target)
                        convertsTo.Add(target);
                }
                else if (constraint.Operator is { } op)
                {
                    var operands = op.Operands.Select(o => ResolveType(o, scope, Diagnostics)).ToArray();
                    if (operands.Any(o => o is null) || ResolveType(op.Result, scope, Diagnostics) is not { } result)
                        continue;
                    operators.Add(new H.OperatorRequirement(op.Symbol + (op.Postfix ? PostfixSuffix : ""), [.. operands.Select(o => o!)], result));
                }
                else if (constraint.Word is "attribute" or "exception")
                {
                    // `T : attribute`, `T : exception`: a class derived from System.Attribute / System.Exception.
                    string name = constraint.Word == "attribute" ? "Attribute" : "Exception";
                    if (Types.GetValueOrDefault("System." + name) is { IsClass: true } root)
                        types.Add(root.Type);
                    else
                        Error("AN1046", $"`{constraint.Word}` needs `System.{name}` (the System.Core library)", scope.Source, constraint.Span);
                }
                else if (constraint.Word is "number" or "snumber")
                {
                    // The interfaces of the numbers (System.Core): `number` is `INumber<T>`, `snumber` is `ISignedNumber<T>`.
                    string name = constraint.Word == "number" ? "INumber" : "ISignedNumber";
                    if (Types.GetValueOrDefault("System." + name) is { IsInterface: true, GenericCount: 1 } iface)
                        types.Add(H.HirType.Named(iface.Token, [self]));
                    else
                        Error("AN1046", $"`{constraint.Word}` needs `System.{name}` (the System.Core library)", scope.Source, constraint.Span);
                }
                else if (constraint.Type is { } typeSyntax)
                {
                    if (ResolveType(typeSyntax, scope, Diagnostics) is not { } type)
                        continue;
                    if (SymbolOf(type) is not { IsInterface: true } and not { IsClass: true } || type is not H.NamedType)
                    {
                        Error("AN1040", $"a constraint names an interface or a class, not `{scope.Source[typeSyntax.Span]}`", scope.Source, typeSyntax.Span);
                        continue;
                    }
                    types.Add(type);
                }
                else if (constraint.OneOf is { } list)
                {
                    foreach (var item in list)
                    {
                        if (ResolveType(item, scope, Diagnostics) is { } type)
                            oneOf.Add(type);
                    }
                }
                else
                {
                    kinds |= constraint.Word switch
                    {
                        "value" => H.ConstraintKinds.Value,
                        "plain" => H.ConstraintKinds.Plain,
                        "copy" => H.ConstraintKinds.Copy,
                        "unmanaged" => H.ConstraintKinds.Unmanaged,
                        "managed" or "class" => H.ConstraintKinds.Managed,
                        "fixedsize" => H.ConstraintKinds.FixedSize,
                        "numeric" => H.ConstraintKinds.Numeric,
                        "integer" => H.ConstraintKinds.Integer,
                        "int" => H.ConstraintKinds.SignedInteger,
                        "uint" => H.ConstraintKinds.UnsignedInteger,
                        "enum" => H.ConstraintKinds.Enum,
                        "bitflag enum" => H.ConstraintKinds.Bitflags | H.ConstraintKinds.Enum,
                        _ => H.ConstraintKinds.Float,
                    };
                    // What every enum has: `==` and `!=`; a bitflag enum also `&`, `|`, `^` and `~` (giving the enum).
                    if (constraint.Word is "enum" or "bitflag enum")
                    {
                        operators.Add(new H.OperatorRequirement("==", [self, self], H.HirType.Bool));
                        operators.Add(new H.OperatorRequirement("!=", [self, self], H.HirType.Bool));
                    }
                    if (constraint.Word == "bitflag enum")
                    {
                        foreach (string symbol in new[] { "&", "|", "^" })
                            operators.Add(new H.OperatorRequirement(symbol, [self, self], self));
                        operators.Add(new H.OperatorRequirement("~", [self], self));
                    }
                }
            }
            // The number words are lists of primitives (`numeric`: every integer and float type): the parameter then works
            // like a list constraint (arithmetic, comparisons, literals, casts), specialized for each argument.
            if (NumberKinds(kinds) is { } numbers)
            {
                var allowed = numbers.Select(k => H.HirType.Of(k)).ToList();
                oneOf = oneOf.Count == 0 ? allowed : [.. oneOf.Where(allowed.Contains)];
            }
            // Constrained by a class (`T : Shape`, `T : exception`): an object, held by a handle (thrown, cast to the class).
            if (types.Any(t => SymbolOf(t) is { IsClass: true }))
                kinds |= H.ConstraintKinds.Object;
            return new H.GenericParam(syntax.Name)
            {
                Types = [.. types], OneOf = [.. oneOf], Kinds = kinds, Operators = [.. operators], ConvertsTo = [.. convertsTo],
                Constructors = [.. constructors], FixedSize = fixedSize,
            };
        }

        /// <summary>The suffix of a postfix operator's symbol in an <see cref="H.OperatorRequirement"/> (<c>!.postfix</c>).</summary>
        internal const string PostfixSuffix = ".postfix";

        /// <summary>
        /// The operators a type parameter's constraints ask for: its own, and those the interfaces it is constrained by ask
        /// of their own parameter (<c>T : INumber&lt;T&gt;</c>: INumber's <c>TSelf : op +(TSelf, TSelf) : TSelf</c>, with T
        /// for TSelf), through the interfaces those extend.
        /// </summary>
        internal IEnumerable<H.OperatorRequirement> OperatorsOf(H.GenericParam param)
        {
            foreach (var op in param.Operators)
                yield return op;
            var seen = new HashSet<H.NamedType>();
            foreach (var constraint in param.Types.OfType<H.NamedType>())
            {
                foreach (var op in InterfaceOperators(constraint, seen))
                    yield return op;
            }
        }

        /// <summary>The operators interface <paramref name="iface"/> (with its arguments) and the interfaces it extends require of their parameters.</summary>
        internal IEnumerable<H.OperatorRequirement> InterfaceOperators(H.NamedType iface, HashSet<H.NamedType> seen)
        {
            if (!seen.Add(iface) || SymbolOf(iface) is not { IsInterface: true } symbol)
                yield break;
            foreach (var generic in symbol.Definition.Generics)
            {
                foreach (var op in generic.Operators)
                    yield return new H.OperatorRequirement(op.Symbol, [.. op.Operands.Select(o => o.Substitute(iface.Arguments))], op.Result.Substitute(iface.Arguments));
            }
            foreach (var extended in symbol.Interfaces)
            {
                foreach (var op in InterfaceOperators((H.NamedType)extended.Substitute(iface.Arguments), seen))
                    yield return op;
            }
        }

        /// <summary>
        /// Whether a type has an operator: the operation built into the primitives when every type is a primitive
        /// (<c>+</c> on <c>i32</c>, <c>-</c> of a signed number), else a declared <c>op</c> of an operand's type with
        /// exactly those operands and result.
        /// </summary>
        internal bool HasOperator(H.OperatorRequirement op)
        {
            bool postfix = op.Symbol.EndsWith(PostfixSuffix, StringComparison.Ordinal);
            string symbol = postfix ? op.Symbol[..^PostfixSuffix.Length] : op.Symbol;
            if (op.Operands.All(o => o is H.PrimitiveType) && op.Result is H.PrimitiveType result)
                return !postfix && BuiltinOperator(symbol, [.. op.Operands.Cast<H.PrimitiveType>()], result);
            string? name = Syntax.Parser.OperatorMethodName(symbol, op.Operands.Length);
            if (name is null)
                return false;
            foreach (var operand in op.Operands)
            {
                if (operand is not H.NamedType named || MemberSymbolOf(named) is not { } type)
                    continue;
                foreach (var candidate in type.FindMethods(name))
                {
                    if (candidate.IsStatic && candidate.GenericCount == 0 && candidate.Parameters.Length == op.Operands.Length
                        && candidate.Parameters.Select(p => p.Substitute(named.Arguments)).SequenceEqual(op.Operands)
                        && candidate.Result.Substitute(named.Arguments) == op.Result)
                        return true;
                }
            }
            return false;
        }

        /// <summary>The operators the primitives have (the same table as the IR's built-in requirements).</summary>
        private static bool BuiltinOperator(string symbol, H.PrimitiveType[] operands, H.PrimitiveType result)
        {
            static bool Numeric(H.PrimitiveType t) => t.Kind is not (H.Primitive.Void or H.Primitive.Bool or H.Primitive.Char);
            static bool Integer(H.PrimitiveType t) => t.Kind.IsInteger();
            static bool Float(H.PrimitiveType t) => Numeric(t) && !Integer(t);
            if (operands.Length == 1)
            {
                var o = operands[0];
                return symbol switch
                {
                    "-" or "+" => o == result && (Integer(o) && o.Kind.IsSigned() || Float(o)),
                    "!" => o == result && o.Kind == H.Primitive.Bool,
                    "~" => o == result && Integer(o),
                    _ => false,
                };
            }
            if (operands.Length != 2)
                return false;
            var (a, b) = (operands[0], operands[1]);
            return symbol switch
            {
                "+" or "-" or "*" or "/" or "%" => a == b && a == result && Numeric(a),
                "&" or "|" or "^" => a == b && a == result && (Integer(a) || a.Kind == H.Primitive.Bool),
                "<<" or ">>" => a == result && Integer(a) && Integer(b),
                "==" or "!=" => a == b && result.Kind == H.Primitive.Bool && (Numeric(a) || a.Kind is H.Primitive.Bool or H.Primitive.Char),
                "<" or "<=" or ">" or ">=" => a == b && result.Kind == H.Primitive.Bool && (Numeric(a) || a.Kind == H.Primitive.Char),
                _ => false,
            };
        }

        private static readonly H.Primitive[] SignedKinds = [H.Primitive.I8, H.Primitive.I16, H.Primitive.I32, H.Primitive.I64, H.Primitive.I128];
        private static readonly H.Primitive[] UnsignedKinds = [H.Primitive.U8, H.Primitive.U16, H.Primitive.U32, H.Primitive.U64, H.Primitive.U128];
        private static readonly H.Primitive[] FloatKinds = [H.Primitive.F32, H.Primitive.F64];

        /// <summary>The primitives the number words of a constraint allow (all the words must hold), or null without any.</summary>
        internal static H.Primitive[]? NumberKinds(H.ConstraintKinds kinds)
        {
            IEnumerable<H.Primitive>? allowed = null;
            void Keep(IEnumerable<H.Primitive> these) => allowed = allowed is null ? these : allowed.Intersect(these);
            if ((kinds & H.ConstraintKinds.Numeric) != 0)
                Keep([.. SignedKinds, .. UnsignedKinds, .. FloatKinds]);
            if ((kinds & H.ConstraintKinds.Integer) != 0)
                Keep([.. SignedKinds, .. UnsignedKinds]);
            if ((kinds & H.ConstraintKinds.SignedInteger) != 0)
                Keep(SignedKinds);
            if ((kinds & H.ConstraintKinds.UnsignedInteger) != 0)
                Keep(UnsignedKinds);
            if ((kinds & H.ConstraintKinds.Float) != 0)
                Keep(FloatKinds);
            return allowed?.ToArray();
        }

        internal static H.Primitive? PrimitiveNamed(string keyword) => keyword switch
        {
            "bool" => H.Primitive.Bool,
            "i8" => H.Primitive.I8, "i16" => H.Primitive.I16, "i32" => H.Primitive.I32, "i64" => H.Primitive.I64,
            "i128" => H.Primitive.I128, "i256" => H.Primitive.I256,
            "u8" => H.Primitive.U8, "u16" => H.Primitive.U16, "u32" => H.Primitive.U32, "u64" => H.Primitive.U64,
            "u128" => H.Primitive.U128, "u256" => H.Primitive.U256,
            "f16" => H.Primitive.F16, "f32" => H.Primitive.F32, "f64" => H.Primitive.F64, "f128" => H.Primitive.F128, "f256" => H.Primitive.F256,
            _ => null,
        };

        /// <summary>
        /// The type arguments of <paramref name="ancestor"/> (a base class of the type <paramref name="type"/> names, or that
        /// type itself) as seen from <paramref name="type"/>; empty when it is neither.
        /// </summary>
        internal H.HirType[] AncestorArguments(H.HirType type, TypeSymbol ancestor)
        {
            if (ancestor.GenericCount == 0 || MemberSymbolOf(type) is not { } symbol)
                return [];
            var arguments = type is H.NamedType n ? n.Arguments : [];
            foreach (var (t, a) in symbol.Chain())
            {
                if (t == ancestor)
                    return [.. a.Select(x => x.Substitute(arguments))];
            }
            return [];
        }

        /// <summary>The symbol whose members a value of <paramref name="type"/> has (its class or value type, a primitive's provider).</summary>
        internal TypeSymbol? MemberSymbolOf(H.HirType type) => type switch
        {
            H.NamedType { Type.IsDefinition: true } n => TypesByToken(n.Type.Index),
            H.NamedType n => external.GetValueOrDefault(n.Type),
            H.PrimitiveType p => PrimitiveTypes.GetValueOrDefault(p.Kind),
            _ => null,
        };

        /// <summary>
        /// Whether <paramref name="type"/> implements the interface <paramref name="iface"/> (directly, through a base
        /// class, or through an interface that extends it); <paramref name="parameterConstraints"/> gives the interfaces a
        /// type parameter is constrained by.
        /// </summary>
        internal bool Implements(H.HirType type, H.NamedType iface, Func<H.GenericParamType, IEnumerable<H.HirType>> parameterConstraints)
        {
            if (type is H.GenericParamType param)
                return parameterConstraints(param).OfType<H.NamedType>().Any(c => Extends(c, iface));
            if (MemberSymbolOf(type) is not { } symbol)
                return false;
            if (type == iface)
                return true;
            var arguments = type is H.NamedType n ? n.Arguments : [];
            foreach (var (t, a) in symbol.Chain())
            {
                foreach (var implemented in t.Interfaces)
                {
                    var actual = (H.NamedType)implemented.Substitute([.. a.Select(x => x.Substitute(arguments))]);
                    if (Extends(actual, iface))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Whether interface <paramref name="type"/> is <paramref name="iface"/> or extends it.</summary>
        private bool Extends(H.NamedType type, H.NamedType iface)
        {
            if (type == iface)
                return true;
            if (SymbolOf(type) is not { } symbol)
                return false;
            foreach (var extended in symbol.Interfaces)
            {
                if (Extends((H.NamedType)extended.Substitute(type.Arguments), iface))
                    return true;
            }
            return false;
        }

        // ── Referenced assemblies ──────────────────────────

        /// <summary>
        /// Makes symbols for the types of the referenced assemblies: their metadata, with every type in it named by a
        /// reference of this assembly (so what the program writes with them refers to them, for the linker to resolve).
        /// </summary>
        private void LoadReferences()
        {
            var placeholder = new SourceText("", "<reference>");
            foreach (var reference in references)
            {
                H.TypeToken Map(H.TypeToken token)
                {
                    if (token.IsDefinition)
                    {
                        var target = reference.Types[token.Index];
                        lock (Assembly)
                            return Assembly.Reference(reference.Name, target.Namespace, target.Name, target.Generics.Count);
                    }
                    var other = reference.TypeRefs[token.Index];
                    lock (Assembly)
                        return Assembly.Reference(reference.References[other.Assembly].Name, other.Namespace, other.Name, other.Arity);
                }
                H.HirType M(H.HirType type) => Larva.Hir.Linking.HirLinker.MapTypes(type, Map);
                var symbols = new TypeSymbol[reference.Types.Count];
                for (int i = 0; i < reference.Types.Count; i++)
                {
                    var source = reference.Types[i];
                    var shadow = Shadow(source, M);
                    var token = Map(H.TypeToken.Def(i));
                    var scope = new DeclarationScope(placeholder, source.Namespace, []) { TypeParameters = [.. source.Generics.Select(g => g.Name)] };
                    var symbol = new TypeSymbol(source.Namespace, source.Name, null, scope, shadow, token)
                    {
                        IsModule = source.Name == "$Module",
                        ReferenceMember = ReferenceMember,
                        Domain = ExternalAssemblies.Contains(reference.Name) ? reference.Name : null,
                    };
                    symbols[i] = symbol;
                    external[token] = symbol;
                    for (string n = source.Namespace; n.Length > 0; n = n.Contains('.') ? n[..n.LastIndexOf('.')] : "")
                        Namespaces.Add(n);
                    if (symbol.IsModule)
                        continue;
                    if (symbol.IsExtension)
                    {
                        symbol.ExtensionReceiver = symbol.IsStatic ? null : shadow.Methods.FirstOrDefault()?.Parameters.FirstOrDefault()?.Name;
                        Extensions.Add(symbol);
                    }
                    if ((shadow.Flags & H.TypeFlags.Primitive) != 0 && shadow.Underlying is H.PrimitiveType primitive)
                    {
                        symbol.Type = primitive;
                        PrimitiveTypes.TryAdd(primitive.Kind, symbol);
                    }
                    if (!Types.TryAdd(symbol.FullName, symbol))
                        Types.TryAdd($"{symbol.FullName}`{symbol.GenericCount}", symbol);
                }
                for (int i = 0; i < symbols.Length; i++)
                {
                    var source = reference.Types[i];
                    if (source.DeclaringType >= 0)
                        symbols[i].Outer = symbols[source.DeclaringType];
                    foreach (var attribute in source.Attributes)
                    {
                        if (AttributeOwner(reference, attribute.Constructor) is "System.FriendOfAttribute"
                            && attribute.Arguments is [H.TypeConstant { Type: var friendType }] && SymbolOf(M(friendType)) is { } friend)
                        {
                            symbols[i].Friends.Add(friend);
                            friend.Friends.Add(symbols[i]);
                        }
                    }
                }
                foreach (var symbol in symbols)
                {
                    var shadow = symbol.Definition;
                    if (shadow.Base is H.NamedType b && SymbolOf(b) is { } baseSymbol)
                    {
                        symbol.Base = baseSymbol;
                        symbol.BaseType = b;
                    }
                    foreach (var iface in shadow.Interfaces.OfType<H.NamedType>())
                        symbol.Interfaces.Add(iface);
                    DeclareExternalMembers(symbol);
                }
            }
        }

        private H.MemberToken ReferenceMember(H.MemberRef member)
        {
            lock (Assembly)
                return Assembly.Reference(member);
        }

        /// <summary>A referenced type's metadata with its types mapped (no bodies: they stay in the assembly that has them).</summary>
        private static H.TypeDef Shadow(H.TypeDef source, Func<H.HirType, H.HirType> map)
        {
            H.GenericParam Generic(H.GenericParam g) => g with
            {
                Types = [.. g.Types.Select(map)],
                OneOf = [.. g.OneOf.Select(map)],
                ConvertsTo = [.. g.ConvertsTo.Select(map)],
                ConvertsFrom = [.. g.ConvertsFrom.Select(map)],
                Constructors = [.. g.Constructors.Select(c => c.Select(map).ToArray())],
                Default = g.Default is { } d ? map(d) : null,
            };
            H.HirConstant? Constant(H.HirConstant? value) => value is H.EnumConstant e ? e with { Type = map(e.Type) } : value;
            var shadow = new H.TypeDef
            {
                Namespace = source.Namespace,
                Name = source.Name,
                Kind = source.Kind,
                Flags = source.Flags,
                Access = source.Access,
                Base = source.Base is { } b ? map(b) : null,
                Underlying = source.Underlying is { } u ? map(u) : null,
            };
            shadow.Generics.AddRange(source.Generics.Select(Generic));
            shadow.Interfaces.AddRange(source.Interfaces.Select(map));
            foreach (var field in source.Fields)
                shadow.Fields.Add(field with { Type = map(field.Type), Value = Constant(field.Value) });
            foreach (var method in source.Methods)
            {
                var copy = new H.MethodDef(method.Name, map(method.Result), [.. method.Parameters.Select(p => p with { Type = map(p.Type) })])
                {
                    Kind = method.Kind,
                    Flags = method.Flags,
                    Access = method.Access,
                    ExternName = method.ExternName,
                    Library = method.Library,
                };
                copy.Generics.AddRange(method.Generics.Select(Generic));
                shadow.Methods.Add(copy);
            }
            foreach (var property in source.Properties)
                shadow.Properties.Add(property with { Type = map(property.Type), Indices = [.. property.Indices.Select(map)] });
            return shadow;
        }

        /// <summary>The fields, methods, constructors and properties of a referenced type, as symbols.</summary>
        private void DeclareExternalMembers(TypeSymbol owner)
        {
            var definition = owner.Definition;
            for (int f = 0; f < definition.Fields.Count; f++)
            {
                var field = definition.Fields[f];
                bool isStatic = (field.Flags & (H.FieldFlags.Static | H.FieldFlags.Const)) != 0;
                owner.Fields[field.Name] = new FieldSymbol(owner, field.Name, field.Type, f, isStatic)
                {
                    IsConst = (field.Flags & H.FieldFlags.Const) != 0,
                    Value = field.Value,
                    Scope = owner.Scope,
                };
            }
            var accessors = new Dictionary<int, MethodSymbol>();
            for (int m = 0; m < definition.Methods.Count; m++)
            {
                var method = definition.Methods[m];
                var scope = owner.Scope with { MethodParameters = [.. method.Generics.Select(g => g.Name)] };
                var symbol = new MethodSymbol(owner, method.Kind == H.MethodKind.Constructor ? ".new" : method.Name,
                    [.. method.Parameters.Select(p => p.Type)], [.. method.Parameters.Select(p => p.Name)], method.Result, m)
                {
                    IsStatic = method.IsStatic,
                    IsVirtual = (method.Flags & (H.MethodFlags.Virtual | H.MethodFlags.Abstract | H.MethodFlags.Override)) != 0,
                    IsAbstract = (method.Flags & H.MethodFlags.Abstract) != 0,
                    IsConstructor = method.Kind == H.MethodKind.Constructor,
                    Scope = scope,
                };
                if (symbol.IsConstructor)
                {
                    owner.Constructors.Add(symbol);
                }
                else if (method.Kind is H.MethodKind.Getter or H.MethodKind.Setter)
                {
                    accessors[m] = symbol;
                }
                else
                {
                    if (!owner.Methods.TryGetValue(method.Name, out var list))
                        owner.Methods[method.Name] = list = [];
                    list.Add(symbol);
                }
            }
            for (int p = 0; p < definition.Properties.Count; p++)
            {
                var property = definition.Properties[p];
                owner.Properties[property.Name] = new PropertySymbol(owner, property.Name, property.Type, p, (property.Flags & H.MemberFlags.Static) != 0)
                {
                    Getter = accessors.GetValueOrDefault(property.Getter),
                    Setter = accessors.GetValueOrDefault(property.Setter),
                };
            }
        }

        /// <summary>The type and name a member token names (a definition here, or a reference to a referenced type's member).</summary>
        private (TypeSymbol? Type, string? Name, int Index) MemberOf(H.MemberToken token)
        {
            if (token.IsDefinition)
                return (TypesByToken(token.Type), null, token.Member);
            H.MemberRef reference;
            lock (Assembly)
                reference = Assembly.MemberRefs[token.Member];
            return (external.GetValueOrDefault(reference.Parent), reference.Name, -1);
        }

        /// <summary>The field a token names (of this assembly or a referenced one).</summary>
        internal FieldSymbol? FieldOf(H.MemberToken token)
        {
            var (type, name, index) = MemberOf(token);
            if (type is null)
                return null;
            return name is not null ? type.Fields.GetValueOrDefault(name) : type.Fields.Values.FirstOrDefault(f => f.Index == index);
        }

        /// <summary>The property a token names (of this assembly or a referenced one).</summary>
        internal PropertySymbol? PropertyOf(H.MemberToken token)
        {
            var (type, name, index) = MemberOf(token);
            if (type is null)
                return null;
            return name is not null ? type.Properties.GetValueOrDefault(name) : type.Properties.Values.FirstOrDefault(p => p.Index == index);
        }

        // ── Finding types ──────────────────────────────────

        /// <summary>
        /// A type by name as seen from <paramref name="scope"/>: in the namespace and its parents (innermost first), then in
        /// the namespaces the file imports.
        /// </summary>
        internal TypeSymbol? FindType(QualifiedName name, DeclarationScope scope, int? arity = null)
        {
            if (name.Parts is ["str"])
                return StringSymbol;
            if (name.Parts is ["any"])
                return ObjectSymbol;
            string written = name.ToString();
            // The type of that name with that many type parameters, else the one keyed by the name alone.
            TypeSymbol? Get(string full) =>
                Types.TryGetValue($"{full}`{arity ?? 0}", out var exact) ? exact : Types.GetValueOrDefault(full);
            foreach (var enclosing in scope.EnclosingTypes)
            {
                if (Get($"{enclosing}.{written}") is { } nested)
                    return nested;
            }
            foreach (var ns in scope.Enclosing())
            {
                if (Get(ns.Length == 0 ? written : $"{ns}.{written}") is { } found)
                    return found;
            }
            if (Imported(written, scope) is { } imported && Get(imported) is { } named)
                return named;
            foreach (var import in scope.Imports)
            {
                if (Get($"{import}.{written}") is { } found)
                    return found;
            }
            return null;
        }

        /// <summary>The HIR type a type syntax names (null after an error).</summary>
        internal H.HirType? ResolveType(TypeSyntax syntax, DeclarationScope scope, DiagnosticBag bag)
        {
            switch (syntax)
            {
                case PrimitiveTypeSyntax p:
                    return H.HirType.Of(p.Keyword switch
                    {
                        TokenKind.Bool => H.Primitive.Bool,
                        TokenKind.I8 => H.Primitive.I8, TokenKind.I16 => H.Primitive.I16, TokenKind.I32 => H.Primitive.I32,
                        TokenKind.I64 or TokenKind.IPtr => H.Primitive.I64, TokenKind.I128 => H.Primitive.I128, TokenKind.I256 => H.Primitive.I256,
                        TokenKind.U8 => H.Primitive.U8, TokenKind.U16 => H.Primitive.U16, TokenKind.U32 => H.Primitive.U32,
                        TokenKind.U64 or TokenKind.UPtr => H.Primitive.U64, TokenKind.U128 => H.Primitive.U128, TokenKind.U256 => H.Primitive.U256,
                        TokenKind.F16 => H.Primitive.F16, TokenKind.F32 => H.Primitive.F32, TokenKind.F64 => H.Primitive.F64,
                        TokenKind.F128 => H.Primitive.F128, _ => H.Primitive.F256,
                    });
                case VoidTypeSyntax:
                    return H.HirType.Void;
                case NamedTypeSyntax named:
                {
                    if (named.Name.Parts is [var single] && named.Arguments.Length == 0 && scope.TypeParameter(single) is { } parameter)
                    {
                        Recorder?.Add(new RecordedSpan(scope.Source, named.Name.Span, RecordedKind.Type) { Type = parameter });
                        return parameter;
                    }
                    TypeSymbol? type;
                    if (named.Name.Parts is ["str"] or ["any"])
                    {
                        type = named.Name.Parts[0] == "str" ? StringSymbol : ObjectSymbol;
                        if (type is null)
                        {
                            bag.Error("AN1013", $"`{named.Name}` is `{(named.Name.Parts[0] == "str" ? "System.String" : "System.Any")}` of the core library, which this program does not have", scope.Source, named.Span);
                            return null;
                        }
                    }
                    else if (FindType(named.Name, scope, named.Arguments.Length) is not { } found)
                    {
                        bag.Error("AN1014", $"unknown type `{named.Name}`", scope.Source, named.Span);
                        return null;
                    }
                    else
                    {
                        type = found;
                    }
                    if (named.Name.Span.Length > 0) // a tuple's `(…)` names its ValueTuple without writing it
                        Recorder?.Add(new RecordedSpan(scope.Source, named.Name.Span, RecordedKind.Type) { Token = type.Token, Type = type.Type });
                    CheckTypeAccess(type, scope, named.Name.Span, bag);
                    if (named.Name.Parts is ["str"] && named.Arguments.Length == 0)
                        return StringType;
                    if (type.IsPrimitive && named.Arguments.Length == 0)
                        return type.Type;
                    var arguments = new H.HirType[named.Arguments.Length];
                    for (int a = 0; a < arguments.Length; a++)
                    {
                        if (ResolveType(named.Arguments[a], scope, bag) is not { } argument)
                            return null;
                        if (argument == H.HirType.Void)
                        {
                            bag.Error("AN1041", "a type argument cannot be void", scope.Source, named.Arguments[a].Span);
                            return null;
                        }
                        arguments[a] = argument;
                    }
                    if (type.OuterGenericCount > 0 && arguments.Length == type.GenericCount - type.OuterGenericCount
                        && scope.TypeParameters.Length >= type.OuterGenericCount)
                    {
                        // A nested type of a generic type, named from inside it: the outer type's parameters go first.
                        arguments = [.. Enumerable.Range(0, type.OuterGenericCount).Select(i => (H.HirType)new H.GenericParamType(H.GenericOwner.Type, i)), .. arguments];
                    }
                    if (arguments.Length != type.GenericCount)
                    {
                        bag.Error("AN1012", type.GenericCount == 0
                            ? $"`{type.Name}` takes no type arguments"
                            : $"`{type.Name}` takes {type.GenericCount} type argument(s), not {arguments.Length}", scope.Source, named.Span);
                        return null;
                    }
                    return new H.NamedType(type.Token, arguments);
                }
                case FixedArrayTypeSyntax fixedArray:
                {
                    if (ResolveType(fixedArray.Element, scope, bag) is not { } element)
                        return null;
                    // The length: a number, `sizeof(T)` or `N * sizeof(T)` (then known once T is).
                    (long count, TypeSyntax? sizeOf) = fixedArray.Length switch
                    {
                        IntegerLiteral n => ((long)n.Value, null),
                        SizeOfExpr s => (1, s.Type),
                        BinaryExpr { Operator: BinaryOperator.Multiply, Left: IntegerLiteral n, Right: SizeOfExpr s } => ((long)n.Value, s.Type),
                        BinaryExpr { Operator: BinaryOperator.Multiply, Left: SizeOfExpr s, Right: IntegerLiteral n } => ((long)n.Value, s.Type),
                        _ => (0, null),
                    };
                    if (count <= 0 || count > int.MaxValue)
                    {
                        bag.Error("AN1073", "a fixed array's length is a positive number, `sizeof(T)` or `N * sizeof(T)`", scope.Source, fixedArray.Length.Span);
                        return null;
                    }
                    H.HirType? sizeOfType = null;
                    if (sizeOf is not null && (sizeOfType = ResolveType(sizeOf, scope, bag)) is null)
                        return null;
                    return new H.InlineArrayType(element, (int)count, sizeOfType);
                }
                case ArrayTypeSyntax array:
                {
                    if (array.Rank > 8)
                    {
                        bag.Error("AN1015", "an array has at most 8 dimensions", scope.Source, array.Span);
                        return null;
                    }
                    return ResolveType(array.Element, scope, bag) is { } element ? new H.ArrayType(element, array.Rank) : null;
                }
                case NullableTypeSyntax nullable:
                {
                    if (ResolveType(nullable.Inner, scope, bag) is not { } inner)
                        return null;
                    if (inner == H.HirType.Void || inner is H.NullableType)
                    {
                        bag.Error("AN1016", $"`{scope.Source[nullable.Span]}` cannot be nullable", scope.Source, nullable.Span);
                        return null;
                    }
                    return new H.NullableType(inner);
                }
                case PointerTypeSyntax pointer:
                    if (Options.DeniesUnsafe(scope.Source.Path))
                        bag.Error("AN1104", "a pointer type: " + AntrilOptions.UnsafeDenied, scope.Source, syntax.Span);
                    return ResolveType(pointer.Target, scope, bag) is { } target ? new H.PointerType(target) : null;
                case FuncTypeSyntax func:
                {
                    var parameters = new H.HirType[func.Parameters.Length];
                    for (int p = 0; p < parameters.Length; p++)
                    {
                        if (ResolveType(func.Parameters[p], scope, bag) is not { } parameter)
                            return null;
                        parameters[p] = parameter;
                    }
                    var result = func.Result is { } r ? ResolveType(r, scope, bag) : H.HirType.Void;
                    if (result is not null && func.Native)
                    {
                        if (parameters.Append(result).FirstOrDefault(t => t != H.HirType.Void && !CrossesToC(t, strings: false)) is { } wrong)
                        {
                            bag.Error("AN1057", $"a `func*` takes and gives numbers, bool, pointers and func* (what C holds), not `{H.Text.HirPrinter.Print(wrong, Assembly)}`", scope.Source, func.Span);
                            return null;
                        }
                        return new H.NativeFuncType(parameters, result);
                    }
                    return result is null ? null : new H.FuncType(parameters, result);
                }
                case ReferenceTypeSyntax reference:
                    if (!Options.Borrows)
                        bag.Error("AN1092", $"`{(reference.Mutable ? "&mut" : "&")}` borrows, and this project's borrow checker is off (BUILD `borrowcheck: off`): pass the value itself, or turn it on (`borrowcheck: enforce`)",
                            scope.Source, syntax.Span);
                    return ResolveType(reference.Target, scope, bag) is { } referenced ? new H.ReferenceType(referenced, reference.Mutable) : null;
            }
            bag.Error("AN1017", "unsupported type", scope.Source, syntax.Span);
            return null;
        }

        internal TypeSymbol? SymbolOf(H.HirType type) => type switch
        {
            H.NamedType { Type.IsDefinition: true } n => TypesByToken(n.Type.Index),
            H.NamedType n => external.GetValueOrDefault(n.Type),
            H.NullableType { Inner: var inner } => SymbolOf(inner),
            _ => null,
        };

        private TypeSymbol[]? byToken;

        internal TypeSymbol? TypesByToken(int index)
        {
            if (byToken is null)
            {
                var table = new TypeSymbol[Assembly.Types.Count];
                foreach (var t in Types.Values.Concat(Modules.Values))
                {
                    if (!t.IsExternal)
                        table[t.Token.Index] = t;
                }
                byToken = table;
            }
            return index < byToken.Length ? byToken[index] : null;
        }

        internal bool IsClass(H.HirType type) => SymbolOf(type) is { IsClass: true } && type is H.NamedType;

        // ── Members ────────────────────────────────────────

        private void DeclareMember(TypeSymbol owner, Node declaration, DeclarationScope scope)
        {
            var source = scope.Source;
            switch (declaration)
            {
                case TypeDecl { Kind: Syntax.TypeKind.Enum or Syntax.TypeKind.BitflagEnum } enumeration:
                    DeclareEnum(owner, enumeration);
                    break;
                case FieldDecl or PropertyDecl or ConstructorDecl when owner.IsExtension:
                    Error("AN1075", "an extension block holds functions (no fields, properties or constructors)", source, declaration.Span);
                    break;
                case FieldDecl field:
                    DeclareField(owner, field, scope);
                    break;
                case FunctionDecl function:
                    DeclareMethod(owner, function, scope);
                    break;
                case PropertyDecl property:
                    DeclareProperty(owner, property, scope);
                    break;
                case ConstructorDecl constructor:
                    DeclareConstructor(owner, constructor, scope);
                    break;
                default:
                    Error("AN1018", $"this member comes in a later phase of the new compiler", source, declaration.Span);
                    break;
            }
        }

        /// <summary>
        /// An enum's members: constants of the enum type. Without a value, a member is the one before plus one (a
        /// <c>bitflag enum</c>: the next bit); the values are of the base (<c>i32</c> unless declared). <c>declbits</c> is
        /// all the declared members' bits together and <c>allbits</c> every bit of the base. A string-based enum's members
        /// are their names unless written (<c>Hello = "hola"</c>), numbered by place.
        /// </summary>
        /// <summary>Enums whose members are declared, and those being declared (one may need another's members first).</summary>
        private readonly HashSet<TypeSymbol> enumsDeclared = [], enumsDeclaring = [];

        private void DeclareEnum(TypeSymbol owner, TypeDecl declaration)
        {
            if (!enumsDeclared.Add(owner))
                return;  // declared already: another enum's member asked for its members first
            enumsDeclaring.Add(owner);
            try
            {
                DeclareEnumMembers(owner, declaration);
            }
            finally
            {
                enumsDeclaring.Remove(owner);
            }
        }

        /// <summary>
        /// An integer enum member's value: a constant integer, made of numbers, constants, the enum's earlier members and the
        /// members of other enums (<c>ReadWrite = Read | Write</c>, <c>Both = Access.Read | Mode.Fast</c>), with
        /// <c>| &amp; ^ ~ + - * / % &lt;&lt; &gt;&gt;</c>. False when it is not one (the diagnostic says why).
        /// </summary>
        private bool EnumMemberValue(Expr expression, TypeSymbol owner, Dictionary<string, BigInteger> earlier, H.HirType baseType, out BigInteger value)
        {
            value = 0;
            switch (expression)
            {
                case NameExpr { TypeArguments.Length: 0 } name when earlier.TryGetValue(name.Name, out var own):
                    value = own;
                    return true;
                case MemberExpr { TypeArguments.Length: 0 } member when QualifiedTarget(member.Target) is { } target
                    && FindType(new QualifiedName(target, member.Target.Span), owner.Scope) is { IsEnum: true } enumeration:
                {
                    if (enumeration == owner)
                    {
                        if (earlier.TryGetValue(member.Name, out var mine))
                        {
                            value = mine;
                            return true;
                        }
                        Error("AN1019", $"`{owner.Name}.{member.Name}` is not one of the members before this one", owner.Scope.Source, member.Span);
                        return false;
                    }
                    if (!enumeration.Fields.ContainsKey(member.Name) && enumeration.Declaration is { } other && !enumsDeclaring.Contains(enumeration))
                        DeclareEnum(enumeration, other);
                    if (enumeration.Fields.TryGetValue(member.Name, out var field) && field.Value is H.EnumConstant { Text: null } constant)
                    {
                        value = constant.Value;
                        return true;
                    }
                    Error("AN1019", $"`{enumeration.Name}.{member.Name}` is not an integer member of `{enumeration.Name}`", owner.Scope.Source, member.Span);
                    return false;
                }
                case BinaryExpr binary when binary.Operator is BinaryOperator.Or or BinaryOperator.And or BinaryOperator.Xor or BinaryOperator.Add
                    or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Remainder
                    or BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight:
                {
                    if (!EnumMemberValue(binary.Left, owner, earlier, baseType, out var left) || !EnumMemberValue(binary.Right, owner, earlier, baseType, out var right))
                        return false;
                    if (binary.Operator is BinaryOperator.Divide or BinaryOperator.Remainder && right.IsZero)
                    {
                        Error("AN1019", "an enum member's value divides by zero", owner.Scope.Source, binary.Span);
                        return false;
                    }
                    value = binary.Operator switch
                    {
                        BinaryOperator.Or => left | right,
                        BinaryOperator.And => left & right,
                        BinaryOperator.Xor => left ^ right,
                        BinaryOperator.Add => left + right,
                        BinaryOperator.Subtract => left - right,
                        BinaryOperator.Multiply => left * right,
                        BinaryOperator.Divide => BigInteger.Divide(left, right),
                        BinaryOperator.Remainder => BigInteger.Remainder(left, right),
                        BinaryOperator.ShiftLeft => left << (int)BigInteger.Min(right, 256),
                        _ => left >> (int)BigInteger.Min(right, 256),
                    };
                    return true;
                }
                case UnaryExpr { Operator: UnaryOperator.Complement or UnaryOperator.Negate } unary:
                {
                    if (!EnumMemberValue(unary.Operand, owner, earlier, baseType, out var operand))
                        return false;
                    value = unary.Operator == UnaryOperator.Complement ? ~operand : -operand;
                    return true;
                }
                default:
                    if (Constant(expression, baseType, owner.Scope, Diagnostics, owner) is H.IntegerConstant integer)
                    {
                        value = integer.Value;
                        return true;
                    }
                    return false;
            }

            static string[]? QualifiedTarget(Expr target) => target switch
            {
                NameExpr { TypeArguments.Length: 0 } n => [n.Name],
                MemberExpr { TypeArguments.Length: 0 } m when QualifiedTarget(m.Target) is { } parts => [.. parts, m.Name],
                _ => null,
            };
        }

        private void DeclareEnumMembers(TypeSymbol owner, TypeDecl declaration)
        {
            bool bitflags = declaration.Kind == Syntax.TypeKind.BitflagEnum;
            var baseType = owner.Definition.Underlying ?? H.HirType.I32;
            var kind = baseType is H.PrimitiveType { Kind: var k } ? k : (H.Primitive?)null;
            // A value-based enum (`enum Color : Rgb`): its members are their places, and each stands for the value written.
            var valueBase = kind is null && baseType is H.NamedType && SymbolOf(baseType) is { IsStruct: true } based ? based : null;
            string baseName = kind?.ToString().ToLowerInvariant() ?? valueBase?.Name ?? "str";
            if (valueBase is not null)
                owner.EnumValues = [];
            var members = declaration.Members.OfType<EnumMemberDecl>().ToList();
            var values = new (BigInteger Value, string? Text)?[members.Count];
            var bits = new List<int>();
            var earlier = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
            BigInteger next = bitflags ? 1 : 0;
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member.Value is NameExpr { Name: "declbits" or "allbits", TypeArguments.Length: 0 } word)
                {
                    if (kind is null)
                        Error("AN1019", $"`{word.Name}` is an integer: this enum's members are {(valueBase is null ? "strings" : $"`{baseName}` values")}", owner.Scope.Source, word.Span);
                    else
                        bits.Add(i);
                    continue;
                }
                if (valueBase is not null)
                {
                    if (member.Value is { } given)
                        owner.EnumValues!.Add((member.Name, given));
                    else
                        Error("AN1019", $"each member of this enum stands for a `{baseName}`: `{member.Name} = new {baseName}(…)`", owner.Scope.Source, member.Span);
                    values[i] = (i, null);
                    continue;
                }
                if (kind is null)
                {
                    string text = member.Name;
                    if (member.Value is StringLiteral literal)
                        text = literal.Value;
                    else if (member.Value is { } other)
                        Error("AN1019", "this enum's members are strings (`Hello = \"hola\"`)", owner.Scope.Source, other.Span);
                    values[i] = (i, text);
                    continue;
                }
                BigInteger value = next;
                if (member.Value is { } written)
                {
                    int before = Diagnostics.Count;
                    if (EnumMemberValue(written, owner, earlier, baseType, out var computed))
                    {
                        // `~Read` in an unsigned enum: the bits of its size.
                        value = !kind.Value.IsSigned() && computed.Sign < 0 ? computed & ((BigInteger.One << kind.Value.Bits()) - 1) : computed;
                        if (!Fits(value, kind.Value))
                            Error("AN1019", $"`{member.Name}` is {value}, which does not fit in a `{baseName}`", owner.Scope.Source, written.Span);
                    }
                    else if (Diagnostics.Count == before)
                        Error("AN1019", $"an enum member's value is a constant `{baseName}`: numbers, constants, the members before it and other enums' members (`Read | Write`)", owner.Scope.Source, written.Span);
                }
                else if (!Fits(value, kind.Value))
                    Error("AN1019", $"`{member.Name}` would be {value}, which does not fit in a `{baseName}`", owner.Scope.Source, member.Span);
                values[i] = (value, null);
                earlier.TryAdd(member.Name, value);
                next = bitflags ? (value <= 0 ? 1 : value * 2) : value + 1;
            }
            foreach (int i in bits)
            {
                BigInteger value = 0;
                if (((NameExpr)members[i].Value!).Name == "allbits")
                    value = kind!.Value.IsSigned() ? BigInteger.MinusOne : (BigInteger.One << kind.Value.Bits()) - 1;
                else
                    for (int j = 0; j < members.Count; j++)
                        if (values[j] is { } declared && !bits.Contains(j))
                            value |= declared.Value;
                values[i] = (value, null);
            }
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (values[i] is not { } found)
                    continue;
                if (owner.Fields.ContainsKey(member.Name))
                {
                    Error("AN1020", $"`{member.Name}` is declared twice", owner.Scope.Source, member.Span);
                    continue;
                }
                var constantValue = new H.EnumConstant(owner.Type, found.Value, found.Text);
                int index = owner.Definition.AddField(new H.FieldDef(member.Name, owner.Type)
                {
                    Flags = H.FieldFlags.Static | H.FieldFlags.Const,
                    Access = H.Access.Public,
                    Value = constantValue,
                });
                owner.Fields[member.Name] = new FieldSymbol(owner, member.Name, owner.Type, index, isStatic: true) { IsConst = true, Value = constantValue };
            }
            // Its text as a method of its own, so a boxed member (`any`, `Console.WriteLine(day)`) shows its name too.
            if (StringType is { } enumText)
                Synthesize(owner, "ToString", enumText, [], SyntheticBody.EnumText);
            // `(Rgb)color` and `(Color)rgb` call these (the enum's own: a library's enum carries them).
            if (valueBase is not null)
            {
                Synthesize(owner, EnumValueMethod, baseType, [new H.Parameter("member", owner.Type)], SyntheticBody.EnumValue, isStatic: true);
                Synthesize(owner, EnumOfMethod, owner.Type, [new H.Parameter("value", baseType)], SyntheticBody.EnumOf, isStatic: true);
            }
        }

        /// <summary>A value-based enum's member as its value, and a value as the member that stands for it (or -1).</summary>
        internal const string EnumValueMethod = "$Value", EnumOfMethod = "$Of";

        private void DeclareField(TypeSymbol owner, FieldDecl field, DeclarationScope scope)
        {
            var source = scope.Source;
            if (owner.Fields.ContainsKey(field.Name))
            {
                Error("AN1020", $"`{field.Name}` is declared twice", source, field.NameSpan);
                return;
            }
            if (ResolveType(field.Type, scope, Diagnostics) is not { } type)
                return;
            CheckDeclared(type, OwnGeneric(owner, null), source, field.Type.Span);
            if (type == H.HirType.Void)
            {
                Error("AN1021", "a field cannot be void", source, field.Type.Span);
                return;
            }
            if ((field.Modifiers & Modifiers.Const) != 0)
            {
                DeclareConstant(owner, field, type, scope);
                return;
            }
            bool isFixed = (field.Modifiers & Modifiers.Fixed) != 0;
            bool isStaticField = owner.IsModule || (field.Modifiers & Modifiers.Static) != 0 || owner.IsStatic;
            if (owner.InPlace is { } before && !isStaticField)
            {
                Error("AN1097", $"`{before.Name}` (an array kept in the object) is the last field: declare `{field.Name}` before it", source, field.NameSpan);
                return;
            }
            if (isFixed && type is H.ArrayType { Rank: 1 } kept)
            {
                DeclareInPlace(owner, field, kept, scope);
                return;
            }
            if (isFixed != type is H.InlineArrayType)
            {
                Error("AN1074", isFixed ? "`fixed` goes on a field of an inline array type (`fixed u8[16] buffer;`)" : $"an inline array field says `fixed`: `fixed {H.Text.HirPrinter.Print(type, Assembly).Replace("fixed ", "")} {field.Name};`", source, field.Type.Span);
                return;
            }
            if (isFixed && (field.Modifiers & Modifiers.Static) != 0)
            {
                Error("AN1074", "a `fixed` field belongs to each object or value (not static)", source, field.Type.Span);
                return;
            }
            bool isStatic = owner.IsModule || (field.Modifiers & Modifiers.Static) != 0 || owner.IsStatic;
            H.HirConstant? value = null;
            // A static field's initializer that is not a constant runs in the type's static constructor.
            if (isStatic && field.Initializer is { } initializer)
                value = Constant(initializer, type, scope, new DiagnosticBag(), owner);
            var flags = isStatic ? H.FieldFlags.Static : H.FieldFlags.None;
            // A record's fields are all readonly: `with` makes a changed copy. A ValueTuple's items may be set (`t.Item1 = 2`).
            if ((field.Modifiers & Modifiers.Readonly) != 0 || !isStatic && (owner.Definition.Flags & H.TypeFlags.Record) != 0 && owner.FullName != "System.ValueTuple")
                flags |= H.FieldFlags.Readonly;
            var (fieldLine, fieldColumn) = source.LineColumn(field.NameSpan.Start);
            int index = owner.Definition.AddField(new H.FieldDef(field.Name, type)
            {
                Flags = flags,
                Access = AccessFor(field.Modifiers, owner.IsModule ? H.Access.Internal : H.Access.Private),
                Value = value,
                Location = new H.SourceLocation(FileIndex(source), fieldLine, fieldColumn),
                Doc = source.DocBefore(field.Span.Start),
            });
            var symbol = new FieldSymbol(owner, field.Name, type, index, isStatic) { Declaration = field, Value = value, Scope = scope };
            owner.Fields[field.Name] = symbol;
            if (field.Initializer is not null && value is null)
                (isStatic ? owner.StaticInitializedFields : owner.InitializedFields).Add(symbol);
        }

        /// <summary>
        /// <c>fixed T[] units;</c> without a size: the array kept in the object, one allocation with it. The last field of a
        /// final class, of units without references (a primitive, or a type parameter); its constructors give the length
        /// (<c>: fixed(n)</c>).
        /// </summary>
        private void DeclareInPlace(TypeSymbol owner, FieldDecl field, H.ArrayType type, DeclarationScope scope)
        {
            var source = scope.Source;
            string? problem = !owner.IsClass || owner.IsModule || owner.IsStatic ? "only a class keeps an array in its objects (`fixed T[] name;`)"
                : (owner.Definition.Flags & H.TypeFlags.Final) == 0 ? "a class that keeps an array in its objects is `final` (nothing derives from it)"
                : (owner.Definition.Flags & H.TypeFlags.Record) != 0 ? "a record does not keep an array in its objects"
                : (field.Modifiers & Modifiers.Static) != 0 ? "an array kept in the object belongs to each object (not static)"
                : type.Element is not (H.PrimitiveType { Kind: not H.Primitive.Void } or H.GenericParamType)
                    ? "an array kept in the object holds numbers, bools or chars (or a type parameter), not references"
                : null;
            if (problem is not null)
            {
                Error("AN1097", problem, source, field.Type.Span);
                return;
            }
            var (line, column) = source.LineColumn(field.NameSpan.Start);
            var flags = H.FieldFlags.InPlace | ((field.Modifiers & Modifiers.Readonly) != 0 ? H.FieldFlags.Readonly : H.FieldFlags.None);
            int index = owner.Definition.AddField(new H.FieldDef(field.Name, type)
            {
                Flags = flags,
                Access = AccessFor(field.Modifiers, H.Access.Private),
                Location = new H.SourceLocation(FileIndex(source), line, column),
                Doc = source.DocBefore(field.Span.Start),
            });
            var symbol = new FieldSymbol(owner, field.Name, type, index, false) { Declaration = field, Scope = scope };
            owner.Fields[field.Name] = symbol;
            owner.InPlace = symbol;
            if (field.Initializer is not null)
                Error("AN1097", "an array kept in the object has no initializer: its constructors fill it", source, field.Initializer.Span);
        }

        /// <summary>
        /// A class with an array kept in its objects: every constructor gives the length (<c>: fixed(n)</c>), and there is at
        /// least one (the objects are made by them).
        /// </summary>
        private void CheckInPlace(TypeSymbol owner)
        {
            if (owner.InPlace is not { } field || field.Declaration is not FieldDecl declaration)
                return;
            var source = owner.Scope.Source;
            if (owner.Constructors.Count == 0)
                Error("AN1098", $"`{owner.Name}` keeps `{field.Name}` in its objects: give it a constructor saying how long it is (`new(…) : fixed(n) {{ … }}`)", source, declaration.NameSpan);
            foreach (var constructor in owner.Constructors)
            {
                if (constructor.Constructor is { FixedLength: null } decl)
                    Error("AN1098", $"a constructor of `{owner.Name}` says how many elements `{field.Name}` holds: `: fixed(n)`", source, decl.HeadSpan);
            }
        }

        private (H.HirType[] Types, string[] Names)? Parameters(ParameterSyntax[] parameters, DeclarationScope scope)
        {
            var types = new H.HirType[parameters.Length];
            bool ok = true;
            for (int p = 0; p < parameters.Length; p++)
            {
                var type = ResolveType(parameters[p].Type, scope, Diagnostics);
                if (type is not null && type != H.HirType.Void)
                {
                    types[p] = type;
                }
                else
                {
                    if (type == H.HirType.Void)
                        Error("AN1023", "a parameter cannot be void", scope.Source, parameters[p].Type.Span);
                    ok = false;
                }
                if (parameters.Take(p).Any(q => q.Name == parameters[p].Name))
                {
                    Error("AN1024", $"two parameters are named `{parameters[p].Name}`", scope.Source, parameters[p].Span);
                    ok = false;
                }
            }
            return ok ? (types, parameters.Select(p => p.Name).ToArray()) : null;
        }

        private void DeclareMethod(TypeSymbol owner, FunctionDecl function, DeclarationScope scope)
        {
            var source = scope.Source;
            scope = scope with { MethodParameters = [.. function.Generics.Select(g => g.Name)] };
            if ((function.Modifiers & Modifiers.Async) != 0 && function.Body is null)
            {
                Error("AN1026", "an async function has a body", source, function.NameSpan);
                return;
            }
            if (Parameters(function.Parameters, scope) is not { } parameters || ResolveType(function.ReturnType, scope, Diagnostics) is not { } result)
                return;
            if (owner.IsExtension)
            {
                // `ext(i32 x) { F() … }`: a static method taking the receiver first; `static ext(i32) { F() … }`: a static method.
                if ((function.Modifiers & (Modifiers.Static | Modifiers.Virtual | Modifiers.Override | Modifiers.Final)) != 0)
                {
                    Error("AN1075", "the functions of an extension block say neither `static` nor `virtual`: `static ext(…)` holds the static ones", source, function.NameSpan);
                    return;
                }
                if (!owner.IsStatic)
                {
                    if (owner.Definition.Underlying is not { } receiverType || owner.ExtensionReceiver is not { } receiverName)
                        return;
                    if (parameters.Names.Contains(receiverName))
                    {
                        Error("AN1075", $"`{receiverName}` is the receiver of the extension: a parameter cannot take its name", source, function.NameSpan);
                        return;
                    }
                    parameters = ([receiverType, .. parameters.Types], [receiverName, .. parameters.Names]);
                }
            }
            bool isExtern = (function.Modifiers & Modifiers.Extern) != 0;
            var import = AttributeNamed(function.Attributes, "Import");
            var export = AttributeNamed(function.Attributes, "Export");
            bool isStatic = owner.IsModule || owner.IsStatic || owner.IsExtension || (function.Modifiers & Modifiers.Static) != 0 || owner.IsEnum || isExtern || import is not null;
            bool isVirtual = (function.Modifiers & (Modifiers.Virtual | Modifiers.Override)) != 0;
            bool isAbstract = function.Body is null && !isExtern && import is null;
            if (isAbstract && (!(owner.IsClass || owner.IsInterface) || owner.IsModule || isStatic && !owner.IsInterface))
            {
                Error("AN1027", $"`{function.Name}` needs a body (only instance methods of a base class, or the methods of an interface, can leave it out)", source, function.NameSpan);
                return;
            }
            if (isExtern && (function.Body is not null || function.Generics.Length > 0))
            {
                Error("AN1042", "an extern function is a runtime service: it has no body and no type parameters", source, function.NameSpan);
                return;
            }
            string? library = null, entry = null;
            if (import is not null)
            {
                if (function.Body is not null || function.Generics.Length > 0 || isExtern || isVirtual)
                {
                    Error("AN1051", "`[Import]` goes on a function without a body or type parameters: it calls a C function", source, function.NameSpan);
                    return;
                }
                (library, entry) = ImportArguments(import);
                if (library is null)
                {
                    Error("AN1052", "`[Import]` names the C library: `[Import(\"user32.dll\")]` or `[Import(\"user32.dll\", \"MessageBoxW\")]`", source, import.Span);
                    return;
                }
                entry ??= function.Name;
                for (int p = 0; p < parameters.Types.Length; p++)
                {
                    if (!CrossesToC(parameters.Types[p], strings: true))
                    {
                        Error("AN1053", $"an imported function takes numbers, bool, pointers, func* and strings, not `{H.Text.HirPrinter.Print(parameters.Types[p], Assembly)}`", source, p < function.Parameters.Length ? function.Parameters[p].Span : function.NameSpan);
                        return;
                    }
                }
                if (result != H.HirType.Void && !CrossesToC(result, strings: false))
                {
                    Error("AN1054", $"an imported function gives a number, a bool, a pointer or a func*, not `{H.Text.HirPrinter.Print(result, Assembly)}` (a C string comes back as a `u8*`: Marshal.PtrToStringUTF8)", source, function.ReturnType.Span);
                    return;
                }
            }
            string? exportName = null;
            if (export is not null)
            {
                exportName = export.Arguments switch
                {
                    [] => function.Name,
                    [StringLiteral { Value: var named }] when named.Length > 0 => named,
                    _ => null,
                };
                if (function.Body is null || function.Generics.Length > 0 || !isStatic || isVirtual || exportName is null)
                {
                    Error("AN1055", "`[Export]` (or `[Export(\"name\")]`) goes on a static function with a body and no type parameters", source, function.NameSpan);
                    return;
                }
                if (parameters.Types.Append(result).FirstOrDefault(t => t != H.HirType.Void && !CrossesToC(t, strings: false)) is { } wrong)
                {
                    Error("AN1056", $"an exported function takes and gives numbers, bool, pointers and func* (what C holds), not `{H.Text.HirPrinter.Print(wrong, Assembly)}`", source, function.NameSpan);
                    return;
                }
            }
            if (isVirtual && (!owner.IsClass || isStatic))
            {
                Error("AN1029", "only instance methods of classes are virtual", source, function.NameSpan);
                return;
            }
            if (owner.Methods.TryGetValue(function.Name, out var same) && same.Any(m => m.Parameters.SequenceEqual(parameters.Types)))
            {
                Error("AN1030", $"`{function.Name}` is declared twice with the same parameters", source, function.NameSpan);
                return;
            }
            if (owner.Fields.ContainsKey(function.Name) || owner.Properties.ContainsKey(function.Name))
            {
                Error("AN1031", $"`{function.Name}` is both a field or property and a method", source, function.NameSpan);
                return;
            }
            var flags = H.MethodFlags.None;
            if (isStatic)
                flags |= H.MethodFlags.Static;
            if (isVirtual || isAbstract || owner.IsInterface && !isStatic)
                flags |= H.MethodFlags.Virtual; // an interface's default method is overridden by the implementer's own
            if ((function.Modifiers & Modifiers.Override) != 0)
                flags |= H.MethodFlags.Override;
            if (isAbstract)
                flags |= H.MethodFlags.Abstract;
            if ((function.Modifiers & Modifiers.Final) != 0)
                flags |= H.MethodFlags.Final;
            if ((function.Modifiers & Modifiers.Unsafe) != 0)
            {
                flags |= H.MethodFlags.Unsafe;
                if (Options.DeniesUnsafe(source.Path))
                    Error("AN1104", $"`unsafe {function.Name}`: " + AntrilOptions.UnsafeDenied, source, function.NameSpan);
            }
            if (isExtern)
                flags |= H.MethodFlags.Extern;
            if (import is not null)
                flags |= H.MethodFlags.Import;
            if (export is not null)
                flags |= H.MethodFlags.Export;
            foreach (var attribute in function.Attributes)
            {
                switch (attribute.Name.ToString())
                {
                    case "Inline" or "System.Inline":
                        flags |= H.MethodFlags.Inline;
                        break;
                    case "NoInline" or "System.NoInline":
                        flags |= H.MethodFlags.NoInline;
                        break;
                }
            }
            // `destroy()`: the class's destructor. Virtual (the runtime runs the object's own), an override of the base's
            // (Object's does nothing); its body runs before the base's (the binder adds that call).
            if (function.Name == "destroy" && !owner.IsModule)
            {
                if (!owner.IsClass)
                    Error("AN1077", "`destroy()` is a class's destructor: a value, an interface or an enum has none", source, function.NameSpan);
                else if (isStatic || parameters.Types.Length > 0 || result != H.HirType.Void || function.Generics.Length > 0)
                    Error("AN1078", "a destructor is `destroy()`: not static, without parameters, result or type parameters", source, function.NameSpan);
                else
                {
                    flags |= H.MethodFlags.Virtual;
                    if (owner.Base?.FindMethods("destroy").Any(m => !m.IsStatic && m.Parameters.Length == 0) == true)
                        flags |= H.MethodFlags.Override;
                    isVirtual = true;
                }
            }
            var (line, column) = source.LineColumn(function.NameSpan.Start);
            var definition = new H.MethodDef(function.Name, result, [.. parameters.Types.Select((t, i) => new H.Parameter(parameters.Names[i], t))])
            {
                Flags = flags,
                Access = AccessFor(function.Modifiers, owner.IsModule ? H.Access.Internal : H.Access.Private),
                Location = new H.SourceLocation(FileIndex(source), line, column),
                ExternName = isExtern ? function.ExternName : import is not null ? entry : exportName,
                Library = library,
                Doc = source.DocBefore(function.Span.Start),
                Kind = function.ConversionTarget is not null ? H.MethodKind.Conversion
                    : function.OperatorSymbol is not null || Syntax.Parser.OperatorSymbol(function.Name) is not null ? H.MethodKind.Operator : H.MethodKind.Normal,
            };
            for (int g = 0; g < function.Generics.Length; g++)
                definition.Generics.Add(Constrained(function.Generics[g], scope, new H.GenericParamType(H.GenericOwner.Method, g)));
            if (definition.Kind != H.MethodKind.Normal)
            {
                // An operator's type parameters are inferred from its operands (a conversion's also from its target).
                for (int g = 0; g < function.Generics.Length; g++)
                {
                    bool inferable = parameters.Types.Any(t => MentionsMethodParam(t, g)) || definition.Kind == H.MethodKind.Conversion && MentionsMethodParam(result, g);
                    if (!inferable)
                        Error("AN1072", $"`{function.Generics[g].Name}` appears in no operand of the operator: it could not be inferred", source, function.Generics[g].Span);
                }
            }
            for (int p = 0; p < parameters.Types.Length; p++)
                CheckDeclared(parameters.Types[p], OwnGeneric(owner, definition), source, p < function.Parameters.Length ? function.Parameters[p].Type.Span : function.NameSpan);
            CheckDeclared(result, OwnGeneric(owner, definition), source, function.ReturnType.Span);
            int index = owner.Definition.AddMethod(definition);
            if (function.OperatorSymbol is { } declaredSymbol)
            {
                // The type's operator table: the symbol, where it goes, and (a symbol of the program's own) its precedence.
                int level = 0;
                var associativity = H.Associativity.Left;
                if (function.Form == OperatorForm.Binary)
                    Operators.TryBinary(declaredSymbol, out level, out associativity);
                if (function.Form is OperatorForm.Binary)
                    owner.Definition.Operators.Add(new H.OperatorDef(declaredSymbol, H.Fixity.Binary, index) { Precedence = level, Associativity = associativity });
                if (function.Form is OperatorForm.Prefix or OperatorForm.Both)
                    owner.Definition.Operators.Add(new H.OperatorDef(declaredSymbol, H.Fixity.Prefix, index));
                if (function.Form is OperatorForm.Postfix or OperatorForm.Both)
                    owner.Definition.Operators.Add(new H.OperatorDef(declaredSymbol, H.Fixity.Postfix, index));
            }
            else if (definition.Kind == H.MethodKind.Operator && Syntax.Parser.OperatorSymbol(function.Name) is { } operatorSymbol)
                owner.Definition.Operators.Add(new H.OperatorDef(operatorSymbol, parameters.Types.Length == 1 ? H.Fixity.Prefix : H.Fixity.Binary, index));
            var symbol = new MethodSymbol(owner, function.Name, parameters.Types, parameters.Names, result, index)
            {
                IsStatic = isStatic,
                IsVirtual = isVirtual || isAbstract,
                IsAbstract = isAbstract,
                Function = function,
                NameSpan = function.NameSpan,
                Scope = scope,
            };
            if (!owner.Methods.TryGetValue(function.Name, out var list))
                owner.Methods[function.Name] = list = [];
            list.Add(symbol);
            if (!isAbstract && !isExtern && import is null)
                bodies.Add(symbol);
        }

        /// <summary>Whether <paramref name="type"/> mentions the method's type parameter <paramref name="index"/>.</summary>
        internal static bool MentionsMethodParam(H.HirType type, int index) => type switch
        {
            H.GenericParamType { Owner: H.GenericOwner.Method } g => g.Index == index,
            H.NamedType n => n.Arguments.Any(a => MentionsMethodParam(a, index)),
            H.ArrayType a => MentionsMethodParam(a.Element, index),
            H.NullableType n => MentionsMethodParam(n.Inner, index),
            H.FuncType f => f.Parameters.Any(p => MentionsMethodParam(p, index)) || MentionsMethodParam(f.Result, index),
            H.ReferenceType r => MentionsMethodParam(r.Target, index),
            H.PointerType p => MentionsMethodParam(p.Target, index),
            _ => false,
        };

        /// <summary>The attribute called <paramref name="name"/> (or <c>System.</c><paramref name="name"/>), if written.</summary>
        private static AttributeSyntax? AttributeNamed(AttributeSyntax[] attributes, string name) =>
            attributes.FirstOrDefault(a => a.Name.ToString() == name || a.Name.ToString() == "System." + name
                || a.Name.ToString() == "System.Runtime.InteropServices." + name);

        /// <summary><c>[Import("lib")]</c>, <c>[Import("lib", "Entry")]</c> or <c>[Import("lib", Entry = "x")]</c>: library and C name.</summary>
        private static (string? Library, string? Entry) ImportArguments(AttributeSyntax attribute)
        {
            string? library = null, entry = null;
            for (int a = 0; a < attribute.Arguments.Length; a++)
            {
                switch (attribute.Arguments[a])
                {
                    case StringLiteral text when a == 0:
                        library = text.Value;
                        break;
                    case StringLiteral text when a == 1:
                        entry = text.Value;
                        break;
                    case AssignExpr { Operator: null, Target: NameExpr { Name: "Entry" or "EntryPoint" }, Value: StringLiteral text }:
                        entry = text.Value;
                        break;
                    default:
                        return (null, null);
                }
            }
            return (string.IsNullOrEmpty(library) ? null : library, string.IsNullOrEmpty(entry) ? null : entry);
        }

        /// <summary>
        /// Whether a value of <paramref name="type"/> crosses to C as it is: numbers (up to 64 bits), bool, char, pointers,
        /// <c>func*</c>, enums. With <paramref name="strings"/>, also <c>str</c> and <c>String&lt;u16&gt;</c> (passed as a
        /// temporary null-terminated copy: UTF-8 or UTF-16).
        /// </summary>
        /// <summary>
        /// Whether a value of this type crosses between GC domains: numbers, bools, enums and pointers to numbers (packets,
        /// see System.Exchange). Objects, strings, arrays, structs and functions belong to one domain.
        /// </summary>
        internal bool CrossesDomains(H.HirType type) => type switch
        {
            H.PrimitiveType => true,
            H.PointerType { Target: H.PrimitiveType { Kind: not H.Primitive.Void } } => true,
            H.NamedType named when SymbolOf(named) is { IsEnum: true } => true,
            _ => false,
        };

        /// <summary>Why <paramref name="method"/> of a type in another GC domain cannot be called from here, or null when it can.</summary>
        internal string? NotCrossing(MethodSymbol method, Func<H.HirType, string> show)
        {
            if (!method.IsStatic || method.IsConstructor)
                return "it is an instance method, and objects of that domain cannot be used from this one";
            if (method.GenericCount > 0 || method.Owner.GenericCount > 0)
                return "it is generic";
            for (int p = 0; p < method.Parameters.Length; p++)
            {
                if (!CrossesDomains(method.Parameters[p]))
                    return $"its parameter `{method.ParameterNames[p]}` is a `{show(method.Parameters[p])}`, an object of that domain that cannot cross";
            }
            if (!CrossesDomains(method.Result))
                return $"its result is a `{show(method.Result)}`, an object of that domain that cannot cross";
            return null;
        }

        /// <summary>The AN1079 text: what belongs to another GC domain, why it cannot be used, and what does cross.</summary>
        internal static string CrossingMessage(string what, string domain, string reason) =>
            $"{what} belongs to another GC domain (`{domain}`, a library built for another gc): {reason}. " +
            "Between domains cross only static functions over numbers, bools and `u8*` packets: pack strings and bytes with System.Exchange";

        internal bool CrossesToC(H.HirType type, bool strings)
        {
            switch (type)
            {
                case H.PrimitiveType p:
                    return p.Kind != H.Primitive.Void && (p.Kind.IsFloat() ? p.Kind is H.Primitive.F32 or H.Primitive.F64 : p.Kind.Bits() <= 64);
                case H.PointerType or H.NativeFuncType:
                    return true;
                case H.NamedType named when SymbolOf(named) is { IsEnum: true }:
                    return true;
                case H.NamedType named when strings && StringSymbol is { } text && named.Type == text.Token
                    && named.Arguments is [H.PrimitiveType { Kind: H.Primitive.U8 or H.Primitive.U16 }]:
                    return true;
                // `str?`: C's `const char*` that may be NULL (null crosses as a null pointer).
                case H.NullableType { Inner: H.NamedType inner } when strings:
                    return CrossesToC(inner, strings: true) && SymbolOf(inner) is not { IsEnum: true };
            }
            return false;
        }

        private int FileIndex(SourceText source) => Assembly.Files.IndexOf(source.Path);

        private void DeclareConstructor(TypeSymbol owner, ConstructorDecl constructor, DeclarationScope scope)
        {
            var source = scope.Source;
            if ((constructor.Modifiers & Modifiers.Static) != 0)
            {
                DeclareStaticConstructor(owner, constructor, scope);
                return;
            }
            if (!owner.IsClass && !owner.IsStruct || owner.IsStatic)
            {
                Error("AN1032", "only classes and values have constructors", source, constructor.HeadSpan);
                return;
            }
            if (Parameters(constructor.Parameters, scope) is not { } parameters)
                return;
            if (owner.Constructors.Any(c => c.Parameters.SequenceEqual(parameters.Types)))
            {
                Error("AN1033", "two constructors take the same parameters", source, constructor.HeadSpan);
                return;
            }
            var (line, column) = source.LineColumn(constructor.HeadSpan.Start);
            var definition = new H.MethodDef(".new", H.HirType.Void, [.. parameters.Types.Select((t, i) => new H.Parameter(parameters.Names[i], t))])
            {
                Kind = H.MethodKind.Constructor,
                Access = AccessFor(constructor.Modifiers, H.Access.Public),
                Location = new H.SourceLocation(FileIndex(source), line, column),
                Doc = source.DocBefore(constructor.Span.Start),
            };
            int ordinal = owner.Definition.Methods.Count(m => m.Kind == H.MethodKind.Constructor);
            int index = owner.Definition.AddMethod(definition);
            var symbol = new MethodSymbol(owner, ".new", parameters.Types, parameters.Names, H.HirType.Void, index) { IsConstructor = true, Constructor = constructor, NameSpan = constructor.HeadSpan, Scope = scope };
            owner.Constructors.Add(symbol);
            bodies.Add(symbol);
            if (constructor.FixedLength is { } length)
            {
                if (constructor.ThisArguments is not null || constructor.BaseArguments is not null)
                    Error("AN1098", "`: fixed(n)` is the whole header: no `: base(…)` or `: (…)` with it", source, constructor.HeadSpan);
                // `$fixed{n}(params) : i32 => length`: what the object's allocation asks first (the lowerer calls it).
                var span = length.Span;
                var made = new FunctionDecl([], Modifiers.Static | Modifiers.Private, new PrimitiveTypeSyntax(TokenKind.I32, span), $"$fixed{ordinal}", constructor.HeadSpan,
                    [], constructor.Parameters, new BlockStmt([new ReturnStmt(length, span)], span), constructor.Span);
                int before = owner.Definition.Methods.Count;
                DeclareMethod(owner, made, scope);
                for (int m = before; m < owner.Definition.Methods.Count; m++)
                    owner.Definition.Methods[m].Flags |= H.MethodFlags.Generated;
            }
        }

        /// <summary>
        /// A property: a getter <c>get_Name()</c> and a setter <c>set_Name(value)</c>; <c>get;</c> and <c>set;</c> read and
        /// write a hidden field (<c>$Name</c>).
        /// </summary>
        private void DeclareProperty(TypeSymbol owner, PropertyDecl property, DeclarationScope scope)
        {
            var source = scope.Source;
            if (owner.Fields.ContainsKey(property.Name) || owner.Properties.ContainsKey(property.Name) || owner.Methods.ContainsKey(property.Name))
            {
                Error("AN1020", $"`{property.Name}` is declared twice", source, property.NameSpan);
                return;
            }
            if (owner.IsEnum)
            {
                Error("AN1043", "properties belong to classes, values and interfaces", source, property.NameSpan);
                return;
            }
            // An interface's property is what its implementers must have: an accessor without a body (`get;`, `base get;`)
            // is required of them; one with a body is the interface's default (an implementer's own accessor replaces it).
            bool isInterface = owner.IsInterface;
            foreach (var accessor in property.Accessors)
            {
                if (!isInterface && accessor.IsAbstract)
                {
                    Error("AN1047", "`base get` and `base set` belong to an interface's properties", source, accessor.Span);
                    return;
                }
            }
            if (ResolveType(property.Type, scope, Diagnostics) is not { } type)
                return;
            CheckDeclared(type, OwnGeneric(owner, null), source, property.Type.Span);
            if (type == H.HirType.Void)
            {
                Error("AN1021", "a property cannot be void", source, property.Type.Span);
                return;
            }
            var getter = property.Accessors.FirstOrDefault(a => a.IsGetter);
            var setter = property.Accessors.FirstOrDefault(a => !a.IsGetter);
            if (getter is null)
            {
                Error("AN1044", $"`{property.Name}` needs a `get`", source, property.NameSpan);
                return;
            }
            bool isStatic = owner.IsModule || owner.IsStatic || (property.Modifiers & Modifiers.Static) != 0;
            bool isVirtual = (property.Modifiers & (Modifiers.Virtual | Modifiers.Override)) != 0;
            if (isVirtual && (!owner.IsClass || isStatic))
            {
                Error("AN1029", "only instance properties of classes are virtual", source, property.NameSpan);
                return;
            }
            var accessorFlags = isStatic ? H.MethodFlags.Static : H.MethodFlags.None;
            if (isVirtual || isInterface && !isStatic)
                accessorFlags |= H.MethodFlags.Virtual; // an interface's default accessor is replaced by the implementer's own
            if ((property.Modifiers & Modifiers.Override) != 0)
                accessorFlags |= H.MethodFlags.Override;
            if ((property.Modifiers & Modifiers.Final) != 0)
                accessorFlags |= H.MethodFlags.Final;
            var access = AccessFor(property.Modifiers, owner.IsModule ? H.Access.Internal : H.Access.Private);
            string? backing = null;
            if (!isInterface && property.Accessors.Any(a => a.Body is null))
            {
                backing = "$" + property.Name;
                int fieldIndex = owner.Definition.AddField(new H.FieldDef(backing, type)
                {
                    Flags = (isStatic ? H.FieldFlags.Static : H.FieldFlags.None) | H.FieldFlags.Generated,
                    Access = H.Access.Private,
                });
                owner.Fields[backing] = new FieldSymbol(owner, backing, type, fieldIndex, isStatic) { Scope = scope };
            }
            var span = property.NameSpan;
            MethodSymbol Accessor(AccessorSyntax accessor, string name, H.HirType result, H.Parameter[] parameters, H.MethodKind kind, BlockStmt? body)
            {
                bool isAbstract = body is null; // only in an interface: elsewhere a missing body reads or writes the hidden field
                var (line, column) = source.LineColumn(accessor.Span.Start);
                var definition = new H.MethodDef(name, result, parameters)
                {
                    Kind = kind,
                    Flags = accessorFlags | (isAbstract ? H.MethodFlags.Abstract : H.MethodFlags.None),
                    Access = AccessOf(accessor.Modifiers, access),
                    Location = new H.SourceLocation(FileIndex(source), line, column),
                };
                int index = owner.Definition.AddMethod(definition);
                var symbol = new MethodSymbol(owner, name, [.. parameters.Select(p => p.Type)], [.. parameters.Select(p => p.Name)], result, index)
                {
                    IsStatic = isStatic,
                    IsVirtual = isVirtual || isInterface && !isStatic,
                    IsAbstract = isAbstract,
                    AccessorBody = body,
                    NameSpan = span,
                    Scope = scope,
                };
                if (!isAbstract)
                    bodies.Add(symbol);
                return symbol;
            }
            var get = Accessor(getter, "get_" + property.Name, type, [], H.MethodKind.Getter,
                isInterface ? getter.Body : getter.Body ?? new BlockStmt([new ReturnStmt(new NameExpr(backing!, [], span), span)], span));
            MethodSymbol? set = null;
            if (setter is not null)
            {
                set = Accessor(setter, "set_" + property.Name, H.HirType.Void, [new H.Parameter("value", type)], H.MethodKind.Setter,
                    isInterface ? setter.Body : setter.Body ?? new BlockStmt([new ExprStmt(new AssignExpr(null, new NameExpr(backing!, [], span), new NameExpr("value", [], span), span), span)], span));
            }
            int propertyIndex = owner.Definition.Properties.Count;
            var (propertyLine, propertyColumn) = source.LineColumn(property.NameSpan.Start);
            owner.Definition.Properties.Add(new H.PropertyDef(property.Name, type)
            {
                Getter = get.Index,
                Setter = set?.Index ?? -1,
                Flags = isStatic ? H.MemberFlags.Static : H.MemberFlags.None,
                Access = access,
                Location = new H.SourceLocation(FileIndex(source), propertyLine, propertyColumn),
                Doc = source.DocBefore(property.Span.Start),
            });
            owner.Properties[property.Name] = new PropertySymbol(owner, property.Name, type, propertyIndex, isStatic) { Getter = get, Setter = set, Declaration = property };
        }

        /// <summary><c>static new() { … }</c>: runs once, before <c>Main</c> (after the static constructors of the types it uses).</summary>
        private void DeclareStaticConstructor(TypeSymbol owner, ConstructorDecl constructor, DeclarationScope scope)
        {
            var source = scope.Source;
            if (owner.IsInterface || owner.IsEnum)
            {
                Error("AN1047", "only classes, values and namespaces have static constructors", source, constructor.HeadSpan);
                return;
            }
            if (constructor.Parameters.Length > 0 || constructor.BaseArguments is not null || constructor.ThisArguments is not null)
            {
                Error("AN1048", "a static constructor takes no parameters and calls no other constructor", source, constructor.HeadSpan);
                return;
            }
            if (owner.StaticConstructor is not null)
            {
                Error("AN1049", "a type has one static constructor", source, constructor.HeadSpan);
                return;
            }
            if (owner.Definition.Generics.Count > 0)
            {
                Error("AN1050", "generic types have no static constructor (their static fields start with constants)", source, constructor.HeadSpan);
                return;
            }
            var (line, column) = source.LineColumn(constructor.HeadSpan.Start);
            int index = owner.Definition.AddMethod(new H.MethodDef(".static", H.HirType.Void)
            {
                Kind = H.MethodKind.StaticConstructor,
                Flags = H.MethodFlags.Static,
                Access = H.Access.Private,
                Location = new H.SourceLocation(FileIndex(source), line, column),
                Doc = source.DocBefore(constructor.Span.Start),
            });
            var symbol = new MethodSymbol(owner, ".static", [], [], H.HirType.Void, index)
            {
                IsStatic = true, IsStaticConstructor = true, Constructor = constructor, NameSpan = constructor.HeadSpan, Scope = scope,
            };
            owner.StaticConstructor = symbol;
            bodies.Add(symbol);
        }

        /// <summary>
        /// A record gets <c>ToString()</c>, <c>Hash()</c> and <c>Equals(T)</c> written for it (and implements <c>IToString</c> and
        /// <c>IHashable&lt;T&gt;</c>) unless it declares them itself.
        /// </summary>
        private void AddRecordMembers()
        {
            if (StringType is not { } text)
                return;
            var toString = Types.GetValueOrDefault("System.IToString");
            var hashable = Types.GetValueOrDefault("System.IHashable");
            foreach (var type in Types.Values.ToArray())
            {
                if (type.IsExternal || (type.Definition.Flags & H.TypeFlags.Record) == 0)
                    continue;
                // The record's own declarations count, not what it inherits (Object's virtual ToString and Hash get overridden).
                bool Declares(string name, params H.HirType[] parameters) =>
                    type.Methods.TryGetValue(name, out var own) && own.Any(m => !m.IsStatic && m.Parameters.SequenceEqual(parameters));
                if (!Declares("ToString"))
                    Synthesize(type, "ToString", text, [], SyntheticBody.ToString);
                if (!Declares("Hash"))
                    Synthesize(type, "Hash", H.HirType.Of(H.Primitive.U64), [], SyntheticBody.Hash);
                if (!Declares("Equals", type.Type))
                    Synthesize(type, "Equals", H.HirType.Bool, [new H.Parameter("other", type.Type)], SyntheticBody.Equals);
                if (toString is { IsInterface: true } && !type.Interfaces.Any(i => i.Type == toString.Token))
                {
                    var iface = new H.NamedType(toString.Token, []);
                    type.Interfaces.Add(iface);
                    type.Definition.Interfaces.Add(iface);
                }
                if (hashable is { IsInterface: true, GenericCount: 1 } && !type.Interfaces.Any(i => i.Type == hashable.Token))
                {
                    var iface = new H.NamedType(hashable.Token, [type.Type]);
                    type.Interfaces.Add(iface);
                    type.Definition.Interfaces.Add(iface);
                }
            }
        }

        private void Synthesize(TypeSymbol owner, string name, H.HirType result, H.Parameter[] parameters, SyntheticBody body, bool isStatic = false)
        {
            // A record class replaces Object's virtual `ToString()` (and any other virtual of the same signature).
            var types = parameters.Select(p => p.Type).ToArray();
            bool overrides = !isStatic && owner.IsClass && owner.Base is { } @base && @base.FindMethods(name).Any(m => m.IsVirtual && !m.IsStatic && m.Parameters.SequenceEqual(types));
            int index = owner.Definition.AddMethod(new H.MethodDef(name, result, parameters)
            {
                Access = H.Access.Public,
                Flags = isStatic ? H.MethodFlags.Static : overrides ? H.MethodFlags.Virtual | H.MethodFlags.Override : H.MethodFlags.None,
                Location = owner.Definition.Location,
            });
            var symbol = new MethodSymbol(owner, name, types, [.. parameters.Select(p => p.Name)], result, index)
            {
                Scope = owner.Scope,
                NameSpan = owner.Declaration?.NameSpan ?? default,
                Synthetic = body,
                IsVirtual = overrides,
                IsStatic = isStatic,
            };
            if (!owner.Methods.TryGetValue(name, out var list))
                owner.Methods[name] = list = [];
            list.Add(symbol);
            bodies.Add(symbol);
        }

        /// <summary>A type with static fields that start with something other than a constant gets a static constructor that sets them.</summary>
        private void AddStaticConstructors()
        {
            foreach (var type in Types.Values.Concat(Modules.Values))
            {
                if (type.IsExternal || type.StaticConstructor is not null || type.StaticInitializedFields.Count == 0)
                    continue;
                if (type.Definition.Generics.Count > 0)
                {
                    var field = type.StaticInitializedFields[0];
                    Error("AN1022", "a static field of a generic type starts with a constant", field.Scope!.Source, field.Declaration!.Initializer!.Span);
                    continue;
                }
                int index = type.Definition.AddMethod(new H.MethodDef(".static", H.HirType.Void)
                {
                    Kind = H.MethodKind.StaticConstructor,
                    Flags = H.MethodFlags.Static | H.MethodFlags.Generated,
                    Access = H.Access.Private,
                });
                var symbol = new MethodSymbol(type, ".static", [], [], H.HirType.Void, index)
                {
                    IsStatic = true, IsStaticConstructor = true, Scope = type.StaticInitializedFields[0].Scope!, NameSpan = type.StaticInitializedFields[0].Declaration!.NameSpan,
                };
                type.StaticConstructor = symbol;
                bodies.Add(symbol);
            }
        }

        /// <summary>
        /// A class with instance field initializers or a base whose constructor needs calling gets a constructor without
        /// parameters when it declares none (it runs the initializers and the base's).
        /// </summary>
        private void AddImplicitConstructors()
        {
            foreach (var type in Types.Values)
            {
                if (type.IsExternal || type.Constructors.Count > 0 || !(type.IsClass || type.IsStruct) || type.IsStatic)
                    continue;
                bool reacts = type.Methods.Values.SelectMany(m => m).Any(m => m.Function is { } f && (f.Modifiers & Modifiers.Reacts) != 0);
                if (type.InitializedFields.Count == 0 && (type.Base is null || type.Base.Constructors.Count == 0) && !reacts)
                    continue;
                int index = type.Definition.AddMethod(new H.MethodDef(".new", H.HirType.Void)
                {
                    Kind = H.MethodKind.Constructor,
                    Access = H.Access.Public,
                    Flags = H.MethodFlags.Generated,
                });
                var symbol = new MethodSymbol(type, ".new", [], [], H.HirType.Void, index) { IsConstructor = true, Scope = type.Scope };
                type.Constructors.Add(symbol);
                bodies.Add(symbol);
            }
        }

        /// <summary>
        /// An <c>override</c> replaces a virtual method of a base class with the same parameters and result; a method that
        /// would replace one without saying <c>override</c> is an error (it would silently hide it).
        /// </summary>
        private void CheckOverrides(TypeSymbol type)
        {
            foreach (var method in type.Methods.Values.SelectMany(m => m))
            {
                var function = method.Function;
                if (function is null || method.IsStatic)
                    continue;
                if (function.Name == "destroy" && method.Parameters.Length == 0)
                    continue; // a destructor overrides the base's without saying so
                MethodSymbol? replaced = null;
                H.HirType[] arguments = [];
                foreach (var (b, a) in type.Chain().Skip(1))
                {
                    replaced = b.Methods.TryGetValue(method.Name, out var list)
                        ? list.FirstOrDefault(m => m.IsVirtual && m.Parameters.Select(p => p.Substitute(a)).SequenceEqual(method.Parameters))
                        : null;
                    arguments = a;
                    if (replaced is not null)
                        break;
                }
                bool overrides = (function.Modifiers & Modifiers.Override) != 0;
                if (overrides && replaced is null)
                    Error("AN1036", $"`{method.Name}` overrides no virtual method of a base class", method.Scope.Source, function.NameSpan);
                else if (overrides && replaced!.Result.Substitute(arguments) != method.Result)
                    Error("AN1037", $"`{method.Name}` returns what the method it overrides returns", method.Scope.Source, function.ReturnType.Span);
                else if (!overrides && replaced is not null)
                    Error("AN1038", $"`{method.Name}` replaces a virtual method of `{replaced.Owner.Name}`: say `override`", method.Scope.Source, function.NameSpan);
                else if (overrides && (replaced!.Definition.Flags & Larva.Hir.MethodFlags.Final) != 0)
                    Error("AN1039", $"`{method.Name}` is final in `{replaced.Owner.Name}`", method.Scope.Source, function.NameSpan);
            }
            foreach (var property in type.Properties.Values)
            {
                if (property.Declaration is not { } declaration || property.IsStatic)
                    continue;
                PropertySymbol? replaced = null;
                H.HirType[] arguments = [];
                foreach (var (b, a) in type.Chain().Skip(1))
                {
                    if (b.Properties.TryGetValue(property.Name, out var found) && found.Getter is { IsVirtual: true })
                    {
                        (replaced, arguments) = (found, a);
                        break;
                    }
                }
                var source = type.Scope.Source;
                bool overrides = (declaration.Modifiers & Modifiers.Override) != 0;
                if (overrides && replaced is null)
                    Error("AN1036", $"`{property.Name}` overrides no virtual property of a base class", source, declaration.NameSpan);
                else if (!overrides && replaced is not null)
                    Error("AN1038", $"`{property.Name}` replaces a virtual property of `{replaced.Owner.Name}`: say `override`", source, declaration.NameSpan);
                else if (overrides && replaced!.Type.Substitute(arguments) != property.Type)
                    Error("AN1037", $"`{property.Name}` has the type of the property it overrides", source, declaration.Type.Span);
                else if (overrides && (replaced!.Setter is null) != (property.Setter is null))
                    Error("AN1046", $"`{property.Name}` has the accessors of the property it overrides (`get`{(replaced!.Setter is null ? "" : " and `set`")})", source, declaration.NameSpan);
                else if (overrides && (replaced!.Getter!.Definition.Flags & Larva.Hir.MethodFlags.Final) != 0)
                    Error("AN1039", $"`{property.Name}` is final in `{replaced.Owner.Name}`", source, declaration.NameSpan);
            }
        }

        /// <summary>A type that says it implements an interface has (or inherits) a method for each of the interface's.</summary>
        private void CheckInterfaces(TypeSymbol type)
        {
            if (type.Declaration is not { } declaration || type.IsInterface)
                return;
            // A primitive's members name it by its type (Int32) or its keyword (i32): both match.
            H.HirType Self(H.HirType t) => type.IsPrimitive && t == type.Type ? type.Definition.Underlying! : t;
            foreach (var iface in type.Interfaces)
            {
                foreach (var (symbol, method, arguments) in InterfaceMethods(iface))
                {
                    if (!method.IsAbstract)
                        continue; // A default method: the implementer may leave it to the interface.
                    var parameters = method.Parameters.Select(p => Self(p.Substitute(arguments))).ToArray();
                    bool found = type.Chain().Any(c => c.Type.Methods.TryGetValue(method.Name, out var list)
                        && list.Any(m => m.IsStatic == method.IsStatic && m.Parameters.Select(p => Self(p.Substitute(c.Arguments))).SequenceEqual(parameters)));
                    if (!found)
                        Error("AN1045", $"`{type.Name}` implements `{symbol.Name}` but has no {(method.IsStatic ? "static " : "")}`{method.Name}({string.Join(", ", parameters.Select(p => H.Text.HirPrinter.Print(p, Assembly)))})`", type.Scope.Source, declaration.NameSpan);
                }
                foreach (var (symbol, property, arguments) in InterfaceProperties(iface))
                {
                    bool needsSetter = property.Setter is { IsAbstract: true };
                    if (!needsSetter && property.Getter is { IsAbstract: false })
                        continue; // Default accessors: the implementer may leave the property to the interface.
                    var propertyType = Self(property.Type.Substitute(arguments));
                    var own = type.Chain().Select(c => c.Type.Properties.TryGetValue(property.Name, out var p)
                        && p.IsStatic == property.IsStatic && Self(p.Type.Substitute(c.Arguments)) == propertyType ? p : null).FirstOrDefault(p => p is not null);
                    if (own is null)
                        Error("AN1045", $"`{type.Name}` implements `{symbol.Name}` but has no {(property.IsStatic ? "static " : "")}property `{property.Name}` of type `{H.Text.HirPrinter.Print(propertyType, Assembly)}`", type.Scope.Source, declaration.NameSpan);
                    else if (needsSetter && own.Setter is null)
                        Error("AN1045", $"`{type.Name}` implements `{symbol.Name}`: its property `{property.Name}` needs a `set`", type.Scope.Source, own.Declaration?.NameSpan ?? declaration.NameSpan);
                }
                foreach (var op in InterfaceOperators(iface, []))
                {
                    var required = new H.OperatorRequirement(op.Symbol, [.. op.Operands.Select(Self)], Self(op.Result));
                    if (!HasOperator(required))
                        Error("AN1045", $"`{type.Name}` implements `{SymbolOf(iface)?.Name}` but has no `op {required.Symbol.Replace(PostfixSuffix, "")}({string.Join(", ", required.Operands.Select(o => H.Text.HirPrinter.Print(o, Assembly)))}) : {H.Text.HirPrinter.Print(required.Result, Assembly)}`", type.Scope.Source, declaration.NameSpan);
                }
            }
        }

        /// <summary>The properties of an interface and of the interfaces it extends, with the arguments of each.</summary>
        internal IEnumerable<(TypeSymbol Interface, PropertySymbol Property, H.HirType[] Arguments)> InterfaceProperties(H.NamedType iface)
        {
            if (SymbolOf(iface) is not { IsInterface: true } symbol)
                yield break;
            foreach (var property in symbol.Properties.Values)
                yield return (symbol, property, iface.Arguments);
            foreach (var extended in symbol.Interfaces)
            {
                foreach (var inherited in InterfaceProperties((H.NamedType)extended.Substitute(iface.Arguments)))
                    yield return inherited;
            }
        }

        /// <summary>The methods of an interface and of the interfaces it extends, with the arguments of each.</summary>
        internal IEnumerable<(TypeSymbol Interface, MethodSymbol Method, H.HirType[] Arguments)> InterfaceMethodsOf(H.NamedType iface) => InterfaceMethods(iface);

        private IEnumerable<(TypeSymbol Interface, MethodSymbol Method, H.HirType[] Arguments)> InterfaceMethods(H.NamedType iface)
        {
            if (SymbolOf(iface) is not { IsInterface: true } symbol)
                yield break;
            foreach (var method in symbol.Methods.Values.SelectMany(m => m))
                yield return (symbol, method, iface.Arguments);
            foreach (var extended in symbol.Interfaces)
            {
                foreach (var inherited in InterfaceMethods((H.NamedType)extended.Substitute(iface.Arguments)))
                    yield return inherited;
            }
        }

        private void CheckAbstract(TypeSymbol type)
        {
            if (type.IsAbstract || type.IsModule)
                return;
            // Every abstract method of the bases must be overridden somewhere below it.
            var open = new List<MethodSymbol>();
            var chain = new List<TypeSymbol>();
            for (var t = type; t is not null; t = t.Base)
                chain.Insert(0, t);
            foreach (var t in chain)
            {
                foreach (var method in t.Methods.Values.SelectMany(m => m))
                {
                    open.RemoveAll(o => o.SameSignature(method) && !method.IsAbstract);
                    if (method.IsAbstract)
                        open.Add(method);
                }
            }
            if (open.Count > 0 && type.Declaration is { } declaration)
                Error("AN1034", $"`{type.Name}` leaves abstract methods without a body ({string.Join(", ", open.Select(m => m.Name))}): declare it a `base class` or override them", type.Scope.Source, declaration.NameSpan);
        }

        /// <summary><c>Main</c> at the top level (of any namespace): the program's entry.</summary>
        private MethodSymbol? FindEntry()
        {
            var mains = Modules.Values.SelectMany(m => m.Methods.TryGetValue("Main", out var list) ? list : []).ToList();
            if (mains.Count > 1)
            {
                foreach (var main in mains.Skip(1))
                    Error("AN1035", "the program has more than one `Main`", main.Scope.Source, main.Function!.NameSpan);
            }
            return mains.FirstOrDefault();
        }

        /// <summary>
        /// Access words that cannot mean anything where they are written: a namespace's type that is <c>private</c> or
        /// <c>inherited</c> (nothing encloses it), a namespace's function or field that is <c>inherited</c> or <c>friendly</c>,
        /// <c>inherited</c> in a type nothing derives from (a value, a static or final class, an enum), and anything but
        /// <c>public</c> in an interface (its members are what implementers give).
        /// </summary>
        private void CheckAccessWords()
        {
            foreach (var unit in units)
                Check(unit.Members, null, unit.Source);

            void Check(IEnumerable<MemberDecl> members, TypeDecl? owner, SourceText source)
            {
                foreach (var member in members)
                {
                    var access = member.Modifiers & Modifiers.Access;
                    string word = Word(access);
                    if (member is NamespaceDecl inner)
                    {
                        Check(inner.Members, null, source);
                        continue;
                    }
                    if (member is TypeDecl type)
                    {
                        if (owner is null && access is Modifiers.Private or Modifiers.Inherited)
                            Error("AN1093", $"`{word}` limits a type to the type around it, and `{type.Name}` is in a namespace: it is `public`, `internal`, `file` or `friendly`", source, type.NameSpan);
                        Check(type.Members, type, source);
                        continue;
                    }
                    if (access == Modifiers.None || member is StaticLoopDecl)
                        continue;
                    var span = new TextSpan(member.Span.Start, Math.Min(member.Span.Length, word.Length));
                    if (owner is null)
                    {
                        if (access is Modifiers.Inherited or Modifiers.Friendly)
                            Error("AN1093", $"`{word}` limits a member to a type and those it shares with, and this is in a namespace: it is `public`, `internal`, `file` or `private`", source, span);
                    }
                    else if (owner.Kind == Syntax.TypeKind.Interface)
                    {
                        if (access != Modifiers.Public)
                            Error("AN1095", $"an interface's members are public (they are what its implementers give): `{word}` does not apply", source, span);
                    }
                    else if (access == Modifiers.Inherited && (owner.Kind is Syntax.TypeKind.Value or Syntax.TypeKind.RecordValue or Syntax.TypeKind.RecordClass or Syntax.TypeKind.Enum or Syntax.TypeKind.BitflagEnum
                        || (owner.Modifiers & (Modifiers.Static | Modifiers.Final)) != 0))
                    {
                        Error("AN1094", $"nothing derives from `{owner.Name}`: `inherited` is `private` here (write that)", source, span);
                    }
                }
            }

            static string Word(Modifiers access) => access switch
            {
                Modifiers.Public => "public",
                Modifiers.Private => "private",
                Modifiers.Internal => "internal",
                Modifiers.Friendly => "friendly",
                Modifiers.Inherited => "inherited",
                Modifiers.File => "file",
                _ => "",
            };
        }

        /// <summary>
        /// Inheritance words that cannot mean anything where they are written: <c>virtual</c> in a type nothing derives from
        /// (a final, static or record class), <c>final</c> on a method or property that does not override (one that is not
        /// virtual is already final: <c>final</c> stops the overriding of an inherited one, <c>final override</c>), and
        /// <c>virtual</c>, <c>override</c> or <c>final</c> on a field (a field is not overridden). A namespace holds
        /// constants, not fields: its state goes in a type.
        /// </summary>
        private void CheckInheritanceWords()
        {
            foreach (var unit in units)
                Check(unit.Members, null, unit.Source);

            void Check(IEnumerable<MemberDecl> members, TypeDecl? owner, SourceText source)
            {
                foreach (var member in members)
                {
                    switch (member)
                    {
                        case NamespaceDecl inner:
                            Check(inner.Members, null, source);
                            break;
                        case TypeDecl type:
                            Check(type.Members, type, source);
                            break;
                        case FieldDecl field:
                            if ((field.Modifiers & (Modifiers.Virtual | Modifiers.Override | Modifiers.Final)) != 0)
                                Error("AN1102", $"a field is not overridden: `{Words(field.Modifiers)}` does not apply to `{field.Name}`", source, field.NameSpan);
                            else if (owner is null && (field.Modifiers & Modifiers.Const) == 0)
                                Error("AN1103", $"a namespace holds constants, not fields: make `{field.Name}` a `const`, or keep it in a type (a `static class`)", source, field.NameSpan);
                            break;
                        case FunctionDecl or PropertyDecl when owner is not null:
                        {
                            var (modifiers, name, span) = member switch
                            {
                                FunctionDecl f => (f.Modifiers, f.Name, f.NameSpan),
                                PropertyDecl p => (p.Modifiers, p.Name, p.NameSpan),
                                _ => default,
                            };
                            if ((modifiers & Modifiers.Final) != 0 && (modifiers & Modifiers.Override) == 0)
                                Error("AN1101", $"`final` stops the overriding of an inherited `{name}`: it goes with `override` (`final override`); one that is not virtual is final already", source, span);
                            else if ((modifiers & Modifiers.Virtual) != 0 && (owner.Kind == Syntax.TypeKind.RecordClass || (owner.Modifiers & (Modifiers.Final | Modifiers.Static)) != 0))
                                Error("AN1100", $"nothing derives from `{owner.Name}`: `virtual` cannot let anything override `{name}` (drop it)", source, span);
                            break;
                        }
                    }
                }
            }

            static string Words(Modifiers modifiers) => string.Join(" ", new[] { (Modifiers.Virtual, "virtual"), (Modifiers.Override, "override"), (Modifiers.Final, "final") }
                .Where(w => (modifiers & w.Item1) != 0).Select(w => w.Item2));
        }

        // ── Bodies ─────────────────────────────────────────

        /// <summary>An array kept in an object is reached by index and Length only: never as an array value (it has no handle).</summary>
        private void CheckInPlaceUses()
        {
            if (!Types.Values.Any(t => t.InPlace is not null))
                return;
            foreach (var symbol in bodies)
            {
                var method = symbol.Owner.Definition.Methods[symbol.Index];
                if (method.Body is not { } body)
                    continue;
                var walker = new InPlaceUses(Assembly);
                walker.Visit(body.Block);
                if (walker.Misused is { } name)
                    Error("AN1099", $"`{name}` is kept in the object: read and write it by index (`{name}[i]`) and ask its `Length`; copy it to have an array", symbol.Scope.Source, symbol.NameSpan);
            }
        }

        private sealed class InPlaceUses(H.HirAssembly assembly) : H.HirWalker
        {
            public string? Misused;

            private H.FieldDef? Kept(H.Expr e) =>
                e is H.FieldExpr { Instance: not null, Field: { IsDefinition: true } token } && assembly.Types[token.Type].Fields[token.Member] is { } field
                && (field.Flags & H.FieldFlags.InPlace) != 0 ? field : null;

            public override void Visit(H.Expr? e)
            {
                switch (e)
                {
                    case H.ElementExpr x when Kept(x.Array) is not null:
                        Visit(((H.FieldExpr)x.Array).Instance);
                        foreach (var index in x.Indices)
                            Visit(index);
                        return;
                    case H.ArrayLengthExpr a when Kept(a.Array) is not null:
                        Visit(((H.FieldExpr)a.Array).Instance);
                        return;
                    case H.FieldExpr f when Kept(f) is { } field:
                        Misused ??= field.Name;
                        return;
                }
                base.Visit(e);
            }
        }

        private void BindBodies()
        {
            AddImplicitConstructors();
            CheckAccessWords();
            CheckInheritanceWords();
            BindAttributes();
            CheckValueLayouts();
            AddStaticConstructors();
            foreach (var type in Types.Values.Concat(Modules.Values))
                type.Seal();
            var bags = new DiagnosticBag[bodies.Count];
            void One(int i)
            {
                var bag = new DiagnosticBag();
                bags[i] = bag;
                var method = bodies[i];
                try
                {
                    method.Definition.Body = new BodyBinder(this, method, bag).Bind();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    var source = method.Scope.Source;
                    bag.Error("AN9999", $"internal error binding `{method}`: {e.Message}", source, method.Function?.NameSpan ?? method.Constructor?.HeadSpan ?? default);
                }
            }
            if (parallel && bodies.Count > 16)
                Parallel.For(0, bodies.Count, One);
            else
                for (int i = 0; i < bodies.Count; i++)
                    One(i);
            foreach (var bag in bags)
                Diagnostics.AddRange(bag);
            MakeIterators();
        }

        // ── Constants ──────────────────────────────────────

        /// <summary>The constants declared (`const`), evaluated once every declaration is known.</summary>
        private readonly List<FieldSymbol> constants = [];

        /// <summary>The constants being evaluated now: one that needs itself is a cycle.</summary>
        private readonly HashSet<FieldSymbol> evaluating = [];

        /// <summary>
        /// Whether a value of <paramref name="type"/> can be a constant written into the code: a number (any width), bool,
        /// a character, a string, an enum value, or null of a nullable one.
        /// </summary>
        internal bool IsConstantType(H.HirType type) => type switch
        {
            H.PrimitiveType { Kind: var k } => k.IsInteger() || k.IsFloat() || k == H.Primitive.Bool,
            H.NullableType { Inner: var inner } => IsConstantType(inner),
            _ => StringType is { } text && type == text || SymbolOf(type) is { IsEnum: true },
        };

        /// <summary>
        /// <c>const T Name = value;</c> in a namespace or a type: static, never changed. A number, bool, character, string or
        /// enum value is worked out when compiling and written where it is used; a value (struct) is made once, from
        /// constants, before the program uses it, and stays as it was made (readonly).
        /// </summary>
        private void DeclareConstant(TypeSymbol owner, FieldDecl field, H.HirType type, DeclarationScope scope)
        {
            var source = scope.Source;
            if ((field.Modifiers & (Modifiers.Static | Modifiers.Readonly | Modifiers.Fixed | Modifiers.Reactive)) != 0)
                Error("AN1086", "a constant is static and never changes already: no `static`, `readonly`, `fixed` or `reactive`", source, field.Type.Span);
            if (field.Initializer is null)
            {
                Error("AN1087", "a constant has its value where it is declared: `const i32 Limit = 10;`", source, field.NameSpan);
                return;
            }
            bool inlined = IsConstantType(type);
            bool made = !inlined && SymbolOf(type) is { IsStruct: true };
            if (!inlined && !made)
            {
                Error("AN1088", $"a constant is a number, bool, character, string, enum value or value (struct), not `{H.Text.HirPrinter.Print(type, Assembly)}`", source, field.Type.Span);
                return;
            }
            var (line, column) = source.LineColumn(field.NameSpan.Start);
            int index = owner.Definition.AddField(new H.FieldDef(field.Name, type)
            {
                Flags = H.FieldFlags.Static | (inlined ? H.FieldFlags.Const : H.FieldFlags.Readonly),
                Access = AccessFor(field.Modifiers, owner.IsModule ? H.Access.Internal : H.Access.Private),
                Location = new H.SourceLocation(FileIndex(source), line, column),
                Doc = source.DocBefore(field.Span.Start),
            });
            var symbol = new FieldSymbol(owner, field.Name, type, index, isStatic: true) { IsConst = inlined, IsConstant = true, Declaration = field, Scope = scope };
            owner.Fields[field.Name] = symbol;
            if (inlined)
            {
                constants.Add(symbol);
                return;
            }
            // A value: made by its static constructor, from constants only.
            if (!MadeOfConstants(field.Initializer, scope, owner))
                Error("AN1089", $"a constant value is made of constants: `const {H.Text.HirPrinter.Print(type, Assembly)} {field.Name} = new {H.Text.HirPrinter.Print(type, Assembly)}(1, 2);` with literals, constants and operators on them", source, field.Initializer.Span);
            owner.StaticInitializedFields.Add(symbol);
        }

        /// <summary>Whether an expression is built from constants only: literals, constants, operators, casts, and values made of them.</summary>
        internal bool MadeOfConstants(Expr expression, DeclarationScope scope, TypeSymbol? within) => expression switch
        {
            IntegerLiteral or FloatLiteral or StringLiteral or BoolLiteral or CharLiteral or NullLiteral => true,
            NewExpr n => n.Arguments.All(a => MadeOfConstants(a, scope, within)),
            UnaryExpr { Operator: UnaryOperator.Negate or UnaryOperator.Not or UnaryOperator.Complement } u => MadeOfConstants(u.Operand, scope, within),
            BinaryExpr b => MadeOfConstants(b.Left, scope, within) && MadeOfConstants(b.Right, scope, within),
            ConditionalExpr c => MadeOfConstants(c.Condition, scope, within) && MadeOfConstants(c.Then, scope, within) && MadeOfConstants(c.Else, scope, within),
            CastExpr c => MadeOfConstants(c.Operand, scope, within),
            // A constant, or an enum's member: an integer one, or one of an enum of values (`Color.Red`, and `(Rgb)Color.Red`).
            NameExpr or MemberExpr => ConstantField(expression, scope, within) is { IsConstant: true } or { IsConst: true } || ConstantOf(expression, scope, new DiagnosticBag(), within) is not null,
            _ => false,
        };

        /// <summary>
        /// A constant's value, worked out the first time it is asked for (a constant may name others, declared before or
        /// after it); null when it is not a constant (reported once) or not one written into the code (a value).
        /// </summary>
        internal H.HirConstant? ConstValue(FieldSymbol field)
        {
            if (!field.IsConst)
                return null;
            if (field.Value is not null || field.Declaration?.Initializer is not { } initializer || field.Scope is not { } scope || field.ConstantFailed)
                return field.Value;
            if (!evaluating.Add(field))
            {
                Error("AN1090", $"the constant `{field.Name}` needs its own value (a cycle of constants)", scope.Source, field.Declaration.NameSpan);
                field.ConstantFailed = true;
                return null;
            }
            try
            {
                var value = Constant(initializer, field.Type, scope, Diagnostics, field.Owner);
                if (value is null)
                {
                    if (!field.ConstantFailed)
                        Error("AN1091", $"the value of `{field.Name}` is not known when compiling: a constant is made of literals, other constants, enum values and operators, casts and `?:` on them", scope.Source, initializer.Span);
                    field.ConstantFailed = true;
                    return null;
                }
                field.Value = value;
                var definitions = field.Owner.Definition.Fields;
                definitions[field.Index] = definitions[field.Index] with { Value = value };
                return value;
            }
            finally
            {
                evaluating.Remove(field);
            }
        }

        /// <summary>
        /// The constant a name means (<c>Limit</c>, <c>Config.Limit</c>, <c>App.Limit</c>, an imported one): a constant of
        /// the type it is used in or its bases, of an enclosing type, of the namespace and its parents, taken by name, or
        /// of an imported namespace; or one of a type or namespace written before it.
        /// </summary>
        internal FieldSymbol? ConstantField(Expr expression, DeclarationScope scope, TypeSymbol? within)
        {
            static FieldSymbol? Constant(FieldSymbol? field) => field is { IsConstant: true } or { IsConst: true } ? field : null;
            switch (expression)
            {
                case NameExpr { TypeArguments.Length: 0 } name:
                {
                    if (Constant(within?.FindField(name.Name)) is { } own)
                        return own;
                    foreach (var enclosing in scope.EnclosingTypes)
                        if ((Types.GetValueOrDefault(enclosing) ?? Types.GetValueOrDefault(enclosing + "`0")) is { } outer && Constant(outer.FindField(name.Name)) is { } nested)
                            return nested;
                    foreach (var ns in scope.Enclosing())
                        if (Modules.TryGetValue(ns, out var module) && Constant(module.Fields.GetValueOrDefault(name.Name)) is { } local)
                            return local;
                    if (scope.Picked.TryGetValue(name.Name, out var picked) && Modules.TryGetValue(picked.Namespace, out var from) && Constant(from.Fields.GetValueOrDefault(picked.Name)) is { } taken)
                        return taken;
                    foreach (var ns in scope.Imports)
                        if (Modules.TryGetValue(ns, out var module) && Constant(module.Fields.GetValueOrDefault(name.Name)) is { } imported)
                            return imported;
                    return null;
                }
                case MemberExpr { TypeArguments.Length: 0 } member:
                {
                    if (TypeNamed(member.Target, scope) is { } type)
                    {
                        // An enum declared after the constant that names it: its members first.
                        if (type.IsEnum && type.Declaration is { } enumeration && !enumsDeclaring.Contains(type))
                            DeclareEnum(type, enumeration);
                        return Constant(type.FindField(member.Name));
                    }
                    if (NamespaceNamed(member.Target, scope) is { } ns && Modules.TryGetValue(ns, out var module))
                        return Constant(module.Fields.GetValueOrDefault(member.Name));
                    return null;
                }
            }
            return null;
        }

        /// <summary>The namespace an expression names (<c>App.Util</c>, an alias of one), if it names one.</summary>
        internal string? NamespaceNamed(Expr expression, DeclarationScope scope)
        {
            var parts = new List<string>();
            var at = expression;
            while (at is MemberExpr member)
            {
                parts.Insert(0, member.Name);
                at = member.Target;
            }
            if (at is not NameExpr name)
                return null;
            parts.Insert(0, name.Name);
            string written = string.Join('.', parts);
            foreach (var ns in scope.Enclosing())
            {
                string full = ns.Length == 0 ? written : $"{ns}.{written}";
                if (Namespaces.Contains(full))
                    return full;
            }
            if (scope.NamespaceAliases.TryGetValue(written, out var aliased))
                return aliased;
            if (Imported(written, scope) is { } imported && Namespaces.Contains(imported))
                return imported;
            return Namespaces.Contains(written) ? written : null;
        }

        /// <summary>
        /// A compile-time constant of <paramref name="type"/>: literals; other constants (<c>const</c>) and enum members by
        /// name; unary and binary operators on numbers (every width, floats too), strings (<c>+</c>), bools and enum flags;
        /// comparisons; casts between numbers and enums; <c>?:</c>. Null when <paramref name="expression"/> is not one
        /// (nothing is reported).
        /// </summary>
        internal H.HirConstant? Constant(Expr expression, H.HirType type, DeclarationScope scope, DiagnosticBag bag, TypeSymbol? within)
        {
            switch (expression)
            {
                case StringLiteral s when StringType is { } text && (type == text || type is H.NullableType { Inner: var held } && held == text):
                    return new H.StringConstant(s.Value);
                case IntegerLiteral i when type is H.PrimitiveType { Kind: var k } && k.IsInteger():
                    return Fits(i.Value, k) ? new H.IntegerConstant(i.Value, k) : null;
                case IntegerLiteral i when type is H.PrimitiveType { Kind: var fk } && fk.IsFloat():
                    return FloatOf(i.Value, fk);
                case FloatLiteral f when type is H.PrimitiveType { Kind: var fk } && fk.IsFloat():
                    return BodyBinder.FloatOf(f, fk);
                case BoolLiteral boolean when type == H.HirType.Bool:
                    return new H.BoolConstant(boolean.Value);
                case CharLiteral character when character.Value.Length == 1 && type is H.PrimitiveType { Kind: var ck } && ck.IsInteger():
                    return Fits(character.Value[0], ck) ? new H.IntegerConstant(character.Value[0], ck) : null;
                case NullLiteral when type is H.NullableType:
                    return new H.NullConstant();
                case NameExpr { Name: var name } when within is { IsEnum: true } && within.Fields.TryGetValue(name, out var member) && member.Value is H.EnumConstant e:
                    return type == within.Type ? e : type is H.PrimitiveType { Kind: var ek } && ek.IsInteger() ? new H.IntegerConstant(e.Value, ek) : null;
                case MemberExpr { Target: NameExpr or MemberExpr } m when TypeNamed(m.Target, scope) is { IsEnum: true } enumType
                    && enumType.Fields.TryGetValue(m.Name, out var enumMember) && enumMember.Value is H.EnumConstant value:
                    return type == enumType.Type ? value : type is H.PrimitiveType { Kind: var vk } && vk.IsInteger() ? new H.IntegerConstant(value.Value, vk) : null;
                case UnaryExpr { Operator: UnaryOperator.Negate } negate when type is H.PrimitiveType { Kind: var nk } && (nk.IsInteger() || nk.IsFloat()):
                    return Constant(negate.Operand, type, scope, bag, within) switch
                    {
                        H.IntegerConstant n when Fits(-n.Value, nk) => new H.IntegerConstant(-n.Value, nk),
                        H.FloatConstant x => new H.FloatConstant(Larva.SoftFloat.Neg(x.Bits, Format(nk)), nk),
                        _ => null,
                    };
                case UnaryExpr { Operator: UnaryOperator.Complement } complement when type is H.PrimitiveType { Kind: var ck } && ck.IsInteger():
                    return Constant(complement.Operand, type, scope, bag, within) is H.IntegerConstant bitsOf ? new H.IntegerConstant(Wrap(~bitsOf.Value, ck), ck) : null;
                case UnaryExpr { Operator: UnaryOperator.Not } not when type == H.HirType.Bool:
                    return Constant(not.Operand, type, scope, bag, within) is H.BoolConstant truth ? new H.BoolConstant(!truth.Value) : null;
                case ConditionalExpr conditional:
                    return Constant(conditional.Condition, H.HirType.Bool, scope, bag, within) is H.BoolConstant { Value: var chosen }
                        ? Constant(chosen ? conditional.Then : conditional.Else, type, scope, bag, within)
                        : null;
                case BinaryExpr binary when Arithmetic(binary.Operator) && (type is H.PrimitiveType { Kind: var bk } && (bk.IsInteger() || bk.IsFloat()) || SymbolOf(type) is { IsEnum: true }):
                {
                    var left = Constant(binary.Left, type, scope, bag, within);
                    var right = Constant(binary.Right, type, scope, bag, within);
                    return left is null || right is null ? null : Arithmetic(binary.Operator, left, right, type);
                }
                case BinaryExpr { Operator: BinaryOperator.Add } concat when StringType is { } stringType && type == stringType:
                    return Constant(concat.Left, type, scope, bag, within) is H.StringConstant head && Constant(concat.Right, type, scope, bag, within) is H.StringConstant tail
                        ? new H.StringConstant(head.Value + tail.Value)
                        : null;
                case BinaryExpr { Operator: BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr } logic when type == H.HirType.Bool:
                {
                    if (Constant(logic.Left, type, scope, bag, within) is not H.BoolConstant p || Constant(logic.Right, type, scope, bag, within) is not H.BoolConstant q)
                        return null;
                    return new H.BoolConstant(logic.Operator == BinaryOperator.LogicalAnd ? p.Value && q.Value : p.Value || q.Value);
                }
                case BinaryExpr { Operator: BinaryOperator.Coalesce } coalesce:
                    return Constant(coalesce.Left, type, scope, bag, within) is { } first and not H.NullConstant ? first : Constant(coalesce.Right, type, scope, bag, within);
            }
            // Anything else (a constant by name, a comparison, a cast…) in its own type, then converted to the one wanted.
            return ConstantOf(expression, scope, bag, within) is var (natural, from) ? Coerce(natural, from, type) : null;
        }

        /// <summary>A constant expression in its own type: what it would be without a type wanted.</summary>
        internal (H.HirConstant Value, H.HirType Type)? ConstantOf(Expr expression, DeclarationScope scope, DiagnosticBag bag, TypeSymbol? within)
        {
            switch (expression)
            {
                case IntegerLiteral i:
                {
                    var kind = new[] { H.Primitive.I32, H.Primitive.I64, H.Primitive.U64, H.Primitive.I128, H.Primitive.U128, H.Primitive.I256, H.Primitive.U256 }
                        .FirstOrDefault(k => Fits(i.Value, k), H.Primitive.Void);
                    return kind == H.Primitive.Void ? null : (new H.IntegerConstant(i.Value, kind), H.HirType.Of(kind));
                }
                case FloatLiteral f:
                {
                    var kind = f.Suffix switch { "f16" => H.Primitive.F16, "f32" => H.Primitive.F32, "f128" => H.Primitive.F128, "f256" => H.Primitive.F256, _ => H.Primitive.F64 };
                    return (BodyBinder.FloatOf(f, kind), H.HirType.Of(kind));
                }
                case StringLiteral s when StringType is { } text:
                    return (new H.StringConstant(s.Value), text);
                case BoolLiteral b:
                    return (new H.BoolConstant(b.Value), H.HirType.Bool);
                case CharLiteral c when c.Value.Length == 1:
                    return (new H.IntegerConstant(c.Value[0], H.Primitive.U16), H.HirType.Of(H.Primitive.U16));
                case NameExpr { Name: var name } when within is { IsEnum: true } && within.Fields.TryGetValue(name, out var member) && member.Value is H.EnumConstant e:
                    return (e, within.Type);
                case MemberExpr { Target: NameExpr or MemberExpr } m when TypeNamed(m.Target, scope) is { IsEnum: true } enumType
                    && enumType.Fields.TryGetValue(m.Name, out var enumMember) && enumMember.Value is H.EnumConstant value:
                    return (value, enumType.Type);
                // `nameof(x)` in a constant: the last name written.
                case CallExpr { Target: NameExpr { Name: "nameof" }, Arguments: [NameExpr or MemberExpr] } nameOf when StringType is { } nameType:
                    return (new H.StringConstant(nameOf.Arguments[0] is NameExpr only ? only.Name : ((MemberExpr)nameOf.Arguments[0]).Name), nameType);
                case NameExpr { Name: var localName } when scope.LocalConstants?.Invoke(localName) is var (localValue, localType):
                    return (localValue, localType);
                case NameExpr or MemberExpr when ConstantField(expression, scope, within) is { IsConst: true } field:
                    return ConstValue(field) is { } constant ? (constant, field.Type) : null;
                case UnaryExpr { Operator: UnaryOperator.Negate or UnaryOperator.Complement or UnaryOperator.Not } unary:
                    return ConstantOf(unary.Operand, scope, bag, within) is var (_, operandType) && Constant(expression, operandType, scope, bag, within) is { } done ? (done, operandType) : null;
                case ConditionalExpr conditional:
                    return Constant(conditional.Condition, H.HirType.Bool, scope, bag, within) is H.BoolConstant { Value: var chosen }
                        ? ConstantOf(chosen ? conditional.Then : conditional.Else, scope, bag, within)
                        : null;
                case CastExpr cast:
                {
                    if (ResolveType(cast.Type, scope, bag) is not { } target || ConstantOf(cast.Operand, scope, bag, within) is not var (operand, from))
                        return null;
                    return Cast(operand, from, target) is { } converted ? (converted, target) : null;
                }
                case BinaryExpr binary:
                {
                    if (ConstantOf(binary.Left, scope, bag, within) is not var (_, leftType) || ConstantOf(binary.Right, scope, bag, within) is not var (_, rightType))
                        return null;
                    var common = Common(leftType, rightType);
                    if (common is null)
                        return null;
                    if (binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual)
                    {
                        if (Constant(binary.Left, common, scope, bag, within) is not { } a || Constant(binary.Right, common, scope, bag, within) is not { } b || Compare(a, b) is not { } order)
                            return null;
                        bool result = binary.Operator switch
                        {
                            BinaryOperator.Equal => order == 0,
                            BinaryOperator.NotEqual => order != 0,
                            BinaryOperator.Less => order < 0,
                            BinaryOperator.LessEqual => order <= 0,
                            BinaryOperator.Greater => order > 0,
                            _ => order >= 0,
                        };
                        return (new H.BoolConstant(result), H.HirType.Bool);
                    }
                    return Constant(expression, common, scope, bag, within) is { } computed ? (computed, common) : null;
                }
            }
            return null;
        }

        private static bool Arithmetic(BinaryOperator op) => op is BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide
            or BinaryOperator.Remainder or BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Xor or BinaryOperator.ShiftLeft or BinaryOperator.ShiftRight;

        /// <summary>An operator on two constants of <paramref name="type"/> (integers wrap as at run time; floats round as IEEE).</summary>
        private H.HirConstant? Arithmetic(BinaryOperator op, H.HirConstant left, H.HirConstant right, H.HirType type)
        {
            switch (left, right)
            {
                case (H.IntegerConstant a, H.IntegerConstant b) when type is H.PrimitiveType { Kind: var k }:
                {
                    BigInteger? result = op switch
                    {
                        BinaryOperator.Add => a.Value + b.Value,
                        BinaryOperator.Subtract => a.Value - b.Value,
                        BinaryOperator.Multiply => a.Value * b.Value,
                        BinaryOperator.Divide when !b.Value.IsZero => BigInteger.Divide(a.Value, b.Value),
                        BinaryOperator.Remainder when !b.Value.IsZero => BigInteger.Remainder(a.Value, b.Value),
                        BinaryOperator.And => a.Value & b.Value,
                        BinaryOperator.Or => a.Value | b.Value,
                        BinaryOperator.Xor => a.Value ^ b.Value,
                        BinaryOperator.ShiftLeft when b.Value >= 0 && b.Value < k.Bits() => a.Value << (int)b.Value,
                        BinaryOperator.ShiftRight when b.Value >= 0 && b.Value < k.Bits() => a.Value >> (int)b.Value,
                        _ => null,
                    };
                    return result is { } r && Fits(r, k) ? new H.IntegerConstant(r, k) : null;
                }
                case (H.FloatConstant a, H.FloatConstant b) when type is H.PrimitiveType { Kind: var k }:
                {
                    var format = Format(k);
                    BigInteger? bits = op switch
                    {
                        BinaryOperator.Add => Larva.SoftFloat.Add(a.Bits, b.Bits, format),
                        BinaryOperator.Subtract => Larva.SoftFloat.Sub(a.Bits, b.Bits, format),
                        BinaryOperator.Multiply => Larva.SoftFloat.Mul(a.Bits, b.Bits, format),
                        BinaryOperator.Divide => Larva.SoftFloat.Div(a.Bits, b.Bits, format),
                        BinaryOperator.Remainder => Larva.SoftFloat.Rem(a.Bits, b.Bits, format),
                        _ => null,
                    };
                    return bits is { } value ? new H.FloatConstant(value, k) : null;
                }
                case (H.EnumConstant a, H.EnumConstant b) when op is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Xor:
                {
                    var value = op == BinaryOperator.And ? a.Value & b.Value : op == BinaryOperator.Or ? a.Value | b.Value : a.Value ^ b.Value;
                    return new H.EnumConstant(type, value);
                }
            }
            return null;
        }

        /// <summary>Two constants compared: negative, zero or positive; null when they are not ordered (NaN, different kinds).</summary>
        private static int? Compare(H.HirConstant a, H.HirConstant b) => (a, b) switch
        {
            (H.IntegerConstant x, H.IntegerConstant y) => x.Value.CompareTo(y.Value),
            (H.FloatConstant x, H.FloatConstant y) when x.Type == y.Type => Larva.SoftFloat.Compare(x.Bits, y.Bits, Format(x.Type)),
            (H.StringConstant x, H.StringConstant y) => string.CompareOrdinal(x.Value, y.Value),
            (H.BoolConstant x, H.BoolConstant y) => x.Value == y.Value ? 0 : x.Value ? 1 : -1,
            (H.EnumConstant x, H.EnumConstant y) => x.Value.CompareTo(y.Value),
            (H.NullConstant, H.NullConstant) => 0,
            _ => null,
        };

        /// <summary>The type two constants meet in for an operator: the same, or the integer that holds both, or the float.</summary>
        private static H.HirType? Common(H.HirType a, H.HirType b)
        {
            if (a == b)
                return a;
            if (a is not H.PrimitiveType { Kind: var x } || b is not H.PrimitiveType { Kind: var y })
                return null;
            if (x.IsFloat() || y.IsFloat())
                return H.HirType.Of(!x.IsFloat() ? y : !y.IsFloat() ? x : x.Bits() >= y.Bits() ? x : y);
            if (x.IsInteger() && y.IsInteger())
            {
                // The wider; a signed one when either is (an unsigned literal meets a signed constant in its type).
                var wider = x.Bits() >= y.Bits() ? x : y;
                return H.HirType.Of(wider);
            }
            return null;
        }

        /// <summary>A constant converted where the language converts by itself: an integer that fits, an integer or a float to a float, a value to its nullable.</summary>
        private H.HirConstant? Coerce(H.HirConstant value, H.HirType from, H.HirType to)
        {
            if (from == to)
                return value;
            if (to is H.NullableType { Inner: var inner })
                return value is H.NullConstant ? value : Coerce(value, from, inner);
            switch (value)
            {
                case H.IntegerConstant i when to is H.PrimitiveType { Kind: var k }:
                    return k.IsInteger() ? Fits(i.Value, k) ? new H.IntegerConstant(i.Value, k) : null : k.IsFloat() ? FloatOf(i.Value, k) : null;
                case H.FloatConstant f when to is H.PrimitiveType { Kind: var k } && k.IsFloat() && k.Bits() >= f.Type.Bits():
                    return new H.FloatConstant(Larva.SoftFloat.Convert(f.Bits, Format(f.Type), Format(k)), k);
                case H.EnumConstant e when to is H.PrimitiveType { Kind: var k } && k.IsInteger():
                    return Fits(e.Value, k) ? new H.IntegerConstant(e.Value, k) : null;
            }
            return null;
        }

        /// <summary>A constant cast, as `(T)value` would at run time: integers wrap, floats round and truncate, enums to and from their numbers.</summary>
        private H.HirConstant? Cast(H.HirConstant value, H.HirType from, H.HirType to)
        {
            if (from == to)
                return value;
            if (SymbolOf(to) is { IsEnum: true })
                return value switch
                {
                    H.IntegerConstant i => new H.EnumConstant(to, i.Value),
                    H.EnumConstant e => new H.EnumConstant(to, e.Value),
                    _ => null,
                };
            if (to is not H.PrimitiveType { Kind: var k })
                return Coerce(value, from, to);
            return value switch
            {
                H.IntegerConstant i when k.IsInteger() => new H.IntegerConstant(Wrap(i.Value, k), k),
                H.IntegerConstant i when k.IsFloat() => FloatOf(i.Value, k),
                H.EnumConstant e when k.IsInteger() => new H.IntegerConstant(Wrap(e.Value, k), k),
                H.FloatConstant f when k.IsFloat() => new H.FloatConstant(Larva.SoftFloat.Convert(f.Bits, Format(f.Type), Format(k)), k),
                H.FloatConstant f when k.IsInteger() && !Larva.SoftFloat.IsNaN(f.Bits, Format(f.Type)) && !Larva.SoftFloat.IsInfinity(f.Bits, Format(f.Type))
                    => new H.IntegerConstant(Wrap(Larva.SoftFloat.ToInteger(f.Bits, Format(f.Type)), k), k),
                _ => Coerce(value, from, to),
            };
        }

        private static Larva.FloatFormat Format(H.Primitive kind) => Larva.FloatFormat.Of(kind.Bits());

        private static H.FloatConstant FloatOf(BigInteger value, H.Primitive kind) => new(Larva.SoftFloat.FromInteger(value, Format(kind)), kind);

        /// <summary>An integer wrapped into the type's width, as the type's arithmetic does.</summary>
        private static BigInteger Wrap(BigInteger value, H.Primitive kind)
        {
            var modulus = BigInteger.One << kind.Bits();
            var r = ((value % modulus) + modulus) % modulus;
            return kind.IsSigned() && r >= modulus >> 1 ? r - modulus : r;
        }

        /// <summary>The type parameters in scope of a declaration: the type's and, inside a method, the method's.</summary>
        internal static Func<H.GenericParamType, H.GenericParam?> OwnGeneric(TypeSymbol owner, H.MethodDef? method) => p =>
        {
            var generics = p.Owner == H.GenericOwner.Type ? owner.Definition.Generics : method?.Generics;
            return generics is not null && p.Index < generics.Count ? generics[p.Index] : null;
        };

        /// <summary>The constraint words a type argument is checked against, and the word (the number groups are lists).</summary>
        internal static readonly Dictionary<H.ConstraintKinds, string> KindWords = new()
        {
            [H.ConstraintKinds.Value] = "value",
            [H.ConstraintKinds.Plain] = "plain",
            [H.ConstraintKinds.Copy] = "copy",
            [H.ConstraintKinds.Unmanaged] = "unmanaged",
            [H.ConstraintKinds.Managed] = "managed",
            [H.ConstraintKinds.New] = "new()",
            [H.ConstraintKinds.Enum] = "enum",
            [H.ConstraintKinds.Bitflags] = "bitflag enum",
        };

        /// <summary>
        /// A type written in a declaration (a field's, a parameter's, a local's): each generic type in it gets its arguments
        /// checked against the constraint words and lists of its parameters (<c>Box&lt;str&gt;</c> with <c>Box&lt;T : unmanaged&gt;</c>
        /// is AN2102). Interfaces, operators and conversions are checked where objects are made and methods called.
        /// </summary>
        internal void CheckDeclared(H.HirType? type, Func<H.GenericParamType, H.GenericParam?> own, SourceText source, TextSpan span, bool values = true)
        {
            // A type that holds values (a variable's, a field's, an element's…): never a static class, which has none.
            if (values && type is H.NamedType top && SymbolOf(top) is { IsStatic: true, IsExtension: false } holder && !holder.Name.StartsWith('$'))
                Diagnostics.Error("AN1105", $"`{holder.Name}` is a static class: it has no values; use its members (`{holder.Name}.…`)", source, span);
            switch (type)
            {
                case H.NamedType { Arguments.Length: > 0 } named when SymbolOf(named) is { } symbol:
                {
                    var generics = symbol.Definition.Generics;
                    for (int g = 0; g < generics.Count && g < named.Arguments.Length; g++)
                    {
                        var argument = named.Arguments[g];
                        if (argument is H.PrimitiveType { Kind: H.Primitive.Void } || SymbolOf(argument) is { } s && s.Token == symbol.Token && argument is H.NamedType)
                            continue;
                        foreach (var kind in KindWords.Keys)
                        {
                            if ((generics[g].Kinds & kind) != 0 && !Meets(argument, kind, own, []))
                                Diagnostics.Error("AN2102", $"`{H.Text.HirPrinter.Print(argument, Assembly)}` is not `{KindWords[kind]}` (the constraint of `{generics[g].Name}`)", source, span);
                        }
                        if (generics[g].OneOf is { Length: > 0 } allowed && !allowed.Contains(argument)
                            && !(argument is H.GenericParamType p && own(p) is { } ownParam && NumberKinds(ownParam.Kinds) is { } kinds && kinds.All(k => allowed.Contains(H.HirType.Of(k)))
                                || argument is H.GenericParamType q && own(q) is { OneOf.Length: > 0 } list && list.OneOf.All(allowed.Contains)))
                            Diagnostics.Error("AN2101", $"`{H.Text.HirPrinter.Print(argument, Assembly)}` is not one of {string.Join(", ", allowed.Select(t => H.Text.HirPrinter.Print(t, Assembly)))} (the constraint of `{generics[g].Name}`)", source, span);
                    }
                    foreach (var argument in named.Arguments)
                        CheckDeclared(argument, own, source, span);
                    break;
                }
                case H.ArrayType array:
                    CheckDeclared(array.Element, own, source, span);
                    break;
                case H.InlineArrayType inline:
                    CheckDeclared(inline.Element, own, source, span);
                    break;
                case H.NullableType nullable:
                    CheckDeclared(nullable.Inner, own, source, span);
                    break;
                case H.PointerType pointer:
                    CheckDeclared(pointer.Target, own, source, span);
                    break;
                case H.ReferenceType reference:
                    CheckDeclared(reference.Target, own, source, span);
                    break;
                case H.FuncType func:
                    foreach (var p in func.Parameters)
                        CheckDeclared(p, own, source, span);
                    CheckDeclared(func.Result, own, source, span);
                    break;
            }
        }

        /// <summary>
        /// Whether a type is of a constraint's kind. <c>value</c>: a primitive, a value type, an enum. <c>unmanaged</c> and
        /// <c>plain</c>: holds no object reference at any depth (pointers are fine). <c>copy</c>: copied freely (numbers,
        /// objects, strings, values that own no memory; arrays own theirs). <c>managed</c>: holds an object reference.
        /// <c>new()</c>: a class with a constructor without parameters, or a value type. A type parameter is of the kinds its
        /// own constraints give it (<paramref name="own"/> finds them).
        /// </summary>
        internal bool Meets(H.HirType type, H.ConstraintKinds kind, Func<H.GenericParamType, H.GenericParam?> own, HashSet<H.HirType> visiting)
        {
            if (kind is H.ConstraintKinds.Enum or H.ConstraintKinds.Bitflags)
            {
                // Only enums (a bitflag enum is an enum too); a type parameter with the constraint.
                return type switch
                {
                    H.NamedType n when SymbolOf(n) is { IsEnum: true } e => kind == H.ConstraintKinds.Enum || e.Definition.Kind == H.TypeKind.Bitflags,
                    H.GenericParamType p => own(p) is { } ownParam && (ownParam.Kinds & (kind == H.ConstraintKinds.Enum ? H.ConstraintKinds.Enum | H.ConstraintKinds.Bitflags : H.ConstraintKinds.Bitflags)) != 0,
                    _ => false,
                };
            }
            switch (type)
            {
                case H.PrimitiveType { Kind: H.Primitive.Void }:
                    return false;
                case H.PrimitiveType or H.PointerType:
                    return kind is not (H.ConstraintKinds.Managed or H.ConstraintKinds.New);
                case H.FuncType:
                    return kind is H.ConstraintKinds.Copy or H.ConstraintKinds.Managed;
                case H.ReferenceType:
                    return kind is H.ConstraintKinds.Copy;
                case H.ArrayType:
                    return kind is H.ConstraintKinds.Managed;
                case H.NullableType n:
                    return kind is not H.ConstraintKinds.New && Meets(n.Inner, kind, own, visiting);
                case H.GenericParamType p:
                {
                    if (own(p) is not { } ownParam)
                        return false;
                    var kinds = ownParam.Kinds;
                    if ((kinds & (H.ConstraintKinds.Plain | H.ConstraintKinds.Unmanaged)) != 0 || NumberKinds(kinds) is not null)
                        kinds |= H.ConstraintKinds.Plain | H.ConstraintKinds.Unmanaged | H.ConstraintKinds.Copy | H.ConstraintKinds.Value;
                    if (ownParam.OneOf.Length > 0 && ownParam.OneOf.All(t => Meets(t, kind, own, visiting)))
                        return true;
                    kinds |= InterfaceKinds(ownParam.Types, type, []);
                    if ((kinds & (H.ConstraintKinds.Plain | H.ConstraintKinds.Unmanaged)) != 0)
                        kinds |= H.ConstraintKinds.Plain | H.ConstraintKinds.Unmanaged | H.ConstraintKinds.Copy | H.ConstraintKinds.Value;
                    if (ownParam.Types.Any(IsClass))
                        kinds |= H.ConstraintKinds.Managed | H.ConstraintKinds.Copy;
                    if (ownParam.Constructors.Any(c => c.Length == 0))
                        kinds |= H.ConstraintKinds.New;
                    return (kinds & kind) != 0;
                }
                case H.NamedType n when SymbolOf(type) is { } symbol:
                {
                    if (symbol.IsPrimitive)
                        return kind is not (H.ConstraintKinds.Managed or H.ConstraintKinds.New);
                    if (symbol.IsEnum)
                        return kind is not (H.ConstraintKinds.Managed or H.ConstraintKinds.New);
                    if (symbol.IsInterface || ObjectSymbol is { } root && type == root.Type)
                        return kind is H.ConstraintKinds.Managed or H.ConstraintKinds.Copy;
                    if (symbol.IsClass)
                        return kind is H.ConstraintKinds.Managed or H.ConstraintKinds.Copy
                            || kind is H.ConstraintKinds.New && !symbol.IsAbstract && !symbol.IsStatic && (symbol.Constructors.Count == 0 || symbol.Constructors.Any(c => c.Definition.Parameters.Count == 0));
                    // A value type: what its fields are.
                    if (kind is H.ConstraintKinds.Value)
                        return true;
                    if (kind is H.ConstraintKinds.New)
                        return symbol.Constructors.Count == 0 || symbol.Constructors.Any(c => c.Definition.Parameters.Count == 0);
                    if (!visiting.Add(type))
                        return kind is not H.ConstraintKinds.Managed;
                    bool result = kind is H.ConstraintKinds.Managed ? false : true;
                    foreach (var field in symbol.Definition.Fields)
                    {
                        if ((field.Flags & H.FieldFlags.Static) != 0)
                            continue;
                        bool fits = Meets(field.Type.Substitute(n.Arguments, []), kind, own, visiting);
                        if (kind is H.ConstraintKinds.Managed ? fits : !fits)
                        {
                            result = kind is H.ConstraintKinds.Managed;
                            break;
                        }
                    }
                    visiting.Remove(type);
                    return result;
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// The kinds the interfaces in <paramref name="constraints"/> ask of their own parameter where <paramref name="self"/>
        /// stands (<c>INumber&lt;TSelf : plain&gt;</c> makes a <c>T : INumber&lt;T&gt;</c> plain), through the interfaces they extend.
        /// </summary>
        internal H.ConstraintKinds InterfaceKinds(IEnumerable<H.HirType> constraints, H.HirType self, HashSet<H.NamedType> seen)
        {
            var kinds = H.ConstraintKinds.None;
            foreach (var iface in constraints.OfType<H.NamedType>())
            {
                if (!seen.Add(iface) || SymbolOf(iface) is not { IsInterface: true } symbol)
                    continue;
                for (int i = 0; i < iface.Arguments.Length && i < symbol.Definition.Generics.Count; i++)
                    if (iface.Arguments[i] == self)
                        kinds |= symbol.Definition.Generics[i].Kinds;
                kinds |= InterfaceKinds(symbol.Interfaces.Select(x => x.Substitute(iface.Arguments)), self, seen);
            }
            return kinds;
        }

        /// <summary>
        /// A type's size in bytes as <c>fixed(N)</c> counts it: primitives by their bits, references and pointers 8, enums
        /// their base, values the sum of their fields; null when it has no fixed size (a parameter without <c>fixed(N)</c>).
        /// </summary>
        internal int? SizeOf(H.HirType type, Func<H.GenericParamType, H.GenericParam?> own, HashSet<H.HirType>? visiting = null)
        {
            switch (type)
            {
                case H.PrimitiveType { Kind: H.Primitive.Void }:
                    return null;
                case H.PrimitiveType { Kind: H.Primitive.Bool }:
                    return 1;
                case H.PrimitiveType p:
                    return Math.Max(1, p.Kind.Bits() / 8);
                case H.PointerType or H.FuncType or H.NativeFuncType or H.ArrayType or H.ReferenceType:
                    return 8;
                case H.GenericParamType g:
                    return own(g) is { FixedSize: > 0 } param ? param.FixedSize : null;
                case H.NamedType n when SymbolOf(n) is { } symbol:
                {
                    if (symbol.IsPrimitive || symbol.IsEnum)
                        return symbol.Definition.Underlying is H.PrimitiveType u ? SizeOf(u, own, visiting) : 4;
                    if (symbol.IsClass || symbol.IsInterface)
                        return 8;
                    visiting ??= [];
                    if (!visiting.Add(type))
                        return null;
                    int total = 0;
                    foreach (var field in symbol.Definition.Fields)
                    {
                        if ((field.Flags & H.FieldFlags.Static) != 0)
                            continue;
                        if (SizeOf(field.Type.Substitute(n.Arguments, []), own, visiting) is not { } size)
                            return null;
                        total += size;
                    }
                    visiting.Remove(type);
                    return total;
                }
                default:
                    return null;
            }
        }

        internal static bool Fits(BigInteger value, H.Primitive kind)
        {
            int bits = kind.Bits();
            return kind.IsSigned()
                ? value >= -(BigInteger.One << (bits - 1)) && value < BigInteger.One << (bits - 1)
                : value >= 0 && value < BigInteger.One << bits;
        }

        /// <summary>The type an expression names (<c>Color</c>, <c>App.Color</c>), if it names one.</summary>
        internal TypeSymbol? TypeNamed(Expr expression, DeclarationScope scope)
        {
            var parts = new List<string>();
            var at = expression;
            while (at is MemberExpr member)
            {
                parts.Insert(0, member.Name);
                at = member.Target;
            }
            if (at is not NameExpr name)
                return null;
            parts.Insert(0, name.Name);
            var arguments = expression is MemberExpr last ? last.TypeArguments : name.TypeArguments;
            return FindType(new QualifiedName([.. parts], expression.Span), scope, arguments.Length);
        }
    }

    /// <summary>The result of compiling Antril source: the HIR assembly (null on errors) and what was reported.</summary>
    public sealed class AntrilResult
    {
        public required H.HirAssembly? Assembly { get; init; }
        public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
        public required CompilationUnit[] Units { get; init; }

        /// <summary>Milliseconds per phase: parse, declarations, bodies.</summary>
        public required IReadOnlyDictionary<string, long> Timings { get; init; }

        public bool Success => Assembly is not null;
    }

    /// <summary>Antril's compiler: source text to a Larva HIR assembly.</summary>
    public static class AntrilCompiler
    {
        public static AntrilResult Compile(IEnumerable<SourceText> sources, string assemblyName = "Program", bool parallel = true) =>
            Compile(sources, [], assemblyName, parallel);

        /// <summary>
        /// Compiles <paramref name="sources"/> against <paramref name="references"/> (assemblies read from <c>.lva</c>): what
        /// the program uses of them becomes type and member references, which <see cref="Larva.Hir.Linking.HirLinker"/>
        /// resolves when the program is linked with them.
        /// </summary>
        public static AntrilResult Compile(IEnumerable<SourceText> sources, IReadOnlyList<H.HirAssembly> references, string assemblyName = "Program", bool parallel = true, IEnumerable<H.HirResource>? resources = null, IEnumerable<string>? externalAssemblies = null, AntrilOptions? options = null)
        {
            var timings = new Dictionary<string, long>();
            var watch = Stopwatch.StartNew();
            var files = sources.ToArray();
            var bags = new DiagnosticBag[files.Length];
            var diagnostics = new DiagnosticBag();
            var (units, table) = Parser.ParseAll(files, bags, references, diagnostics, parallel);
            foreach (var bag in bags)
                diagnostics.AddRange(bag);
            timings["parse"] = watch.ElapsedMilliseconds;
            if (diagnostics.HasErrors)
                return new AntrilResult { Assembly = null, Diagnostics = diagnostics, Units = units, Timings = timings };
            // Variadic templates: one ordinary generic declaration per pack size the program uses.
            var expanded = Variadic.Expand(units, diagnostics, references, table);
            if (diagnostics.HasErrors)
                return new AntrilResult { Assembly = null, Diagnostics = diagnostics, Units = units, Timings = timings };
            var binder = new Binder(assemblyName, expanded, diagnostics, parallel, references) { Operators = table, Options = options ?? AntrilOptions.Default };
            if (resources is not null)
                binder.Assembly.Resources.AddRange(resources); // `$resource("name")` binds against them; the .lva carries them
            if (externalAssemblies is not null)
                binder.ExternalAssemblies.UnionWith(externalAssemblies);
            binder.Assembly.LanguageData.AddRange(Variadic.Recorded(units)); // programs that use the library expand them
            var assembly = binder.Bind(timings);
            return new AntrilResult { Assembly = diagnostics.HasErrors ? null : assembly, Diagnostics = diagnostics, Units = units, Timings = timings };
        }

        public static AntrilResult Compile(string text, string path = "program.ant") => Compile([new SourceText(text, path)]);
    }
}
